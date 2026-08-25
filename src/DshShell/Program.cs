using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DshWeb.Chrome; // CustomTitleBar / WindowChromeController（自绘标题栏已迁出至 Chrome 层）
using DshWeb.Managers; // F11LowLevelHook（F11 钩子已迁出至 Managers 层）
using DshWeb.Win32; // Win32Constants/NativeMethods（结构体与 P/Invoke 已迁出）
using DshWeb.Windows; // DshShellForm / TrayMenuForm（窗体类已迁出至 Windows 层）
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

    /// <summary>解析目标地址/端口；空值/非法值回退默认 3080。
    /// 统一委托 ShellLogic.ResolveTarget（契约测试覆盖生产路径，含 DSH_WEB_PORT）。</summary>
    private static (string Url, int Port) ResolveTarget() =>
        ShellLogic.RuntimeConfig.ResolveTarget(
            Environment.GetEnvironmentVariable("DSH_WEB_URL"),
            Environment.GetEnvironmentVariable("DSH_WEB_PORT"));

    /// 设置 DSH_WEB_URL 时视为"外部托管服务"，壳不再自动拉起 dsh（DSH_WEB_PORT 则相反：壳托管拉起）。
    /// 测试开关：DSH_TEST_FORCE_MANAGED=1 时强制使用托管模式（忽略 DSH_WEB_URL）。
    private static readonly bool ServerManagedExternally =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSH_WEB_URL"))
        && !string.Equals(Environment.GetEnvironmentVariable("DSH_TEST_FORCE_MANAGED"), "1", StringComparison.OrdinalIgnoreCase);

    // WebView2 crash throttle / main-web ref / recovery flag: migrated to WebViewManager.
    // Theme monitor (_themeTimer/_themeWatcher/_themeEventsHandler): migrated to WindowManager.

    /// 本次会话是否由壳拉起了 dsh 服务（决定"跟随窗口/托盘退出"时是否停它；外部托管/用户手动起的服务不动）。
    private static bool _serviceStartedByShell;

    /// <summary>屏幕拓扑抽象（v0.4.0 Headless 化）：默认 WinForms Screen，测试注入 Fake 拓扑。</summary>
    internal static DshWeb.Win32.IScreenProvider ScreenProvider { get; set; } = new DshWeb.Win32.WinFormsScreenProvider();

    /// <summary>恢复窗口位置（经 IScreenProvider 取拓扑 → ShellLogic 纯函数；测试注入 Fake 验证）。</summary>
    internal static (int X, int Y) RestoreWindowPosition(int x, int y, int width, int height)
        => ShellLogic.RestoreWindowPosition(x, y, width, height,
            ScreenProvider.GetAllWorkingAreas(), ScreenProvider.PrimaryWorkingArea);

    /// 本次会话壳托管服务的监听 PID（内存缓存，关窗时直接使用，避免再跑 netstat 造成卡顿）。
    private static int _servicePid;

    // ---- 安全模式重构（ADR-022）：隔离空 profile + --profile 指向 ----
    /// <summary>安全模式状态（落盘持久化，崩溃/重启仍记忆）。</summary>
    internal static readonly DshWeb.Domain.SafeModeState SafeMode =
        new(DshWeb.Domain.SafeModeState.DefaultStorePath(DshHomeDir));

    /// <summary>安全模式 profile 构建器（隔离 .dsh-safe，不碰用户文件）。</summary>
    internal static readonly DshWeb.Domain.SafeProfileBuilder SafeProfile = new(DshHomeDir);

    // ---- 启动健康融合监控（ADR-023）：进程/日志/HTTP/页面四观察位主动拉取，CDP 只采集 ----
    /// <summary>生效签名档：DSH_BOOT_SIGNATURES（JSON）可整体覆盖默认值（沙盒注入假签名用）。</summary>
    internal static readonly ShellLogic.BootGuard.BootProfile BootSignatures =
        ShellLogic.BootGuard.ResolveProfile(Environment.GetEnvironmentVariable("DSH_BOOT_SIGNATURES"));

    /// <summary>启动健康监控实例（服务就绪后创建；null = 尚未启动/已停止）。</summary>
    internal static DshWeb.Lifecycle.BootHealthMonitor? BootMonitor { get; private set; }

    // Tray state (_trayIcon/_trayExitRequested): migrated to WindowManager.Instance.

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

    /// <summary>壳的数据目录（settings.json / 统一日志 / service-pid 等）：DSH_HOME\dsh-launcher。</summary>
    private static string DataDir => Path.Combine(DshHomeDir, "dsh-launcher");

    /// <summary>统一日志路径（v0.3.0 单一日志文件）：壳的 JSON Lines 与 dsh 服务输出同文件。</summary>
    private static string UnifiedLogPath => Path.Combine(DataDir, "dsh.log");

    /// <summary>沙盒模式标志：DSH_SANDBOX=1 时禁用所有机器级副作用（自启/数据清理/首装网络安装）。
    /// 纯环境读取下沉至 ShellLogic.RuntimeConfig（Manager 层同源门控）；此处保留组合根转发。</summary>
    internal static bool IsSandboxMode => ShellLogic.RuntimeConfig.IsSandboxMode;

    /// <summary>启动时迁移旧版数据（%LOCALAPPDATA% → DSH_HOME）：实现见 AppEnvironment。</summary>
    private static void MigrateLegacyData() => Managers.AppEnvironment.MigrateLegacyData();

    /// <summary>清理卸载后 ProgramData 空目录残留：实现见 AppEnvironment（沙盒门控在调用侧）。</summary>
    private static void CleanupProgramDataResidue()
    {
        if (IsSandboxMode) return; // [SANDBOX] 禁用机器级副作用
        Managers.AppEnvironment.CleanupProgramDataResidue();
    }

    /// <summary>自启落地（HKLM 意图标志 → HKCU Run 拉壳）：实现见 AppEnvironment。</summary>
    private static void EnsureAutoStartRequested()
    {
        if (IsSandboxMode) return; // [SANDBOX] 禁用机器级副作用
        Managers.AppEnvironment.EnsureAutoStartRequested(Trace);
    }

    /// <summary>
    /// 启动轨迹日志：v0.3.0 起统一走 <see cref="Logger"/>（DSH_HOME\dsh-launcher\dsh.log，JSON Lines）。
    /// 保留 Trace 名称以最小化调用点改动；写失败静默（日志不影响启动）。
    /// </summary>
    internal static void Trace(string message) => Logger.Info(message);

    /// <summary>ShowWindow 转发（WindowManager 托盘唤起 SW_RESTORE 用；internal 供 Managers 访问）。</summary>
    internal static void ShowWindowNative(IntPtr hwnd, int nCmdShow) => ShowWindow(hwnd, nCmdShow);

    /// <summary>
    /// P0-2（质量治理）+ 静默失败收口：崩溃留痕钩子。未捕获异常先写日志（E9001 + 异常全文）。
    /// - UI 线程异常经 Application.ThreadException：只记日志不弹窗（消息泵继续、应用可存活，
    ///   弹模态框反而打断交互；日志已含全文可定位）。
    /// - 主线程/后台线程未捕获异常经 AppDomain.UnhandledException：写日志后进程即终止——此前
    ///   "双击后无声消失"零可见线索，现补 [E9001] 弹窗（非无头模式），至少让用户看到失败原因
    ///   与日志路径（v0.4.x 用户回归："一段时间后静默失败"的收口之一）。
    /// 克制：只加诊断与可见性、不加恢复逻辑（恢复 = 用户重新打开）。
    /// </summary>
    private static void RegisterCrashHooks()
    {
        Application.ThreadException += (_, e) =>
            Logger.Error("unhandled UI-thread exception: " + e.Exception, ErrorCodes.E9001,
                new { ex = e.Exception.ToString() });
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Logger.Error("unhandled exception: " + e.ExceptionObject, ErrorCodes.E9001,
                new { ex = e.ExceptionObject?.ToString() });
            TryShowFatalDialog("unhandled exception", e.ExceptionObject?.ToString());
        };
    }

    /// <summary>终态崩溃可见化（静默失败收口）：弹 [E9001] 对话框展示异常摘要与统一日志路径。
    /// 仅非无头模式弹出；弹窗自身失败不影响已完成的日志留痕。</summary>
    private static void TryShowFatalDialog(string kind, string? detail)
    {
        if (NoUiMode || E2EMode) return; // 无头/探针模式维持纯 stdout+log，防模态窗挂起自动化
        try
        {
            var summary = string.IsNullOrWhiteSpace(detail) ? "" :
                (detail.Length > 800 ? detail[..800] + "…" : detail) + "\n";
            MessageBox.Show(
                $"[{ErrorCodes.E9001}] dsh-launcher 发生内部错误（{kind}），无法继续运行。\n\n" +
                summary +
                $"\n完整日志：{UnifiedLogPath}",
                "dsh-launcher", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { /* 弹窗失败仅留日志 */ }
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
    /// <summary>
    /// 组合根（ADR-001/ADR-024）：环境初始化 + CLI 分派 + 单实例闸门 + 启动流水线装配 + 消息泵。
    /// 【铁律】本方法（及整个文件）严禁业务原语——进程拉起/HTTP/删除 IO 全部经
    /// Managers/ 与 Domain/ 模块执行；启动编排 100% 由 LauncherApp.RunStartupAsync 驱动。
    /// </summary>
    private static void Main()
    {
        // [INVARIANT] Main must be synchronous. WebView2 environment creation (native
        // CreateCoreWebView2EnvironmentWithOptions) strictly requires STA thread.
        // .NET 10 does not apply [STAThread] to async Task Main (MTA → RPC_E_CHANGED_MODE).
        // See docs/ARCHITECTURE_DECISIONS.md ADR-001.

        var args = Environment.GetCommandLineArgs();

        InitializeProcessEnvironment();
        if (HandleCommandLineArgs(args)) return; // CLI modes: diagnose / ui-selftest / ui-probe

        InitializeCoreDataAndLogs();
        if (!EnsureSingleInstanceAndAutostart()) return;

        if (!EnsureServiceAndRuntime()) return;

        RunUserInterface(args);
    }

    // ===== Pipeline stage methods (extracted from Main for readability) =====

    /// <summary>Stage 1: DPI awareness + WinForms global init. Must run before ANY window creation.</summary>
    private static void InitializeProcessEnvironment()
    {
        // [INVARIANT] DPI awareness must be set BEFORE any window/control creation. See ADR-003.
        SetProcessDpiAwarenessContext((IntPtr)(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        // [INVARIANT] WinForms global init must complete before ANY window/control creation. See ADR-004.
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
    }

    /// <summary>Stage 2: Handle CLI args (--diagnose, --ui-selftest, --ui-probe). Returns true if Main should exit.</summary>
    private static bool HandleCommandLineArgs(string[] args)
    {
        // --diagnose: CLI diagnostic export (no UI, no modal, stdout output).
        if (args.Any(a => string.Equals(a, "--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            Logger.Init(Path.Combine(DshHomeDir, "dsh-launcher", "dsh.log"));
            var zip = DiagnoseExport.Run(args, DshHomeDir, Logger.Path);
            if (zip is not null)
            {
                Logger.Info("diagnostic export written: " + zip);
                Console.WriteLine("dsh-launcher diagnose: " + zip);
                Console.WriteLine("已脱敏：不含任何密钥/会话/插件数据。可随 Issue 一起上传。");
            }
            else
            {
                Console.Error.WriteLine("dsh-launcher diagnose failed [" + ErrorCodes.E5001 + "]（详见统一日志：" + Logger.Path + "）");
            }
            return true;
        }

        // --ui-selftest: headless UI geometry selftest (GitHub CI).
        if (args.Any(a => a.Equals("--ui-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(RunUiSelftest());
            return true;
        }

        // --ui-probe: headless window probe for e2e geometry/F11/titlebar assertions.
        if (args.Any(a => a.Equals("--ui-probe", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.SetEnvironmentVariable("DSH_E2E", "1"); // E2E mode: ShowError → log+stdout only
            Environment.Exit(RunUiProbe());
            return true;
        }

        return false;
    }

    /// <summary>Stage 3: Logger init, crash hooks, feature flag trace.</summary>
    private static void InitializeCoreDataAndLogs()
    {
        Logger.Init(UnifiedLogPath);
        Trace("feature flag: DSH_USE_NEW_LIFECYCLE="
            + (Environment.GetEnvironmentVariable("DSH_USE_NEW_LIFECYCLE") == "1" ? "1 (new)" : "unset (legacy)"));
        // [INVARIANT] Crash hooks write E9001 log before process terminates. No recovery logic.
        RegisterCrashHooks();
        if (Environment.GetEnvironmentVariable("DSH_TEST_CRASH") == "1")
            throw new InvalidOperationException("test crash hook (DSH_TEST_CRASH=1)");
        Trace($"start target={Target.Url} external={ServerManagedExternally}");
    }

    /// <summary>Stage 4: Single-instance mutex + old version cleanup + orphan shortcut cleanup. Returns false if not first instance.</summary>
    private static bool EnsureSingleInstanceAndAutostart()
    {
        using var mutex = new Mutex(true, $@"Local\DshWeb.SingleInstance.{Target.Port}", out var firstInstance);
        if (!firstInstance)
        {
            // [静默失败收口] 主窗等待从 20s 收紧到 5s，且找不到不再无声退出——给出 [E1009]
            // Info 弹窗说明"另一实例正在启动但窗口未就绪"。此前用户在首实例慢启动期间连点图标，
            // 每次点击都落入这个最长 20s 的静默黑洞后无声消失（v0.4.x 用户回归：
            // "双击启动器不会有弹窗，会在一段时间后静默失败"的最直接吻合点）。
            var existing = FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, "DeepSeek Harness");
            for (var i = 0; existing == IntPtr.Zero && i < 10; i++)
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
                Trace("second instance: main window not found within 5s; surfacing E1009 instead of silent exit");
                ShowError(ErrorCodes.E1009,
                    $"检测到另一个 dsh-launcher 实例正在启动（端口 {Target.Port}），但其窗口 5 秒内没有出现。\n\n" +
                    $"请稍候再试一次；若反复出现，请查看统一日志：{UnifiedLogPath}",
                    level: Logger.Level.Info);
            }
            return false;
        }
        Trace("first instance");
        if (!IsSandboxMode) // [SANDBOX] 禁用机器级副作用
        {
            Windows.LegacyUpgradeCleanup.TryPromptOldVersionCleanup(NoUiMode);
            Windows.LegacyUpgradeCleanup.CleanupOrphanShortcuts();
        }
        return true;
    }

    /// <summary>Stage 5: SplashForm pipeline + service readiness check + NoUiMode. Returns false on failure/cancel.</summary>
    private static bool EnsureServiceAndRuntime()
    {
        using (var splash = new SplashForm(RunLauncherAppPipelineAsync, visible: !NoUiMode && !ServerManagedExternally))
        {
            Application.Run(splash);

            var outcome = splash.Result;
            if (outcome is null) return false;
            if (splash.CancelledByUser)
            {
                Trace("startup canceled by user");
                return false;
            }
            if (!outcome.Ready)
            {
                HandleStartupFailure(outcome);
                return false;
            }
            _serviceStartedByShell = outcome.ServiceStartedByShell;
            if (outcome.ServiceStartedByShell)
                RecordServicePid();
        }

        if (!ServerManagedExternally && !_serviceStartedByShell)
            TryAdoptOrphanService();

        // ---- ADR-023：服务就绪即进入启动健康监控。其内部的跨会话回滚武装（含 dsh 身份发现）
        // 移入后台线程执行——此前它连同下面的 PortOpen 终检都在 Splash 关闭后的 UI 线程上同步跑
        // （node --version 探测可达数秒），造成"Splash 关闭 → 主窗出现"之间的死窗期
        // （v0.4.x 用户回归："点击很久之后才会打开，弹窗没有一点击就出现"）。
        StartBootHealthMonitor();

        // 就绪判定的单一真相源 = 流水线 outcome.Ready（TCP+HTTP 双探针刚验证通过）。
        // 不再重复 PortOpen 同步终检：服务若恰在此间隙退出，主窗加载失败路径与健康监控会接管报错；
        // 此处多等一次只会白吃一段死窗时间。
        if (NoUiMode)
        {
            Trace("no-ui mode: service ready; exiting without window");
            return false;
        }

        return true;
    }

    /// <summary>Stage 6: Create main form, wire up WebView2/theme/tray, run Application.Run.</summary>
    private static void RunUserInterface(string[] args)
    {
        // 正常模式启动：清理历史遗留的隔离 profile（Task 4）。安全模式启动时 SafeProfile.Build
        // 会幂等重建，故此处清理无副作用；若本次为安全模式则保留（服务正在使用）。
        if (!SafeMode.IsActive && SafeProfile.SafeProfileExists())
        {
            SafeProfile.Cleanup();
            Trace("SAFEMODE: cleaned up stale safe profile (normal mode launch)");
        }

        // 测试标记：DSH_TEST_INSTANCE=1 时添加窗口标题后缀
        var testSuffix = string.Equals(Environment.GetEnvironmentVariable("DSH_TEST_INSTANCE"), "1", StringComparison.OrdinalIgnoreCase)
            ? " [TEST]" : "";
        var form = new DshShellForm
        {
            Text = "DeepSeek Harness" + testSuffix,
            ClientSize = new Size(1280, 840),
            StartPosition = FormStartPosition.CenterScreen,
            MinimumSize = new Size(800, 600),
            // [INVARIANT] Frameless + custom titlebar: DWM titlebar does not auto-refresh on theme switch.
            FormBorderStyle = FormBorderStyle.None,
            Icon = TrayWhaleIcon ?? SystemIcons.Application
        };
        var mainHwnd = form.Handle;
        using var f11Hook = new F11LowLevelHook(form.ToggleFullscreen,
            () => F11LowLevelHook.GetForegroundWindow() == mainHwnd);
        var titleHeight = (int)Math.Round(32 * form.DeviceDpi / 96f);
        form.TitleBar = new CustomTitleBar(form, ResolveDarkMode())
        {
            Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2, titleHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        form.Controls.Add(form.TitleBar);

        var web = new WebView2
        {
            Bounds = new Rectangle(1, 1 + titleHeight, form.ClientSize.Width - 2, form.ClientSize.Height - titleHeight - 2),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            // [INVARIANT] ImeMode.Disable: WinForms IME conflicts with Chromium's. See ADR-008.
            ImeMode = ImeMode.Disable,
        };
        form.Controls.Add(web);
        form.MainWebView2 = web;
        WebViewManager.MainWeb = web;

        form.HandleCreated += (_, _) => ApplyWindowShadow(form.Handle);
        form.DpiChanged += (_, _) =>
        {
            var scale = form.DeviceDpi / 96f;
            form.TitleBar.Rescale(scale);
            form.LayoutChrome();
        };

        // 托盘图标：由 dsh-launcher-lifetime 插件控制（通过 settings.json 的 serviceLifetime）
        // 壳只读取配置，不硬编码托盘逻辑
        // 仅在 Tray 模式下创建托盘（更新通知通过其他机制实现，不依赖托盘）
        var lifetimeMode = ReadLifetimeMode();
        if (lifetimeMode == ShellLogic.ServiceLifetime.Tray)
        {
            WindowManager.Instance.IsTrayWantedProvider = () => true;
            WindowManager.Instance.TrayWhaleIconProvider = () => TrayWhaleIcon ?? SystemIcons.Application;
            WindowManager.Instance.TrayExitAction = () =>
            {
                WindowManager.Instance.MarkTrayExitRequested();
                // [2026-08 关窗异步化] 托盘退出与关窗共用编排：窗口即刻消失，
                // 服务清理后台执行（原为 UI 线程同步 StopShellService，卡 1.5s+）
                BeginShutdownAsync(GetMainFormForDialog());
            };
            WindowManager.Instance.TrayMenuFactory = exitAction => new TrayMenuForm(exitAction);
            WindowManager.Instance.VerifyDependencies();
            WindowManager.Instance.EnsureTrayIcon(form);
        }

        WindowManager.Instance.ResolveDarkModeProvider = () => ResolveDarkMode();
        WindowManager.Instance.ApplyWindowThemeAction = (f, dark) => ApplyThemeIcon(f);
        WindowManager.Instance.DshHomeDirProvider = () => DshHomeDir;
        WindowManager.Instance.PopupFactory = CreatePopupForm;
        WindowManager.Instance.ApplyShadowAction = ApplyWindowShadow;
        WindowManager.Instance.ShowWindowAction = ShowWindowNative;
        WindowManager.Instance.TraceAction = Trace;
        WebViewManager.DownloadNotifyAction = NotifyDownloadComplete;
        ApplyThemeIcon(form);
        form.HandleCreated += (_, _) => ApplyThemeIcon(form);
        WindowManager.Instance.RegisterThemeWatcher(form);
        form.Shown += (_, _) => Trace("main form shown");

        form.FormClosing += (_, e) =>
        {
            // 异步退出编排已在进行（BeginShutdownAsync → Application.Exit 收尾触发）：
            // 放行关闭，绝不拦截/重复清理。
            if (_shutdownInitiated) return;

            var mode = ReadLifetimeMode();
            // Tray 模式：关闭窗口隐藏到托盘（插件控制，壳读取配置）
            // 仅在托盘图标存在时拦截（避免插件未启用时误拦截）
            if (mode == ShellLogic.ServiceLifetime.Tray
                && WindowManager.Instance.TrayIcon is not null
                && !WindowManager.Instance.TrayExitRequested
                && !_isBuildInProgress)
            {
                e.Cancel = true;
                form.Hide();
                WebViewManager.HiddenSince = DateTime.UtcNow;
                return;
            }

            // ---- 任务五：防误关拦截 ----
            // 后台正在构建更新时（npm install），关闭窗口会导致构建中断、环境损坏。
            // 拦截关闭，弹确认框；只有用户明确点击"强制关闭"才放行。
            if (_isBuildInProgress)
            {
                e.Cancel = true;
                var result = MessageBox.Show(
                    form,
                    "正在下载构建更新，关闭可能导致环境损坏。\n\n是否继续等待？",
                    "DeepSeek Harness - 更新构建中",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (result == DialogResult.No)
                {
                    // 用户选择强制关闭：取消构建（kill npm 进程）并放行
                    _buildCts?.Cancel();
                    _isBuildInProgress = false;
                    try { CancelBuildStatusDwell(); } catch { /* 定时器未创建 */ }
                    Trace("user forced close during build; build process canceled");
                }
                else
                {
                    // 用户选择等待：保持窗口打开
                    Trace("user chose to wait for build completion");
                    return;
                }
            }

            // [2026-08 关窗异步化] 不再在 UI 线程同步停服务（netstat 轮询 + taskkill 等待
            // 实测卡 1.5s+）：取消本次关闭 → 窗口即刻隐藏（视觉上已关），清理转后台，
            // 完成后 Application.Exit；3s 看门狗兜底强制退出。
            e.Cancel = true;
            BeginShutdownAsync(form);
        };

        form.Load += async (_, _) =>
        {
            if (_applyRestartPendingVersion is { } applyVersion && !_applyRestartDeferred)
            {
                PromptApplyRestart(form, applyVersion);
            }
            var savedWindow = WindowStateStore.Load();
            var scale = (double)form.DeviceDpi / 96.0;
            if (savedWindow is not null)
            {
                var w = Math.Max(savedWindow.WidthLogical, 800);
                var h = Math.Max(savedWindow.HeightLogical, 600);
                form.Size = new Size((int)Math.Round(w * scale), (int)Math.Round(h * scale));
            }
            else if (Math.Abs(scale - 1.0) > 0.01)
            {
                form.ClientSize = new Size((int)Math.Round(1280 * scale), (int)Math.Round(840 * scale));
            }
            if (savedWindow is not null)
            {
                var (x, y) = RestoreWindowPosition(
                    savedWindow.X, savedWindow.Y, form.Width, form.Height);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(x, y);
                Trace($"window restored to ({x},{y}) size={form.Width}x{form.Height}");
                if (savedWindow.IsMaximized)
                {
                    form.WindowState = FormWindowState.Maximized;
                    Trace("window restored to maximized state");
                }
            }
            var userDataFolder = Environment.GetEnvironmentVariable("DSH_WEBVIEW2_DATA");
            if (string.IsNullOrWhiteSpace(userDataFolder))
            {
                userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DshWeb", "WebView2");
            }
            try
            {
                await InitWebViewAsync(web, userDataFolder);
                web.CoreWebView2.Navigate(Target.Url);
                var navWarned = false;
                web.CoreWebView2.NavigationCompleted += (_, e) =>
                {
                    if (e.IsSuccess) WebViewManager.ResetCrashCount();
                    BootMonitor?.OnNavigationCompleted(); // ADR-023：页面层探针武装点
                    if (!e.IsSuccess && !navWarned)
                    {
                        navWarned = true;
                        ShowError(ErrorCodes.E2004,
                            $"页面加载失败。\n\n请确认 {Target.Url} 上运行的是 dsh 服务（端口可能被其他程序占用，或服务已异常退出）。\n\n统一日志：{UnifiedLogPath}");
                    }
                };
                WireBootHealthPageLayer();
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("0x800700B7", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error("webview2 user-data-dir locked by another instance: " + ex.Message,
                        ErrorCodes.E1006, new { hint = "another-dsh-launcher-running" });
                    ShowError(ErrorCodes.E1006,
                        "无法初始化 WebView2：WebView2 数据目录正被另一个 dsh-launcher 实例占用。\n\n"
                        + "请先关闭其他 dsh-launcher 窗口（或检查任务管理器中是否有其他 DshWeb.exe），再重新打开。");
                    form.Close();
                    return;
                }
                if (await TryInstallWebView2Async())
                {
                    try
                    {
                        await InitWebViewAsync(web, userDataFolder);
                        web.CoreWebView2.Navigate(Target.Url);
                        var navWarned = false;
                        web.CoreWebView2.NavigationCompleted += (_, e) =>
                        {
                            if (e.IsSuccess) WebViewManager.ResetCrashCount();
                            BootMonitor?.OnNavigationCompleted(); // ADR-023：页面层探针武装点
                            if (!e.IsSuccess && !navWarned)
                            {
                                navWarned = true;
                                ShowError(ErrorCodes.E2004,
                                    $"页面加载失败。\n\n请确认 {Target.Url} 上运行的是 dsh 服务（端口可能被其他程序占用，或服务已异常退出）。\n\n统一日志：{UnifiedLogPath}");
                            }
                        };
                        WireBootHealthPageLayer();
                        return;
                    }
                    catch
                    {
                        // Retry failed: fall through to E1006
                    }
                }
                ShowError(ErrorCodes.E1006,
                    "无法初始化 WebView2：\n" + ex.Message
                    + "\n\n请确认系统已安装 Microsoft Edge WebView2 Runtime（Windows 10/11 通常已自带）。");
                form.Close();
            }
        };

        ScheduleUpdateCheck(form);

        // ---- 任务一：插件崩溃安全模式接线 ----
        // WebViewManager 检测到插件不兼容的致命错误消息时广播 PluginCrashDetected。
        // 组合根接线：经 AskEnterSafeModeOnce（每会话仅询问一次，ADR-023 与页面层共用闸门）
        // → 弹模态询问用户 → 重启 dsh 服务进入安全模式（两级降级阶梯 L1/L2）。
        WebViewManager.PluginCrashDetected += errorMsg =>
        {
            try
            {
                form.BeginInvoke(() =>
                {
                    // 外部托管模式：尝试通过 URL 参数通知服务进入安全模式
                    if (ServerManagedExternally)
                    {
                        if (!AskEnterSafeModeOnce(form,
                            "检测到插件冲突导致启动失败（dsh 前端无法加载）。", errorMsg))
                        {
                            return;
                        }
                        try
                        {
                            // 在 URL 中添加 safe_mode 参数
                            var uri = new Uri(Target.Url);
                            var safeModeUrl = uri.Query.Length > 0
                                ? $"{Target.Url}&safe_mode=1"
                                : $"{Target.Url}?safe_mode=1";
                            Trace($"navigating to safe mode URL: {safeModeUrl}");
                            form.BeginInvoke(() =>
                            {
                                if (WebViewManager.MainWeb?.CoreWebView2 is not null)
                                {
                                    WebViewManager.MainWeb.CoreWebView2.Navigate(safeModeUrl);
                                }
                            });
                        }
                        catch (Exception ex)
                        {
                            Logger.Warn("safe mode URL navigation failed: " + ex.Message);
                        }
                        return;
                    }

                    // 壳托管模式：进入安全模式（隔离空 profile）并重启真实 dsh
                    if (!AskEnterSafeModeOnce(form,
                        "检测到插件冲突导致启动失败（dsh 前端无法加载）。",
                        "是否进入安全模式（禁用第三方插件，仅保留 dsh 核心功能）？\n（不会修改你的任何配置文件）"))
                    {
                        return;
                    }
                    Trace("user accepted safe mode; entering 2-tier isolated-profile safe mode");
                    RunSafeModeLadder(form);
                });
            }
            catch (Exception ex)
            {
                Logger.Warn("safe mode dialog failed: " + ex.Message);
            }
        };

        Application.Run(form);
        Trace("main loop exited");
    }

    /// <summary>
    /// 无头 UI 几何自测（GitHub CI 用）：建主窗 → 最大化 → 断言"窗口矩形 == 工作区"（0px 铺满，ADR-001）。
    /// 不依赖 dsh 服务 / Node / WebView2 内容，只验证自绘边框的 Win32 消息（WS_CAPTION 移除 + WM_GETMINMAXINFO）。
    /// 退出码：0=通过，1=几何不符，2=内部异常。结果同时写统一日志与 stdout（CI 抓取）。
    /// </summary>
    private static int RunUiSelftest()
    {
        Logger.Init(UnifiedLogPath);
        try
        {
            var form = new DshShellForm
            {
                Text = "dsh selftest",
                ClientSize = new Size(1280, 840),
                MinimumSize = new Size(800, 600),
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
            };
            form.TitleBar = new CustomTitleBar(form, ResolveDarkMode())
            {
                Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2,
                    (int)Math.Round(32 * form.DeviceDpi / 96f)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            form.Controls.Add(form.TitleBar);
            form.Show();
            form.Refresh(); // 强制重绘（替代 DoEvents，避免重入风险）
            form.WindowState = FormWindowState.Maximized;
            form.Refresh();

            var wa = Screen.FromHandle(form.Handle).WorkingArea;
            var b = form.Bounds;
            var ok = b.X == wa.X && b.Y == wa.Y && b.Width == wa.Width && b.Height == wa.Height;
            var msg = $"UI-SELFTEST pass={ok} bound=({b.X},{b.Y},{b.Width}x{b.Height}) workarea=({wa.X},{wa.Y},{wa.Width}x{wa.Height})";
            Logger.Info(msg);
            Console.WriteLine(msg);
            WriteSelftestResult(ok, msg);
            form.Close();
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            var msg = "UI-SELFTEST threw: " + ex.Message;
            Logger.Error(msg);
            Console.Error.WriteLine(msg);
            WriteSelftestResult(false, msg);
            return 2;
        }
    }

    /// <summary>
    /// --ui-probe 无服务窗口探针（Task 0，CI geo 探针用）：不拉 dsh 服务、不导航真实内容，
    /// 只开 DshShellForm（自绘标题栏 + WebView2 + F11 钩子），WebView2 导航 about:blank。
    /// 供 e2e 探针从外部做几何（最大化==工作区）/F11（SendInput 注入翻转）/标题栏（子控件存在、
    /// Visible、高≈32×DPI）/白屏（DSH_WEBVIEW2_READYSTATE 的 document.readyState）断言。
    /// 动机：e2e 隔离 dsh 服务在全新 DSH_HOME 起不来（dsh 生态 profile 初始化缺
    /// dsh-client-ui-plan），而 geo 探针验证的窗口行为本身不依赖服务内容——解耦后 CI 可稳定跑。
    /// 返回 0=正常关闭，2=异常。
    /// </summary>
    private static int RunUiProbe()
    {
        Logger.Init(UnifiedLogPath);
        try
        {
            var form = new DshShellForm
            {
                Text = "DeepSeek Harness", // 与真实主窗同名，供探针 FindWindow 定位
                ClientSize = new Size(1280, 840),
                MinimumSize = new Size(800, 600),
                FormBorderStyle = FormBorderStyle.None,
            };
            form.TitleBar = new CustomTitleBar(form, ResolveDarkMode())
            {
                Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2,
                    (int)Math.Round(32 * form.DeviceDpi / 96f)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            form.Controls.Add(form.TitleBar);
            // 与真实主窗对齐（见本文件建窗处的 HandleCreated 订阅）：启用 DWM NC 渲染后，
            // 最大化窗口才会向四周外扩 frame——WM_GETMINMAXINFO 的 frame 补偿（pos=work+frame,
            // size=work-2*frame）才成立。探针此前缺此行 → CI（Server runner）上 DWM 不外扩、
            // 补偿落空 → 最大化后四周留 8px 缝隙（e2e-geo G1/G10 回归根因）。
            form.HandleCreated += (_, _) => ApplyWindowShadow(form.Handle);

            var web = new WebView2
            {
                Bounds = new Rectangle(1, 1 + form.TitleBar.Height,
                    form.ClientSize.Width - 2, form.ClientSize.Height - form.TitleBar.Height - 2),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                // 与主窗一致：禁用 WinForms IME 状态管理（防 ImmSetOpenStatus 崩溃，见主窗注释）
                ImeMode = ImeMode.Disable,
            };
            form.Controls.Add(web);
            form.MainWebView2 = web;
            WebViewManager.MainWeb = web; // readyState 测试钩子按 ReferenceEquals(web, MainWeb) 门控，必须先设

            // F11 钩子（与真实路径一致）：仅主窗前台时切换并吞键。
            // 跨线程修复（Step2b）：缓存 hwnd 再进 lambda，避免销毁期 ObjectDisposedException。
            var probeHwnd = form.Handle;
            using var f11Hook = new F11LowLevelHook(form.ToggleFullscreen,
                () => F11LowLevelHook.GetForegroundWindow() == probeHwnd);
            Trace($"ui-probe: f11 hook installed hwnd=0x{probeHwnd.ToInt64():X}"); // 诊断：确认走 --ui-probe 分支

            form.Shown += async (_, _) =>
            {
                var userDataFolder = Environment.GetEnvironmentVariable("DSH_WEBVIEW2_DATA");
                if (string.IsNullOrWhiteSpace(userDataFolder))
                    userDataFolder = Path.Combine(Path.GetTempPath(), "dsh-ui-probe-wv2");
                try
                {
                    await InitWebViewAsync(web, userDataFolder);
                    web.CoreWebView2.Navigate("about:blank"); // 无需网络，readyState 钩子照常触发
                }
                catch (Exception ex)
                {
                    Logger.Error("ui-probe webview init failed: " + ex.Message);
                }
            };

            // TestHook（Task 2 维度三）：DSH_TEST_MODE=1 时启动 NamedPipe 几何控制服务。
            // 生产路径零接触（Enabled 恒 false 即不建 pipe 不开线程）；供 E2E 发 ToggleMaximize/
            // GetWindowRect/GetWorkArea 精确断言"最大化 0px 间隙"。
            using var hookCts = new CancellationTokenSource();
            Task? hookTask = null;
            if (DshWeb.Win32.UiTestHook.Enabled)
            {
                hookTask = Task.Run(() => DshWeb.Win32.UiTestHook.RunAsync(
                    form.Handle, hookCts.Token,
                    onShutdown: () => form.BeginInvoke(() => form.Close())));
                Trace($"ui-probe: test hook listening ({DshWeb.Win32.UiTestHook.PipeName(Environment.ProcessId)})");
            }

            Application.Run(form);
            hookCts.Cancel();
            if (hookTask is not null)
            {
                try { hookTask.Wait(TimeSpan.FromSeconds(1)); } catch { /* 退出清理不阻断 */ }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("ui-probe threw: " + ex.Message);
            return 2;
        }
    }

    /// <summary>
    /// 把自测结果落盘（CI 可靠取回通道——GUI 子系统应用的 stdout/退出码在 pwsh 里未必可靠回传）。
    /// 写入当前目录 ui-selftest-result.txt；可用 DSH_TEST_RESULT 覆盖路径。
    /// [ADR-024] 实现迁至 SelftestReporter（组合根零文件写入原语）。
    /// </summary>
    private static void WriteSelftestResult(bool pass, string detail)
        => Managers.SelftestReporter.Write(pass, detail);

    private enum PendingUpdate { None, Dsh, LauncherSecurity }
    private static PendingUpdate _pendingUpdate;
    private static string _pendingLatest = "", _pendingLocal = "";
    private static Form? _pendingForm;
    /// <summary>本次会话已下载过（MarkPending）的 dsh 版本（v0.4.0 T3：下载成功后又弹"有更新"去重）。</summary>
    private static readonly HashSet<string> _sessionStagedVersions = new(StringComparer.OrdinalIgnoreCase);

    // ---- 任务五：后台更新构建状态（防误关 + UI 反馈） ----
    /// <summary>后台构建是否正在进行（FormClosing 读取以决定是否拦截）。</summary>
    private static volatile bool _isBuildInProgress;
    /// <summary>构建取消令牌源（用户强制关闭时取消 npm build 进程）。</summary>
    private static CancellationTokenSource? _buildCts;

    /// <summary>测试钩子：DSH_NO_UI=1 时所有用户弹窗（ShowError/状态窗/确认框）改为仅写日志，
    /// 供自动化/负向测试在无窗口环境运行（不打扰真实桌面）。仅测试使用，文档注明。</summary>
    private static bool NoUiMode =>
        string.Equals(Environment.GetEnvironmentVariable("DSH_NO_UI"), "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>e2e/探针模式（Task 0 模态硬化）：--ui-probe 参数或 DSH_E2E=1 时，ShowError 一律
    /// 只写日志 + stdout，不弹模态框——根治 E2004 模态窗在探针/e2e 路径挂起问题（探针 WaitMain
    /// 等窗口 30s + 模态弹窗不关 = 看似卡死）。仅测试路径，正常 GUI 不受影响。</summary>
    private static bool E2EMode =>
        string.Equals(Environment.GetEnvironmentVariable("DSH_E2E"), "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>统一出错弹窗（v0.3.0 显式差错控制）：正文含 [错误码]，错误一并写入结构化日志；
    /// 消息文本可 Ctrl+C 复制，便于粘贴到 Issue。
    /// 质量治理 P1-7：可指定日志级别——"用户取消/拒绝"类非故障（如 E1002）传 Info，
    /// 避免污染错误码汇总；log 参数供"显式 Logger 已写过"的场景去重（如 E4001 双写）。
    /// DSH_NO_UI=1（测试钩子）时不弹窗，仅写日志并返回 OK（进程可自然退出，无残留窗口）。</summary>
    private static DialogResult ShowError(string code, string detail,
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.Warning,
        Logger.Level level = Logger.Level.Error, bool log = true)
    {
        if (log)
        {
            if (level == Logger.Level.Error) Logger.Error(detail, code);
            else if (level == Logger.Level.Warn) Logger.Warn(detail, code);
            else Logger.Info(detail, code);
        }
        if (NoUiMode) return DialogResult.OK; // 测试钩子：只记录不弹窗
        // e2e/探针模式（模态硬化）：--ui-probe / DSH_E2E=1 时只写日志 + stdout，不弹模态。
        // 否则探针路径（如 geo 启壳后服务误判不可用）会弹 E2004 模态窗，与探针 WaitMain 30s
        // 等待叠加造成"看似卡死"。正常 GUI 不受影响。
        if (E2EMode)
        {
            try { Console.WriteLine($"[{code}] {ErrorCodes.Describe(code)}\n{detail}"); } catch { /* stdout 不可用时忽略 */ }
            return DialogResult.OK;
        }
        return MessageBox.Show($"[{code}] {ErrorCodes.Describe(code)}\n\n{detail}", "DeepSeek Harness", buttons, icon);
    }

    /// <summary>v0.3.1 P2：WebView2 缺失兜底安装——实现迁至 WebRuntimeInstaller（ADR-024：
    /// 下载/进程原语离开组合根文件；本转发保留调用点与语义）。</summary>
    private static Task<bool> TryInstallWebView2Async() => Managers.WebRuntimeInstaller.TryInstallWebView2Async();

    /// <summary>
    /// 统一启动流水线（v0.4.2 收尾）：由 LauncherApp（状态机 + Manager）驱动，替代旧
    /// RunStartupPipelineAsync。组合根职责：装配 Manager 副作用委托（维护 IO/拉起/就绪探针/
    /// 僵尸清理）并桥接 SplashForm 的 IProgress&lt;Message&gt; 与内联确认面板。LauncherApp 自身
    /// 不引用 Program（无循环依赖），Headless 可测（tests/DshShell.Tests/Managers/LauncherApp*Tests.cs）。
    /// </summary>
    private static async Task<SplashForm.Outcome> RunLauncherAppPipelineAsync(
        IProgress<SplashForm.Message> progress,
        Func<string, string, Task<bool>> confirm,
        CancellationToken ct)
    {
        var app = CreateLauncherApp(confirm);
        // IProgress<string>（LauncherApp 进度）→ IProgress<Message>（SplashForm 状态标签）。
        // 任务二："[warn] " 前缀 → 黄色告警（日志 fallback 提示），其余为普通进度。
        // 任务一："[apply] " 前缀 → 更新安装进度（Splash 据此禁用取消按钮 + 更新 Label）。
        IProgress<string> textProgress = new Progress<string>(t =>
        {
            if (t.StartsWith("[apply] ", StringComparison.Ordinal))
                progress.Report(new SplashForm.Message("apply", t.Substring("[apply] ".Length), IsApplyingUpdate: true));
            else if (t.StartsWith("[warn] ", StringComparison.Ordinal))
                progress.Report(new SplashForm.Message("probe", t.Substring("[warn] ".Length), IsError: false, IsWarn: true));
            else
                progress.Report(new SplashForm.Message("probe", t));
        });
        // 任务一：把 Splash 进度桥接进 RunBackgroundMaintenance → ApplyPending → npm 实时日志。
        // 应用更新阶段用 "[apply] " 前缀标记（Splash 更新 Label 并禁用取消按钮）。
        _updateApplyProgress = s => textProgress.Report("[apply] " + s);
        // 首次运行预装进度：普通文本前缀（保持取消可用，不进入 IsApplyingUpdate）。
        _firstRunProvisionProgress = s => textProgress.Report(s);
        try
        {
            var ok = await app.RunStartupAsync(textProgress, ct);
            return new SplashForm.Outcome(
                ok,
                app.WaitResult,
                app.ServiceStartedByShell,
                UnifiedLogPath,
                ok ? null : app.LastErrorCode,
                ok ? null : app.LastErrorDetail);
        }
        finally
        {
            // 首装失败详情镜像（HandleStartupFailure 的 [E1012] 展示数据源）
            _firstRunProvisionError = app.FirstRunProvisionError ?? _firstRunProvisionError;
            // apply 前身份版本镜像（npm 回滚降级目标）
            if (SessionUpdates is Managers.DshUpdateManager engine
                && engine.PreApplyIdentityVersion is not null)
                _preApplyIdentityVersion = engine.PreApplyIdentityVersion;
            _updateApplyProgress = null; // 本次会话结束，清理桥接（防跨会话污染）
            _firstRunProvisionProgress = null; // 同上：首次运行预装进度桥接
        }
    }

    /// <summary>装配 LauncherApp：注入真实副作用（与 Program 静态状态解耦，组合根接线）。
    /// 【ADR-024】服务拉起不再经 wscript/vbs 委托——LauncherApp 直接调 IServiceManager.Start(identity)；
    /// 更新编排经 IDshUpdateManager 引擎实例（本会话共享，UI 回调在此接线）。</summary>
    private static LauncherApp CreateLauncherApp(Func<string, string, Task<bool>> confirm)
    {
        var updates = new Managers.DshUpdateManager(DataDir, Target.Port)
        {
            // UI 收口回调：更新失败弹窗 / 首装进度滚动 / PromptRestart 版本登记
            NotifyApplyFailed = NotifyUpdateApplyFailed,
            ProvisionProgress = s => _firstRunProvisionProgress?.Invoke(s),
            DeferRestartPrompt = v => _applyRestartPendingVersion = v,
        };
        // [update-guard] apply 成功 → 武装回滚闸门（新版启动自检失败时自动回滚）
        updates.UpdateApplied += v => _updateRollbackArmedVersion = v;
        SessionUpdates = updates;

        var service = new ServiceManager();
        return new LauncherApp(
            runtime: new RuntimeManager(confirmDownload: () =>
                // 自动化环境不打断（原 EnsureNodeForStartupAsync 顶部语义）
                NoUiMode
                    ? Task.FromResult(false)
                    : confirm(
                        "dsh-launcher - 需要 Node.js",
                        "检测到 Node.js 问题（dsh 服务运行必需）。\n\n" +
                        (RuntimeResolver.NodeMissingReason() == "too-old"
                            ? "系统 Node.js 版本过低或不可用（需要 18 或更高版本）。\n"
                            : "未检测到 Node.js。\n") +
                        "是否自动下载便携版 Node.js 到用户目录？\n" +
                        "（约 30MB，仅用于本启动器，不改动系统环境；版本采用 LTS 固定版）")),
            service: service,
            staleCleanup: _ => SweepStaleServicePid(),
            updates: updates,
            serviceLogPath: UnifiedLogPath)
        {
            BackgroundMaintenance = ct => RunBackgroundMaintenance(updates, ct, _updateApplyProgress), // 阶段 0 可取消；进度桥接到 Splash
            SweepStaleAndApplyUpdate = () =>
            {
                // v0.4.0：pending 应用已上移到阶段 0（BackgroundMaintenance）——npm install -g
                // 可能耗时 30-60s，原在"正在启动 dsh 服务…"阶段会让用户误以为卡死且取消无效。
                if (!ShellLogic.ServiceReadiness.PortOpen("127.0.0.1", Target.Port))
                {
                    SweepStaleServicePid();   // 僵尸清扫：上次崩溃记录过、已不在监听的进程
                }
            },
            ReadinessProbe = ct => Task.Run(() =>
                service.PollReadiness(ct, Target.Port, Target.Url, UnifiedLogPath, E2EMode), ct),
        };
    }

    /// <summary>本会话的更新引擎引用（CreateLauncherApp 装配；供主窗流程复用同一实例，
    /// 保证 PreApplyIdentityVersion/回滚武装等会话状态一致）。Headless/测试可为 null。</summary>
    internal static IDshUpdateManager? SessionUpdates { get; private set; }

    /// <summary>阶段 0 后台维护 IO（原 Main 同步项：日志轮转/数据迁移/自启落地等，由 LauncherApp 后台驱动）。
    /// v0.4.0：延迟更新应用也在此执行——属耗时 IO（30-60s），放阶段 0 后用户看到的
    /// "正在启动 dsh 服务…"即真实拉起，不再有"卡住"的误导。
    /// 【ADR-024】pending 决策/应用编排委托给更新引擎（updates.HandlePendingAtStartup /
    /// CleanupStagingCache）；本方法只做组合根侧的无 UI 维护项。
    /// <paramref name="ct"/> 传入引擎 → npm 安装可被取消（Splash 取消立即生效）。
    /// <paramref name="progress"/>：Splash 桥接，"正在应用更新 (vX)…"与 npm 实时日志滚动上报。</summary>
    private static void RunBackgroundMaintenance(
        Managers.DshUpdateManager updates, CancellationToken ct, Action<string>? progress = null)
    {
        if (!ShellLogic.ServiceReadiness.PortOpen("127.0.0.1", Target.Port)) Logger.RotateIfNeeded(); // 仅无活服务占用时轮转
        Logger.WarnIfOversized(); // P2：常驻超长日志（>50MB 且 >24h）告警
        WindowStateStore.Init(DataDir);
        StagedUpdate.Init(DataDir);
        UpdateDataGuard.Init(DataDir, DshHomeDir); // [update-guard] apply 前快照 / 自检失败回滚
        updates.HandlePendingAtStartup(ct, progress,
            p => ShellLogic.ServiceReadiness.PortOpen("127.0.0.1", p)); // v0.4.0 T2：按决策处理，端口开着不再静默跳过
        updates.CleanupStagingCache(); // 下载缓存管理：清理 DataDir\staging 中 >7 天的过期包
        MigrateLegacyData();           // 旧版 %LOCALAPPDATA% 数据迁移到 DSH_HOME
        CleanupProgramDataResidue();   // 清理卸载后 ProgramData 空目录残留
        EnsureAutoStartRequested();    // 自启落地：MSI 机器级意图标志 → 当前用户 HKCU Run
    }

    /// <summary>本次会话"稍后"标记：PromptRestart 拒绝后同会话不再弹（T2 规则 2）。</summary>
    private static bool _applyRestartDeferred;

    /// <summary>等待主窗就绪后一次性弹"立即重启应用"提示的版本（T2 规则 2，由主窗 Load 消费）。</summary>
    private static string? _applyRestartPendingVersion;

    /// <summary>[update-guard] 已应用且未确认健康的更新版本（回滚武装标记；null=未武装）。
    /// 武装来源：① 本会话 apply 成功；② 跨会话发现"当前身份版本存在未确认快照"。
    /// 启动自检失败时一次性消费（无论成败），防"失败→重试→再失败"循环。</summary>
    private static string? _updateRollbackArmedVersion;

    /// <summary>[update-guard] apply 开始前记录的运行身份版本——npm 全局路径回滚时的降级目标
    /// （SelfContained 路径回滚靠隔离运行时目录，不需要它）。</summary>
    private static string? _preApplyIdentityVersion;

    /// <summary>更新安装进度桥接（任务一）：RunLauncherAppPipelineAsync 装配时指向 Splash 进度转发，
    /// RunBackgroundMaintenance → ApplyPendingDshUpdate → npm 实时日志逐行上报。会话结束清空防污染。</summary>
    private static Action<string>? _updateApplyProgress;

    /// <summary>首次运行预装（SelfContained 运行时）进度桥接：指向 Splash 进度转发（普通文本，可取消）。
    /// 仅"本地未安装 dsh"的首次运行触发；会话结束清空防污染。</summary>
    private static Action<string>? _firstRunProvisionProgress;

    /// <summary>首装全局安装失败详情（引擎 FirstRunProvisionError 的会话镜像；E1012 展示用）。</summary>
    internal static string? _firstRunProvisionError;

    /// <summary>
    /// 读取当前 dsh 版本：委托 DshDiscovery 统一发现（与 UpdateChecker 同源，ADR-024）。
    /// 会话级缓存：避免重复探测。
    /// </summary>
    private static string? _cachedGlobalDshVersion = "unset";

    private static string? ReadGlobalDshVersion()
    {
        if (_cachedGlobalDshVersion != "unset") return _cachedGlobalDshVersion;
        _cachedGlobalDshVersion = DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime().Version;
        return _cachedGlobalDshVersion;
    }

    /// <summary>
    /// 按当前身份拉起 dsh 服务（ADR-024：组合根唯一启动入口，委托 IServiceManager.Start）。
    /// 安全模式激活时以隔离 profile 身份（--profile .dsh-safe）重启。
    /// 返回 false = 拉起失败（E2001）。旧 wscript/start-dsh.vbs 中间层已彻底移除——
    /// 启动命令只信 Identity.NodeExePath × Identity.DshEntryJsPath。
    /// </summary>
    private static bool StartDshServiceViaIdentity()
    {
        var identity = DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime();
        if (SafeMode.IsActive)
            identity = identity.WithProfile(SafeProfile.SafeProfileDir);
        var ok = ShellService.Start(identity, Target.Port, UnifiedLogPath);
        if (ok) Trace(identity.IsSafeProfile
            ? "service start via identity (SAFE profile)"
            : "service start via identity");
        return ok;
    }

    /// <summary>组合根共享的服务 Manager 实例（无状态；安全模式/回滚/重启询问等主窗流程复用）。</summary>
    private static readonly ServiceManager ShellService = new();

    /// <summary>
    /// 安全模式双重观测（ADR-022 Task 3）：readiness + 插件崩溃签名消失。
    /// - 阶段一：等 TCP+HTTP ready（最长 60s）；
    /// - 阶段二：观察窗口（5s）内不再收到新的插件崩溃签名（WebViewManager.LastPluginCrashUtc 不再前进）。
    /// 任一失败 ⇒ 明确返回 false（调用方据此响亮报错，绝不假成功）。
    /// </summary>
    private static bool WaitSafeModeVerified()
    {
        // 阶段一：readiness（快探经 ServiceLifecycleOps——HTTP 原语已迁出组合根文件）
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && !Managers.ServiceLifecycleOps.IsReady(Target.Port, Target.Url))
            Task.Delay(500).Wait();
        if (!Managers.ServiceLifecycleOps.IsReady(Target.Port, Target.Url))
        {
            Logger.Error("safe mode verification: service not ready within 60s", ErrorCodes.E1011);
            return false;
        }

        // 阶段二：崩溃签名消失（观察窗口 5s 内无新的插件崩溃消息）
        var baseline = WebViewManager.LastPluginCrashUtc;
        var observeDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < observeDeadline)
        {
            if (WebViewManager.LastPluginCrashUtc > baseline)
            {
                Logger.Error("safe mode verification: plugin crash signature still present", ErrorCodes.E1011);
                return false; // 签名仍在 → 安全模式失败
            }
            Task.Delay(300).Wait();
        }
        Trace("SAFEMODE: verification OK (ready + crash signature absent)");
        return true;
    }

    /// <summary>
    /// 在后台线程执行一次给定梯级的安全模式启动（ADR-022 分级策略）。
    /// 步骤：构建该梯级 .dsh-safe → Activate(落盘) → 停旧服务 → 以 --profile 重启 →
    /// 双重观测（readiness + 崩溃签名消失）。全部通过才返回 true。
    /// </summary>
    private static bool TryStartSafeMode(DshWeb.Windows.DshShellForm form, DshWeb.Domain.SafeProfileTier tier)
    {
        try
        {
            Trace($"SAFEMODE(bg): building tier {tier}");
            if (!SafeProfile.Build(tier))
            {
                Logger.Error($"safe mode disabled: failed to build tier {tier} profile", ErrorCodes.E1010);
                return false;
            }
            SafeMode.Activate(tier);
            Trace($"SAFEMODE: activated tier {tier}, safe profile={SafeProfile.SafeProfileDir}");

            // 关键：安全模式用隔离 profile 启动。设置 DSH_PROFILE 环境变量，使
            // start-dsh.vbs 回退路径也走 `--profile .dsh-safe`（根级，替代 web 子命令）。
            // SelfContained 分支由 SafeMode.IsActive 已切换为 --profile。两路一致。
            Environment.SetEnvironmentVariable("DSH_PROFILE", DshWeb.Domain.SafeProfileBuilder.SafeProfileName);

            // ADR-023：壳主动重启服务 = 判定挂起窗口（进程退出/HTTP 断链/日志错误都不判 failed）
            BootMonitor?.Suspend();

            Trace("SAFEMODE(bg): stopping service");
            StopShellService();
            Trace("SAFEMODE(bg): StopShellService returned");
            var restartOk = StartDshServiceViaIdentity();
            Trace($"SAFEMODE(bg): identity-driven start returned {restartOk}");
            if (!restartOk)
            {
                BootMonitor?.Stop(); // 重启失败且不再有服务可监视
                SafeMode.Deactivate();
                return false;
            }

            var safeOk = WaitSafeModeVerified();
            Trace($"SAFEMODE(bg): verification={safeOk}");
            if (!safeOk)
            {
                // 安全模式未真正生效：退出安全状态、恢复窗口原样（不谎报成功）
                BootMonitor?.Stop(); // 两级阶梯都失败 → 不再有受监视的健康服务
                SafeMode.Deactivate();
                Environment.SetEnvironmentVariable("DSH_PROFILE", null);
                try
                {
                    form.BeginInvoke(() =>
                    {
                        try
                        {
                            if (form.TitleBar is not null) form.TitleBar._titleText = "DeepSeek Harness";
                            form.Text = "DeepSeek Harness";
                            form.TitleBar?.Invalidate();
                        }
                        catch { }
                    });
                }
                catch { }
                return false;
            }

            // —— 只有真正通过双重观测（readiness + 崩溃签名消失）才标注安全模式横幅 ——
            // ADR-023：恢复监控（清终态回 Pending、attach 新进程；页面层随下方 Reload 的
            // NavigationCompleted 重新武装）——安全模式下的服务同样受崩溃检测保护。
            BootMonitor?.ResumeAfterRestart(ResolveServicePidBestEffort());
            try
            {
                form.BeginInvoke(() =>
                {
                    try
                    {
                        if (form.TitleBar is not null) form.TitleBar._titleText = "DeepSeek Harness（安全模式）";
                        form.Text = "DeepSeek Harness（安全模式）";
                        form.TitleBar?.Invalidate();
                        // 刷新页面（此时服务已按 --profile .dsh-safe 正常提供核心 UI）
                        if (WebViewManager.MainWeb?.CoreWebView2 is not null)
                        {
                            try { WebViewManager.MainWeb.CoreWebView2.Reload(); } catch { }
                        }
                    }
                    catch { }
                });
            }
            catch { }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("safe mode start exception: " + ex.Message);
            BootMonitor?.Stop(); // 重启流程异常中断：服务状态未知，停止监控防误报
            SafeMode.Deactivate();
            return false;
        }
    }

    // ==================== 启动健康融合监控（ADR-023）组合根接线 ====================

    /// <summary>
    /// 服务就绪后创建并启动 BootHealthMonitor：进程层（RecordServicePid/认领的 PID attach）、
    /// 日志层（统一日志增量扫描）、HTTP 层（Target.Url 回死探测）。页面层由主窗
    /// NavigationCompleted 后的 <see cref="WireBootHealthPageLayer"/> 武装。
    /// 幂等：已有实例时跳过（Headless/重复调用安全）。
    /// </summary>
    private static void StartBootHealthMonitor()
    {
        if (BootMonitor is not null) return;
        try
        {
            var pid = ResolveServicePidBestEffort();
            var monitor = new DshWeb.Lifecycle.BootHealthMonitor(
                BootSignatures,
                UnifiedLogPath,
                Target.Url,
                pageProbe: script => WebViewManager.ExecuteScriptOnMainWebAsync(script),
                // 统一 [boot-monitor] 前缀：所有层轨迹在统一日志中可被 grep/场景断言识别
                trace: message => Trace("[boot-monitor] " + message));
            monitor.Failed += HandleBootHealthFailed;
            // [update-guard] 好符号确认健康 → 快照落"已确认"、解除回滚武装
            monitor.HealthyDetected += HandleUpdateConfirmedHealthy;
            // 吸收态证据追加（如进程死后 HTTP 层补充）→ 重写 safe-mode-state 融合视图（S24 验收）
            monitor.VerdictUpdated += v =>
            {
                try
                {
                    PersistBootFailureEvidence(v);
                    Logger.Info("boot-monitor: failure evidence re-persisted (fusion view updated)");
                }
                catch (Exception ex) { Logger.Warn("boot-monitor: re-persist failed: " + ex.Message); }
            };
            BootMonitor = monitor;
            // [update-guard] 跨会话观察期武装：含 dsh 身份发现（可能 spawn node --version 探测）
            // 与注册表/文件读取，移入后台线程——不再阻塞 Splash 关闭后的建窗路径（死窗期修复，
            // 见 EnsureServiceAndRuntime 注释）。武装产物仅被后续健康失败裁决读取，时序足够。
            _ = Task.Run(ArmUpdateRollbackGuardFromPersistedState);
            monitor.Start();
            if (pid > 0) monitor.AttachProcess(pid);
            Logger.Info($"[boot-monitor] started url={Target.Url} log={UnifiedLogPath} servicePid={(pid > 0 ? pid.ToString() : "n/a")}");
        }
        catch (Exception ex)
        {
            // 监控自身装配失败绝不阻断启动（降级为无监控运行）
            Logger.Warn("[boot-monitor] failed to start: " + ex.Message);
            BootMonitor = null;
        }
    }

    /// <summary>尽力解析受监视服务 PID：壳记录的 _servicePid 优先，否则按端口反查（外部托管场景）。</summary>
    private static int ResolveServicePidBestEffort()
    {
        if (_servicePid > 0) return _servicePid;
        try
        {
            var pid = ShellLogic.ProcessManagement.GetProcessIdByPort(Target.Port);
            return ShellLogic.ProcessManagement.IsLikelyDshService(pid) ? pid : 0;
        }
        catch { return 0; }
    }

    /// <summary>
    /// 页面层武装 + CDP 精确层订阅（主窗 WebView2 初始化完成后调用一次）。
    /// 探针执行经 WebViewManager 封送 UI 线程；CDP 只采集不判定。
    /// </summary>
    private static void WireBootHealthPageLayer()
    {
        try
        {
            WebViewManager.CdpExceptionCaptured -= OnCdpExceptionCaptured; // 防重订阅
            WebViewManager.CdpExceptionCaptured += OnCdpExceptionCaptured;
        }
        catch (Exception ex)
        {
            Logger.Warn("[boot-monitor] cdp wiring failed (precise layer disabled): " + ex.Message);
        }
    }

    private static void OnCdpExceptionCaptured(string rawJson) => BootMonitor?.CollectCdpException(rawJson);

    /// <summary>
    /// failed 裁决处理（四层证据融合出口）：证据写 safe-mode.json + 静默导出诊断包，
    /// 然后经每会话一次闸门询问是否进入安全模式（两级降级阶梯 L1/L2）。
    /// 询问与阶梯封送到 UI 线程（Report 触发线程是后台轮询/进程事件线程）。
    /// </summary>
    private static void HandleBootHealthFailed(DshWeb.Lifecycle.BootVerdict verdict)
    {
        try
        {
            // 0) [update-guard/E4003] 回滚闸门：当前运行的是"已应用、未确认健康"的更新版本，
            //    启动自检失败极可能由新版自身或其数据迁移导致 → 不进安全模式/手动重启询问，
            //    直接自动回滚（还原共享数据 + 隔离新运行时）并用旧版重启服务。
            //    （2026-08-23 用户回归：rc.2 迁移 .credentials.yaml 后回退 rc.8 必炸。）
            if (_updateRollbackArmedVersion is not null &&
                ShellLogic.UpdateGuardPolicy.DecideBootFailure(_updateRollbackArmedVersion)
                    == ShellLogic.UpdateGuardPolicy.BootFailureAction.RollbackAndRestart)
            {
                HandleUpdateRollbackOnBootFailure(verdict);
                return;
            }

            // 1) 证据落盘：safe-mode-state.json 的 lastFailure 字段（原子写，崩溃/重启仍可查）
            PersistBootFailureEvidence(verdict);

            // 2) 诊断包：静默落 DataDir\diagnostics\（失败仅 Warn，不二次弹窗打扰）
            ExportBootDiagnostics();

            // 3) 分支询问（2026-08 用户回归：无插件也弹"插件不兼容/安全模式"——误导）：
            //    - 有插件相关证据（坏签名命中 / 本会话插件崩溃消息）→ 安全模式阶梯（原路径）；
            //    - 纯"好符号缺席"（无坏签名、无插件崩溃）→ 与插件无关，不弹安全模式
            //      （无第三方插件可禁，安全模式必然无效），改问"重启 dsh 服务"。
            var detail = string.Join("\n", verdict.Evidence.Select(e => $"· [{e.Layer}] {e.Summary}"));
            var form = GetMainFormForDialog();
            if (VerdictIndicatesPluginInvolvement(verdict))
            {
                var askBody = "是否进入安全模式（禁用第三方插件，仅保留 dsh 核心功能）？\n"
                    + "（不会修改你的任何配置文件）\n\n检测到的证据：\n" + detail;
                var headline = $"{ErrorCodes.Describe(verdict.ErrorCode)}（[{verdict.ErrorCode}]）";
                if (form is not null && form.IsHandleCreated)
                    form.BeginInvoke(() => AskAndMaybeEnterSafeMode(form, headline, askBody));
                else
                    AskAndMaybeEnterSafeMode(form, headline, askBody);
            }
            else
            {
                var headline = $"dsh 页面启动自检未通过（[{ErrorCodes.E2008}]）：页面已加载但启动确认符号持续缺席。\n"
                    + "未检测到插件相关证据，多与 dsh 版本兼容性或服务状态有关。\n\n检测到的证据：\n" + detail;
                if (form is not null && form.IsHandleCreated)
                    form.BeginInvoke(() => AskRestartDshServiceAfterBootFailure(form, headline));
                else
                    Logger.Warn("[boot-monitor] no-plugin boot failure without main window; restart ask skipped (logged only)");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("[boot-monitor] failure handling threw: " + ex.Message);
        }
    }

    /// <summary>
    /// [update-guard] 跨会话武装：当前身份版本存在"未确认健康"的快照（上次会话应用更新后
    /// 没走到好符号就结束了）→ 本次启动仍在回滚观察期，启动自检失败同样自动回滚。
    /// </summary>
    private static void ArmUpdateRollbackGuardFromPersistedState()
    {
        try
        {
            if (_updateRollbackArmedVersion is not null) return; // 本会话已武装（apply 成功），不覆盖
            var identityVersion = DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime().Version;
            var unconfirmed = UpdateDataGuard.UnconfirmedSnapshotVersion(identityVersion);
            if (unconfirmed is null) return;
            _updateRollbackArmedVersion = unconfirmed;
            Logger.Info($"[update-guard] rollback guard armed (cross-session) for v{unconfirmed}");
        }
        catch (Exception ex)
        {
            // 发现链失败属预期内操作失败：降级为不武装，走既有恢复流程
            Logger.Warn("[update-guard] persisted-arm check failed: " + ex.Message);
        }
    }

    /// <summary>[update-guard] 好符号确认：新版本真实跑起来了 → 快照标记健康、解除武装。</summary>
    private static void HandleUpdateConfirmedHealthy()
    {
        var version = _updateRollbackArmedVersion;
        if (version is null) return;
        _updateRollbackArmedVersion = null; // 先 disarm 再持久化：确认动作自身失败最多回到观察期，不会误回滚
        try
        {
            UpdateDataGuard.MarkConfirmedHealthy(version);
            Logger.Info($"[update-guard] update v{version} confirmed healthy; rollback guard disarmed");
        }
        catch (Exception ex)
        {
            Logger.Warn("[update-guard] healthy-confirm failed: " + ex.Message);
        }
    }

    /// <summary>
    /// [update-guard/E4003] 启动自检失败 × 回滚闸门已武装：停服 → 还原更新前共享数据 →
    /// 隔离新运行时（SelfContained 路径）/ 尽力降级全局包（npm 路径）→ 以旧版重启服务并恢复监控。
    /// 武装标记一次性消费（无论成败），绝不重复回滚。复用安全模式重启的观测语义：
    /// Suspend（壳主动重启窗口不判死）→ 停启 → 就绪等待 → ResumeAfterRestart。
    /// </summary>
    private static void HandleUpdateRollbackOnBootFailure(DshWeb.Lifecycle.BootVerdict verdict)
    {
        var version = _updateRollbackArmedVersion!;
        _updateRollbackArmedVersion = null; // 一次性消费（防循环）
        try
        {
            // 证据先行：失败裁决与诊断包照常落盘，回滚原因可追责
            PersistBootFailureEvidence(verdict);
            ExportBootDiagnostics();
            Logger.Error(
                $"[update-rollback] update v{version} failed boot self-check [{verdict.ErrorCode}]; " +
                "rolling back pre-update data and quarantining runtime",
                ErrorCodes.E4003, new { version, code = verdict.ErrorCode });

            BootMonitor?.Suspend();
            Trace("[update-rollback] stopping service before rollback");
            StopShellService();

            var result = UpdateDataGuard.RollbackAfterFailedUpdate(
                version, $"boot self-check failed [{verdict.ErrorCode}]");

            // npm 全局路径：没有运行时目录可隔离 → 尽力把全局包降回 apply 前版本
            // （--prefer-offline 离线优先；失败透明上报，不阻塞旧版重启——旧版可能本就是全局包）
            if (result.QuarantinedRuntimeDir is null
                && !string.IsNullOrWhiteSpace(_preApplyIdentityVersion)
                && !string.Equals(_preApplyIdentityVersion, version, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    Logger.Info($"[update-rollback] best-effort npm downgrade to v{_preApplyIdentityVersion}");
                    var sources = Managers.ProcessRunner.GetNpmRegistrySources();
                    Managers.ProcessRunner.TryNpmOverRegistries(
                        sources,
                        srcIdx => Managers.ProcessRunner.RunNpmCommand(
                            $"install -g \"@deepseek-ai/dsh@{_preApplyIdentityVersion}\" --prefer-offline --no-audit --no-fund"
                            + sources[srcIdx],
                            out _, default, null),
                        "rollback-downgrade", out _);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[update-rollback] npm downgrade threw (continuing): {ex.Message}");
                }
            }

            Trace("[update-rollback] restarting service on previous version");
            var startOk = StartDshServiceViaIdentity();
            if (!startOk)
            {
                BootMonitor?.Stop();
                ShowError(ErrorCodes.E4003,
                    $"dsh 更新 v{version} 启动自检失败，数据已自动回滚，但服务重启失败，请查看统一日志后重新打开 dsh-launcher。",
                    log: false);
                return;
            }

            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline && !Managers.ServiceLifecycleOps.IsReady(Target.Port, Target.Url))
                Thread.Sleep(500);
            if (!Managers.ServiceLifecycleOps.IsReady(Target.Port, Target.Url))
            {
                Logger.Error("[update-rollback] service not ready within 90s after rollback", ErrorCodes.E2004);
                BootMonitor?.Stop(); // 服务状态未知，停止监控防误报
                ShowError(ErrorCodes.E4003,
                    $"dsh 更新 v{version} 启动自检失败，数据已自动回滚，但旧版服务 90 秒内未就绪，请查看统一日志。",
                    log: false);
                return;
            }

            BootMonitor?.ResumeAfterRestart(ResolveServicePidBestEffort());
            try
            {
                var form = GetMainFormForDialog();
                if (form is not null && form.IsHandleCreated)
                    form.BeginInvoke(() =>
                    {
                        try { WebViewManager.MainWeb?.CoreWebView2?.Reload(); } catch { /* 页面已关 */ }
                    });
            }
            catch { /* 窗体已关闭 */ }

            ShowError(ErrorCodes.E4003,
                $"dsh 更新 v{version} 启动自检失败，已自动回滚。\n\n" +
                $"· 已还原更新前的配置数据（{(result.DataRestored ? string.Join("、", result.RestoredFiles) : "无需还原/快照缺失")}）\n" +
                $"· 新版本运行时{(result.QuarantinedRuntimeDir is null ? "无（npm 路径已尽力降级）" : "已隔离出启动发现链")}\n" +
                "· 服务正以旧版本重新启动。\n\n" +
                "如需排查新版问题，请携带统一日志与 update-guard\\rollback-history.jsonl 反馈。",
                log: false);
            Logger.Info($"[update-rollback] completed for v{version}; service restored on previous version");
        }
        catch (Exception ex)
        {
            Logger.Warn("[update-rollback] threw: " + ex.Message);
            BootMonitor?.Stop();
        }
    }

    /// <summary>
    /// 裁决是否携带插件相关证据（决定 E2008 弹"安全模式"还是"重启服务"）：
    /// 页面层坏签名命中（detail 以 dom[/err[ 开头，见 BootGuard.EvaluatePageProbe）或
    /// 本会话收到过插件崩溃 WebMessage（WebViewManager.PluginCrashDetected 路径）。
    /// CDP 异常不参与判定：核心页面自身异常也会被采集，不足以归因插件（保守防误导）。
    /// </summary>
    private static bool VerdictIndicatesPluginInvolvement(DshWeb.Lifecycle.BootVerdict verdict)
    {
        foreach (var e in verdict.Evidence)
        {
            if (e.Layer == DshWeb.Lifecycle.BootLayer.Page
                && e.Detail is not null
                && (e.Detail.StartsWith("dom[", StringComparison.Ordinal)
                    || e.Detail.StartsWith("err[", StringComparison.Ordinal)))
                return true;
        }
        return WebViewManager.LastPluginCrashUtc != default;
    }

    /// <summary>
    /// 无插件证据的启动自检失败恢复动作：询问后后台重启 dsh 服务，就绪后刷新页面。
    /// 复用安全模式重启的观测语义：Suspend（壳主动重启窗口不判死）→ 停启 → 就绪等待 →
    /// ResumeAfterRestart（重挂进程层；页面层随 Reload 的 NavigationCompleted 重新武装）。
    /// </summary>
    private static void AskRestartDshServiceAfterBootFailure(DshShellForm form, string headline)
    {
        try
        {
            try { form.Activate(); } catch { }
            var r = MessageBox.Show(form,
                headline + "\n\n是否立即重启 dsh 服务？（不会修改你的任何配置文件）",
                "DeepSeek Harness - 启动异常",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            Trace($"restart-service ask answered: {(r == DialogResult.Yes ? "yes" : "no")}");
            if (r != DialogResult.Yes) return;
            _ = Task.Run(async () =>
            {
                try
                {
                    BootMonitor?.Suspend();
                    Trace("restart-service(bg): stopping service");
                    StopShellService();
                    var startOk = StartDshServiceViaIdentity();
                    Trace($"restart-service(bg): identity-driven start returned {startOk}");
                    if (!startOk)
                    {
                        BootMonitor?.Stop();
                        try { form.BeginInvoke(() => ShowError(ErrorCodes.E2001,
                            "dsh 服务重启失败，请查看统一日志后重新打开 dsh-launcher。", log: false)); } catch { }
                        return;
                    }
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < deadline && !Managers.ServiceLifecycleOps.IsReady(Target.Port, Target.Url))
                        await Task.Delay(500);
                    if (!Managers.ServiceLifecycleOps.IsReady(Target.Port, Target.Url))
                    {
                        Logger.Error("restart-service: service not ready within 60s after reboot", ErrorCodes.E2004);
                        BootMonitor?.Stop(); // 服务状态未知，停止监控防误报
                        try { form.BeginInvoke(() => ShowError(ErrorCodes.E2004,
                            "dsh 服务重启后 60 秒内未就绪，请查看统一日志。", log: false)); } catch { }
                        return;
                    }
                    BootMonitor?.ResumeAfterRestart(ResolveServicePidBestEffort());
                    try
                    {
                        form.BeginInvoke(() =>
                        {
                            try { WebViewManager.MainWeb?.CoreWebView2?.Reload(); } catch { /* 页面已关 */ }
                        });
                    }
                    catch { /* 窗体已关闭 */ }
                }
                catch (Exception ex)
                {
                    Logger.Warn("restart-service(bg) threw: " + ex.Message);
                    BootMonitor?.Stop();
                }
            });
        }
        catch (Exception ex)
        {
            Logger.Warn("restart-service ask failed: " + ex.Message);
        }
    }

    private static void AskAndMaybeEnterSafeMode(DshShellForm? form, string headline, string askBody)
    {
        try
        {
            if (!AskEnterSafeModeOnce(form, headline, askBody))
            {
                // 2026-08 修复点4：用户对安全模式询问答 "no"。若此前处于粘滞激活态
                // （上一会话进入安全模式、本会话尚未退出），则明确解粘滞——否则后续所有会话会
                // 静默以 --profile .dsh-safe 降级启动。恢复正常启动路径。
                if (SafeMode.IsActive)
                {
                    SafeMode.Deactivate();
                    Environment.SetEnvironmentVariable("DSH_PROFILE", null);
                    Trace("SAFEMODE: user declined; deactivated sticky safe-mode and cleared DSH_PROFILE");
                }
                return;
            }
            if (form is null || form.IsDisposed)
            {
                // 无主窗可承载安全模式重启（罕见：失败早于建窗）——响亮记录，不静默假成功
                Logger.Warn("[boot-monitor] safe mode accepted but no main window available; ladder skipped");
                return;
            }
            RunSafeModeLadder(form);
        }
        catch (Exception ex)
        {
            Logger.Warn("[boot-monitor] safe-mode ask/ladder threw: " + ex.Message);
        }
    }

    private static DshShellForm? GetMainFormForDialog()
        => Application.OpenForms.OfType<DshShellForm>().FirstOrDefault();

    /// <summary>证据持久化到 safe-mode.json（best-effort：失败只 Warn，不影响询问流程）。</summary>
    private static void PersistBootFailureEvidence(DshWeb.Lifecycle.BootVerdict verdict)
    {
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(
                DshWeb.Lifecycle.BootHealthMonitor.BuildFailureRecord(verdict)));
            SafeMode.RecordFailure(doc.RootElement);
            Trace("boot-monitor: failure evidence persisted to safe-mode-state.json");
        }
        catch (Exception ex)
        {
            Logger.Warn("[boot-monitor] evidence persistence failed: " + ex.Message);
        }
    }

    /// <summary>静默导出诊断包到 DSH_HOME\dsh-launcher\diagnostics\（含 safe-mode.json 证据）。</summary>
    private static void ExportBootDiagnostics()
    {
        _ = Task.Run(() =>
        {
            var zip = DiagnoseExport.ExportTo(
                Path.Combine(DataDir, "diagnostics",
                    $"boot-failure-{DateTime.UtcNow:yyyyMMdd-HHmmss}.zip"),
                DshHomeDir, UnifiedLogPath, minLevel: Logger.Level.Warn);
            Trace(zip is null
                ? "boot-monitor: diagnostic export failed (see E5001)"
                : $"boot-monitor: diagnostic package written: {zip}");
        });
    }

    /// <summary>
    /// 每会话仅一次的安全模式询问闸门。顺序：监控状态机闸门（BootMonitor.TryConsumeSessionPrompt）
    /// → 测试钩子（DSH_TEST_SAFE_MODE_ANSWER=yes|no，沙盒自动化用）/ 无头模式仅记日志 → 真实弹窗。
    /// 返回 true = 用户（或钩子）同意进入安全模式。
    /// </summary>
    private static bool AskEnterSafeModeOnce(DshShellForm? form, string headline, string body)
    {
        // 闸门一：状态机内的会话级一次性闸门（与页面层/插件消息路径共用）
        var gate = BootMonitor?.TryConsumeSessionPrompt() ?? true;
        if (!gate)
        {
            Trace("safe-mode ask suppressed: already asked this session");
            return false;
        }

        // 闸门二：测试钩子 / 无头环境（不打断自动化、不留挂起弹窗）
        var hookAnswer = Environment.GetEnvironmentVariable("DSH_TEST_SAFE_MODE_ANSWER");
        if (!string.IsNullOrWhiteSpace(hookAnswer))
        {
            var yes = string.Equals(hookAnswer, "yes", StringComparison.OrdinalIgnoreCase);
            Logger.Info($"[boot-monitor] safe-mode ask auto-answered={hookAnswer.ToLowerInvariant()} (test hook)");
            return yes;
        }
        if (NoUiMode)
        {
            Logger.Info("[boot-monitor] safe-mode ask suppressed (no-ui mode)");
            return false;
        }

        var result = MessageBox.Show(
            form,
            headline + "\n\n" + body,
            "DeepSeek Harness - 启动异常",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        Trace($"safe-mode ask answered: {(result == DialogResult.Yes ? "yes" : "no")}");
        return result == DialogResult.Yes;
    }

    /// <summary>
    /// 执行安全模式两级降级阶梯（ADR-022）：Tier1 保留全部 @deepseek-ai 核心 → 失败降 Tier2 最小核心；
    /// 每一级以物理证据（readiness + 崩溃签名消失）把关。后台线程执行（进程终止/同步等待不卡 UI）。
    /// </summary>
    private static void RunSafeModeLadder(DshShellForm form)
    {
        _ = Task.Run(() =>
        {
            try
            {
                var ok = TryStartSafeMode(form, DshWeb.Domain.SafeProfileTier.Tier1KeepDeepSeekCore); // L1
                if (!ok)
                {
                    Trace("SAFEMODE(bg): Tier1(L1) failed, falling back to Tier2 (minimal core)");
                    ok = TryStartSafeMode(form, DshWeb.Domain.SafeProfileTier.Tier2Minimal); // L2
                }
                if (!ok)
                {
                    // 两级都失败 → 响亮报错（含证据），绝不宣称成功
                    Trace("SAFEMODE(bg): all safe-mode tiers failed");
                    SafeMode.Deactivate();
                    form.BeginInvoke(() => MessageBox.Show(form,
                        "安全模式启动失败：两级隔离 profile 均未通过启动验证（" +
                        ErrorCodes.E1011 + "）。\n请查看统一日志，或手动卸载问题插件后以正常模式启动。",
                        "DeepSeek Harness - 安全模式失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("safe mode restart failed: " + ex.Message);
                SafeMode.Deactivate();
            }
        });
    }

    /// <summary>启动失败/取消的统一处理（v0.4.1 从 Main 内联块提取，逻辑与原 v0.3.x 一致）。</summary>
    private static void HandleStartupFailure(SplashForm.Outcome outcome)
    {
        var logPath = outcome.LogPath;
        // v0.3.0：启动失败时清理"本次拉起但未就绪"的半启动服务（避免残留占端口）
        if (outcome.WaitResult is "logerror" or "timeout" && outcome.ServiceStartedByShell
            && ShellLogic.ServiceReadiness.PortOpen("127.0.0.1", Target.Port))
        {
            var pid = ShellLogic.ProcessManagement.GetProcessIdByPort(Target.Port);
            if (pid > 0)
            {
                Logger.Warn("service failed to become ready; cleaning up", ErrorCodes.E2005, new { pid });
                if (KillProcess(pid)) ClearServicePidFile(); // P2-10：杀不干净则保留 pid 文件
            }
            else
            {
                ClearServicePidFile();
            }
        }
        // P0-1（质量治理）：用户取消 ≠ 放弃服务——后台下载/启动可能仍在进行（与取消文案一致），
        // 但服务必须可被下次启动接管：已监听则记录 PID（此前无 pid 文件 → TryAdoptOrphanService
        // 永远无法认领，服务成为永久无主孤儿，占住端口无人管理）。
        else if (outcome.WaitResult == "canceled" && outcome.ServiceStartedByShell
            && ShellLogic.ServiceReadiness.PortOpen("127.0.0.1", Target.Port))
        {
            RecordServicePid();
            Trace("canceled: service left running; pid recorded for next-start adoption");
        }

        // Node 缺失/下载失败：错误码随 outcome 直达，无需再读日志
        if (outcome.ErrorCode is not null)
        {
            // [静默失败收口] 首装全局安装失败时 StartService=false 走通用 E2001 文案
            // （"缺少 start-dsh.vbs"）会误导——按 StartupFailurePolicy 改用真实根因 E1012 展示。
            var mapped = ShellLogic.StartupFailurePolicy.MapFirstRunInstallFailure(
                outcome.ErrorCode, _firstRunProvisionError);
            if (mapped is not null)
            {
                ShowError(mapped.Value.Code, mapped.Value.Detail, level: Logger.Level.Error);
                return;
            }
            ShowError(outcome.ErrorCode, outcome.ErrorDetail ?? "启动失败。",
                level: outcome.ErrorCode == ErrorCodes.E1002 ? Logger.Level.Info : Logger.Level.Error);
            return;
        }

        var waitResult = outcome.WaitResult ?? "timeout";
        var tail = ShellLogic.ReadLogTail(logPath, 12);
        var tailText = tail.Count == 0 ? "（日志为空或不可读）" : string.Join("\n", tail.Select(l => "  " + l));
        var body = waitResult switch
        {
            "canceled" => "已取消启动。若服务仍在后台下载/启动，可稍后重新打开 dsh-launcher。",
            "logerror" => "启动过程报错（可能是下载失败、权限或环境问题）。\n\n日志尾部：\n" + tailText,
            _ => "启动超时：可能是首次下载 dsh 组件较慢（可稍后重试），也可能是网络/代理问题。\n\n日志尾部：\n" + tailText
                + "\n\n完整日志：" + logPath,
        };
        var code = waitResult switch
        {
            "logerror" => ErrorCodes.E2003,
            "timeout" => ErrorCodes.E2002,
            "canceled" => ErrorCodes.E2006, // P0-1：取消不是内部错误（此前误归 E9001）
            _ => ErrorCodes.E9001,
        };
        // 质量治理 P1-7：用户主动取消不是错误——按 Info 记录，避免污染错误码汇总
        ShowError(code, "dsh 服务未能就绪。\n\n" + body,
            level: waitResult == "canceled" ? Logger.Level.Info : Logger.Level.Error);
    }

    /// <summary>v0.3.0 主窗口位置/大小持久化（多显示器记忆）：位置与尺寸存 96dpi 逻辑值（跨 DPI 恢复时按当前 DPI 缩放）。
    /// v0.3.1 修复：Normal 状态必须用 Bounds——WinForms 的 RestoreBounds 只在窗口
    /// 最小化/最大化时更新（Normal 时恒为初始字段值 (-1,-1,初始尺寸)），此前用
    /// RestoreBounds 导致位置记忆从未生效（每次重启回默认位置/大小）。
    /// v0.3.3 新增：保存 IsMaximized 标志，最大化后关闭、重启时恢复最大化状态。</summary>
    private static void SaveWindowState(Form form)
    {
        try
        {
            if (form.WindowState == FormWindowState.Minimized) return;
            // Normal → Bounds（当前真实边界）；最小化/最大化 → RestoreBounds（还原后的边界）
            var rb = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            if (rb.Width <= 0 || rb.Height <= 0) return;
            var scale = form.DeviceDpi / 96f;
            WindowStateStore.Save(new WindowStateStore.WindowState(
                rb.X, rb.Y,
                (int)Math.Round(rb.Width / scale),
                (int)Math.Round(rb.Height / scale),
                form.WindowState == FormWindowState.Maximized));
        }
        catch (Exception ex)
        {
            // 质量治理：窗口位置保存失败此前静默（用户配置的位置丢失无诊断入口）
            Logger.Warn("window state save failed; position memory unavailable this session", ctx: new { error = ex.Message });
        }
    }

    /// <summary>
    /// 启动后异步检查更新（仅启动时一次，避免频繁请求 GitHub/npm）：
    /// - dsh-launcher 自身：**普通更新不推送**，只有标记为**安全/重要更新**（Release
    ///   body 含 "SECURITY" 或 tag 含 "-sec"）才托盘气泡提示（点击打开 Releases 下载页）
    /// - dsh（@deepseek-ai/dsh）：有新版即提示（点击一键 npm 更新）
    /// 网络失败/无更新静默，不打扰用户；匿名限流影响可控。
    /// </summary>
    private static void ScheduleUpdateCheck(Form form)
    {
        _pendingForm = form;
        _ = Task.Run(async () =>
        {
            try
            {
                // 质量治理 P1-6：存在"已下载待应用"的 dsh 更新（pending-update.json 未被清除
                // = 服务健康跳过应用或应用失败）→ 气泡提示一次（不打断会话），重启后生效。
                // 不依赖网络，先于 GitHub/npm 检查执行。
                var pendingVersion = StagedUpdate.ReadPendingVersion();
                if (!string.IsNullOrWhiteSpace(pendingVersion))
                {
                    var v = pendingVersion;
                    form.BeginInvoke(() => NotifyPendingApply(v));
                }

                using var http = Managers.WebRuntimeInstaller.CreateHttpClient(TimeSpan.FromSeconds(15)); // P2-9：弱网放宽 8s→15s

                // 1) launcher 安全更新优先（安全修复比功能更新重要）。
                // 独立 try/catch：此步任何意外异常都不得中断后面的 dsh 检查（此前整段任务只有
                // 一个静默总 catch，一处抛出 → dsh 检查无声消失，日志零痕迹难排查）。
                try
                {
                    var lr = await UpdateChecker.FetchLatestLauncherReleaseAsync(http);
                    if (lr is not null && lr.IsSecurity
                        && UpdateChecker.CompareVersions(lr.Version, UpdateChecker.CurrentLauncherVersion) > 0)
                    {
                        form.BeginInvoke(() => NotifyPending(PendingUpdate.LauncherSecurity, lr.Version,
                            UpdateChecker.CurrentLauncherVersion ?? "?"));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("launcher security update check failed; continuing with dsh check",
                        ctx: new { error = ex.Message });
                }

                // 2) dsh 新版
                var latest = await UpdateChecker.FetchLatestDshVersionAsync(http);
                var local = UpdateChecker.ResolveLocalDshVersion();
                // 诊断留痕（v0.4.1）：无论命中与否都记录 latest/local。此前只在 latest 为空时
                // 留痕——"检测成功、准备弹气泡"路径完全静默，气泡一旦被系统吞掉（托盘不可见/
                // 专注助手收进通知中心），用户与日志双双无迹可查（rc6→rc7 无提示排查根因之一）。
                Trace($"dsh update check: latest={latest ?? "<null>"} local={local ?? "<null>"}");
                // [Fix] local 为 null 时仍然提示更新（用户卸载了全局 dsh 或首次安装）
                if (!string.IsNullOrWhiteSpace(latest)
                    && (string.IsNullOrWhiteSpace(local) || UpdateChecker.CompareVersions(latest, local) > 0))
                {
                    // v0.4.0 T3 去重：已下载待应用（pending）且 pending.Version >= 检测版本 → 不弹
                    // "有更新"（更新死循环根因 C：下载成功 → pending → 重开又弹）。气泡已由上方
                    // NotifyPendingApply 提示过一次。
                    if (!string.IsNullOrWhiteSpace(pendingVersion)
                        && UpdateChecker.CompareVersions(pendingVersion, latest) >= 0)
                    {
                        Trace($"dsh update {latest} already staged (pending={pendingVersion}); skip");
                        return;
                    }
                    // v0.4.0 T3：本次会话已下载过同版本 → 不再重复提示（下载成功后又弹）
                    if (_sessionStagedVersions.Contains(latest))
                    {
                        Trace($"dsh update {latest} already downloaded this session; skip");
                        return;
                    }
                    // v0.3.1：用户拒绝过的版本跳过（新版本 > 跳过版本时重新提示）
                    var skipped = StagedUpdate.ReadSkippedDshVersion();
                    if (skipped is not null && UpdateChecker.CompareVersions(latest, skipped) <= 0)
                    {
                        Trace($"dsh update {latest} skipped by user (skipped={skipped})");
                        return;
                    }
                    Trace($"dsh update {latest} available (local={local ?? "<null>"}); prompting tray balloon");
                    form.BeginInvoke(() => NotifyPending(PendingUpdate.Dsh, latest, local));
                }
            }
            catch (Exception ex)
            {
                // 质量治理：检测失败不再完全静默——至少留 Warn 痕迹（日志失败本身不影响启动）
                Logger.Warn("update check aborted unexpectedly", ctx: new { error = ex.Message });
            }
        });
    }

    /// <summary>下载完成但非"无害扩展名"（可能含可执行代码）时的提示：系统通知告知落盘位置，
    /// 不自动打开——防恶意页面触发下载后自动执行本地代码（S2 修复）。
    /// [v0.4.1] 从托盘气泡迁移到系统 Toast（不再依赖托盘图标）。</summary>
    private static void NotifyDownloadComplete(string filePath)
    {
        SystemToast.TryShow(_pendingForm, "下载完成",
            "文件已保存：\n" + filePath, TimeSpan.FromSeconds(8), onClick: null);
    }

    private static void NotifyPending(PendingUpdate type, string latest, string local)
    {
        _pendingUpdate = type;
        _pendingLatest = latest;
        _pendingLocal = local;
        var (title, body) = type == PendingUpdate.LauncherSecurity
            ? ("dsh-launcher 安全更新", $"检测到重要安全更新 {latest}（当前 {local}）。点击查看下载。\n如有严重漏洞请尽快更新。")
            : ("dsh 有新版本", $"检测到 dsh {latest}（当前 {local}）。点击此处在后台下载更新。");
        // [v0.4.1] 系统 Toast 替代托盘气泡：通知不再依赖托盘图标（此前非 tray 常驻模式下
        // TrayIcon 恒为 null，更新气泡被静默丢弃——rc6→rc7 无提示根因）。
        // 点击 → OnPendingBalloonClicked（SystemToast 内部编组回 UI 线程），语义与原 BalloonTipClicked 一致。
        var shown = SystemToast.TryShow(_pendingForm, title, body,
            TimeSpan.FromSeconds(25), // 驻留 25s，安全更新要让人看到
            () => OnPendingBalloonClicked(null, EventArgs.Empty));
        if (shown)
            Logger.Info($"update toast shown: {title} / {body.Replace("\n", " ")}");
        else
            Logger.Warn("update toast unavailable; user will not see this update notice");
    }

    /// <summary>质量治理 P1-6/P1-8："已下载待应用"更新的一次性气泡提示（无点击行为）。
    /// 触发条件：pending-update.json 存在（服务健康跳过应用，或应用失败保留）。
    /// v0.3.1 降噪：应用失败达到 MaxNotifyFailures 次后不再每次启动弹气泡
    /// （持续失败会重复打扰），降级为仅日志（手动 npm 命令提示保留在日志文案）。</summary>
    private static void NotifyPendingApply(string version)
    {
        try
        {
            var (_, failCount, _, _, _) = StagedUpdate.ReadPending();
            if (failCount >= StagedUpdate.MaxNotifyFailures)
            {
                Logger.Warn($"staged dsh update {version} kept failing to apply ({failCount} tries); " +
                    "suppressing balloon. Manual: npm install -g @deepseek-ai/dsh@" + version);
                return;
            }
            // [v0.4.1] 系统 Toast 替代托盘气泡（不依赖托盘图标；TryShow 绝不抛出）
            var shown = SystemToast.TryShow(_pendingForm, "dsh 更新待应用",
                $"dsh {version} 主程序已下载。下次重启启动器后自动安装（需联网解析依赖，预计 1-2 分钟）。",
                TimeSpan.FromSeconds(15), onClick: null);
            if (!shown)
                Logger.Warn("pending-apply toast unavailable", ctx: new { version });
        }
        catch (Exception ex)
        {
            Logger.Warn("pending-apply notice failed", ctx: new { error = ex.Message });
        }
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
            Managers.WebRuntimeInstaller.OpenExternally("https://github.com/Ruler4396/dsh-launcher/releases/latest");
        }
        _pendingUpdate = PendingUpdate.None;
    }

    /// <summary>点击气泡后：确认 → 后台下载 dsh 新版（npm pack，不碰运行中的环境）→
    /// 写 pending-update.json，下次启动时自动应用（延迟应用，v0.3.0，绝不打断当前会话）。
    /// v0.3.1：用户拒绝 → 持久化跳过该版本（下次启动不再提示，除非检测到更新的版本）。</summary>
    private static void PromptDshUpdate(Form form, string latest, string local)
    {
        // 带 owner 的 MessageBox 会居中于 owner 且置于其上层；调用前先 Activate 把主窗提到前台，
        // 避免"询问弹窗被其他窗口遮挡/不跳到前台"（v0.4.0 用户反馈）。
        try { form.Activate(); } catch { /* 窗体已关闭则忽略 */ }
        // [2026-08 用户反馈] 文案精简：只说"更新到哪个版本 + 是否静默安装"。
        var r = MessageBox.Show(
            form,
            $"检测到 dsh 新版本 {latest}（当前 {local}）。\n是否在后台静默下载，下次重启时自动安装？",
            "dsh 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes)
        {
            StagedUpdate.MarkSkippedDshVersion(latest); // 用户拒绝：跳过此版本，避免每次启动重复提示
            return;
        }
        _ = Task.Run(() => DownloadDshUpdateStaged(form, latest));
    }

    /// <summary>
    /// v0.4.0 T2 规则 2：服务在跑且待应用版本不一致时的一次性询问（主窗 Load 后调用）。
    /// [立即重启应用] = 停服务 → npm install -g → 拉起服务 → Reload 页面（版本即刻生效）；
    /// [稍后] = 本次会话不再提示（仅日志，服务保持当前版本）。
    /// 取消时**不** MarkSkipped（pending 仍在，下次启动继续按决策处理）。
    /// </summary>
    private static void PromptApplyRestart(Form form, string version)
    {
        try
        {
            form.Activate();
            var r = MessageBox.Show(
                form,
                $"已下载 dsh {version}，但 dsh 服务正在运行中。\n\n" +
                "是否立即重启服务以应用新版本？\n" +
                "（立即 = 停止服务 → 安装 → 自动重新拉起并刷新页面）",
                "dsh 更新待应用", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes)
            {
                _applyRestartDeferred = true; // 本次会话不再提示（仅日志）
                Logger.Info($"user deferred applying staged dsh update {version}");
                return;
            }
            _ = Task.Run(async () =>
            {
                StopShellService(); // 停当前服务（含接管/本次拉起的）
                // 重启即应用路径同样优先本地 tarball（不 npx 现场拉主包）；tarball 缺失回退线上
                var pending = StagedUpdate.ReadPending();
                var localTarball = StagedUpdate.LocateTarball(pending.Version, pending.Tarball);
                var installSpec = localTarball ?? $"@deepseek-ai/dsh@{version}";
                // 任务二一致性：--no-audit --no-fund；快源优先、失败沿源序列降级
                string applyErrorTail = "";
                var applySources = Managers.ProcessRunner.GetNpmRegistrySources();
                if (Managers.ProcessRunner.TryNpmOverRegistries(applySources, srcIdx => Managers.ProcessRunner.RunNpmCommand(
                        $"install -g \"{installSpec}\" --no-audit --no-fund" + applySources[srcIdx],
                        out applyErrorTail), "apply-restart", out _))
                {
                    StagedUpdate.ClearPending();
                    Logger.Info($"staged dsh update applied (restart): {version}");
                    StartDshServiceViaIdentity(); // 按身份直启新版本服务（ADR-024）
                    // 等待就绪后刷新页面（最长 60s）
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < deadline && !Managers.ServiceLifecycleOps.IsReady(Target.Port, Target.Url))
                        await Task.Delay(500);
                    try
                    {
                        form.BeginInvoke(() =>
                        {
                            if (WebViewManager.MainWeb?.CoreWebView2 is not null)
                            {
                                try { WebViewManager.MainWeb.CoreWebView2.Reload(); } catch { /* 页面已关 */ }
                            }
                        });
                    }
                    catch { /* 窗体已关闭 */ }
                }
                else
                {
                    Logger.Warn("staged dsh update apply (restart) failed: " + applyErrorTail,
                        ErrorCodes.E4002, new { version });
                    try { form.BeginInvoke(() => ShowError(ErrorCodes.E4002,
                        $"dsh {version} 更新安装失败。\n\n可稍后重试，或在命令行手动执行：\nnpm install -g @deepseek-ai/dsh@{version}",
                        log: false)); } catch { /* 窗体已关闭 */ }
                }
            });
        }
        catch { /* 弹窗失败：记日志不打断启动 */ }
    }

    /// <summary>
    /// 后台完整构建 SelfContained 运行时（重启零 npm 解析）。
    ///
    /// 流程：npm pack 下载 tarball → 完整构建到 staging/runtime-build-{version}/ → MarkPending。
    /// 构建允许慢（300+ 依赖，5-10 分钟），但完全异步不阻塞当前会话。
    /// pnpm 机会主义加速：检测到 pnpm 可用则用（24 秒 vs 10+ 分钟），绝不主动安装。
    /// </summary>
    private static void DownloadDshUpdateStaged(Form form, string latest)
    {
        var staging = Path.Combine(DataDir, "staging");
        var buildDir = Path.Combine(staging, $"runtime-build-{latest}");
        _buildCts = new CancellationTokenSource();
        _isBuildInProgress = true;
        _lastBuildUiText = null; // 新构建：重置 UI 合流状态（防上次构建的节流窗口吞掉首帧）
        _lastBuildUiPercent = int.MinValue;
        try
        {
            Directory.CreateDirectory(staging);
            // [2026-08-22 回归] 清场再构建：复用残留 buildDir 会让 pnpm 命中旧 lockfile
            // 秒级 no-op "成功"，把上次中断/失败的破损布局原样保留——bin 入口校验失败的
            // 根因之一（10:02/10:32 两次 4 秒假成功均因此）。每次必须全新安装。
            if (Directory.Exists(buildDir)) Managers.ProcessRunner.TryDeleteDir(buildDir);
            Directory.CreateDirectory(buildDir);
            // [2026-08-22 回归·竞态关闭] 旧 pending 若指向本 buildDir，清场即失效——
            // 不清除的话，下次启动"强制应用"会把半成品目录搬到 runtimes\<ver>（12:23:29
            // 现场事故），既产生坏目标又让后续应用撞"already exists"。
            var (_, stalePendVer, _, _, stalePendRuntime) = StagedUpdate.ReadPending();
            if (!string.IsNullOrWhiteSpace(stalePendRuntime) &&
                string.Equals(Path.GetFullPath(stalePendRuntime), Path.GetFullPath(buildDir), StringComparison.OrdinalIgnoreCase))
            {
                StagedUpdate.ClearPending();
                Logger.Info($"cleared stale pending '{stalePendVer}' pointing at buildDir being rebuilt");
            }

            // 立即显示初始进度（用户点击更新后第一时间看到反馈）
            UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Building, $"正在构建更新（v{latest}）...", 0f);
            // [2026-08 取证锚点] 此前从点击到 pnpm detection 之间日志真空，无法定位静默失败
            Logger.Info($"staged update flow started: v{latest}");

            // ---- 步骤 1：npm pack 下载 tarball（快源优先，失败沿序列降级） ----
            var tarballName = $"deepseek-ai-dsh-{latest}.tgz";
            var tarballPath = Path.Combine(buildDir, tarballName);
            string errorTail = "";
            var regSources = Managers.ProcessRunner.GetNpmRegistrySources();
            var ok = Managers.ProcessRunner.TryNpmOverRegistries(regSources, srcIdx => Managers.ProcessRunner.RunNpmCommand(
                $"pack @deepseek-ai/dsh@{latest} --pack-destination \"" + buildDir + "\""
                    + regSources[srcIdx],
                out errorTail), "download-tarball", out var packSourceIdx);
            // tarball 下载完成，进度更新由 pnpm 检测后统一处理
            if (!ok || !File.Exists(tarballPath))
            {
                Logger.Error("staged dsh update download failed: " + errorTail, ErrorCodes.E4001, new { latest });
                Managers.ProcessRunner.TryDeleteDir(buildDir);
                // [2026-08 回归修复] 失败必须有可见结论：红色终态驻留 + E4001 弹窗（此前仅弹窗，
                // 且部分场景弹窗被吞后用户只看到进度条消失）
                UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Failed,
                    ShellLogic.UpdateProgress.ComposeTerminalTitleText(success: false, latest, willRetry: false));
                try
                {
                    string reason = string.IsNullOrWhiteSpace(errorTail)
                        ? "底层执行引擎未能启动 Node.js 环境"
                        : errorTail;
                    string hint = ShellLogic.NpmHelpers.IsNpmNotFoundError(errorTail)
                        ? "未检测到 npm 环境，请确保已安装 Node.js 18+。"
                        : "可稍后重试，或手动执行：npm install -g @deepseek-ai/dsh@" + latest;
                    form.BeginInvoke(() => ShowError(ErrorCodes.E4001,
                        $"dsh {latest} 下载失败。\n\n原因：{reason}\n\n{hint}", log: false));
                }
                catch { }
                return;
            }
            Logger.Info($"dsh tarball downloaded: {tarballName}");

            // ---- 步骤 2：完整构建（pnpm ~10s / npm ~60s，10%→90%）----
            // 内核抽至 DshUpdateManager（RealOS 可测）；UI 时序经回调原样保留：
            // 初始脉冲态 → pnpm 真实百分比 →（pnpm 失败边界刷新脉冲）npm 降级。
            var buildTool = "npm";

            // 初始态：不确定进度（脉冲动画由 CustomTitleBar 的 marquee 定时器驱动，
            // 无需轮询线程反复 Invalidate——旧实现 100/500ms 线程是闪烁源之一）。
            UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Building, $"正在构建更新（v{latest}）...", 0f);

            // 进度回调：pnpm 真实百分比（packageId 自归一化）；文案不含相位后缀
            // （旧实现 resolving/linking 来回翻转是"文案闪烁"的直接来源）。
            Action<int>? progressCallback = percent =>
                UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Building,
                    $"已构建更新 {percent}%（v{latest}）", percent / 100f);

            var (buildOk, _) = DshUpdateManager.BuildRuntimeFromTarball(
                tarballPath, tarballName, buildDir, regSources, packSourceIdx,
                percentProgress: progressCallback,
                beforeNpmFallback: () => UpdateBuildStatus(
                    form, CustomTitleBar.BuildStatus.Building, $"正在构建更新（v{latest}）...", 0f));

            if (!buildOk)
            {
                Logger.Warn($"dsh runtime build failed; preserving tarball for next launch retry",
                    ErrorCodes.E4001, new { version = latest });
                HandleStagedBuildFailure(form, latest,
                    "底层包管理器（pnpm/npm）构建运行时失败，详见日志",
                    tarballPath, staging, tarballName);
                Managers.ProcessRunner.TryDeleteDir(buildDir);
                return;
            }

            // ---- 步骤 3：解析 bin 入口，校验构建完整性（90%→100%） ----
            UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Building, $"已构建更新 95%（v{latest}）", 0.95f);
            var dshPkg = Path.Combine(buildDir, "node_modules", "@deepseek-ai", "dsh", "package.json");
            if (!File.Exists(dshPkg))
            {
                Logger.Error($"build succeeded but dsh package.json missing: {dshPkg}", ErrorCodes.E4001);
                // [2026-08 回归修复] 此前静默 return：进度条消失、无任何失败提示
                HandleStagedBuildFailure(form, latest,
                    "构建产物不完整（缺少 @deepseek-ai/dsh 包清单）",
                    tarballPath, staging, tarballName);
                Managers.ProcessRunner.TryDeleteDir(buildDir);
                return;
            }

            var binEntry = DshUpdateManager.ResolveBuiltBinEntry(buildDir);

            if (binEntry is null)
            {
                Logger.Error($"build succeeded but bin entry not resolvable in {dshPkg}", ErrorCodes.E4001);
                // [2026-08 回归修复] 此前静默 return：进度条消失、无任何失败提示
                HandleStagedBuildFailure(form, latest,
                    "构建产物不完整（bin 入口无法解析，可能是 dsh 版本布局变更）",
                    tarballPath, staging, tarballName);
                Managers.ProcessRunner.TryDeleteDir(buildDir);
                return;
            }
            Logger.Info($"staged update validated: v{latest} bin={binEntry}");

            // ---- 步骤 4：写入 pending（含 runtimeDir） ----
            StagedUpdate.MarkPending(latest, tarballName, prefetched: true, runtimeDir: buildDir);
            _sessionStagedVersions.Add(latest);

            var balloon = $"dsh {latest} 已在后台构建完成。下次重启启动器时将自动切换（秒级）。";
            // [v0.4.1] 系统 Toast 替代托盘气泡（TryShow 内部自行编组线程，绝不抛出）；
            // [2026-08 回归修复] 记录 Toast 是否成功——失败时标题栏驻留是唯一可见反馈。
            var toastShown = SystemToast.TryShow(form, "dsh 更新已就绪", balloon, TimeSpan.FromSeconds(8), onClick: null);
            Logger.Info($"update success notification: toast={toastShown}; title bar dwell={BuildTerminalDwellMs}ms");
            Logger.Info($"dsh runtime build complete: {latest}",
                ctx: new { tool = buildTool, bin = binEntry, buildDir });

            // 任务五：构建完成 UI 反馈（Ready 终态驻留 ~12s，此前一帧都不可见）
            UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Ready,
                ShellLogic.UpdateProgress.ComposeTerminalTitleText(success: true, latest));
        }
        catch (Exception ex)
        {
            Logger.Error("staged dsh update build error: " + ex.Message, ErrorCodes.E4001);
            Managers.ProcessRunner.TryDeleteDir(buildDir);
            // [2026-08 回归修复] 异常路径同样给终态（tarball 可见性未知 → 不承诺自动重试）
            try
            {
                UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Failed,
                    ShellLogic.UpdateProgress.ComposeTerminalTitleText(success: false, latest, willRetry: false));
            }
            catch { /* 窗体已关闭 */ }
            try { form.BeginInvoke(() => ShowError(ErrorCodes.E4001, ex.Message, log: false)); } catch { }
        }
        finally
        {
            // 任务五：重置构建占用状态。
            // [2026-08 回归修复] 不再无条件把标题栏清回 Idle——终态（Ready/Failed）由
            // UpdateBuildStatus 的驻留定时器保活 12s 后自行清理；每个退出路径都必须已设置终态。
            _isBuildInProgress = false;
            _buildCts?.Dispose();
            _buildCts = null;
        }
    }

    /// <summary>
    /// 暂存更新构建失败的统一收口（npm 失败 / 包清单缺失 / bin 入口缺失三处共用）：
    /// ① 保住 tarball 到 staging 根供下次启动免下载重试 + MarkPending(prefetched:false)；
    /// ② 标题栏 Failed 红色终态驻留 ~12s（ComposeTerminalTitleText 契约文案）；
    /// ③ Toast 尽力通知（结果记日志）；④ E4001 错误弹窗给出原因与后续动作。
    /// [2026-08 用户回归：更新结束无成功/失败提示]
    /// </summary>
    private static void HandleStagedBuildFailure(Form form, string latest, string userReason,
        string? tarballPath, string stagingDir, string? tarballName)
    {
        var preserved = false;
        try
        {
            if (tarballPath is not null && tarballName is not null)
                preserved = StagedUpdate.PreserveTarballForRetry(tarballPath, stagingDir, tarballName);
            if (preserved)
            {
                StagedUpdate.MarkPending(latest, tarballName!, prefetched: false);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"staged update failure handling could not preserve retry state: {ex.Message}");
        }

        UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Failed,
            ShellLogic.UpdateProgress.ComposeTerminalTitleText(success: false, latest, willRetry: preserved));

        try
        {
            var toastShown = SystemToast.TryShow(form, "dsh 更新构建失败",
                $"dsh {latest} 后台构建失败。{(preserved ? "已保留下载，下次启动启动器时将自动重试。" : "可重新点击更新重试。")}",
                TimeSpan.FromSeconds(8), onClick: null);
            Logger.Info($"update failure notification: toast={toastShown}, preserved={preserved}");
        }
        catch { /* Toast 失败不阻断 */ }

        try
        {
            form.BeginInvoke(() => ShowError(ErrorCodes.E4001,
                $"dsh {latest} 更新构建失败。\n\n原因：{userReason}\n\n{(preserved ? "已保留下载内容，下次重启启动器时将自动重试。" : "可稍后重新点击更新重试。")}",
                log: false));
        }
        catch { /* 窗体已关闭 */ }
    }

    /// <summary>任务五：更新标题栏构建状态（UI 反馈）。
    /// 线程安全：可从后台构建线程调用，自动 Invoke 到 UI 线程。
    /// <paramref name="percent"/> 进度百分比（0.0 - 1.0），0 表示未知进度。
    /// [2026-08 回归修复] UI 合流节流：pnpm ndjson 每秒数百行回调，旧实现每行都
    /// BeginInvoke+Invalidate 整个标题栏（重绘风暴=闪烁源）。现在 150ms 节流窗口内
    /// 且视觉状态（文本+整数百分比）未变化时直接丢弃。
    /// [2026-08 回归修复 #2] 终态驻留：Ready/Failed 设置后由驻留定时器保活约 12s 再清回
    /// Idle——此前 finally 无条件立即清态 + OnPaint 只画 Building，成功/失败结论一帧不可见，
    /// 是"进度条出现一下就消失、无结果提示"的直接原因。每个退出路径必须设置终态。</summary>
    private static string? _lastBuildUiText;
    private static int _lastBuildUiPercent = int.MinValue;
    private static long _lastBuildUiApplyTicks;
    private static System.Windows.Forms.Timer? _buildStatusDwellTimer;

    /// <summary>终态在标题栏的驻留时长：足够阅读结论，又不至于永远占着标题栏。</summary>
    private const int BuildTerminalDwellMs = 12000;

    /// <summary>取消未到期的终态驻留定时器（UI 线程调用）。</summary>
    private static void CancelBuildStatusDwell()
    {
        _buildStatusDwellTimer?.Stop();
        _buildStatusDwellTimer?.Dispose();
        _buildStatusDwellTimer = null;
    }

    /// <summary>驻留到期：把标题栏从终态清回 Idle（窗体已关则跳过）。</summary>
    private static void ClearBuildStatusToIdle(DshShellForm form)
    {
        try
        {
            if (form.IsDisposed || form.TitleBar is null) return;
            form.TitleBar._buildStatus = CustomTitleBar.BuildStatus.Idle;
            form.TitleBar._buildProgressText = "";
            form.TitleBar._buildProgressPercent = 0f;
            form.TitleBar.Invalidate();
        }
        catch { /* 窗体已关闭 */ }
    }

    private static void UpdateBuildStatus(Form form, CustomTitleBar.BuildStatus status, string text, float percent = 0f)
    {
        try
        {
            if (form.IsDisposed) return;
            var pctInt = (int)Math.Round(percent * 100f);
            var isTerminal = status is CustomTitleBar.BuildStatus.Ready or CustomTitleBar.BuildStatus.Failed;
            // 快路径合流（Invoke 前）：视觉状态未变且处于节流窗口 → 不排队、不重绘；终态直通
            if (!isTerminal
                && text == _lastBuildUiText
                && pctInt == _lastBuildUiPercent
                && Environment.TickCount64 - _lastBuildUiApplyTicks < 150
                && form.InvokeRequired)
            {
                return;
            }
            if (form.InvokeRequired)
            {
                form.BeginInvoke(() => UpdateBuildStatus(form, status, text, percent));
                return;
            }
            _lastBuildUiText = text;
            _lastBuildUiPercent = pctInt;
            _lastBuildUiApplyTicks = Environment.TickCount64;
            if (form is DshShellForm sf && sf.TitleBar is not null)
            {
                sf.TitleBar._buildStatus = status;
                sf.TitleBar._buildProgressText = text;
                sf.TitleBar._buildProgressPercent = percent;
                sf.TitleBar.Invalidate();
            }
            // 终态驻留定时器（UI 线程）：新 Building 取消旧驻留；新终态覆盖旧驻留重新计时
            CancelBuildStatusDwell();
            if (isTerminal)
            {
                _buildStatusDwellTimer = new System.Windows.Forms.Timer { Interval = BuildTerminalDwellMs };
                _buildStatusDwellTimer.Tick += (_, _) =>
                {
                    CancelBuildStatusDwell();
                    if (form is DshShellForm sf2) ClearBuildStatusToIdle(sf2);
                };
                _buildStatusDwellTimer.Start();
            }
        }
        catch { /* UI 更新失败不影响构建 */ }
    }

    /// <summary>读取服务停留模式（实现见 AppEnvironment.ReadLifetimeMode；ADR-024 迁移转发）。</summary>
    private static ShellLogic.ServiceLifetime ReadLifetimeMode() => Managers.AppEnvironment.ReadLifetimeMode(DshHomeDir);

    // ---- 服务进程生命周期（实现迁至 Managers/ServiceLifecycleOps.cs；ADR-024。
    //      组合根保留薄转发：调用点语义不变，业务原语不再出现在本文件） ----

    /// <summary>记录本次壳拉起的服务 PID（服务就绪后调用），供下次启动接管残留服务。</summary>
    private static void RecordServicePid()
    {
        Managers.ServiceLifecycleOps.RecordServicePid(DataDir, Target.Port);
        _servicePid = ShellLogic.ProcessManagement.GetProcessIdByPort(Target.Port); // 内存缓存同步（原语义）
    }

    /// <summary>
    /// 端口已开但本实例没拉起服务时调用：若监听进程正是壳上次拉起的残留服务
    /// （PID 记录在账本），则校验健康后接管管理；坏状态/旧版本进程不得带病运行——
    /// 监听但 HTTP 不通 → 清理（只动我们记录的 PID）。实现见 ServiceLifecycleOps。
    /// </summary>
    private static void TryAdoptOrphanService()
    {
        var adopted = Managers.ServiceLifecycleOps.TryAdoptOrphanService(DataDir, Target.Port, Target.Url);
        if (adopted > 0)
        {
            _serviceStartedByShell = true;
            _servicePid = adopted;
            Trace($"adopted orphan service pid={adopted}");
        }
    }

    /// <summary>端口未开时的遗留清扫（拉起服务前调用）：只清理我们记录过的僵尸 PID。实现见 ServiceLifecycleOps。</summary>
    private static void SweepStaleServicePid() => Managers.ServiceLifecycleOps.SweepStaleServicePid(DataDir, Target.Port);

    private static void ClearServicePidFile() => Managers.ServiceLifecycleOps.ClearPidFile(DataDir, Target.Port);

    /// <summary>停止指定 PID：薄委托 ServiceLifecycleOps → ShellLogic.ProcessManagement.KillServiceProcess
    /// （身份校验 + 端口归属双重防误杀；等待 taskkill 退出、强杀确认、失败重试一次、失败上报 E2005）。</summary>
    private static bool KillProcess(int pid) => Managers.ServiceLifecycleOps.KillProcess(Target.Port, pid);

    /// <summary>
    /// 停止"壳本次会话拉起的"dsh 服务：优先内存缓存的 PID，端口反查兜底；温和 taskkill 未停
    /// 则强制 /f /T；端口释放限时探测 + 占用者认领兜底；杀不干净保留 pid 文件由下次启动清扫。
    /// 实现见 ServiceLifecycleOps.StopService（ADR-024 迁移，逻辑逐行保持原语义）。
    /// </summary>
    private static void StopShellService()
        => Managers.ServiceLifecycleOps.StopService(DataDir, Target.Port, _servicePid);

    // ---- 关窗/退出异步化（2026-08 用户回归：点关闭后 UI 线程同步停服务卡 1.5s+） ----

    /// <summary>退出编排进行中标志（组合根会话状态，风格同 _isBuildInProgress）：
    /// 幂等闸门 + FormClosing 收尾放行。</summary>
    private static bool _shutdownInitiated;

    /// <summary>
    /// 关窗与托盘退出共用的异步退出编排：
    /// ① 窗口即刻隐藏（视觉上"已关闭"，<100ms）；② SaveWindowState / 主题注销 / BootMonitor.Stop
    /// 在 UI 线程快速完成；③ StopShellService（netstat→TcpTable 反查 + taskkill 等待）转后台线程；
    /// ④ 清理完成后回 UI 线程 Application.Exit；⑤ 3s 看门狗兜底：taskkill 挂死也强制退出，
    /// 绝不让用户对着已消失的窗口等。幂等（重复调用安全）。
    /// </summary>
    private static void BeginShutdownAsync(DshShellForm? mainForm)
    {
        if (_shutdownInitiated) return;
        _shutdownInitiated = true;
        try
        {
            if (mainForm is { IsDisposed: false })
            {
                try { SaveWindowState(mainForm); }
                catch (Exception ex) { Logger.Warn("shutdown: save window state failed: " + ex.Message); }
                try { mainForm.Hide(); } catch { /* 已在关闭中 */ }
            }
            try { WindowManager.Instance.ReleaseThemeWatcher(); } catch { }
            // [2026-08 回归修复] 关窗时终止终态驻留定时器（防 Tick 触达已释放窗体）
            try { CancelBuildStatusDwell(); } catch { }
            BootMonitor?.Stop(); // ADR-023：壳主动收尾，监控停止（此后进程退出不再判 failed）
            var shouldStopService = ShellLogic.LifecycleDecisions.ShouldStopServiceOnClose(
                ReadLifetimeMode(), ServerManagedExternally, _serviceStartedByShell);
            // 看门狗：消息泵仍在跑（窗口只是隐藏），Timer 到点强制结束进程
            var watchdog = new System.Windows.Forms.Timer { Interval = 3000 };
            watchdog.Tick += (_, _) =>
            {
                watchdog.Stop();
                Logger.Warn("shutdown: cleanup exceeded 3s watchdog; forcing exit");
                Environment.Exit(0);
            };
            watchdog.Start();
            _ = Task.Run(() =>
            {
                try
                {
                    if (shouldStopService) StopShellService();
                }
                catch (Exception ex)
                {
                    Logger.Warn("shutdown: StopShellService threw: " + ex.Message);
                }
                finally
                {
                    Trace("shutdown: cleanup done; exiting message loop");
                    try
                    {
                        if (mainForm is { IsDisposed: false }) mainForm.BeginInvoke(new Action(Application.Exit));
                        else Environment.Exit(0);
                    }
                    catch
                    {
                        Environment.Exit(0); // 句柄已失效/封送失败：直接结束，不留僵尸进程
                    }
                }
            });
        }
        catch (Exception ex)
        {
            // 编排自身失败也要保证能退出（透明留痕后强制结束）
            Logger.Error("shutdown orchestration failed: " + ex.Message, ErrorCodes.E9001);
            Environment.Exit(0);
        }
    }

    /// <summary>
    /// 更新应用失败的模态告知（UI 收口；ADR-024）。
    /// 【策略收敛】pending 保留/清理决策已全部移入更新引擎
    /// （DshUpdateManager.NotifyApplyFailedInternal：重试类保留 pending、非重试类清 pending 后回调）；
    /// 本方法只负责把"非重试类失败"以模态弹窗明确告知用户（含真实原因），不再重复策略判断。
    /// </summary>
    private static void NotifyUpdateApplyFailed(string version, string errorTail)
    {
        try
        {
            var detail = string.IsNullOrWhiteSpace(errorTail) ? "未知原因" : errorTail;
            if (!NoUiMode)
            {
                // 任务三：显示主窗口之前必须弹模态，明确告知失败原因与后续动作
                var dlg = _pendingForm; // 更新提示托盘宿主（可能为 null，回退无 owner）
                var text = $"自动应用更新失败 (v{version})。\n\n将继续使用旧版本启动。\n\n原因：{detail}\n\n" +
                           "您可以稍后在设置中重试更新。";
                if (dlg is not null)
                {
                    dlg.BeginInvoke(() => MessageBox.Show(dlg, text, "dsh 更新失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error));
                }
                else
                {
                    MessageBox.Show(text, "dsh 更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        catch { /* 弹窗失败不影响启动 */ }
    }

    // 服务启动状态窗已迁移为 Windows/SplashForm.cs（v0.4.1 极速启动模型）：
    // 双缓冲 + 内联确认面板 + IProgress<T> 回填进度，替代旧的 CreateStartupStatusForm
    //（DoEvents 手动消息泵 + ShowDialog 嵌套模态循环的方案已整体废弃）。

    /// <summary>
    /// WebView2 初始化已迁入 <see cref="DshWeb.Managers.WebViewManager.InitializeAsync"/>
    ///（Step 4，static 字段语义映射见 docs/refactor-static-mapping.md）。
    /// 此方法保留为调用点兼容转发（Program.Main 编排最终形态前的过渡壳）。
    /// </summary>
    internal static Task InitWebViewAsync(WebView2 web, string userDataFolder)
        => DshWeb.Managers.WebViewManager.InitializeAsync(web, userDataFolder);

    /// 插件内部弹窗用的轻量窗口（与主窗口共享 WebView2 用户数据，保持登录态/会话）。
    internal static (Form Form, WebView2 Web) CreatePopupForm()
    {
        var popupWeb = new WebView2();
        var form = new DshShellForm
        {
            // 初始标题区别于主窗口（"DeepSeek Harness"）：单实例逻辑按标题找主窗口，
            // 弹窗开着时第二实例不会被误聚焦到 popup（B2）。页面加载后 DocumentTitle
            // 会覆盖成实际页面标题。
            Text = "dsh-launcher 弹窗",
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
    internal static Icon? LoadIconResource(string resourceSuffix)
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
    internal static Icon? _darkWhaleIcon;

    /// <summary>白色鲸鱼图标（窗口深色主题/托盘深色背景时用）。</summary>
    internal static Icon? _lightWhaleIcon;

    /// <summary>蓝色鲸鱼图标（托盘/任务栏按钮固定用：DeepSeek 蓝 #4D6BFE，深浅背景都清晰）。</summary>
    private static Icon? _blueWhaleIcon;

    /// <summary>检测系统应用深色模式（实现见 AppEnvironment.IsSystemDarkMode；ADR-024 迁移转发）。</summary>
    private static bool IsSystemDarkMode() => Managers.AppEnvironment.IsSystemDarkMode();

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
    internal static bool ResolveDarkMode()
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
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    internal static extern IntPtr TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const int WM_NCLBUTTONDOWN = 0x00A1;
    internal const int HTCAPTION = 0x0002;

    /// <summary>给无边框窗口加 DWM 阴影（DWMWA_NCRENDERING_POLICY=ENABLED）。</summary>
    internal static void ApplyWindowShadow(IntPtr hwnd)
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
    /// 无边框主窗口：处理最大化限制在工作区（WM_GETMINMAXINFO）与边缘缩放（WM_NCHITTEST）。
    /// 标题栏由 <see cref="CustomTitleBar"/> 自绘。
    /// </summary>
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
        if (WindowManager.Instance.TrayIcon is not null)
        {
            try { WindowManager.Instance.TrayIcon.Icon = TrayWhaleIcon ?? SystemIcons.Application; } catch { /* ignore */ }
        }
    }

    // Step 5b：RegisterThemeWatcher/SafeFileMtime/ReleaseThemeWatcher 已迁入
    // WindowManager.Instance（主题监听：FSW+系统事件+2s 轮询，P2-7 统一释放）。

    // [ADR-024] Program 私有 PortOpen/HttpReady 包装已删除：所有端口/HTTP 探测经
    // ShellLogic.ServiceReadiness（限定名调用）或 Managers.ServiceLifecycleOps.IsReady。
}
