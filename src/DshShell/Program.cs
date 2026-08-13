using System.Diagnostics;
using System.Drawing;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshWeb;

internal static class Program
{
    private const string Url = "http://127.0.0.1:3080";
    private const int Port = 3080;
    private const int SW_RESTORE = 9;

    /// 渲染进程崩溃自动重载的节流时间戳（避免崩溃死循环）。
    private static long _lastReloadTick;

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
        // 单实例：重复启动只把已开窗口带到前台，避免多开 WebView2 进程白白占用内存
        using var mutex = new Mutex(true, @"Local\DshWeb.SingleInstance", out var firstInstance);
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

        // 服务未启动时自动拉起（调用同目录下的 start-dsh.vbs 静默启动）
        if (!PortOpen())
        {
            var vbs = Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs");
            if (File.Exists(vbs))
            {
                Process.Start(new ProcessStartInfo("wscript.exe", "\"" + vbs + "\"") { UseShellExecute = true });
                for (var i = 0; i < 90 && !PortOpen(); i++)
                    Thread.Sleep(1000);
            }
            else
            {
                MessageBox.Show("未找到 start-dsh.vbs，无法启动 dsh 服务。", "DeepSeek Harness",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
        }

        if (!PortOpen())
        {
            MessageBox.Show("dsh 服务启动超时，请查看日志：%USERPROFILE%\\.dsh-web.log", "DeepSeek Harness",
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
            web.CoreWebView2.Navigate(Url);
        };

        Application.Run(form);
    }

    /// <summary>
    /// 统一的 WebView2 初始化：设置 + 权限 + 下载 + 弹窗 + 崩溃自愈。
    /// 主窗口与插件弹出的内部窗口共用，保证行为一致。
    /// </summary>
    private static async Task InitWebViewAsync(WebView2 web, string userDataFolder)
    {
        web.CreationProperties = new CoreWebView2CreationProperties { UserDataFolder = userDataFolder };
        await web.EnsureCoreWebView2Async();

        var settings = web.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = true;   // 保留右键菜单（复制/粘贴等）
        settings.AreDevToolsEnabled = true;              // 保留 F12（仅实际打开时才占用内存）
        settings.IsGeneralAutofillEnabled = false;       // 关闭表单自动填充，减少后台开销
        settings.IsPasswordAutosaveEnabled = false;      // 不保存密码

        // 权限：自动放行插件/DSH 依赖的能力，其余保持默认拒绝。
        // - Notifications：桌面通知插件（dsh-notification 等）
        // - ClipboardRead：复制/粘贴（此 SDK 版本剪贴板读写的唯一权限项）
        // - Autoplay：声音类插件（打字音效、自定义提示音）无手势时也能播放
        // - MultipleAutomaticDownloads：多文件导出插件不被拦截
        // - PersistentStorage：插件 IndexedDB/localStorage 免于被驱逐
        // 麦克风/摄像头保持默认拒绝（隐私），将来有语音类插件再改为弹窗询问。
        web.CoreWebView2.PermissionRequested += (_, e) =>
        {
            if (e.PermissionKind is CoreWebView2PermissionKind.Notifications
                or CoreWebView2PermissionKind.ClipboardRead
                or CoreWebView2PermissionKind.Autoplay
                or CoreWebView2PermissionKind.MultipleAutomaticDownloads
                or CoreWebView2PermissionKind.PersistentStorage)
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
                var name = SanitizeFileName(SuggestDownloadName(
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

        // 弹窗策略：
        // - http(s) 外部链接 → 系统默认浏览器
        // - http(s) 同源（127.0.0.1/localhost）→ 新建轻量壳窗口（保留会话，避免主窗口被导航走）
        // - blob: / data: / about: 等 → WebView2 默认行为（插件生成的预览等）
        web.CoreWebView2.NewWindowRequested += async (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
                return;

            if (uri.Host is not ("127.0.0.1" or "localhost"))
            {
                e.Handled = true;
                try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                return;
            }

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

    /// <summary>
    /// 从 Content-Disposition / 下载 URI / MIME 推导建议文件名。
    /// （当前 SDK 版本没有 SuggestedFileName API，只能自行推导。）
    /// </summary>
    private static string SuggestDownloadName(string? disposition, string? downloadUri, string? mimeType)
    {
        string? name = null;
        if (!string.IsNullOrWhiteSpace(disposition))
        {
            var m = Regex.Match(disposition, @"filename\*?=(?:UTF-8'')?[""']?(?<name>[^""';]+)");
            if (m.Success && !string.IsNullOrWhiteSpace(m.Groups["name"].Value))
                name = m.Groups["name"].Value.Trim();
        }
        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(downloadUri)
            && Uri.TryCreate(downloadUri, UriKind.Absolute, out var uri))
        {
            var segment = Path.GetFileName(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(segment))
                name = segment;
        }
        name = string.IsNullOrWhiteSpace(name)
            ? $"dsh-{DateTime.Now:yyyyMMddHHmmss}"
            : Uri.UnescapeDataString(name);

        // blob: 等无扩展名下载：按 MIME 类型补一个扩展名，便于识别
        if (!Path.HasExtension(name) && !string.IsNullOrWhiteSpace(mimeType))
        {
            var ext = MimeToExtension(mimeType);
            if (ext is not null) name += ext;
        }
        return name;
    }

    private static string? MimeToExtension(string mime)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["text/plain"] = ".txt",
            ["text/markdown"] = ".md",
            ["text/html"] = ".html",
            ["text/csv"] = ".csv",
            ["application/json"] = ".json",
            ["application/pdf"] = ".pdf",
            ["application/zip"] = ".zip",
            ["application/x-zip-compressed"] = ".zip",
            ["application/gzip"] = ".gz",
            ["application/x-tar"] = ".tar",
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/gif"] = ".gif",
            ["image/webp"] = ".webp",
            ["image/svg+xml"] = ".svg",
            ["audio/mpeg"] = ".mp3",
            ["audio/wav"] = ".wav",
            ["video/mp4"] = ".mp4",
        };
        return map.TryGetValue(mime.Split(';')[0].Trim(), out var ext) ? ext : null;
    }

    /// 清理文件名中的非法字符，避免拼接路径时报错。
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var result = sb.ToString().Trim();
        return result.Length == 0 ? $"dsh-{DateTime.Now:yyyyMMddHHmmss}" : result;
    }

    private static bool PortOpen()
    {
        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", Port);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
