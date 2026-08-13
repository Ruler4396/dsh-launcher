using System.Diagnostics;
using System.Drawing;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshWeb;

internal static class Program
{
    private const string DefaultUrl = "http://127.0.0.1:3080";
    private const int SW_RESTORE = 9;

    /// 目标服务地址/端口：默认 3080，可用环境变量 DSH_WEB_URL 覆盖（免重建，见 ShellLogic.ResolveTarget）。
    private static readonly (string Url, int Port) Target = ShellLogic.ResolveTarget(
        Environment.GetEnvironmentVariable("DSH_WEB_URL"));

    /// 设置 DSH_WEB_URL 时视为“外部托管服务”，壳不再自动拉起 dsh。
    private static readonly bool ServerManagedExternally =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSH_WEB_URL"));

    /// 渲染进程崩溃自动重载的节流时间戳（避免崩溃死循环）。
    private static long _lastReloadTick;

    /// 共享 WebView2 环境：主窗口与插件弹窗共用同一用户数据目录与浏览器参数。
    private static CoreWebView2Environment? _sharedEnvironment;

    /// <summary>
    /// 创建（或复用）共享 WebView2 环境。
    /// AdditionalBrowserArguments 放行无手势自动播放：WebView2 在当前 SDK 中不会为
    /// Autoplay 触发 PermissionRequested 事件（直接静默拒绝），
    /// --autoplay-policy=no-user-gesture-required 是唯一可用的开关（声音类插件依赖）。
    /// </summary>
    private static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync(string userDataFolder)
    {
        if (_sharedEnvironment is null)
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            };
            _sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
        }
        return _sharedEnvironment;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [STAThread]
    private static void Main()
    {
        // 单实例：重复启动只把已开窗口带到前台，避免多开 WebView2 进程白白占用内存。
        // 锁按目标端口隔离，不同服务可各开一个壳窗口。
        using var mutex = new Mutex(true, $@"Local\DshWeb.SingleInstance.{Target.Port}", out var firstInstance);
        if (!firstInstance)
        {
            var existing = FindWindow(null, "DeepSeek Harness");
            if (existing != IntPtr.Zero)
            {
                ShowWindow(existing, SW_RESTORE);
                SetForegroundWindow(existing);
            }
            return;
        }

        // 服务未启动时自动拉起（调用同目录下的 start-dsh.vbs 静默启动）。
        // 设置了 DSH_WEB_URL 时不自动拉起（视为外部托管服务）。
        if (!ServerManagedExternally && !PortOpen(Target.Port))
        {
            var vbs = Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs");
            if (File.Exists(vbs))
            {
                Process.Start(new ProcessStartInfo("wscript.exe", "\"" + vbs + "\"") { UseShellExecute = true });
                for (var i = 0; i < 90 && !PortOpen(Target.Port); i++)
                    Thread.Sleep(1000);
            }
            else
            {
                MessageBox.Show($"未找到 start-dsh.vbs，无法启动 dsh 服务（{Target.Url}）。", "DeepSeek Harness",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        if (!PortOpen(Target.Port))
        {
            MessageBox.Show($"dsh 服务不可用（{Target.Url}），请确认服务已启动并查看日志：%USERPROFILE%\\.dsh-web.log", "DeepSeek Harness",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var icon = LoadEmbeddedIcon();
        var form = new Form
        {
            Text = "DeepSeek Harness",
            ClientSize = new Size(1280, 840),
            StartPosition = FormStartPosition.CenterScreen,
            MinimumSize = new Size(800, 600),
            Icon = icon ?? SystemIcons.Application
        };

        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);
        form.FormClosing += (_, _) =>
        {
            try { web.Dispose(); } catch { /* ignore */ }
            if (icon is not null)
            {
                try { DestroyIcon(icon.Handle); } catch { /* ignore */ }
                icon.Dispose();
            }
        };

        form.Load += async (_, _) =>
        {
            // WebView2 user data goes to %LOCALAPPDATA%\DshWeb to keep the app dir clean
            // (固定目录：避免系统临时目录被清理导致会话/插件登录态丢失)
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshWeb", "WebView2");
            await InitWebViewAsync(web, userDataFolder);
            web.CoreWebView2.Navigate(Target.Url);
        };

        Application.Run(form);
    }

    /// <summary>
    /// 统一的 WebView2 初始化：设置 + 权限 + 下载 + 弹窗 + 崩溃自愈。
    /// 主窗口与插件弹出的内部窗口共用，保证行为一致。
    /// </summary>
    private static async Task InitWebViewAsync(WebView2 web, string userDataFolder)
    {
        var env = await GetSharedEnvironmentAsync(userDataFolder);
        await web.EnsureCoreWebView2Async(env);

        var settings = web.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = true;   // 保留右键菜单（复制/粘贴等）
        settings.AreDevToolsEnabled = true;              // 保留 F12（仅实际打开时才占用内存）
        settings.IsGeneralAutofillEnabled = false;       // 关闭表单自动填充，减少后台开销
        settings.IsPasswordAutosaveEnabled = false;      // 不保存密码

        // 权限：自动放行插件/DSH 依赖的能力（见 ShellLogic.IsAutoGrantedPermission），
        // 其余保持默认拒绝。麦克风/摄像头默认拒绝（隐私），将来有语音类插件再改为弹窗询问。
        web.CoreWebView2.PermissionRequested += (_, e) =>
        {
            if (ShellLogic.IsAutoGrantedPermission(e.PermissionKind))
                e.State = CoreWebView2PermissionState.Allow;
        };

        // 下载：固定保存到系统“下载”文件夹（自动避开同名文件），完成后用默认程序打开
        web.CoreWebView2.DownloadStarting += (_, e) =>
        {
            try
            {
                var downloads = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloads);
                var name = ShellLogic.SanitizeFileName(ShellLogic.SuggestDownloadName(
                    e.DownloadOperation.ContentDisposition, e.DownloadOperation.Uri, e.DownloadOperation.MimeType));
                var path = Path.Combine(downloads, name);
                for (var i = 1; File.Exists(path); i++)
                    path = Path.Combine(downloads,
                        $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
                e.Handled = true;   // 禁用 WebView2 默认下载对话框
                e.ResultFilePath = path;
                e.DownloadOperation.StateChanged += (_, _) =>
                {
                    if (e.DownloadOperation.State == CoreWebView2DownloadState.Completed)
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo(e.DownloadOperation.ResultFilePath) { UseShellExecute = true });
                        }
                        catch { /* 无默认程序打开时忽略 */ }
                    }
                };
            }
            catch { /* 处理失败时回退 WebView2 默认下载行为 */ }
        };

        // 弹窗策略（分类逻辑见 ShellLogic.ClassifyPopup）：
        // - 外部 http(s) 链接 → 系统默认浏览器
        // - 同源 http(s) 弹窗 → 新建轻量壳窗口（保留会话，避免主窗口被导航走）
        // - blob: / data: / about: 等 → WebView2 默认行为（插件生成的预览等）
        web.CoreWebView2.NewWindowRequested += async (_, e) =>
        {
            switch (ShellLogic.ClassifyPopup(e.Uri))
            {
                case ShellLogic.PopupTarget.External:
                    e.Handled = true;
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                    return;
                case ShellLogic.PopupTarget.Internal:
                {
                    var deferral = e.GetDeferral();
                    try
                    {
                        var popup = CreatePopupForm();
                        await InitWebViewAsync(popup.Web, userDataFolder);
                        popup.Web.CoreWebView2.DocumentTitleChanged += (_, _) =>
                        {
                            var title = popup.Web.CoreWebView2.DocumentTitle;
                            if (!string.IsNullOrWhiteSpace(title)) popup.Form.Text = title;
                        };
                        e.NewWindow = popup.Web.CoreWebView2;
                        popup.Form.Show();
                    }
                    finally { deferral.Complete(); }
                    return;
                }
                default:
                    return;
            }
        };

        // 渲染进程崩溃/无响应：自动重载避免白屏（每 10 秒最多一次，防止崩溃死循环）
        web.CoreWebView2.ProcessFailed += (_, e) =>
        {
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
            {
                var now = Environment.TickCount64;
                if (now - _lastReloadTick > 10_000)
                {
                    _lastReloadTick = now;
                    try { web.CoreWebView2.Reload(); } catch { }
                }
            }
        };
    }

    /// 插件内部弹窗用的轻量窗口（与主窗口共享 WebView2 用户数据，保持登录态/会话）。
    private static (Form Form, WebView2 Web) CreatePopupForm()
    {
        var popupWeb = new WebView2 { Dock = DockStyle.Fill };
        var form = new Form
        {
            Text = "DeepSeek Harness",
            ClientSize = new Size(900, 640),
            StartPosition = FormStartPosition.CenterParent,
            Icon = SystemIcons.Application
        };
        form.Controls.Add(popupWeb);
        form.FormClosing += (_, _) =>
        {
            try { popupWeb.Dispose(); } catch { /* ignore */ }
        };
        return (form, popupWeb);
    }

    private static Icon? LoadEmbeddedIcon()
    {
        try
        {
            var name = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("favicon.png", StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null) return null;
            using var bmp = new Bitmap(stream);
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return null;
        }
    }

    private static bool PortOpen(int port)
    {
        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", port);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
