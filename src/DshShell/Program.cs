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

    /// 本次会话壳托管服务的监听 PID（内存缓存，关窗时直接使用，避免再跑 netstat 造成卡顿）。
    private static int _servicePid;

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

        var form = new DshShellForm
        {
            Text = "DeepSeek Harness",
            ClientSize = new Size(1280, 840),
            StartPosition = FormStartPosition.CenterScreen,
            MinimumSize = new Size(800, 600),
            // 无边框 + 自绘标题栏：主题切换即时生效，不依赖 DWM 标题栏重绘
            // （实测本机 DWM 属性切换后标题栏画面不刷新，只有焦点变化才重绘）
            FormBorderStyle = FormBorderStyle.None,
            Icon = TrayWhaleIcon ?? SystemIcons.Application // 系统任务栏图标固定白色鲸鱼
        };
        var titleHeight = (int)Math.Round(32 * form.DeviceDpi / 96f);
        form.TitleBar = new CustomTitleBar(form, ResolveDarkMode())
        {
            // 手动布局 + Anchor（不依赖 Dock 布局顺序：Dock 下 WebView2 内容会盖住标题栏区域，
            // 表现为"正常内容被标题栏挡住一部分"）。四周留 1px 作为窗口边框（Form.BackColor=边框色）
            Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2, titleHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        form.Controls.Add(form.TitleBar);

        var web = new WebView2
        {
            Bounds = new Rectangle(1, 1 + titleHeight, form.ClientSize.Width - 2, form.ClientSize.Height - titleHeight - 2),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        form.Controls.Add(web);
        _mainWeb = web;

        // 无边框窗口阴影（DWM NCRENDERING_POLICY；带 WebView2 时系统阴影实际不呈现，边框替代质感）
        form.HandleCreated += (_, _) => ApplyWindowShadow(form.Handle);

        // DPI 变化（跨缩放显示器移动窗口）：重算标题栏尺寸并重新布局内容区
        form.DpiChanged += (_, _) =>
        {
            var scale = form.DeviceDpi / 96f;
            var h = (int)Math.Round(32 * scale);
            form.TitleBar.Rescale(scale);
            form.TitleBar.Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2, h);
            web.Bounds = new Rectangle(1, 1 + h, form.ClientSize.Width - 2, form.ClientSize.Height - h - 2);
        };

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

            // 真正退出路径不显式 Dispose WebView2：Dispose 会等待浏览器进程关闭，
            // 造成关窗卡顿 1-2 秒；进程退出后 WebView2 子进程会自动检测父进程退出并清理。
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

        ScheduleUpdateCheck(form); // 启动后异步检查更新（dsh 新版 / launcher 安全更新才推送）

        Application.Run(form);
        Trace("main loop exited");
    }

    private enum PendingUpdate { None, Dsh, LauncherSecurity }
    private static PendingUpdate _pendingUpdate;
    private static string _pendingLatest = "", _pendingLocal = "";
    private static Form? _pendingForm;

    /// <summary>
    /// 启动后异步检查更新（仅启动时一次，避免频繁请求 GitHub/npm）：
    /// - dsh-launcher 自身：**普通更新不推送**，只有标记为**安全/重要更新**（Release
    ///   body 含 "SECURITY" 或 tag 含 "-sec"）才托盘气泡提示（点击打开 Releases 下载页）
    /// - dsh（@deepseek-ai/dsh）：有新版即提示（点击一键 npm 更新）
    /// 网络失败/无更新静默，不打扰用户；匿名限流影响可控。
    /// </summary>
    private static void ScheduleUpdateCheck(Form form)
    {
        if (_trayIcon is null) return;
        _pendingForm = form;
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-launcher");

                // 1) launcher 安全更新优先（安全修复比功能更新重要）
                var lr = await UpdateChecker.FetchLatestLauncherReleaseAsync(http);
                if (lr is not null && lr.IsSecurity
                    && UpdateChecker.CompareVersions(lr.Version, UpdateChecker.CurrentLauncherVersion) > 0)
                {
                    form.BeginInvoke(() => NotifyPending(PendingUpdate.LauncherSecurity, lr.Version,
                        UpdateChecker.CurrentLauncherVersion ?? "?"));
                    return;
                }

                // 2) dsh 新版
                var latest = await UpdateChecker.FetchLatestDshVersionAsync(http);
                var local = UpdateChecker.ResolveLocalDshVersion();
                if (!string.IsNullOrWhiteSpace(latest) && !string.IsNullOrWhiteSpace(local)
                    && UpdateChecker.CompareVersions(latest, local) > 0)
                {
                    form.BeginInvoke(() => NotifyPending(PendingUpdate.Dsh, latest, local));
                }
            }
            catch { /* 检测失败静默 */ }
        });
    }

    private static void NotifyPending(PendingUpdate type, string latest, string local)
    {
        try
        {
            _pendingUpdate = type;
            _pendingLatest = latest;
            _pendingLocal = local;
            _trayIcon.BalloonTipClicked -= OnPendingBalloonClicked;
            _trayIcon.BalloonTipClicked += OnPendingBalloonClicked;
            var (title, body) = type == PendingUpdate.LauncherSecurity
                ? ("dsh-launcher 安全更新", $"检测到重要安全更新 {latest}（当前 {local}）。点击查看下载。")
                : ("dsh 有新版本", $"检测到 dsh {latest}（当前 {local}）。点击此处更新。");
            _trayIcon.ShowBalloonTip(10000, title, body, ToolTipIcon.Info);
        }
        catch { /* 气泡提示失败忽略 */ }
    }

    private static void OnPendingBalloonClicked(object? s, EventArgs e)
    {
        var f = _pendingForm;
        if (_pendingUpdate == PendingUpdate.Dsh && f is not null)
        {
            PromptDshUpdate(f, _pendingLatest, _pendingLocal);
        }
        else if (_pendingUpdate == PendingUpdate.LauncherSecurity)
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/Ruler4396/dsh-launcher/releases/latest") { UseShellExecute = true }); }
            catch { /* 打开失败忽略 */ }
        }
        _pendingUpdate = PendingUpdate.None;
    }

    /// <summary>点击气泡后：确认 → npm 全局更新 dsh → 完成提示（异步执行 npm，不卡壳）。</summary>
    private static void PromptDshUpdate(Form form, string latest, string local)
    {
        var r = MessageBox.Show(
            $"检测到 dsh 新版本 {latest}（当前 {local}）。\n\n是否现在更新？\n" +
            "（执行 npm install -g @deepseek-ai/dsh@latest；更新完成后重启 dsh-launcher 生效）",
            "dsh 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        try
        {
            var psi = new ProcessStartInfo("npm.cmd", "install -g @deepseek-ai/dsh@latest")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var p = Process.Start(psi);
            if (p is null) return;
            _ = Task.Run(() =>
            {
                try
                {
                    p.WaitForExit(120000);
                    var ok = p.ExitCode == 0;
                    var msg = ok
                        ? $"dsh 已更新到 {latest}。\n\n请重启 dsh-launcher 使新版本生效。"
                        : "dsh 更新失败（npm 报错）。\n\n可稍后在命令行手动执行：\nnpm install -g @deepseek-ai/dsh@latest";
                    form.BeginInvoke(() => MessageBox.Show(msg, "dsh 更新",
                        MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning));
                }
                catch { /* 完成提示失败忽略 */ }
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"无法启动 npm 更新：{ex.Message}", "dsh 更新",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
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

    /// <summary>
    /// 读取服务停留模式；缺失/非法回退跟随窗口。兼容旧版路径（%LOCALAPPDATA%，
    /// 迁移前旧插件写入的位置）：新位置读不到时回退旧位置，读到后迁移并清理，
    /// 避免"用户选了常驻/托盘驻留，壳却按默认跟随窗口执行"的路径错位。
    /// </summary>
    private static ShellLogic.ServiceLifetime ReadLifetimeMode()
    {
        var json = SafeReadText(SettingsPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "dsh-launcher", "settings.json");
                if (File.Exists(legacy))
                {
                    json = SafeReadText(legacy);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        try
                        {
                            Directory.CreateDirectory(DataDir);
                            File.WriteAllText(SettingsPath, json);
                            File.Delete(legacy);
                        }
                        catch { /* 迁移失败按旧值执行 */ }
                    }
                }
            }
            catch { /* 旧路径不可读按默认执行 */ }
        }
        return ShellLogic.ParseLifetimeMode(json);
    }

    /// <summary>壳托管服务的 PID 记录文件（按端口隔离）：崩溃/异常退出后残留的服务可被下次启动接管管理。</summary>
    private static string ServicePidFile => Path.Combine(DataDir, $"service-pid-{Target.Port}.txt");

    /// <summary>记录本次壳拉起的服务 PID（服务就绪后调用），供下次启动接管残留服务。</summary>
    private static void RecordServicePid()
    {
        try
        {
            var pid = FindPidListeningOn(Target.Port);
            if (pid > 0)
            {
                _servicePid = pid;
                File.WriteAllText(ServicePidFile, pid.ToString());
            }
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
                _servicePid = pid;
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
    /// 停止"壳本次会话拉起的"dsh 服务：优先用内存缓存的 PID（就绪时已记录，
    /// 关窗路径不再跑 netstat）。温和 taskkill 对无窗口进程（wscript 隐藏启动的
    /// node）发 WM_CLOSE 无效，必须**在壳退出前同步确认**：短等待未停则立即
    /// 强制 /f——此前强制杀在后台 Task 里延迟 1.5s，壳退出后 Task 未及执行，
    /// 导致"跟随窗口"关窗后服务残留（issue #…）。全程限时（&lt;1s），不卡关窗。
    /// </summary>
    private static void StopShellService()
    {
        try
        {
            var pid = _servicePid;
            if (pid <= 0) pid = FindPidListeningOn(Target.Port); // 兜底：内存没有时再查
            if (pid <= 0)
            {
                ClearServicePidFile();
                return;
            }
            Process.Start(new ProcessStartInfo("taskkill", "/pid " + pid)
            { UseShellExecute = false, CreateNoWindow = true });

            // 同步等待温和终止（node 通常几百毫秒内退出）；未停则在退出前强制
            var deadline = DateTime.UtcNow.AddMilliseconds(900);
            while (DateTime.UtcNow < deadline && PortOpen(Target.Port))
                Thread.Sleep(100);
            if (PortOpen(Target.Port))
            {
                Process.Start(new ProcessStartInfo("taskkill", "/f /pid " + pid)
                { UseShellExecute = false, CreateNoWindow = true });
            }
            ClearServicePidFile();
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
            // 左键单击：窗口置顶显示；右键：弹出自绘托盘菜单（浅色毛玻璃层，仅"退出"）。
            tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowMainWindow(form);
                else if (e.Button == MouseButtons.Right) ShowTrayMenu();
            };
            _trayIcon = tray;
        }
        catch
        {
            // 托盘创建失败不影响壳主流程
        }
    }

        /// <summary>在鼠标位置弹出托盘菜单（自绘浅色毛玻璃层）。</summary>
    private static void ShowTrayMenu()
    {
        try
        {
            var menu = new TrayMenuForm(() =>
            {
                _trayExitRequested = true;
                // 常驻模式：只退出壳（服务保留）；托盘驻留/跟随窗口：停掉壳拉起的服务
                if (ReadLifetimeMode() != ShellLogic.ServiceLifetime.AlwaysOn && _serviceStartedByShell)
                    StopShellService();
                Application.Exit();
            });
            var pt = Cursor.Position;
            // 菜单位于鼠标左上方（右键弹菜单位置习惯），略微内偏移避免越过屏幕边缘
            menu.Location = new Point(pt.X - menu.Width + 12, pt.Y - menu.Height - 6);
            menu.Show();
        }
        catch { /* 菜单显示失败不影响壳 */ }
    }

    /// <summary>
    /// 托盘右键菜单：自绘浅色弹出层（Acrylic 毛玻璃 + 大圆角 + 内容垂直居中 + 仅"退出"）。
    /// 浅色观感：白 tint 毛玻璃、#E5E7EB 边框、#6B7280 应用名、#DC2626 红色退出、hover 淡红。
    /// 图标用 GraphicsPath 矢量绘制（电源符号），文字/图标分图层且不缩放，DPI 下清晰。
    /// </summary>
    private sealed class TrayMenuForm : Form
    {
        private const int MenuWidth = 168;
        private const int PadX = 6;
        private const int HeaderHeight = 34;
        private const int ExitHeight = 38;
        private const int CornerRadius = 12;

        private static readonly Color TextSecondary = Color.FromArgb(55, 65, 81);       // #374151 深灰（白底清晰）
        private static readonly Color TextDanger = Color.FromArgb(185, 28, 28);         // #B91C1C 深红（白底清晰）
        private static readonly Color TextDangerHover = Color.FromArgb(220, 38, 38);    // #DC2626
        private static readonly Color BorderColor = Color.FromArgb(229, 231, 235);      // #E5E7EB
        private static readonly Color SepColor = Color.FromArgb(243, 244, 246);         // #F3F4F6
        private static readonly Color DotColor = Color.FromArgb(239, 68, 68);           // #EF4444

        private readonly Action _onExit;
        private bool _hoverExit;
        private Rectangle _exitRect;

        public TrayMenuForm(Action onExit)
        {
            _onExit = onExit;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.White;
            Size = new Size(MenuWidth, PadX * 2 + HeaderHeight + 1 + ExitHeight);
            Font = new Font("Segoe UI", 9F); // 字体栈：Segoe UI / Microsoft YaHei / PingFang SC（系统回退）
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW：浮层投影
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // 实心浅色背景 + Region 圆角（不用 Acrylic 毛玻璃：实测 Win10 上模糊背景
            // 让文字几乎不可见；成熟托盘菜单均为实心/近实心底 + 清晰深色文字）
            try { using var path = RoundedRect(new Rectangle(Point.Empty, Size), CornerRadius); Region = new Region(path); } catch { }
            // 弹出动画：opacity 0→1，120ms
            Opacity = 0;
            var t = new System.Windows.Forms.Timer { Interval = 12 };
            var start = DateTime.UtcNow;
            t.Tick += (_, _) =>
            {
                var p = Math.Min(1.0, (DateTime.UtcNow - start).TotalMilliseconds / 120.0);
                Opacity = p;
                if (p >= 1.0) t.Stop();
            };
            t.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            // 1px 边框（浅灰，Region 圆角内）
            using (var pen = new Pen(BorderColor))
                g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            // 标题行：红点 + 应用名，垂直居中
            int headerY = PadX;
            using (var dot = new SolidBrush(DotColor))
                g.FillEllipse(dot, PadX + 9, headerY + (HeaderHeight - 6) / 2, 6, 6);
            using var titleFont = new Font("Segoe UI", 9F, FontStyle.Regular);
            TextRenderer.DrawText(g, "dsh-launcher", titleFont,
                new Rectangle(PadX + 23, headerY, Width - PadX * 2 - 30, HeaderHeight),
                TextSecondary, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);

            // 分隔线
            int sepY = headerY + HeaderHeight;
            using (var sepPen = new Pen(SepColor))
                g.DrawLine(sepPen, PadX + 9, sepY, Width - PadX - 9, sepY);

            // 退出项：hover 淡红圆角背景 + 矢量电源图标 + 红色文字（垂直居中）
            _exitRect = new Rectangle(PadX, sepY + 1, Width - PadX * 2, ExitHeight);
            if (_hoverExit)
            {
                using var hb = new SolidBrush(Color.FromArgb(20, 220, 38, 38));
                using var path = RoundedRect(_exitRect, 8);
                g.FillPath(hb, path);
            }
            int iconCX = _exitRect.X + 9 + 8;
            int iconCY = _exitRect.Y + _exitRect.Height / 2;
            using (var pen = new Pen(_hoverExit ? TextDangerHover : TextDanger, 2f))
            using (var path = PowerIcon(iconCX, iconCY, 9))
                g.DrawPath(pen, path);
            using var exitFont = new Font("Segoe UI", 9F, FontStyle.Regular);
            TextRenderer.DrawText(g, "退出", exitFont,
                new Rectangle(_exitRect.X + 26, _exitRect.Y, _exitRect.Width - 34, _exitRect.Height),
                _hoverExit ? TextDangerHover : TextDanger,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }

        /// <summary>电源符号矢量路径（弧 + 竖线，中心 (cx,cy) 半径 r）。</summary>
        private static System.Drawing.Drawing2D.GraphicsPath PowerIcon(int cx, int cy, float r)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            p.AddArc(new RectangleF(cx - r, cy - r, r * 2, r * 2), 135f, 270f);
            p.AddLine(cx, cy - r - 2f, cx, cy);
            return p;
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private bool HitExit(Point p) => _exitRect.Contains(p);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var h = HitExit(e.Location);
            if (h != _hoverExit) { _hoverExit = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverExit) { _hoverExit = false; Invalidate(); }
            base.OnMouseLeave(e);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HitExit(e.Location)) { Close(); _onExit(); return; }
            base.OnMouseClick(e);
        }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Close(); }
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }
    }

    /// <summary>
    /// 显示并置顶主窗口（托盘左键单击 / 菜单唤起）：开着就提到最上层并聚焦，
    /// 隐藏着就显示出来；**最小化时先还原**（Activate 对最小化窗口无效，
    /// 此前最小化后单击托盘"点不回来"）；含 WebView2 崩溃/长隐藏恢复。
    /// </summary>
    private static void ShowMainWindow(Form form)
    {
        if (!form.Visible) form.Show();
        if (form.WindowState == FormWindowState.Minimized)
        {
            // 最小化 → 还原（SW_RESTORE），否则 Activate 无效，窗口不会出现
            ShowWindow(form.Handle, SW_RESTORE);
            form.WindowState = FormWindowState.Normal;
        }
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
        // 隐藏状态下 Reload 无效，等托盘恢复窗口时由 ShowMainWindow 兜底重载。
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
        var popupWeb = new WebView2();
        var form = new DshShellForm
        {
            Text = "DeepSeek Harness",
            ClientSize = new Size(900, 640),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.None, // 自绘标题栏（与主窗口一致，主题即时切换）
            Icon = SystemIcons.Application
        };
        var titleHeight = (int)Math.Round(32 * form.DeviceDpi / 96f);
        form.TitleBar = new CustomTitleBar(form, ResolveDarkMode())
        {
            // 四周 1px 窗口边框（Form.BackColor=边框色）
            Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2, titleHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        form.Controls.Add(form.TitleBar);
        form.HandleCreated += (_, _) => ApplyWindowShadow(form.Handle);
        popupWeb.Bounds = new Rectangle(1, 1 + titleHeight, form.ClientSize.Width - 2, form.ClientSize.Height - titleHeight - 2);
        popupWeb.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        form.DpiChanged += (_, _) =>
        {
            var scale = form.DeviceDpi / 96f;
            var h = (int)Math.Round(32 * scale);
            form.TitleBar.Rescale(scale);
            form.TitleBar.Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2, h);
            popupWeb.Bounds = new Rectangle(1, 1 + h, form.ClientSize.Width - 2, form.ClientSize.Height - h - 2);
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

    /// <summary>蓝色鲸鱼图标（托盘/任务栏按钮固定用：DeepSeek 蓝 #4D6BFE，深浅背景都清晰）。</summary>
    private static Icon? _blueWhaleIcon;

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

    /// <summary>读取 dsh 前端的主题选择（DSH_HOME/settings.yaml 的 ui-theme.preference）。
    /// 严格限定在 ui-theme 段内查找，避免误读其他段（如 agent-default-model 等）的 preference 键。</summary>
    private static string? ReadDshThemePreference()
    {
        try
        {
            var yaml = Path.Combine(DshHomeDir, "settings.yaml");
            if (!File.Exists(yaml)) return null;
            var inUiTheme = false;
            foreach (var raw in File.ReadAllLines(yaml))
            {
                var t = raw.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                if (t.StartsWith("ui-theme:", StringComparison.Ordinal))
                {
                    inUiTheme = true;
                    continue;
                }
                if (inUiTheme)
                {
                    if (t.StartsWith("preference:", StringComparison.Ordinal))
                        return t["preference:".Length..].Trim().Trim('"', '\'').ToLowerInvariant();
                    // 遇到下一段（无缩进的顶层键）则离开 ui-theme 段
                    if (!raw.StartsWith(' ') && !raw.StartsWith('\t'))
                        inUiTheme = false;
                }
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

    /// <summary>白色鲸鱼（托盘/任务栏固定用，深色鲸鱼在深色背景上看不清）。</summary>
    /// <summary>蓝色鲸鱼（托盘/任务栏按钮固定用：DeepSeek 蓝 #4D6BFE，深浅背景都清晰；
    /// 不用白色——白色在浅色背景/浅色任务栏上看不清，蓝色则始终可见）。</summary>
    private static Icon? TrayWhaleIcon => _blueWhaleIcon ??= LoadIconResource("favicon-blue.png");

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rectUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_FRAME = 0x0400;
    private const uint RDW_ALLCHILDREN = 0x0080;

    private const int WM_NCPAINT = 0x0085;
    private const int WM_NCACTIVATE = 0x0086;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SPI_SETNONCLIENTMETRICS = 0x002A;
    private const int GWL_STYLE = -16;

    /// <summary>
    /// 强制标题栏深色/浅色（Win10 1809+ 的沉浸式深色标题栏）：让标题栏与图标/前端主题
    /// 保持一致。DWM 属性设置后标题栏**不会自动重绘**（表现为"切换没反应，点走再点回来
    /// 才变"；实测本机 SWP_FRAMECHANGED/RedrawWindow/DwmFlush/WM_NCPAINT 均不触发）。
    /// 追加两记重手段：①同值重设窗口样式（SetWindowLongPtr 强制系统重算窗口帧）；
    /// ②广播 WM_SETTINGCHANGE(SPI_SETNONCLIENTMETRICS)（系统级非客户区设置变更通知）。
    /// </summary>
    private static void SetTitleBarDark(Form form, bool dark)
    {
        try
        {
            if (form.Handle == IntPtr.Zero) return;
            var value = dark ? 1 : 0;
            var hr = DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int));
            if (hr != 0)
            {
                hr = DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int)); // Win10 1809 用 19
            }
            if (hr != 0)
                Trace($"title bar dark set failed hr=0x{hr:X8} dark={dark}");
            DwmGetWindowAttribute(form.Handle, 20, out var actual, sizeof(int));
            Trace($"title bar dark: set dark={dark} hr=0x{hr:X8} actual={actual}");
            // 组合拳：窗口帧重算 + 非客户区重绘 + 系统设置变更广播
            SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            RedrawWindow(form.Handle, IntPtr.Zero, IntPtr.Zero,
                RDW_FRAME | RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
            try { DwmFlush(); } catch { }
            SendMessage(form.Handle, WM_NCPAINT, (IntPtr)1, IntPtr.Zero);
            // 同值重设窗口样式：强制系统重算窗口帧（SWP_FRAMECHANGED 在本机无效）
            try
            {
                var style = GetWindowLongPtr(form.Handle, GWL_STYLE);
                if (style != IntPtr.Zero)
                    SetWindowLongPtr(form.Handle, GWL_STYLE, style);
            }
            catch { }
            SendMessage(form.Handle, WM_SETTINGCHANGE, (IntPtr)SPI_SETNONCLIENTMETRICS, IntPtr.Zero);
            // 模拟"点走再点回来"：焦点变化时系统正是发 WM_NCACTIVATE 触发非客户区重绘，
            // 手动发一条（wParam=1 激活态）让标题栏按当前 DWM 属性立即重绘，不改变真实焦点。
            SendMessage(form.Handle, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
        }
        catch
        {
            // 标题栏配色失败不影响功能
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern IntPtr TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    /// <summary>给无边框窗口加 DWM 阴影（DWMWA_NCRENDERING_POLICY=ENABLED）。</summary>
    private static void ApplyWindowShadow(IntPtr hwnd)
    {
        try
        {
            const int DWMWA_NCRENDERING_POLICY = 2;
            const int DWMNCRP_ENABLED = 2;
            var policy = DWMNCRP_ENABLED;
            DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
        }
        catch { /* 阴影失败不影响功能 */ }
    }

    /// <summary>
    /// 自绘标题栏（无边框窗口用）：背景/文字/按钮颜色完全自绘，主题切换即时生效，
    /// 不依赖 DWM 标题栏重绘（实测本机 DWM 属性切换后标题栏画面不刷新，只有焦点变化才重绘）。
    /// 提供：标题 + 主题鲸鱼图标 + 最小化/最大化/关闭按钮 + 拖拽移动 + 双击最大化 + 右键系统菜单。
    /// </summary>
    private sealed class CustomTitleBar : Panel
    {
        private readonly Form _owner;
        private float _scale;
        private int _btnWidth;
        private bool _dark;
        private bool _hoverMin, _hoverMax, _hoverClose;

        private static readonly Font TitleFont = new("Microsoft YaHei UI", 9F);
        private static readonly Color DarkBg = Color.FromArgb(32, 32, 32);
        private static readonly Color LightBg = Color.FromArgb(240, 240, 240);
        private static readonly Color DarkText = Color.White;
        private static readonly Color LightText = Color.FromArgb(30, 30, 30);
        private static readonly Color DarkHover = Color.FromArgb(58, 58, 58);
        private static readonly Color LightHover = Color.FromArgb(229, 229, 229);
        private static readonly Color CloseHover = Color.FromArgb(232, 17, 35);

        public CustomTitleBar(Form owner, bool dark)
        {
            _owner = owner;
            _dark = dark;
            // DPI 缩放：150% 缩放下 32px 物理高度会显得又矮又挤（按钮/图标/间距全按逻辑缩放）
            _scale = owner.DeviceDpi / 96f;
            _btnWidth = (int)Math.Round(46 * _scale);
            BackColor = _dark ? DarkBg : LightBg;
            MouseDown += OnMouseDown;
            MouseUp += OnMouseUp;
            MouseDoubleClick += OnDoubleClick;
            MouseMove += OnMouseMove;
            MouseLeave += (_, _) =>
            {
                if (_hoverMin || _hoverMax || _hoverClose)
                {
                    _hoverMin = _hoverMax = _hoverClose = false;
                    Invalidate();
                }
            };
        }

        /// <summary>主题切换：自绘颜色立即更新（无 DWM 重绘问题）。</summary>
        public void ApplyTheme(bool dark)
        {
            _dark = dark;
            BackColor = _dark ? DarkBg : LightBg;
            Invalidate();
        }

        /// <summary>DPI 变化时重算缩放比例与按钮宽度。</summary>
        public void Rescale(float scale)
        {
            _scale = scale;
            _btnWidth = (int)Math.Round(46 * _scale);
            Invalidate();
        }

        private Rectangle BtnRect(int i) => new(Width - _btnWidth * (3 - i), 0, _btnWidth, Height);

        private int HitButton(int x)
        {
            for (var i = 0; i < 3; i++)
                if (BtnRect(i).Contains(x, Height / 2)) return i;
            return -1;
        }

        private void OnMouseDown(object? s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowSystemMenu(e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (HitButton(e.X) >= 0) return; // 按钮点击交给 MouseUp
            // 拖拽移动窗口（系统级 HTCAPTION 拖拽）
            ReleaseCapture();
            SendMessage(_owner.Handle, (uint)WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        private void OnMouseUp(object? s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            switch (HitButton(e.X))
            {
                case 0: _owner.WindowState = FormWindowState.Minimized; break;
                case 1:
                    _owner.WindowState = _owner.WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal : FormWindowState.Maximized;
                    break;
                case 2: _owner.Close(); break;
            }
        }

        private void OnDoubleClick(object? s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HitButton(e.X) < 0)
                _owner.WindowState = _owner.WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        private void OnMouseMove(object? s, MouseEventArgs e)
        {
            var btn = HitButton(e.X);
            var h1 = btn == 0;
            var h2 = btn == 1;
            var h3 = btn == 2;
            if (h1 != _hoverMin || h2 != _hoverMax || h3 != _hoverClose)
            {
                _hoverMin = h1;
                _hoverMax = h2;
                _hoverClose = h3;
                Invalidate();
            }
        }

        private void ShowSystemMenu(Point p)
        {
            try
            {
                var hMenu = GetSystemMenu(_owner.Handle, false);
                if (hMenu == IntPtr.Zero) return;
                TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                    _owner.Left + p.X, _owner.Top + p.Y, 0, _owner.Handle, IntPtr.Zero);
            }
            catch { /* 系统菜单失败忽略 */ }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(_dark ? DarkBg : LightBg);
            var textColor = _dark ? DarkText : LightText;

            // 标题栏图标（主题对应鲸鱼，按 DPI 缩放）
            var icon = _dark
                ? (_lightWhaleIcon ??= LoadIconResource("favicon-white.png"))
                : (_darkWhaleIcon ??= LoadIconResource("favicon.png"));
            var iconSize = (int)Math.Round(16 * _scale);
            if (icon is not null)
            {
                g.DrawIcon(icon, new Rectangle((int)Math.Round(10 * _scale), (Height - iconSize) / 2, iconSize, iconSize));
            }

            // 标题
            var titleLeft = (int)Math.Round(34 * _scale);
            TextRenderer.DrawText(g, "DeepSeek Harness", TitleFont,
                new Rectangle(titleLeft, 0, Math.Max(0, Width - _btnWidth * 3 - titleLeft - 8), Height),
                textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            // 窗口按钮：用 Segoe MDL2 字形（最小化/最大化/还原/关闭），清晰且与系统图标一致
            using (var btnFont = new Font("Segoe MDL2 Assets", (float)Math.Round(11 * _scale), FontStyle.Regular, GraphicsUnit.Pixel))
            {
                for (var i = 0; i < 3; i++)
                {
                    var r = BtnRect(i);
                    var hover = (i == 0 && _hoverMin) || (i == 1 && _hoverMax) || (i == 2 && _hoverClose);
                    if (hover)
                    {
                        using var hb = new SolidBrush(i == 2 ? CloseHover : (_dark ? DarkHover : LightHover));
                        g.FillRectangle(hb, r);
                    }
                    var glyph = i switch
                    {
                        0 => '\uE921', // Minimize
                        1 => _owner.WindowState == FormWindowState.Maximized ? '\uE923' : '\uE922', // Restore / Maximize
                        _ => '\uE8BB', // ChromeClose
                    };
                    var glyphColor = hover && i == 2 && _dark ? Color.White : textColor;
                    TextRenderer.DrawText(g, glyph.ToString(), btnFont, r, glyphColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }

            // 底部细分隔线
            using var line = new Pen(_dark ? Color.FromArgb(48, 48, 48) : Color.FromArgb(225, 225, 225));
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>
    /// 无边框主窗口：处理最大化限制在工作区（WM_GETMINMAXINFO）与边缘缩放（WM_NCHITTEST）。
    /// 标题栏由 <see cref="CustomTitleBar"/> 自绘。
    /// </summary>
    private sealed class DshShellForm : Form
    {
        internal CustomTitleBar? TitleBar;

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTCLIENT = 0x0001;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private const int ResizeEdge = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_GETMINMAXINFO:
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
                    var wa = Screen.FromHandle(Handle).WorkingArea;
                    mmi.ptMaxSize.X = wa.Width;
                    mmi.ptMaxSize.Y = wa.Height;
                    mmi.ptMaxPosition.X = wa.Left;
                    mmi.ptMaxPosition.Y = wa.Top;
                    Marshal.StructureToPtr(mmi, m.LParam, false);
                    m.Result = IntPtr.Zero;
                    return;
                }
                case WM_NCHITTEST:
                    base.WndProc(ref m);
                    if (m.Result == (IntPtr)HTCLIENT && WindowState != FormWindowState.Maximized)
                    {
                        var pt = new Point(m.LParam.ToInt32() & 0xFFFF, (m.LParam.ToInt32() >> 16) & 0xFFFF);
                        var r = RectangleToScreen(ClientRectangle);
                        var left = pt.X < r.Left + ResizeEdge;
                        var right = pt.X > r.Right - ResizeEdge;
                        var top = pt.Y < r.Top + ResizeEdge;
                        var bottom = pt.Y > r.Bottom - ResizeEdge;
                        if (left && top) m.Result = (IntPtr)HTTOPLEFT;
                        else if (right && top) m.Result = (IntPtr)HTTOPRIGHT;
                        else if (left && bottom) m.Result = (IntPtr)HTBOTTOMLEFT;
                        else if (right && bottom) m.Result = (IntPtr)HTBOTTOMRIGHT;
                        else if (left) m.Result = (IntPtr)HTLEFT;
                        else if (right) m.Result = (IntPtr)HTRIGHT;
                        else if (top) m.Result = (IntPtr)HTTOP;
                        else if (bottom) m.Result = (IntPtr)HTBOTTOM;
                    }
                    return;
                default:
                    base.WndProc(ref m);
                    return;
            }
        }
    }

    /// <summary>标题栏小图标消息（WM_SETICON + ICON_SMALL）。
    /// <para><b>固定白色鲸鱼</b>：Windows 11 任务栏按钮读取的是小图标（ICON_SMALL），
    /// 若跟随主题（浅色 → 深色鲸鱼）任务栏 logo 会变黑——因此小图标恒为白色，
    /// 与托盘一致；标题栏内的鲸鱼由自绘 OnPaint 跟随主题，不受此影响。</para></summary>
    private static void SetTitleBarIcon(Form form)
    {
        try
        {
            if (form.Handle == IntPtr.Zero) return;
            var icon = _blueWhaleIcon ??= LoadIconResource("favicon-blue.png");
            if (icon is not null)
                SendMessage(form.Handle, 0x0080 /* WM_SETICON */, (IntPtr)0 /* ICON_SMALL */, icon.Handle);
        }
        catch
        {
            // 标题栏图标设置失败不影响功能
        }
    }

    /// <summary>
    /// 应用主题（以用户的选择为主——dsh 前端主题设置，其次跟随系统）：
    /// - **系统任务栏图标 + 托盘图标：固定白色鲸鱼**（Windows 11 任务栏按钮读 ICON_SMALL，
    ///   因此小图标也固定白色，任何主题下任务栏 logo 始终为白；任务栏/托盘多为深色背景，
    ///   深色鲸鱼看不清）
    /// - **窗口标题栏**：自绘鲸鱼图标跟随主题（深色 → 白色鲸鱼，浅色 → 深色鲸鱼），
    ///   标题栏背景用 DWM 沉浸式深色/浅色（DwmSetWindowAttribute）
    /// - 主题状态写入 <c>theme.json</c>（插件设置页可读取显示当前情况）
    /// </summary>
    private static void ApplyThemeIcon(Form form)
    {
        var dark = ResolveDarkMode();
        try { form.Icon = TrayWhaleIcon ?? SystemIcons.Application; } catch { /* ignore */ }
        SetTitleBarIcon(form);
        // 自绘标题栏主题（主窗口/弹窗）：自绘颜色即时生效，无 DWM 重绘问题
        if (form is DshShellForm sf && sf.TitleBar is not null)
        {
            sf.TitleBar.ApplyTheme(dark);
            // 窗口 1px 边框色（替代阴影的质感）：深色比标题栏亮一档、浅色比标题栏深一档
            try { form.BackColor = dark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(208, 208, 208); } catch { }
        }
        else
        {
            SetTitleBarDark(form, dark); // 兜底：未自绘标题栏的窗口
        }
        if (_trayIcon is not null)
        {
            try { _trayIcon.Icon = TrayWhaleIcon ?? SystemIcons.Application; } catch { /* ignore */ }
        }
        WriteThemeState(dark);
    }

    /// <summary>把壳当前的主题判定写入 theme.json，供插件设置页显示诊断（"现在是什么情况"）。</summary>
    private static void WriteThemeState(bool dark)
    {
        try
        {
            var state = "{\"preference\":" + System.Text.Json.JsonSerializer.Serialize(ReadDshThemePreference())
                + ",\"resolved\":\"" + (dark ? "dark" : "light")
                + "\",\"at\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\"}";
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "theme.json"), state);
        }
        catch
        {
            // 状态写入失败不影响功能
        }
    }

    /// <summary>
    /// 主题实时刷新（无感切换）：dsh 前端写 settings.yaml 时 FileSystemWatcher 立即触发
    /// （Changed + Renamed 都监听——dsh 可能原子替换写文件），500ms 轮询兜底
    /// （watcher 漏事件也能 2 个周期内赶上）。两路共用同一去抖状态：
    /// 主题结果变化才应用（换图标/标题栏会让任务栏重绘，频繁触发会造成"一卡一卡"）。
    /// </summary>
    private static void RegisterThemeWatcher(Form form)
    {
        var lastDark = ResolveDarkMode();
        void ApplyIfThemeChanged()
        {
            try
            {
                var nowDark = ResolveDarkMode();
                if (nowDark != lastDark)
                {
                    lastDark = nowDark;
                    ApplyThemeIcon(form);
                    Trace($"theme changed: {(nowDark ? "dark" : "light")}");
                }
            }
            catch
            {
                // 轮询失败下次再试
            }
        }

        try
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                    ApplyIfThemeChanged();
            };
        }
        catch
        {
            // 系统主题监听失败不影响启动
        }

        try
        {
            var dir = DshHomeDir;
            if (Directory.Exists(dir))
            {
                var watcher = new FileSystemWatcher(dir, "settings.yaml")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += (_, _) => { try { form.BeginInvoke(ApplyIfThemeChanged); } catch { } };
                watcher.Renamed += (_, _) => { try { form.BeginInvoke(ApplyIfThemeChanged); } catch { } };
            }
        }
        catch
        {
            // 文件监听失败不影响启动（轮询兜底）
        }

        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) => ApplyIfThemeChanged();
        timer.Start();
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
