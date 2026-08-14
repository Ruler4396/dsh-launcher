using System.Diagnostics;
using System.Drawing;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace DshWeb;

internal static class Program
{
    private const string DefaultUrl = "http://127.0.0.1:3080";
    private const int SW_RESTORE = 9;

    /// 目标服务地址/端口：默认 3080。优先级：DSH_WEB_URL（视为外部托管，壳不拉起服务）→
    /// DSH_WEB_PORT（壳按此端口托管拉起服务，3080 被占用时可用）→ 默认 3080。
    private static readonly (string Url, int Port) Target = ResolveTarget();

    /// <summary>解析目标地址/端口；空值/非法值回退默认 3080。</summary>
    private static (string Url, int Port) ResolveTarget()
    {
        var envUrl = Environment.GetEnvironmentVariable("DSH_WEB_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            try
            {
                var uri = new Uri(envUrl, UriKind.Absolute);
                if (uri.Scheme is "http" or "https")
                    return (uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), uri.Port);
            }
            catch
            {
                // 非法输入回退默认
            }
        }
        var envPort = Environment.GetEnvironmentVariable("DSH_WEB_PORT");
        if (int.TryParse(envPort, out var port) && port is > 0 and < 65536)
            return ($"http://127.0.0.1:{port}", port);
        return ("http://127.0.0.1:3080", 3080);
    }

    /// 设置 DSH_WEB_URL 时视为“外部托管服务”，壳不再自动拉起 dsh（DSH_WEB_PORT 则相反：壳托管拉起）。
    private static readonly bool ServerManagedExternally =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSH_WEB_URL"));

    /// 渲染进程崩溃自动重载的节流时间戳（避免崩溃死循环）。
    private static long _lastReloadTick;

    /// 主窗口的 WebView2 控件（托盘恢复窗口时需要检查/恢复渲染）。
    private static WebView2? _mainWeb;

    /// WebView2 渲染/GPU 进程崩溃标志：窗口隐藏期间进程崩溃后，恢复窗口时必须重载页面
    /// 兜底，否则显示出来是白屏（隐藏状态下 Reload 无效，只能在窗口可见后执行）。
    private static bool _webviewRecoveryNeeded;

    /// 上次隐藏窗口的时间戳：长隐藏（>5 分钟）期间渲染进程可能被系统回收（无崩溃事件），
    /// 恢复窗口时强制重载页面兜底，避免白屏。
    private static DateTime _hiddenSince = DateTime.MinValue;

    /// 本次会话是否由壳拉起了 dsh 服务（决定"跟随窗口/托盘退出"时是否停它；外部托管/用户手动起的服务不动）。
    private static bool _serviceStartedByShell;

    /// 托盘图标（仅"托盘驻留"模式创建并保持引用，避免被 GC）。
    private static NotifyIcon? _trayIcon;

    /// 托盘"退出"请求（允许 FormClosing 真正关闭，而不是再次隐藏到托盘）。
    private static bool _trayExitRequested;

    private static readonly object TraceLock = new();

    /// <summary>
    /// dsh 主目录（与 dsh 生态一致，向其他插件学习：配置不散落在 %LOCALAPPDATA%，
    /// 跟着 dsh 走，卸载/迁移时一并处理）：DSH_HOME 环境变量，未设置时 ~/.dsh。
    /// </summary>
    private static string DshHomeDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }
    }

    /// <summary>壳的数据目录（settings.json / shell.log / service-pid 等）：DSH_HOME\dsh-launcher。</summary>
    private static string DataDir => Path.Combine(DshHomeDir, "dsh-launcher");

    /// <summary>
    /// 启动时迁移旧版数据（%LOCALAPPDATA%\dsh-launcher → DSH_HOME\dsh-launcher）：
    /// settings.json 保留用户的选择；旧文件迁移后删除，避免卸载后残留。
    /// 旧版曾把 WebView2 用户数据放 %LOCALAPPDATA%\DshWeb（标准位置，保持不动）。
    /// </summary>
    private static void MigrateLegacyData()
    {
        try
        {
            var legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-launcher");
            var newDir = DataDir;
            if (!Directory.Exists(legacyDir) || string.Equals(legacyDir, newDir, StringComparison.OrdinalIgnoreCase))
                return;
            Directory.CreateDirectory(newDir);

            var legacySettings = Path.Combine(legacyDir, "settings.json");
            var newSettings = Path.Combine(newDir, "settings.json");
            if (File.Exists(legacySettings) && !File.Exists(newSettings))
            {
                try { File.Copy(legacySettings, newSettings); } catch { /* 复制失败保留旧文件 */ }
            }

            // 清理旧目录（shell.log / service-pid 等历史文件一并删除，无残留）
            foreach (var file in Directory.GetFiles(legacyDir))
            {
                try { File.Delete(file); } catch { /* 被占用则跳过 */ }
            }
            try { if (Directory.GetFiles(legacyDir).Length == 0) Directory.Delete(legacyDir); } catch { }
        }
        catch
        {
            // 迁移失败不影响启动
        }
    }

    /// <summary>
    /// 启动轨迹日志（DSH_HOME\dsh-launcher\shell.log）：记录壳的关键决策点
    /// （单实例、端口探测、服务拉起、就绪判定、窗口显示），用于排查"窗口没出来/要多点一次"
    /// 等启动问题。写失败静默忽略，不影响启动。
    /// </summary>
    private static void Trace(string message)
    {
        try
        {
            lock (TraceLock)
            {
                Directory.CreateDirectory(DataDir);
                File.AppendAllText(Path.Combine(DataDir, "shell.log"),
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} pid={Environment.ProcessId} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 轨迹日志失败不影响启动
        }
    }

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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>设置进程 DPI 感知上下文（Per-Monitor V2）。</summary>
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    private static void Main()
    {
        // 进程级 Per-Monitor V2 DPI 感知：必须在任何窗口/控件创建之前调用，
        // 否则 150% 等缩放下 Windows 对 WebView2 内容做位图拉伸（字体/图标模糊，issue #2）。
        // 用 user32 直接调用（WinForms 的 Application.SetHighDpiMode 在部分环境下
        // 可能因先前的 MessageBox 等窗口创建而失效）。
        SetProcessDpiAwarenessContext((IntPtr)(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

        // WinForms 全局初始化必须在任何窗口/控件创建之前完成：冷启动流程会先创建
        // 启动状态窗（IWin32Window），若此时才调用 SetCompatibleTextRenderingDefault
        // 会抛 InvalidOperationException 导致进程静默崩溃——主窗口不出现，用户只能
        // 二次点击（服务已在跑、跳过状态流后才轮到正常调用）才开窗。这是"要二次点击"
        // 的根因，必须放在 Main 最前面。
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Trace($"start target={Target.Url} external={ServerManagedExternally}");
        MigrateLegacyData(); // 旧版 %LOCALAPPDATA% 数据迁移到 DSH_HOME（settings.json 保留、旧目录清理）

        // 单实例：重复启动只把已开窗口带到前台，避免多开 WebView2 进程白白占用内存。
        // 锁按目标端口隔离，不同服务可各开一个壳窗口。
        using var mutex = new Mutex(true, $@"Local\DshWeb.SingleInstance.{Target.Port}", out var firstInstance);
        if (!firstInstance)
        {
            // 首次实例可能仍在启动（状态窗/服务拉起中，主窗口还没创建）。
            // 等待其主窗口出现再聚焦，避免"点了没反应"（用户以为要再点一次）。
            var existing = FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, "DeepSeek Harness");
            for (var i = 0; existing == IntPtr.Zero && i < 40; i++)
            {
                Thread.Sleep(500);
                existing = FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, "DeepSeek Harness");
            }
            if (existing != IntPtr.Zero)
            {
                Trace($"second instance: found main window 0x{existing.ToInt64():X}, restore+foreground");
                ShowWindow(existing, SW_RESTORE);
                SetForegroundWindow(existing);
            }
            else
            {
                Trace("second instance: main window not found within 20s");
            }
            return;
        }
        Trace("first instance");

        // 升级场景：检测并提示清理旧版本（per-user 0.1.0-0.1.5 等）。
        // MSI 的跨作用域 MajorUpgrade 在标准机器上找不到 HKCU 里的 per-user 旧版，
        // 这里由壳提示用户提权卸载（提权卸载不触发 Config.Msi 1926）。
        TryPromptOldVersionCleanup();

        // 自愈孤儿快捷方式：per-user 旧版被（提权）卸载后，其用户级快捷方式可能残留
        //（指向已删除的 exe），这里每次启动扫描并清理，避免开始菜单/桌面出现幽灵图标。
        CleanupOrphanShortcuts();

        // 服务未就绪时自动拉起/等待（仅壳托管模式；DSH_WEB_URL 视为外部托管，直接开窗）。
        // 就绪 = 端口可连 + HTTP 有响应：dsh 前端在端口监听后可能还需数十秒才提供 HTTP，
        // 若只等 TCP 就提前"成功"，主窗口会加载失败（白屏，用户以为没反应而多点一次）；
        // 若探测太早判失败，用户要二次点击才能开窗。这里统一在状态窗里等 HTTP 就绪。
        if (!ServerManagedExternally && !HttpReady())
        {
            if (!PortOpen(Target.Port))
            {
                // 依赖预检：启动服务需要 Node.js（dsh 或 npx 都由 node 运行）。
                // 缺失时立即提示，避免静默等待超时才报"服务不可用"。
                if (!ShellLogic.HasExecutableOnPath("node.exe", Environment.GetEnvironmentVariable("PATH")))
                {
                    MessageBox.Show(
                        "未检测到 Node.js，无法启动 dsh 服务。\n\n请先安装 Node.js 18 或更高版本（https://nodejs.org），然后重新打开 dsh-launcher。",
                        "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                var vbs = Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs");
                if (!File.Exists(vbs))
                {
                    MessageBox.Show($"未找到 start-dsh.vbs，无法启动 dsh 服务（{Target.Url}）。", "DeepSeek Harness",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // 端口透传给 start-dsh.vbs（进程级环境变量，wscript → cmd → dsh 依次继承）；
                // 不设时 vbs 默认 3080。DSH_HOME 等环境变量同理自动继承。
                Environment.SetEnvironmentVariable("DSH_PORT", Target.Port.ToString());
                Process.Start(new ProcessStartInfo("wscript.exe", "\"" + vbs + "\"") { UseShellExecute = true });
                _serviceStartedByShell = true;
                Trace("service start requested via start-dsh.vbs");
            }
            else
            {
                // 端口已开但 HTTP 前端尚未就绪（服务可能刚被拉起、正在初始化）：也显示
                // 状态窗等待，避免直接开窗看到白屏（用户以为没反应而多点一次）。
                Trace("port open but HTTP not ready; waiting with status window");
            }

            // 启动状态窗：等待服务就绪。首次运行 npx 需要下载 dsh 组件（可能几分钟），
            // 此期间明确提示而不是静默干等；可随时取消。
            // 轮询期间持续检查启动日志：一旦出现明确错误（下载失败/权限/无 npx 等）
            // 结束等待（有 15 秒宽限期，避免启动过程中的良性告警误判）。
            var logPath = ShellLogic.ResolveLogPath(Target.Port,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            using var status = CreateStartupStatusForm();
            var cts = new CancellationTokenSource();
            var pollTask = Task.Run(() =>
            {
                var lastLogCheck = DateTime.MinValue;
                var logErrorSeen = false;
                var logErrorSince = DateTime.MinValue;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                for (var i = 0; i < 180; i++)
                {
                    if (cts.IsCancellationRequested) return "canceled";
                    if ((DateTime.Now - lastLogCheck).TotalSeconds >= 5)
                    {
                        lastLogCheck = DateTime.Now;
                        var content = SafeReadText(logPath);
                        if (ShellLogic.LogShowsStartupError(content))
                        {
                            if (!logErrorSeen)
                            {
                                logErrorSeen = true;
                                logErrorSince = DateTime.Now;
                                // 日志出现错误标志：不立即判死——启动过程中的良性告警（如网络探测）
                                // 也会命中，误判会导致用户"要多点一次"。给 15 秒宽限期，期间
                                // HTTP 就绪仍算成功；只有持续失败才判定启动出错。
                                Trace("poll: log shows error markers, grace 15s");
                            }
                        }
                        else
                        {
                            logErrorSeen = false; // 日志恢复干净，重置记时
                        }
                    }
                    if (PortOpen(Target.Port))
                    {
                        try
                        {
                            using var resp = http.GetAsync(Target.Url).GetAwaiter().GetResult();
                            Trace("poll: ready (tcp + http)");
                            return "ready"; // TCP + HTTP 都已就绪
                        }
                        catch
                        {
                            // HTTP 尚未就绪（前端还在启动），继续等
                        }
                    }
                    if (logErrorSeen && DateTime.Now - logErrorSince >= TimeSpan.FromSeconds(15))
                    {
                        Trace("poll: log error markers persisted 15s, giving up");
                        return "logerror";
                    }
                    Thread.Sleep(1000);
                }
                Trace("poll: timeout after 180s");
                return "timeout";
            });
            _ = pollTask.ContinueWith(_ =>
            {
                try { status.Invoke(status.Close); } catch { /* 窗口已关闭 */ }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            status.ShowDialog();
            var waitResult = pollTask.GetAwaiter().GetResult();
            Trace($"status window closed, waitResult={waitResult}");

            if (waitResult != "ready")
            {
                var tail = ShellLogic.ReadLogTail(logPath, 12);
                var tailText = tail.Count == 0 ? "（日志为空或不可读）" : string.Join("\n", tail.Select(l => "  " + l));
                var body = waitResult switch
                {
                    "canceled" => "已取消启动。若服务仍在后台下载/启动，可稍后重新打开 dsh-launcher。",
                    "logerror" => "启动过程报错（可能是下载失败、权限或环境问题）。\n\n日志尾部：\n" + tailText,
                    _ => "启动超时：可能是首次下载 dsh 组件较慢（可稍后重试），也可能是网络/代理问题。\n\n日志尾部：\n" + tailText
                        + "\n\n完整日志：" + logPath,
                };
                MessageBox.Show("dsh 服务未能就绪。\n\n" + body, "DeepSeek Harness",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 就绪判定（TCP + HTTP）已在轮询内完成；此处无需再探测。
            RecordServicePid(); // 记录本次拉起的服务 PID（供下次启动接管残留服务）
        }

        // 端口已开且本次没拉起服务：接管上次崩溃/退出残留的壳托管服务
        if (!ServerManagedExternally && !_serviceStartedByShell)
            TryAdoptOrphanService();

        if (!PortOpen(Target.Port))
        {
            var logPath = ShellLogic.ResolveLogPath(Target.Port,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            MessageBox.Show($"dsh 服务不可用（{Target.Url}），请确认服务已启动并查看日志：{logPath}", "DeepSeek Harness",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var form = new Form
        {
            Text = "DeepSeek Harness",
            ClientSize = new Size(1280, 840),
            StartPosition = FormStartPosition.CenterScreen,
            MinimumSize = new Size(800, 600),
            Icon = ThemeWindowIcon ?? SystemIcons.Application
        };

        var web = new WebView2 { Dock = DockStyle.Fill };
        form.Controls.Add(web);
        _mainWeb = web;

        // 托盘图标始终显示（任何服务模式）：提供"服务模式"切换与退出的常驻入口。
        // 之前只在"托盘驻留"模式创建，导致默认"常驻"模式下用户找不到切换入口。
        EnsureTrayIcon(form);
        // 窗口图标跟随主题（深色 → 白色鲸鱼 + 深色标题栏），主题切换时实时更新。
        ApplyThemeIcon(form);
        form.HandleCreated += (_, _) => ApplyThemeIcon(form); // 句柄创建后应用标题栏配色
        RegisterThemeWatcher(form);
        form.Shown += (_, _) => Trace("main form shown");

        form.FormClosing += (_, e) =>
        {
            // 生命周期模式（由 dsh-launcher-lifetime 插件写入 settings.json，壳执行）：
            // 常驻(0) / 托盘驻留(1) / 跟随窗口(2)。
            var mode = ReadLifetimeMode();

            // 托盘驻留：拦截关闭，隐藏到托盘（服务继续）。
            // 必须先于 WebView2 销毁判断：WebView2 一旦 Dispose，从托盘唤起时
            // 控件已销毁，窗口只剩空白（历史上 WebView2 销毁在拦截之前 → 必然白屏）。
            if (!_trayExitRequested && mode == ShellLogic.ServiceLifetime.Tray)
            {
                e.Cancel = true;
                form.Hide();
                _hiddenSince = DateTime.Now;
                return;
            }

            try { web.Dispose(); } catch { /* ignore */ }
            // 图标为进程级缓存（GDI 对象随进程退出释放），此处不销毁，
            // 避免托盘驻留/主题切换时复用已销毁的句柄。

            if (mode == ShellLogic.ServiceLifetime.FollowWindow && _serviceStartedByShell)
            {
                // 跟随窗口：关窗即停服务（只停壳本次拉起的）
                StopShellService();
            }
            _trayIcon?.Dispose();
            _trayIcon = null;
        };

        form.Load += async (_, _) =>
        {
            // PerMonitorV2：ClientSize 是物理像素。按窗口初始 DPI 放大，保持
            // 150% 等缩放下窗口的逻辑大小与 100% 一致（否则窗口会显得很小）。
            var scale = (double)form.DeviceDpi / 96.0;
            if (Math.Abs(scale - 1.0) > 0.01)
                form.ClientSize = new Size((int)Math.Round(1280 * scale), (int)Math.Round(840 * scale));

            // WebView2 user data goes to %LOCALAPPDATA%\DshWeb to keep the app dir clean
            // (固定目录：避免系统临时目录被清理导致会话/插件登录态丢失)
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshWeb", "WebView2");
            try
            {
                await InitWebViewAsync(web, userDataFolder);
                web.CoreWebView2.Navigate(Target.Url);
                // 页面加载失败（如端口被其他程序占用、服务异常退出）：明确提示而非白屏静默
                var navWarned = false;
                web.CoreWebView2.NavigationCompleted += (_, e) =>
                {
                    if (!e.IsSuccess && !navWarned)
                    {
                        navWarned = true;
                        MessageBox.Show(
                            $"页面加载失败。\n\n请确认 {Target.Url} 上运行的是 dsh 服务（端口可能被其他程序占用，或服务已异常退出）。\n\n日志：%USERPROFILE%\\.dsh-web.log",
                            "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                };
            }
            catch (Exception ex)
            {
                // WebView2 Runtime 缺失等初始化失败：明确提示而不是静默无窗口
                MessageBox.Show(
                    "无法初始化 WebView2：\n" + ex.Message +
                    "\n\n请确认系统已安装 Microsoft Edge WebView2 Runtime（Windows 10/11 通常已自带）。",
                    "DeepSeek Harness", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                form.Close();
            }
        };

        Application.Run(form);
        Trace("main loop exited");
    }

    /// <summary>
    /// 升级场景：检测已安装的其他版本 dsh-launcher（per-user 旧版 0.1.0-0.1.5 等），
    /// 提示用户提权卸载。用户选择"否"时记录 HKCU 标记，之后不再打扰（直到旧版被移除）。
    /// 卸载失败（被取消/旧版仍在运行）不阻断启动，提示用户稍后到"设置 → 应用"手动卸载。
    /// </summary>
    private static void TryPromptOldVersionCleanup()
    {
        try
        {
            // 当前产品代码（安装时写入 HKLM\Software\dsh-launcher\CurrentProductCode）：永远不清理自己
            string? currentCode = null;
            try
            {
                using var selfKey = Registry.LocalMachine.OpenSubKey(@"Software\dsh-launcher");
                currentCode = selfKey?.GetValue("CurrentProductCode") as string;
            }
            catch
            {
                // 读不到按无当前产品处理（便携版等）
            }

            var olds = ShellLogic.FilterByUpgradeCode(
                ShellLogic.ReadCandidateProducts(), ReadUpgradeCodeOfProduct);
            olds = ShellLogic.PickOldInstalls(olds, currentCode);
            if (olds.Count == 0) return;

            const string skipKeyName = @"Software\dsh-launcher";
            try
            {
                using var skipKey = Registry.CurrentUser.OpenSubKey(skipKeyName);
                if (skipKey?.GetValue("SkipOldUninstall") is int skipFlag && skipFlag == 1)
                    return;
            }
            catch
            {
                // 读不到标记按未标记处理
            }

            var list = string.Join("\n", olds.Select(o => $"  • {o.ProductCode}  (v{o.Version})"));
            var answer = MessageBox.Show(
                "检测到旧版本的 dsh-launcher，建议先卸载旧版本，避免两个版本共存。\n\n" + list +
                "\n\n是否现在卸载？\n（卸载需要管理员确认；请先关闭其他 dsh-launcher 窗口）",
                "DeepSeek Harness", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                try { Registry.CurrentUser.CreateSubKey(skipKeyName)?.SetValue("SkipOldUninstall", 1); }
                catch { /* ignore */ }
                return;
            }

            var failed = 0;
            foreach (var old in olds)
            {
                try
                {
                    var psi = new ProcessStartInfo("msiexec.exe", $"/x {old.ProductCode} /qn /norestart")
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit();
                    if (p is null || p.ExitCode != 0) failed++;
                }
                catch
                {
                    failed++;
                }
            }

            MessageBox.Show(failed == 0
                ? "旧版本已全部卸载。"
                : "部分旧版本未能卸载（可能被取消，或旧版本窗口仍在运行）。\n可稍后在 设置 → 应用 中手动卸载。",
                "DeepSeek Harness", MessageBoxButtons.OK,
                failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch
        {
            // 检测/清理失败不打扰用户
        }
    }

    /// <summary>
    /// 读取产品的 UpgradeCode（经其缓存 MSI 的 Property 表）。用于精确识别"我们的产品"，
    /// 避免误清理其他恰好同名的软件。任何一步失败返回 null（该产品将被过滤掉）。
    /// </summary>
    private static string? ReadUpgradeCodeOfProduct(string productCode)
    {
        try
        {
            dynamic installer = Activator.CreateInstance(
                Type.GetTypeFromProgID("WindowsInstaller.Installer") ?? throw new InvalidOperationException());
            var localPackage = (string)installer.ProductInfo(productCode, "LocalPackage");
            if (string.IsNullOrWhiteSpace(localPackage) || !File.Exists(localPackage))
                return null;
            dynamic db = installer.OpenDatabase(localPackage, 0);
            dynamic view = db.OpenView("SELECT `Value` FROM `Property` WHERE `Property`='UpgradeCode'");
            view.Execute();
            dynamic rec = view.Fetch();
            return rec is null ? null : (string)rec.StringData(1);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 清理 per-user 旧版本（0.1.0-0.1.5）残留的用户级快捷方式。
    /// 旧版被（提权）卸载后，其用户开始菜单/桌面快捷方式可能不被删除（MSI 提权卸载
    /// 跳过 per-user 上下文组件）。只删除"目标确实是 DshWeb.exe"的快捷方式，
    /// 用户自行创建的同名 .lnk（指向其他程序）不受影响；无法读取目标时保守不删。
    /// </summary>
    private static void CleanupOrphanShortcuts()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userMenuDir = Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs\dsh-launcher");
        try
        {
            // 目录是 MSI 专用名；只有确认里面有我们的快捷方式（指向 DshWeb.exe）才整体删除
            if (Directory.Exists(userMenuDir))
            {
                var hasOurs = Directory.GetFiles(userMenuDir, "*.lnk")
                    .Any(lnk => ShellLogic.IsOurShortcutTarget(GetShortcutTarget(lnk)));
                if (hasOurs) Directory.Delete(userMenuDir, true);
            }
        }
        catch
        {
            // 忽略无法访问的目录
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var userDesktopLnk = Path.Combine(desktop, "dsh-launcher.lnk");
        try
        {
            if (File.Exists(userDesktopLnk)
                && ShellLogic.IsOurShortcutTarget(GetShortcutTarget(userDesktopLnk)))
            {
                File.Delete(userDesktopLnk);
            }
        }
        catch
        {
            // 忽略
        }

        // 清理孤儿自启：HKCU Run 的 dsh-launcher 指向的 start-dsh.vbs 已不存在
        //（per-machine 提权卸载跳过 per-user 组件时残留），避免下次登录白启一个死项。
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (runKey?.GetValue("dsh-launcher") is string runValue)
            {
                var m = Regex.Match(runValue, "\"([^\"]+start-dsh\\.vbs)\"");
                var vbsPath = m.Success ? m.Groups[1].Value : null;
                if (vbsPath is null || !File.Exists(vbsPath))
                    runKey.DeleteValue("dsh-launcher", false);
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>读取 .lnk 的目标路径；失败返回 null（保守不删）。</summary>
    private static string? GetShortcutTarget(string lnkPath)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(
                Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException());
            dynamic lnk = shell.CreateShortcut(lnkPath);
            return (string)lnk.TargetPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取文件文本（容错，失败返回 null）。</summary>
    private static string? SafeReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 服务就绪判定：端口可连 + HTTP 有响应。dsh 前端在端口监听后可能还需数十秒
    /// 才提供 HTTP，只探测 TCP 会提前"成功"（主窗口白屏）。
    /// </summary>
    private static bool HttpReady()
    {
        if (!PortOpen(Target.Port)) return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = http.GetAsync(Target.Url).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>settings.json 路径（dsh-launcher-lifetime 插件写入，壳读取）：DSH_HOME\dsh-launcher\settings.json。</summary>
    private static string SettingsPath => Path.Combine(DataDir, "settings.json");

    /// <summary>读取服务停留模式；缺失/非法回退常驻。</summary>
    private static ShellLogic.ServiceLifetime ReadLifetimeMode() =>
        ShellLogic.ParseLifetimeMode(SafeReadText(SettingsPath));

    /// <summary>壳托管服务的 PID 记录文件（按端口隔离）：崩溃/异常退出后残留的服务可被下次启动接管管理。</summary>
    private static string ServicePidFile => Path.Combine(DataDir, $"service-pid-{Target.Port}.txt");

    /// <summary>记录本次壳拉起的服务 PID（服务就绪后调用），供下次启动接管残留服务。</summary>
    private static void RecordServicePid()
    {
        try
        {
            var pid = FindPidListeningOn(Target.Port);
            if (pid > 0) File.WriteAllText(ServicePidFile, pid.ToString());
        }
        catch
        {
            // 记录失败不影响启动
        }
    }

    /// <summary>
    /// 端口已开但本实例没拉起服务时调用：若监听进程正是壳上次拉起的残留服务
    /// （PID 记录在 ServicePidFile），则接管管理（跟随窗口关窗时停掉），
    /// 避免崩溃/异常退出后服务永久残留。
    /// </summary>
    private static void TryAdoptOrphanService()
    {
        try
        {
            if (!File.Exists(ServicePidFile)) return;
            if (!int.TryParse(File.ReadAllText(ServicePidFile).Trim(), out var pid) || pid <= 0) return;
            if (FindPidListeningOn(Target.Port) == pid)
            {
                _serviceStartedByShell = true;
                Trace($"adopted orphan service pid={pid}");
            }
        }
        catch
        {
            // 接管失败不影响启动
        }
    }

    private static void ClearServicePidFile()
    {
        try { if (File.Exists(ServicePidFile)) File.Delete(ServicePidFile); } catch { }
    }

    /// <summary>
    /// 停止"壳本次会话拉起的"dsh 服务：按端口找监听进程 PID，先温和终止，未停再强制。
    /// 只应在 <see cref="_serviceStartedByShell"/> 为 true 时调用。停止成功后清除 PID 记录。
    /// </summary>
    private static void StopShellService()
    {
        try
        {
            var pid = FindPidListeningOn(Target.Port);
            if (pid <= 0)
            {
                ClearServicePidFile();
                return;
            }
            using (var p = Process.Start(new ProcessStartInfo("taskkill", "/pid " + pid)
            { UseShellExecute = false, CreateNoWindow = true }))
                p?.WaitForExit(3000);
            if (!PortOpen(Target.Port))
            {
                ClearServicePidFile(); // 已停止
                return;
            }
            using (var p = Process.Start(new ProcessStartInfo("taskkill", "/f /pid " + pid)
            { UseShellExecute = false, CreateNoWindow = true }))
                p?.WaitForExit(3000);
            if (!PortOpen(Target.Port)) ClearServicePidFile();
        }
        catch
        {
            // 停服务失败不影响退出
        }
    }

    /// <summary>按端口找出监听进程 PID（netstat 解析）；找不到返回 0。</summary>
    private static int FindPidListeningOn(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p tcp")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi);
            if (p is null) return 0;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var token = ":" + port + " ";
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                if (!line.Contains(token)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && int.TryParse(parts[^1], out var pid)) return pid;
            }
        }
        catch
        {
            // 忽略
        }
        return 0;
    }

    /// <summary>创建托盘图标（懒加载，幂等）；左键/双击切换窗口，右键菜单为显示/隐藏与退出。
    /// 服务停留模式改由 dsh-launcher-lifetime 插件在 Harness 设置页里配置（不再放托盘菜单）。</summary>
    private static void EnsureTrayIcon(Form form)
    {
        if (_trayIcon is not null) return;
        try
        {
            var tray = new NotifyIcon
            {
                // 托盘背景多为深色，固定用白色鲸鱼（深色鲸鱼看不清）
                Icon = TrayWhaleIcon ?? SystemIcons.Application,
                Text = "dsh-launcher",
                Visible = true,
            };
            tray.ContextMenuStrip = CreateTrayMenu(form);
            // 左键单击：窗口置顶显示（开着就提到最上层，不会误关窗口）；
            // 右键：只弹菜单（NotifyIcon 默认行为，不动窗口）。
            tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowMainWindow(form);
            };
            _trayIcon = tray;
        }
        catch
        {
            // 托盘创建失败不影响壳主流程
        }
    }

    /// <summary>托盘菜单字体：微软雅黑（中英文系统均自带），9pt 观感干净。</summary>
    private static readonly Font TrayMenuFont = new("Microsoft YaHei UI", 9F);

    /// <summary>用 Segoe MDL2 Assets 字形渲染 16x16 菜单图标（Windows 10+ 自带该字体）。</summary>
    private static Image? RenderMdl2Icon(char glyph, Color color)
    {
        try
        {
            using var font = new Font("Segoe MDL2 Assets", 15F, FontStyle.Regular, GraphicsUnit.Pixel);
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                using var brush = new SolidBrush(color);
                g.DrawString(glyph.ToString(), font, brush, -2F, -3F); // 字形基线微调，视觉居中
            }
            return bmp;
        }
        catch
        {
            return null; // 字体缺失等：回退无图标
        }
    }

    /// <summary>
    /// 创建托盘右键菜单：白底、浅灰 hover、深色文字、微软雅黑，带图标（眼睛=显示/隐藏、
    /// 电源=退出），内容自适应宽度。不用系统默认样式（跟随深色主题变黑、hover 用系统主题色）。
    /// </summary>
    private static ContextMenuStrip CreateTrayMenu(Form form)
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new LightMenuRenderer(),
            Font = TrayMenuFont,
            ShowImageMargin = true,   // 有图标，保留左侧图标区
            ShowCheckMargin = false,
            Padding = new Padding(4),
        };
        menu.Items.Add(new ToolStripMenuItem("显示 / 隐藏窗口",
            RenderMdl2Icon('\uE8F1', Color.FromArgb(60, 60, 60)), // MDL2: RedEye
            (_, _) => ToggleMainWindow(form))
        {
            Padding = new Padding(8, 6, 12, 6),
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出",
            RenderMdl2Icon('\uE7E8', Color.FromArgb(60, 60, 60)), // MDL2: PowerButton
            (_, _) =>
            {
                _trayExitRequested = true;
                // 常驻模式：只退出壳（服务保留）；托盘驻留/跟随窗口：停掉壳拉起的服务
                if (ReadLifetimeMode() != ShellLogic.ServiceLifetime.AlwaysOn && _serviceStartedByShell)
                    StopShellService();
                Application.Exit();
            })
        {
            Padding = new Padding(8, 6, 12, 6),
        });
        return menu;
    }

    /// <summary>
    /// 托盘菜单浅色渲染器：白底 + 1px 浅灰边框 + 浅灰 hover + 深色文字，
    /// 不随系统深浅色主题变化，观感干净现代。
    /// </summary>
    private sealed class LightMenuRenderer : ToolStripProfessionalRenderer
    {
        private static readonly Color MenuBack = Color.White;
        private static readonly Color MenuBorder = Color.FromArgb(222, 222, 222);
        private static readonly Color MenuHover = Color.FromArgb(243, 246, 249);
        private static readonly Color MenuText = Color.FromArgb(30, 30, 30);
        private static readonly Color MenuSeparator = Color.FromArgb(230, 230, 230);

        public LightMenuRenderer()
        {
            RoundedEdges = false; // 圆角交给系统阴影/直角，避免自绘与系统圆角叠加
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var b = new SolidBrush(MenuBack);
            e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var r = e.AffectedBounds;
            using var p = new Pen(MenuBorder);
            e.Graphics.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || e.Item is ToolStripSeparator) return;
            var r = new Rectangle(Point.Empty, e.Item.Size);
            using var b = new SolidBrush(MenuHover);
            e.Graphics.FillRectangle(b, r);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = MenuText;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var y = e.Item.Height / 2;
            using var p = new Pen(MenuSeparator);
            e.Graphics.DrawLine(p, 12, y, e.Item.Width - 12, y);
        }
    }

    /// <summary>
    /// 显示并置顶主窗口（托盘左键单击 / 菜单唤起）：开着就提到最上层并聚焦，
    /// 隐藏着就显示出来；含 WebView2 崩溃/长隐藏恢复。
    /// </summary>
    private static void ShowMainWindow(Form form)
    {
        if (!form.Visible) form.Show();
        form.Activate();
        // WebView2 在窗口隐藏期间可能出问题导致恢复后白屏：
        // - 渲染/GPU 进程崩溃（ProcessFailed 已置标志）→ 延迟重载页面恢复
        // - 隐藏超过 5 分钟，渲染进程可能被系统回收（无崩溃事件）→ 强制重载兜底
        // 隐藏状态下 Reload 无效；且刚显示时立即 Reload 与 WebView2 的可见性处理
        // 存在竞态（实测隐藏→恢复→立即 Reload 后进程崩溃），必须延迟执行。
        var longHidden = _hiddenSince != DateTime.MinValue
            && DateTime.Now - _hiddenSince >= TimeSpan.FromMinutes(5);
        if (_webviewRecoveryNeeded || longHidden)
        {
            _webviewRecoveryNeeded = false;
            _hiddenSince = DateTime.MinValue;
            TryReloadWebViewDeferred(form);
        }
    }

    /// <summary>切换主窗口显示/隐藏（托盘菜单项用）。</summary>
    private static void ToggleMainWindow(Form form)
    {
        if (form.Visible)
        {
            form.Hide();
            _hiddenSince = DateTime.Now;
        }
        else
        {
            ShowMainWindow(form);
        }
    }

    /// <summary>
    /// 延迟重载主窗口 WebView2 页面（隐藏/显示后的崩溃恢复）。延迟 500ms 等窗口
    /// 可见性处理完成；期间窗口若再次隐藏/关闭则放弃本次重载并留待下次恢复（标志复位）。
    /// </summary>
    private static async void TryReloadWebViewDeferred(Form form)
    {
        try
        {
            await Task.Delay(500);
            if (form.IsDisposed || !form.Visible || _mainWeb is { IsDisposed: true })
            {
                // 窗口又隐藏/关闭了：下次恢复窗口时再处理
                _webviewRecoveryNeeded = true;
                return;
            }
            if (_mainWeb?.CoreWebView2 is not null)
            {
                Trace("tray restore: reloading webview after process failure (deferred)");
                _mainWeb.CoreWebView2.Reload();
            }
        }
        catch
        {
            // 重载失败静默（页面可能已自行恢复）
        }
    }

    /// <summary>
    /// 服务启动状态窗：显示"正在启动 dsh 服务"（含首次下载提示），可取消。
    /// 由外部轮询端口，就绪后调用 Close() 自动关闭；取消按钮设 DialogResult.Cancel 并关闭。
    /// </summary>
    private static Form CreateStartupStatusForm()
    {
        var f = new Form
        {
            // 标题不用主窗口的"DeepSeek Harness"：单实例逻辑按标题找主窗口，
            // 避免第二次点击把状态窗误当成主窗口聚焦（表现为"点了两次没反应"）。
            Text = "dsh-launcher 启动中",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(440, 150),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ControlBox = false,
        };
        var label = new Label
        {
            Text = "正在启动 dsh 服务…\n首次运行需要下载 dsh 组件，可能需要几分钟。\n完成后会自动打开窗口，请稍候。",
            Location = new Point(20, 18),
            AutoSize = true,
        };
        var bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Location = new Point(20, 78),
            Size = new Size(400, 16),
        };
        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(350, 110),
            Size = new Size(70, 26),
        };
        cancel.Click += (_, _) =>
        {
            f.DialogResult = DialogResult.Cancel;
            f.Close();
        };
        f.Controls.Add(label);
        f.Controls.Add(bar);
        f.Controls.Add(cancel);
        return f;
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

        // 渲染进程/GPU 进程崩溃或无响应：记下崩溃痕迹，窗口可见时自动重载避免白屏
        //（每 10 秒最多一次，防止崩溃死循环）。窗口隐藏期间的崩溃不立即 Reload——
        // 隐藏状态下 Reload 无效，等托盘恢复窗口时由 ToggleMainWindow 兜底重载。
        web.CoreWebView2.ProcessFailed += (_, e) =>
        {
            Trace($"webview process failed: {e.ProcessFailedKind}");
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.GpuProcessExited)
            {
                _webviewRecoveryNeeded = true;
                if (_mainWeb is { Visible: true, IsDisposed: false })
                {
                    var now = Environment.TickCount64;
                    if (now - _lastReloadTick > 10_000)
                    {
                        _lastReloadTick = now;
                        try { web.CoreWebView2.Reload(); } catch { }
                    }
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

    /// <summary>从嵌入资源按资源名后缀加载图标（favicon.png 深色鲸鱼 / favicon-white.png 白色鲸鱼）。</summary>
    private static Icon? LoadIconResource(string resourceSuffix)
    {
        try
        {
            var name = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>深色鲸鱼图标（窗口浅色主题/任务栏浅色时用）。</summary>
    private static Icon? _darkWhaleIcon;

    /// <summary>白色鲸鱼图标（窗口深色主题/托盘深色背景时用）。</summary>
    private static Icon? _lightWhaleIcon;

    /// <summary>检测系统应用深色模式（注册表 AppsUseLightTheme=0）。</summary>
    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取 dsh 前端的主题选择（DSH_HOME/settings.yaml 的 ui-theme.preference）。</summary>
    private static string? ReadDshThemePreference()
    {
        try
        {
            var yaml = Path.Combine(DshHomeDir, "settings.yaml");
            if (!File.Exists(yaml)) return null;
            foreach (var line in File.ReadAllLines(yaml))
            {
                var t = line.Trim();
                if (t.StartsWith("ui-theme:", StringComparison.Ordinal)) continue;
                if (t.StartsWith("preference:", StringComparison.Ordinal))
                    return t["preference:".Length..].Trim().Trim('"', '\'').ToLowerInvariant();
            }
        }
        catch
        {
            // 读取失败回退系统主题
        }
        return null;
    }

    /// <summary>
    /// 解析壳的主题：以用户的选择为主——dsh 前端设置页里的主题选择
    /// （ui-theme.preference: dark / light / system）优先；system 或未设置时跟随系统深色模式。
    /// </summary>
    private static bool ResolveDarkMode()
    {
        var pref = ReadDshThemePreference();
        if (pref == "dark") return true;
        if (pref == "light") return false;
        return IsSystemDarkMode();
    }

    /// <summary>按当前主题选择窗口图标（深色 → 白色鲸鱼，浅色 → 深色鲸鱼）。</summary>
    private static Icon? ThemeWindowIcon =>
        ResolveDarkMode()
            ? (_darkWhaleIcon ??= LoadIconResource("favicon.png"))
            : (_lightWhaleIcon ??= LoadIconResource("favicon-white.png"));

    /// <summary>白色鲸鱼（托盘/任务栏深色背景固定用，深色鲸鱼看不清）。</summary>
    private static Icon? TrayWhaleIcon => _lightWhaleIcon ??= LoadIconResource("favicon-white.png");

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    /// <summary>
    /// 强制标题栏深色/浅色（Win10 1809+ 的沉浸式深色标题栏）：让标题栏与图标/前端主题
    /// 保持一致——之前只换图标、标题栏仍是浅色时，白色鲸鱼在浅色标题栏上看不见。
    /// </summary>
    private static void SetTitleBarDark(Form form, bool dark)
    {
        try
        {
            if (form.Handle == IntPtr.Zero) return;
            var value = dark ? 1 : 0;
            if (DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int)) != 0)
                DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int)); // Win10 1809 用 19
        }
        catch
        {
            // 标题栏配色失败不影响功能
        }
    }

    /// <summary>
    /// 应用主题：窗口图标 + 标题栏配色（深色 → 白色鲸鱼 + 深色标题栏；浅色 → 深色鲸鱼 + 浅色标题栏），
    /// 托盘图标固定白色。以用户的选择为主（dsh 前端主题设置），其次跟随系统。
    /// </summary>
    private static void ApplyThemeIcon(Form form)
    {
        var dark = ResolveDarkMode();
        try { form.Icon = (dark ? _darkWhaleIcon : _lightWhaleIcon) ?? ThemeWindowIcon ?? SystemIcons.Application; } catch { }
        SetTitleBarDark(form, dark);
        if (_trayIcon is not null)
        {
            try { _trayIcon.Icon = TrayWhaleIcon ?? SystemIcons.Application; } catch { /* ignore */ }
        }
    }

    /// <summary>
    /// 监听主题变化：系统主题切换（SystemEvents）+ dsh 前端主题设置变化
    /// （DSH_HOME/settings.yaml 的 ui-theme.preference，用户在 dsh 设置页切换主题时写入）。
    /// </summary>
    private static void RegisterThemeWatcher(Form form)
    {
        try
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                    ApplyThemeIcon(form);
            };
        }
        catch
        {
            // 系统主题监听失败不影响启动
        }

        try
        {
            var dir = DshHomeDir;
            if (!Directory.Exists(dir)) return;
            var watcher = new FileSystemWatcher(dir, "settings.yaml")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            var lastApply = DateTime.MinValue;
            watcher.Changed += (_, _) =>
            {
                // 防抖：settings.yaml 可能被连续写多次
                if (DateTime.Now - lastApply < TimeSpan.FromSeconds(2)) return;
                lastApply = DateTime.Now;
                try { form.BeginInvoke(() => ApplyThemeIcon(form)); } catch { }
            };
        }
        catch
        {
            // 前端主题监听失败不影响启动（图标按启动时定格）
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
