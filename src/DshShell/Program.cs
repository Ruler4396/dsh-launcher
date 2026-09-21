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
using static DshWeb.Win32.NativeMethods;
using static DshWeb.Win32.Win32Constants;
using DshWeb.Windows; // DshShellForm / TrayMenuForm（窗体类已迁出至 Windows 层）
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace DshWeb;

internal static class Program
{
    private const string DefaultUrl = "http://127.0.0.1:3080";

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
        if (NoUiMode || E2EMode || Environment.GetCommandLineArgs().Any(a => a.Equals("--diagnose", StringComparison.OrdinalIgnoreCase) || a.Equals("--ui-selftest", StringComparison.OrdinalIgnoreCase) || a.Equals("--ui-probe", StringComparison.OrdinalIgnoreCase))) return; // 无头/探针/CLI 三模式维持纯 stdout+log，防模态窗挂起自动化（审查 N2 的配套守卫）
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
        // [2026-08-29 token 栅栏] 必须先于 EnsureServiceAndRuntime 订阅：服务横幅在启动轮询期间
        // 即到达（实测 55.561 触发 vs RunUserInterface 55.787 建窗——晚订阅必然错过事件）。
        WireServiceTokenFollow();
        // [审查 N1 2026-09-21] mutex 句柄的持有期 = 主窗全存活期：方法体内 `using var` 随返回
        // 释放句柄，单实例闸门整个存活期失效（二实例直入完整启动，E1009 分支永不触发）。
        using var singleInstanceLock = EnsureSingleInstanceAndAutostart(); if (singleInstanceLock is null) return;

        if (!EnsureServiceAndRuntime()) return;

        RunUserInterface(args);
    }

    /// <summary>
    /// [2026-08-29 token 栅栏] 订阅静态通道（覆盖 identity / DSH_SERVICE_CMD 两条启动路径的
    /// 全部 ServiceManager 实例）：dsh ≥0.1.2 的启动横幅带根路径 token，壳必须跟随导航，
    /// 否则 WebView 停在 401 错误页（E2004）且页面探针挂死。横幅可能远早于建窗到达——
    /// 先记值（_serviceTokenUrl），初始导航与后续刷新点统一消费 <see cref="CurrentWebUrl"/>。
    /// Main 只调用一次（静态事件重复订阅 = 重复导航）。
    /// </summary>
    private static void WireServiceTokenFollow()
    {
        Managers.ServiceManager.ServiceTokenUrlObserved += url =>
        {
            _serviceTokenUrl = url;
            var form = GetMainFormForDialog();
            if (form is not null && form.IsHandleCreated)
            {
                Trace($"token follow: fresh token, queuing navigation on UI thread");
                form.BeginInvoke(NavigateMainWebToCurrentServiceUrl);
            }
            // 主窗未建：仅记值，初始导航（RunUserInterface）消费 CurrentWebUrl
        };
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
        // [INVARIANT] Crash hooks write E9001 log before process terminates. No recovery logic.
        // [审查 N2 2026-09-21] 注册点从 stage 3 提前到 stage 1 末：此前 CLI 三模式在钩子注册前
        // 就 return，stage 1/2 与 CLI 抛异常零留痕——"双击后无声消失"的文档口径曾声称已根治。
        // CLI 路径若真崩溃，弹窗由 TryShowFatalDialog 的 CLI 守卫拦截（保持纯 stdout+exit code，
        // 防无人值守自动化被模态框挂死）；日志在 Logger.Init 前的写入按 Logger 既有语义丢弃。
        RegisterCrashHooks();
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
        // RegisterCrashHooks 已前移至 InitializeProcessEnvironment（审查 N2：CLI 分发前必须挂上钩子）
        if (Environment.GetEnvironmentVariable("DSH_TEST_CRASH") == "1")
            throw new InvalidOperationException("test crash hook (DSH_TEST_CRASH=1)");
        Trace($"start target={Target.Url} external={ServerManagedExternally}");
    }

    /// <summary>Stage 4: Single-instance mutex + old version cleanup + orphan shortcut cleanup.
    /// 返回"必须存活到 Main 结束的 mutex"（Main 的 using 作用域负责 Dispose）；非首实例返回 null。</summary>
    private static Mutex? EnsureSingleInstanceAndAutostart()
    {
        // [F21] mutex 名由纯函数产出（契约测试锁定格式）；按端口隔离实例组。
        // [审查 N1] 这里**不得**写 using var：句柄随本方法返回关闭 = 单实例锁整个存活期失效。
        var mutex = new Mutex(true, ShellLogic.LifecycleDecisions.SingleInstanceMutexName(Target.Port), out var firstInstance);
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
            mutex.Dispose(); return null; // 二实例不持有锁，只关自己的句柄
        }
        Trace("first instance");
        if (!IsSandboxMode) // [SANDBOX] 禁用机器级副作用
        {
            Windows.LegacyUpgradeCleanup.TryPromptOldVersionCleanup(NoUiMode);
            Windows.LegacyUpgradeCleanup.CleanupOrphanShortcuts();
        }
        return mutex;
    }

    /// <summary>Stage 5: SplashForm pipeline + service readiness check + NoUiMode. Returns false if failure/canceled.</summary>
    private static bool EnsureServiceAndRuntime()
    {
        // [真机 2026-09-20 用户实环境] 坏插件崩在就绪前时，旧实现答完"是"只弹一句回执就结束进程，
        // 把"重开启动器"这一步丢给用户手动做——修完没留下走得通的路。现在答"是"就地重跑一次启动
        // 流水线（粘滞标志已置 → 这次拉起带 .dsh-safe），成功即进主窗并弹可退出的安全模式卡。
        // 重试只有一次：第二次不再给安全模式询问（allowSafeModeAsk=false），落到原失败文案，
        // 所以这个循环不可能来回弹。
        for (var attempt = 0; ; attempt++)
        {
            var step = RunStartupPipelineOnce(allowSafeModeAsk: attempt == 0);
            if (step != StartupStep.RetryInSafeMode) return step == StartupStep.Continue;
        }
    }

    /// <summary>启动流水线的三种去向：继续开主窗 / 结束进程 / 已置安全模式需要重跑一次。</summary>
    private enum StartupStep { Continue, Fail, RetryInSafeMode }

    private static StartupStep RunStartupPipelineOnce(bool allowSafeModeAsk)
    {
        using (var splash = new SplashForm(RunLauncherAppPipelineAsync, visible: !NoUiMode && !ServerManagedExternally))
        {
            Application.Run(splash);

            var outcome = splash.Result;
            if (outcome is null) return StartupStep.Fail;
            if (splash.CancelledByUser)
            {
                Trace("startup canceled by user");
                return StartupStep.Fail;
            }
            if (!outcome.Ready)
                return HandleStartupFailure(outcome, allowSafeModeAsk)
                    ? StartupStep.RetryInSafeMode : StartupStep.Fail;
            if (outcome.ServiceStartedByShell)
                RecordServicePid();
        }

        if (!ServerManagedExternally && SessionApp?.ServiceStartedByShell != true)
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
            return StartupStep.Fail;
        }

        return StartupStep.Continue;
    }

    /// <summary>
    /// 关窗三分支（Phase 4 · T6 从 <c>RunUserInterface</c> 里拆出——那条lambda 曾把 66 行
    /// 三种拦截语义糊在组合根最大的方法里）：
    /// ① 托盘驻留 → 隐藏而非关闭；② 后台构建在跑 → 询问是否等待；③ 其余 → 异步退出编排。
    /// 判定本身都在纯函数/引擎里（<c>LifecycleDecisions.ShouldInterceptCloseToTray</c>、
    /// <c>DshUpdateManager.BuildInProgress</c>），这里只剩"把决定变成窗体动作"。
    /// </summary>
    private static void HandleMainFormClosing(DshShellForm form, FormClosingEventArgs e)
    {
        // 异步退出编排已在进行（BeginShutdownAsync → Application.Exit 收尾触发）：
        // 放行关闭，绝不拦截/重复清理。
        if (_shutdownInitiated) return;

        // [T6b] 构建占用状态归更新引擎自己持有（原来是组合根的 static volatile bool，
        // 由本处理器从外面伸手改成 false——那是"谁在跑构建"这件事的第二份真相）。
        var updates = SessionUpdates as Managers.DshUpdateManager;
        var building = updates?.BuildInProgress == true;

        // [F15] 系统会话终止（关机/注销均映射 WindowsShutDown）：永不拦截——把窗口藏进
        // 托盘等于阻塞系统关机（OS 弹"阻止关机"或超时强杀，且强杀不走任何清理）。
        // FollowWindow 由下方 BeginShutdownAsync 正常停服；Tray 模式关机时也直接走退出编排。
        if (TryHideToTrayOnClose(form, e, systemSessionEnding:
                e.CloseReason == CloseReason.WindowsShutDown, building)) return;
        if (building && ShouldWaitForBuildInstead(form, e, updates!)) return;

        // [2026-08 关窗异步化] 不再在 UI 线程同步停服务（netstat 轮询 + taskkill 等待
        // 实测卡 1.5s+）：取消本次关闭 → 窗口即刻隐藏（视觉上已关），清理转后台，
        // 完成后 Application.Exit；3s 看门狗兜底强制退出。
        e.Cancel = true;
        BeginShutdownAsync(form);
    }

    /// <summary>Tray 驻留模式下把关闭动作降级为隐藏。返回 true = 本次关闭已被拦截。
    /// [Regression_TrayResidentSwitchAtRuntime] 图标可能尚未创建（启动时非 Tray、运行中才
    /// 切到托盘驻留）：先按需补建再拦截；补建失败则放行真实关闭——fail-open，绝不把用户
    /// 困在无托盘可唤起的隐藏窗口里。</summary>
    private static bool TryHideToTrayOnClose(DshShellForm form, FormClosingEventArgs e,
        bool systemSessionEnding, bool buildInProgress)
    {
        var mode = ReadLifetimeMode();
        if (!ShellLogic.LifecycleDecisions.ShouldInterceptCloseToTray(
                mode, WindowManager.Instance.TrayExitRequested, systemSessionEnding)
            || buildInProgress) return false;

        WindowManager.Instance.EnsureTrayIcon(form);
        if (WindowManager.Instance.TrayIcon is null)
        {
            Logger.Warn("tray-resident close could not create tray icon; closing for real",
                ctx: new { mode = mode.ToString() });
            return false;
        }
        e.Cancel = true;
        form.Hide();
        WebViewManager.HiddenSince = DateTime.UtcNow;
        return true;
    }

    /// <summary>任务五：防误关拦截。后台正在构建更新时（npm install）关闭会中断构建、损坏环境，
    /// 故先拦截关闭并询问；返回 true = 用户选择继续等待（窗口保持打开）。
    /// 选择"强制关闭"时请求取消构建并落到正常退出编排：下载/npm 安装阶段可中断
    ///（ProcessRunner 取消即 Kill 整棵进程树）；pnpm 安装阶段目前不可中断，那条已登记在
    /// docs/ARCHITECTURE-DEBT-LEDGER.md——这里不假装取消成功。</summary>
    private static bool ShouldWaitForBuildInstead(DshShellForm form, FormClosingEventArgs e,
        Managers.DshUpdateManager updates)
    {
        e.Cancel = true;
        var result = MessageBox.Show(
            form,
            "正在下载构建更新，关闭可能导致环境损坏。\n\n是否继续等待？",
            "DeepSeek Harness - 更新构建中",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result != DialogResult.No)
        {
            Trace("user chose to wait for build completion");
            return true;
        }
        var requested = updates.TryCancelRunningBuild();
        try { CancelBuildStatusDwell(); } catch { /* 定时器未创建 */ }
        Trace($"user forced close during build; cancellation requested={requested}"
            + "（pnpm 安装阶段不可中断，见债务台账）");
        return false;
    }

    /// <summary>Stage 6: Create main form, wire up WebView2/theme/tray, run Application.Run.</summary>
    private static void RunUserInterface(string[] args)
    {
        // [2026-08-29 token 栅栏] 订阅已在 Main.WireServiceTokenFollow 完成（先于服务启动）——
        // 此处订阅必然晚于横幅到达，会永久错过事件（实测竞态），严禁重复订阅。

        // 正常模式启动：清理历史遗留的隔离 profile（Task 4）。安全模式启动时 SafeProfile.Build
        // 会幂等重建，故此处清理无副作用；若本次为安全模式则保留（服务正在使用）。
        // [issue #28-4 数据销毁护栏] 不再无条件递归删除：在安全模式会话里装的插件就落在
        // .dsh-safe 自己目录里（pnpm 实体化 node_modules），旧实现等于每次正常启动都
        // 物理销毁用户刚装的东西。只有"目录里仍是壳自己写的、未被改动的产物"才允许删。
        if (!SafeMode.IsActive && SafeProfile.SafeProfileExists())
        {
            var (onlyLauncherArtifacts, manifestUnchanged) = SafeProfile.InspectForCleanup(SafeMode.Tier);
            if (ShellLogic.SafeProfileCleanupPolicy.ShouldDelete(
                    SafeMode.IsActive, onlyLauncherArtifacts, manifestUnchanged))
            {
                SafeProfile.Cleanup();
                Trace("SAFEMODE: cleaned up stale safe profile (normal mode launch)");
            }
            else
            {
                Logger.Warn($"SAFEMODE: 隔离 profile 中存在非壳生成的内容（可能是在安全模式期间安装的插件），"
                    + $"已保留目录不删：{SafeProfile.SafeProfileDir}", ErrorCodes.E1010);
            }
        }

        // 测试标记：DSH_TEST_INSTANCE=1 时添加窗口标题后缀
        var testSuffix = string.Equals(Environment.GetEnvironmentVariable("DSH_TEST_INSTANCE"), "1", StringComparison.OrdinalIgnoreCase)
            ? " [TEST]" : "";
        var form = new DshShellForm
        {
            Text = "DeepSeek Harness" + testSuffix,
            ClientSize = new Size(1280, 840),
            StartPosition = FormStartPosition.CenterScreen,
            // [INVARIANT] Frameless + custom titlebar: DWM titlebar does not auto-refresh on theme switch.
            FormBorderStyle = FormBorderStyle.None,
            Icon = TrayWhaleIcon ?? SystemIcons.Application
        };
        var mainHwnd = form.Handle;
        // 最小尺寸随 DPI 折算（纯函数）：本窗体无边框 + 手工布局，WinForms 不会替我们缩放
        // MinimumSize——写死 800×600 在 200% 屏上等于允许把窗口缩到设计值的一半。
        form.MinimumSize = DshWeb.Win32.WindowGeometry.MinimumWindowSize(form.DeviceDpi);
        using var f11Hook = new F11LowLevelHook(form.ToggleFullscreen,
            () => F11LowLevelHook.GetForegroundWindow() == mainHwnd);
        var titleHeight = ShellLogic.DpiScale.Px(32, ShellLogic.DpiScale.Of(form.DeviceDpi));
        form.TitleBar = new CustomTitleBar(form, ResolveDarkMode())
        {
            Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2, titleHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        form.Controls.Add(form.TitleBar);
        // [2026-09 版本徽标] 标题栏紧跟标题显示 dsh 当前版本（发现层原始版本号），
        // 点击弹出原生版本信息窗（dsh/启动器当前+最新版本 + 启动器下载地址）。
        form.TitleBar._dshVersion = UpdateChecker.ResolveLocalDshVersion() ?? "";
        form.TitleBar.VersionClick = () => ShowVersionInfoDialog(form);
        // [2026-09-20 用户要求] 点标题栏红色的"（安全模式）"→ 重新调出右下角退出卡片。
        // 卡片可以被 × 关掉，关掉之后若没有这个入口，用户只能手动重启一次、等它再弹一次卡才能退出。
        form.TitleBar.SafeModeMarkerClick = () => AnnounceSafeModeActive(form, explicitRequest: true);

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
        // DPI 变化的几何重算（字号/最小尺寸/窗口尺寸/客户区布局）整体归 DshShellForm
        // .OnDpiChanged 一处负责——组合根不再抄第二份，也补上了"跨屏后窗口尺寸要跟着倍率走"
        // （真机 T11 实测缺口）。

        // 托盘图标：由 dsh-launcher-lifetime 插件控制（通过 settings.json 的 serviceLifetime）
        // 壳只读取配置，不硬编码托盘逻辑；通知走壳自绘卡片，不依赖托盘（issue #25 收口）。
        // [Regression_TrayResidentSwitchAtRuntime] 委托装配与"启动时的模式"解耦：
        // 此前四个委托只在启动时 mode==Tray 的分支内装配——运行中在设置页切到"托盘驻留"
        // 后，壳里永远没有托盘（FormClosing 因 TrayIcon==null 不拦截 → 直接整壳退出），
        // 与设置页"立即生效"的承诺相悖。现改为始终装配；IsTrayWantedProvider 每次调用
        // 现读配置（ReadLifetimeMode 自带插件降级/purge），启动模式仅决定是否立刻建图标。
        WindowManager.Instance.IsTrayWantedProvider = () =>
            ReadLifetimeMode() == ShellLogic.ServiceLifetime.Tray;
        WindowManager.Instance.TrayWhaleIconProvider = () => TrayWhaleIcon ?? SystemIcons.Application;
        WindowManager.Instance.TrayExitAction = () =>
        {
            WindowManager.Instance.MarkTrayExitRequested();
            // [2026-08 关窗异步化] 托盘退出与关窗共用编排：窗口即刻消失，
            // 服务清理后台执行（原为 UI 线程同步 StopShellService，卡 1.5s+）
            BeginShutdownAsync(GetMainFormForDialog());
        };
        WindowManager.Instance.TrayMenuFactory = (exitAction, deviceDpi) => new TrayMenuForm(exitAction, deviceDpi);
        WindowManager.Instance.VerifyDependencies();
        if (ReadLifetimeMode() == ShellLogic.ServiceLifetime.Tray)
        {
            WindowManager.Instance.EnsureTrayIcon(form); // 启动即处于托盘驻留：立刻可见
        }

        WindowManager.Instance.ResolveDarkModeProvider = () => ResolveDarkMode();
        WindowManager.Instance.ApplyWindowThemeAction = (f, dark) => ApplyThemeIcon(f);
        WindowManager.Instance.DshHomeDirProvider = () => DshHomeDir;
        WindowManager.Instance.PopupFactory = CreatePopupForm;
        WindowManager.Instance.ApplyShadowAction = ApplyWindowShadow;
        WindowManager.Instance.ShowWindowAction = ShowWindowNative;
        WindowManager.Instance.TraceAction = Trace;
        WebViewManager.DownloadNotifyAction = NotifyDownloadComplete;
        // [F13] 主窗渲染崩溃汇入状态机（Running→Running 自转移广播；非 Running 态内部吸收）。
        WebViewManager.MainWebCrashed += () => SessionApp?.HandleWebViewCrashed();
        ApplyThemeIcon(form);
        form.HandleCreated += (_, _) => ApplyThemeIcon(form);
        WindowManager.Instance.RegisterThemeWatcher(form);
        // [issue #28-4] 粘滞安全模式现在在启动路径上也会真正生效（与重启路径对称），因此必须
        // 同时可见：否则用户看到的就是"插件凭空消失、界面上没有任何解释"。横幅 + 一条可点击
        // 退出的通知（没有退出通道，对称遵守等于把用户永久困在降级态）。一次性：Shown 只挂一次。
        var safeModeNoticeSent = false;
        form.Shown += (_, _) =>
        {
            Trace("main form shown");
            LogDisplayTopology(form);
            if (SafeMode.IsActive && !safeModeNoticeSent)
            {
                safeModeNoticeSent = true;
                AnnounceSafeModeActive(form);
            }
        };

        form.FormClosing += (_, e) => HandleMainFormClosing(form, e);

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
                await InvalidateWebCacheOnVersionChangeAsync(web); // [v0.4.5] dsh 版本变更 → 仅磁盘缓存一次性失效（先于首航）
                web.CoreWebView2.Navigate(CurrentWebUrl); // token 跟随：dsh ≥0.1.2 根路径需 ?token=
                // [2026-08-29 token 栅栏] 瞬态失败自愈：唯一实现在 WebViewManager.ArmNavigationRetry，
                // 决策为纯函数 ShellLogic.NavigationResiliencePolicy（此处原有两份抄写且已互相漂移）。
                WebViewManager.ArmNavigationRetry(web, () => CurrentWebUrl, m => Trace(m),
                    () => BootMonitor?.OnNavigationCompleted(),
                    () => form.BeginInvoke(() => ShowError(ErrorCodes.E2004,
                            $"页面加载失败。\n\n请确认 {Target.Url} 上运行的是 dsh 服务（端口可能被其他程序占用，或服务已异常退出）。\n\n统一日志：{UnifiedLogPath}")));
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
                        web.CoreWebView2.Navigate(CurrentWebUrl); // token 跟随：dsh ≥0.1.2 根路径需 ?token=
                        // 与首次初始化同一条自愈实现（此前这里是第二份抄写，且漏了重试留痕 Trace）
                        WebViewManager.ArmNavigationRetry(web, () => CurrentWebUrl, m => Trace(m),
                            () => BootMonitor?.OnNavigationCompleted(),
                            () => form.BeginInvoke(() => ShowError(ErrorCodes.E2004,
                            $"页面加载失败。\n\n请确认 {Target.Url} 上运行的是 dsh 服务（端口可能被其他程序占用，或服务已异常退出）。\n\n统一日志：{UnifiedLogPath}")));
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

        // [DSH_TEST_NOTICE_CARD=1] 通知链路自检：走一次真实卡片呈现（回归测试用固定入口，
        // 断言"通知走通且进程内没有加载 wpnapps.dll"）。旧的 DSH_TEST_TOAST 随 SystemToast 一起删除。
        if (string.Equals(Environment.GetEnvironmentVariable("DSH_TEST_NOTICE_CARD"), "1",
                StringComparison.OrdinalIgnoreCase))
        {
            var ok = Windows.NoticeCard.Present(form, "dsh-launcher 通知自检",
                $"通知卡片自检 {DateTime.Now:HH:mm:ss}（DSH_TEST_NOTICE_CARD）", TimeSpan.FromSeconds(15));
            Trace($"notice card self-test: presented={ok}");
            // 去重实证：同一条内容连送两次，第二次必须落在冷却窗里被抑制
            //（回归测试断言 dsh.log 出现 "notice suppressed as duplicate"）。
            // 正文固定不含时间戳，否则键会随秒变化、断言不稳定。
            for (var i = 0; i < 2; i++)
                Windows.NoticeCard.Present(form, "dsh-launcher 通知去重自检",
                    "同一条内容在冷却窗内只应呈现一次（DSH_TEST_NOTICE_CARD）",
                    TimeSpan.FromSeconds(15));
        }

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
                            // 安全模式标志的拼法下沉为纯函数（Phase 4 · T6a：原来在组合根里
                            // `new Uri()` + 三元手拼，同一条规则没有测试可钉）
                            var safeModeUrl = DshWeb.Domain.SafeModeLaunchPolicy
                                .NavigationUrlWithSafeModeFlag(Target.Url);
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
    /// [Phase 4 · T7] 三个探针/自测方法体已迁至 Windows/UiSelftestProbe.cs。
    /// 组合根这里只留该做的事：把窗口装配能力（DPI 主题/DWM 阴影/WebView2 初始化/版本弹窗）
    /// 注入给探针——探针与生产窗体的装配规则从此只有一份。
    /// </summary>
    private static Windows.UiSelftestProbe.Context ProbeContext()
        => new(UnifiedLogPath, ResolveDarkMode, ApplyWindowShadow, InitWebViewAsync, ShowVersionInfoDialog);

    private static int RunUiSelftest() => Windows.UiSelftestProbe.RunUiSelftest(ProbeContext());

    private static int RunUiProbe() => Windows.UiSelftestProbe.RunUiProbe(ProbeContext());

    private enum PendingUpdate { None, Dsh, LauncherSecurity }
    private static PendingUpdate _pendingUpdate;
    private static string _pendingLatest = "";
    private static string? _pendingLocal;
    // ---- 任务五：后台更新构建状态（防误关 + UI 反馈） ----
    // 「构建是否正在跑」与它的取消源都在 Managers/DshUpdateManager（BuildInProgress /
    // TryCancelRunningBuild，由 BuildStagedUpdate 自己置位清零）——组合根只读，不再代写。

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
        SessionApp = app; // [F13] 持有到会话结束：运行期关停/崩溃事件经此汇入状态机
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
            // [T5] apply 前身份版本不再镜像到组合根静态：回滚协调器经委托直读更新引擎
            //（原 `_preApplyIdentityVersion` 是同一事实的第二份副本，只为让静态方法读到而存在）
            _updateApplyProgress = null; // 本次会话结束，清理桥接（防跨会话污染）
            _firstRunProvisionProgress = null; // 同上：首次运行预装进度桥接
        }
    }

    /// <summary>运行期服务重启协调器（Phase 4 · T1：预算 + 停-起-等就绪-重挂监控的事务本体）。
    /// 组合根不再自带重试计数/冷却时间戳静态，也不再持有 62 行的重启事务。</summary>
    internal static Lifecycle.ServiceRestartCoordinator? RestartCore { get; private set; }

    /// <summary>安全模式进/出与两阶段验证（Phase 4 · T3）。组合根不再持有这条带补偿事务。</summary>
    internal static Lifecycle.SafeModeLifecycle? SafeModeFlow { get; private set; }

    /// <summary>更新回滚 saga（Phase 4 · T5）：武装标记/健康确认/回滚事务全在协调器里，
    /// 组合根只剩"把结论映射成一次 [E4003] 可见化"。</summary>
    internal static Lifecycle.UpdateRollbackCoordinator? RollbackFlow { get; private set; }

    /// <summary>装配 LauncherApp：注入真实副作用（与 Program 静态状态解耦，组合根接线）。
    /// 【ADR-024】服务拉起不再经 wscript/vbs 委托——LauncherApp 直接调 IServiceManager.Start(identity)；
    /// 更新编排经 IDshUpdateManager 引擎实例（本会话共享，UI 回调在此接线）。</summary>
    private static LauncherApp CreateLauncherApp(Func<string, string, Task<bool>> confirm)
    {
        WebViewManager.PopupFactory = CreatePopupForm; // 弹窗经注入构造：Manager 不再向上回调 Program 静态

        RestartCore = new Lifecycle.ServiceRestartCoordinator(
            new Lifecycle.ServiceRestartCoordinator.Dependencies(
                Trace,
                SessionShuttingDown: () => SessionCts.IsCancellationRequested,
                StopService: expectSelfRespawn => StopShellService(expectSelfRespawn).AdoptedReplacementPid,
                StartViaIdentity: () => (StartDshServiceViaIdentity(out var safe), safe),
                WaitForFreshToken: ms => WaitForFreshServiceToken(_tokenBeforeServiceRestart, ms),
                IsReady: () => Managers.ServiceLifecycleOps.IsReady(Target.Port, Target.Url),
                RecordPid: RecordServicePid,
                ResolvePid: ResolveServicePidBestEffort,
                ApplySafeModeVisibility: a => SafeModeFlow?.ApplyVisibility(a),
                SuspendMonitor: () => BootMonitor?.Suspend(),
                StopMonitor: () => BootMonitor?.Stop(),
                ResumeMonitor: pid => BootMonitor?.ResumeAfterRestart(pid),
                SafeModeActive: () => SafeMode.IsActive,
                Port: Target.Port,
                TryFireLifecycle: t => SessionApp?.TryFire(t) == true));

        SafeModeFlow = new Lifecycle.SafeModeLifecycle(
            new Lifecycle.SafeModeLifecycle.Dependencies(
                Trace,
                SessionShuttingDown: () => SessionCts.IsCancellationRequested,
                BuildProfile: tier => SafeProfile.Build(tier),
                Activate: tier => SafeMode.Activate(tier),
                Deactivate: () => SafeMode.Deactivate(),
                SafeProfileDir: () => SafeProfile.SafeProfileDir,
                SuspendMonitor: () => BootMonitor?.Suspend(),
                StopMonitor: () => BootMonitor?.Stop(),
                ResumeMonitor: pid => BootMonitor?.ResumeAfterRestart(pid),
                StopService: () => { StopShellService(); },
                StartViaIdentity: () => StartDshServiceViaIdentity(out _),
                WaitForFreshToken: () => WaitForFreshServiceToken(_tokenBeforeServiceRestart),
                IsReady: () => Managers.ServiceLifecycleOps.IsReady(Target.Port, Target.Url),
                RecordPid: RecordServicePid,
                ResolvePid: ResolveServicePidBestEffort,
                PluginCrashUtc: () => WebViewManager.LastPluginCrashUtc,
                NoteShellRestart: () => RestartCore?.NoteShellRestart(),
                PostToMainForm: k => { var f = GetMainFormForDialog(); TryPostToMainForm(f, () => k(f)); },
                NavigateToServiceUrl: PostNavigateToServiceUrl,
                // 等待态：本类可能在后台线程（安全模式阶梯）请求，ShowWaitingPage 自己投递回 UI 线程
                ShowWaitingPage: ShowWaitingPage,
                Port: Target.Port,
                TryFireLifecycle: t => SessionApp?.TryFire(t) == true));

        var updates = new Managers.DshUpdateManager(DataDir, Target.Port)
        {
            // UI 收口回调：更新失败弹窗 / 首装进度滚动 / PromptRestart 版本登记
            NotifyApplyFailed = NotifyUpdateApplyFailed,
            ProvisionProgress = s => _firstRunProvisionProgress?.Invoke(s),
            DeferRestartPrompt = v => _applyRestartPendingVersion = v,
        };
        // [update-guard] 回滚 saga（Phase 4 · T5）：事务本体在 Lifecycle/UpdateRollbackCoordinator，
        // 组合根只做接线。重启半程复用 T1 的 RestartCore（不再抄第二遍"停-起-等 token-等就绪-
        // 重挂监控"），回滚自持 RollingBackUpdate 状态，故 driveLifecycleState=false。
        RollbackFlow = new Lifecycle.UpdateRollbackCoordinator(
            new Lifecycle.UpdateRollbackCoordinator.Dependencies(
                Trace,
                PersistFailureEvidence: PersistBootFailureEvidence,
                ExportDiagnostics: ExportBootDiagnostics,
                SuspendMonitor: () => BootMonitor?.Suspend(),
                StopMonitor: () => BootMonitor?.Stop(),
                // 还原/隔离前必须先停服：见 UpdateRollbackCoordinator.RunAsync 的注释。
                StopService: () => { StopShellService(); },
                DiscoverIdentityVersion: () => DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime().Version,
                UnconfirmedSnapshotVersion: UpdateDataGuard.UnconfirmedSnapshotVersion,
                MarkHealthy: UpdateDataGuard.MarkConfirmedHealthy,
                PreApplyIdentityVersion: () => updates.PreApplyIdentityVersion,
                RollbackData: UpdateDataGuard.RollbackAfterFailedUpdate,
                RestartService: budget => RestartCore is { } core
                    ? core.RestartAsync("update-rollback", readyBudgetSeconds: budget, driveLifecycleState: false)
                    : Task.FromResult(Lifecycle.ServiceRestartCoordinator.Outcome.StartFailed),
                DowngradeGlobalPackage: v => updates.TryDowngradeGlobalPackageForRollback(v),
                NavigateToServiceUrl: PostNavigateToServiceUrl,
                ShowError: (code, msg) => ShowError(code, msg, log: false),
                TryFireLifecycle: t => SessionApp?.TryFire(t) == true));
        // apply 成功 → 武装回滚闸门（新版启动自检失败时自动回滚）
        updates.UpdateApplied += v => RollbackFlow.ArmFromAppliedUpdate(v);
        SessionUpdates = updates;

        // [F4] 注入服务身份账本：ProbePort 对 HTTP 不通的 node 占用者，账本内才判 Zombie
        //（自愈清理），账本外判 Foreign 绝不强杀——用户自己的 node 程序不再被误杀。
        var service = new ServiceManager(knownServicePid: IsKnownDshServicePid);
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
            // [issue #28-3 伪重启修复] 健康残留服务是否必须清理重启：三重门控（非外部托管 ×
            // 账本内（本壳上次拉起且没停干净 = 上次会话异常终止）× 驻留模式要求服务跟随壳）。
            // 账本判定复用 ServiceManager 的已知服务身份账本（ServiceLifecycleOps.PidFilePath）。
            RestartHealthyLeftoverPolicy = () =>
            {
                if (ServerManagedExternally) return false;
                var owner = ShellLogic.ProcessManagement.GetProcessIdByPort(Target.Port);
                if (owner <= 0) return false;
                var restart = ShellLogic.LifecycleDecisions.ShouldRestartLeftoverService(
                    ReadLifetimeMode(),
                    ledgerOwned: IsKnownDshServicePid(owner, Target.Port),
                    externallyManaged: false);
                if (restart) Trace($"[leftover] healthy leftover service pid={owner} is ledger-owned and lifetime mode requires shell ownership");
                return restart;
            },
            // [issue #28-4] 初始拉起与所有重启路径共用同一条 profile 判定（EnsureSafeProfileIdentity）。
            // 修复前 "--profile .dsh-safe" 只在重启路径生效 → 同一份粘滞 safe-mode.json 下
            // "托盘退出重开插件都在，点 DSH 内置重启插件消失"。
            ServiceIdentityDecorator = EnsureSafeProfileIdentity,
        };
    }

    /// <summary>本会话的更新引擎引用（CreateLauncherApp 装配；供主窗流程复用同一实例，
    /// 保证 PreApplyIdentityVersion/回滚武装等会话状态一致）。Headless/测试可为 null。</summary>
    internal static IDshUpdateManager? SessionUpdates { get; private set; }

    /// <summary>
    /// [F13] 本会话的启动编排实例（RunLauncherAppPipelineAsync 装配后持有）：
    /// 使运行期事件（关停请求/WebView 崩溃）能够汇入 LauncherLifecycle 状态机——
    /// 此前 app 是局部量，启动完成后无人持有，状态机失联、退出全程旁路。
    /// </summary>
    internal static LauncherApp? SessionApp { get; private set; }

    /// <summary>
    /// [F14] 会话级取消源：退出编排（BeginShutdownAsync）触发 Cancel，安全模式阶梯/
    /// 重启服务/更新重拉三组后台 Task 在关键边界检查，杜绝"窗口已退出、后台还在
    /// 停服/拉起/装 npm"的未定义交错。
    /// </summary>
    private static readonly CancellationTokenSource SessionCts = new();

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
        WebCacheVersionLedger.Init(DataDir); // [v0.4.5] 版本变更一次性缓存清理的基线账本
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
    /// 会话级缓存：避免重复探测。[F20] 已加载标志替代 "unset" 魔法哨兵——local 为 null
    /// （版本未知）是合法缓存值，不能与"未缓存"混用。
    /// </summary>
    private static string? _cachedGlobalDshVersion;
    private static bool _cachedGlobalDshVersionLoaded;

    private static string? ReadGlobalDshVersion()
    {
        if (!_cachedGlobalDshVersionLoaded)
        {
            _cachedGlobalDshVersion = DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime().Version;
            _cachedGlobalDshVersionLoaded = true;
        }
        return _cachedGlobalDshVersion;
    }

    /// <summary>
    /// [v0.4.5] 版本变更一次性缓存清理（dsh 版本与上次记录不同 → 只清 WebView2 磁盘 HTTP 缓存）。
    /// 三层职责全部下沉，本方法只做三步薄编排（纯组合根职责）：
    ///  决策 → <see cref="ShellLogic.CacheInvalidationPolicy.ShouldInvalidate"/>（纯函数，契约测试锁定）；
    ///  基线 → <see cref="WebCacheVersionLedger"/>（AtomicWrite 账本）；
    ///  执行 → <see cref="Managers.WebViewManager.ClearDiskCacheAsync"/>（仅 DiskCache 种类，绝不碰用户数据）。
    /// 时机：主窗 WebView2 初始化后、首次导航前——新版本资源从零建立缓存，不误删本次会话数据。
    /// 版本取值与 UpdateChecker/标题栏徽标同源（委托 DshDiscovery，因果地图身份一致性铁律）。
    /// 任何一步失败 → Warn 降级，绝不阻断启动/导航；先清后写基线（崩溃重跑按旧基线再清，幂等无害）。
    /// 当前版本不可判（null）时：不清、也不写基线（账本 Write 对空白/null 直接拒绝，保基线防漏清）。
    /// </summary>
    private static async Task InvalidateWebCacheOnVersionChangeAsync(WebView2 web)
    {
        try
        {
            var current = UpdateChecker.ResolveLocalDshVersion();
            var lastSeen = WebCacheVersionLedger.Read();
            var shouldClear = ShellLogic.CacheInvalidationPolicy.ShouldInvalidate(lastSeen, current);
            Trace($"web cache invalidation: lastSeen={lastSeen ?? "<none>"} current={current ?? "<unprobed>"} clear={shouldClear}");
            if (shouldClear)
                await WebViewManager.ClearDiskCacheAsync(web);
            if (current is not null)
                WebCacheVersionLedger.Write(current); // 清后建立新基线；版本相同时也记录（无需重清）
        }
        catch (Exception ex)
        {
            // 透明降级：缓存清理失败/决策异常都不允许干扰启动（宁可保留陈旧缓存，不可误删/阻断）
            Logger.Warn("web cache invalidation aborted; continuing normally (cache untouched)",
                ctx: new { error = ex.Message });
        }
    }

    /// <summary>
    /// [issue #28-4 同族缺口收口] 拉起前把"粘滞安全模式 → 实际身份"补齐的**唯一**入口：
    /// 初始启动（<c>LauncherApp.ServiceIdentityDecorator</c>）与所有重启路径
    /// （<see cref="StartDshServiceViaIdentity"/>）都经这里，杜绝"某条路径忘了 ensure"。
    ///
    /// 为什么必须 ensure 而不只是 Decorate：粘滞标志说"在安全模式"，但 <c>.dsh-safe</c> 目录
    /// 可能已被清理/被用户删除/是升级残留 —— 此时直接带 <c>--profile</c> 拉起，dsh 硬失败
    /// <c>profile ".dsh-safe" does not exist</c> → exit 1 → 壳 E2002，<b>用户连界面都进不去，
    /// 也就永远点不到"退出安全模式"</b>（与 issue #25 同一类陷阱：入口不可用即等于被困）。
    /// 因此：缺目录先重建；重建仍失败则<b>退回正常模式并解粘滞</b>——"插件被禁用但界面可用"
    /// 远好于"界面起不来且无法退出"。
    /// </summary>
    private static DshWeb.Domain.DshRuntimeIdentity EnsureSafeProfileIdentity(
            DshWeb.Domain.DshRuntimeIdentity identity)
    {
        if (DshWeb.Domain.SafeModeLaunchPolicy.NeedsRebuild(
                SafeMode.IsActive, SafeProfile.SafeProfileExists()))
        {
            var rebuilt = false;
            try { rebuilt = SafeProfile.Build(SafeMode.Tier); }
            catch (Exception ex)
            {
                Logger.Warn("SAFEMODE: sticky profile rebuild threw: " + ex.Message, ErrorCodes.E1010);
            }
            if (DshWeb.Domain.SafeModeLaunchPolicy.ShouldFallBackToNormal(rebuilt))
            {
                Logger.Warn("SAFEMODE: 粘滞标志在但隔离 profile 缺失且重建失败 → 本次按正常模式启动"
                    + "（插件仍禁用，但界面与退出入口必须可用）", ErrorCodes.E1010);
                SafeMode.Deactivate();
            }
            else Trace("SAFEMODE: sticky profile missing → rebuilt before launch");
        }
        return DshWeb.Domain.SafeModeLaunchPolicy.Decorate(
            identity, SafeMode.IsActive, SafeProfile.SafeProfileDir);
    }

    /// <summary>
    /// 按当前身份拉起 dsh 服务（ADR-024：组合根唯一启动入口，委托 IServiceManager.Start）。
    /// 安全模式激活时以隔离 profile 身份（--profile .dsh-safe）重启。
    /// 返回 false = 拉起失败（E2001）。旧 wscript/start-dsh.vbs 中间层已彻底移除——
    /// 启动命令只信 Identity.NodeExePath × Identity.DshEntryJsPath。
    /// [issue #28-4] profile 判定不再内联在此：统一走 <see cref="EnsureSafeProfileIdentity"/>，
    /// 与初始拉起同源，杜绝"启动带全套插件、重启掉插件"的不对称。<paramref name="usedSafeProfile"/>
    /// = 实际用于拉起的那份身份是否带隔离 profile —— 安全模式可见性横幅的唯一凭据
    /// （以真正跑起来的进程为准）。
    /// </summary>
    private static bool StartDshServiceViaIdentity(out bool usedSafeProfile)
    {
        var identity = EnsureSafeProfileIdentity(
            DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime());
        usedSafeProfile = identity.IsSafeProfile;
        var ok = ShellService.Start(identity, Target.Port, UnifiedLogPath);
        if (ok) Trace(identity.IsSafeProfile
            ? "service start via identity (SAFE profile)"
            : "service start via identity");
        return ok;
    }

    /// <summary>无 profile 关注点的调用方用的薄壳重载。</summary>

    /// <summary>组合根共享的服务 Manager 实例（无状态；安全模式/回滚/重启询问等主窗流程复用）。</summary>
    private static readonly ServiceManager ShellService = new();

    // ===== [2026-08-29 token 栅栏] 服务 URL 跟随（dsh ≥0.1.2 web-startup 信任栅栏）=====

    /// <summary>
    /// 最近一次从服务 stdout 观察到的带 token web URL（dsh ≥0.1.2；0.1.1 无此横幅保持 null）。
    /// 每次服务进程重启都会换新 token——经静态事件在全线程更新，UI 线程消费。
    /// </summary>
    private static volatile string? _serviceTokenUrl;

    /// <summary>WebView 导航目标：优先服务自报的 token URL，回退裸 Target.Url（旧版 dsh）。</summary>
    private static string CurrentWebUrl => _serviceTokenUrl ?? Target.Url;

    /// <summary>服务重启前的 token 基线（StopShellService 捕获；WaitForFreshServiceToken 消费）。</summary>
    private static volatile string? _tokenBeforeServiceRestart;


    /// <summary>
    /// 把主窗 WebView 导航到当前服务 URL（UI 线程调用）。服务重启/安全模式切换后
    /// 以导航替代 Reload：新进程新 token，Reload 停留的旧地址会 401。web 未建/已关时静默跳过。
    /// </summary>
    /// <summary>
    /// 把主窗换成壳自绘的等待态：HTML 由纯函数 <c>ShellLogic.WaitingPage</c> 产出并转义，
    /// 导航原语在 <c>WebViewManager.ShowWaitingPage</c>（唯一 NavigateToString 点），本方法只做
    /// "投递到 UI 线程"这一件组合根的事。调用点之后一定有"导航回真实页面"的一步（重启核心负责）。
    /// </summary>
    private static void ShowWaitingPage(string headline, string detail)
    {
        var html = ShellLogic.WaitingPage.Html(headline, detail);
        // 留痕必须在 UI 线程真的画完之后：反过来会写出"等待态已显示"其实根本没显示
        TryPostToMainForm(GetMainFormForDialog(), () => Trace(WebViewManager.ShowWaitingPage(html)
            ? $"waiting state shown: {headline}" : $"waiting state NOT shown (main web unavailable): {headline}"));
    }
    private static void NavigateMainWebToCurrentServiceUrl()
    {
        // 先导航、后留痕：反过来会出现"说要导航 → 其实没导航"的两行日志（真机 21:55:25 实测到）
        if (WebViewManager.NavigateMainWeb(CurrentWebUrl))
            Trace($"token follow: navigating main web to {CurrentWebUrl}");
        else Trace("token follow: main web unavailable; skip navigation");
    }

    /// <summary>
    /// [2026-08-29 token 栅栏] 服务重启后，等待新进程的 token 横幅到达再刷新导航。
    /// 空 <paramref name="tokenBefore"/> = 重启前无 token（旧版 dsh 或首启横幅未到）：等待窗口内
    /// 出现任意 token 即返回；超时（dsh 未打印横幅/旧版）原样返回，刷新退回 Target.Url。
    /// 为什么必须等：服务重启存在秒级空窗（进程退出 → 新进程监听），其间导航只会把 WebView
    /// 停在 404/401 错误页（实测驻留），而晚到的 token 跟随导航无法保证后到覆盖——
    /// 刷新点先等横幅，从根上消灭竞态。仅限后台线程调用（内部 Thread.Sleep 轮询）。
    /// </summary>
    private static void WaitForFreshServiceToken(string? tokenBefore, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !SessionCts.IsCancellationRequested)
        {
            var current = _serviceTokenUrl;
            if (!string.Equals(current, tokenBefore, StringComparison.Ordinal))
            {
                Trace(current is null
                    ? "token wait: no banner observed within window (legacy dsh?)"
                    : $"token wait: fresh token observed");
                return;
            }
            Thread.Sleep(200);
        }
        Trace("token wait: timed out; refreshing with current service url");
    }

    /// <summary>
    /// 启动时记录一次显示拓扑，用来收口一个本仓库**自相矛盾**的前提：
    /// <c>Screen.WorkingArea</c> 与 Win32 <c>GetMonitorInfo().rcWork</c> 是否同一坐标空间？
    /// <c>Win32/DisplayMetricsProvider</c> 的注释说前者是"96 DPI 基准的逻辑像素"，而
    /// <c>ShellLogic.RestoreWindowPosition</c> 的注释说"两者均为物理像素"。托盘菜单钳位
    /// （WindowManager.ShowTrayMenu）与窗口位置还原分别依赖其中一种说法——说法不同就是不同的
    /// 正确代码。100% 缩放下两者数值相等，本机永远测不出差别，所以这里只**测量并留痕**：
    /// 不一致时 Warn（在缩放屏上第一次启动就能看见），一致时一行 INFO。
    /// 不做任何"顺手统一"——那需要一台真正缩放机器的数据，猜错会把好的改成坏的。
    /// </summary>
    private static void LogDisplayTopology(DshWeb.Windows.DshShellForm form)
    {
        try
        {
            var m = new Win32.Win32DisplayMetricsProvider().GetMonitorMetrics(form.Handle);
            var wa = Screen.FromControl(form).WorkingArea;
            var same = wa == m.WorkArea;
            var line = "display topology: winforms.workArea=" + wa + " win32.rcWork=" + m.WorkArea
                + " win32.dpi=" + m.Dpi + " form.deviceDpi=" + form.DeviceDpi
                + " screens=" + Screen.AllScreens.Length + " sameSpace=" + same;
            if (same) Trace(line);
            else Logger.Warn(line);
        }
        catch (Exception ex) { Logger.Warn("display topology probe failed: " + ex.Message); }
    }

    /// <summary>
    /// [issue #28-4] 安全模式对用户可见 + 给一条退出通道。启动路径现在与重启路径**对称地**
    /// 遵守粘滞的 safe-mode.json，因此必须同时告诉用户"插件不在"是安全模式所致、以及怎么退出；
    /// 没有退出通道，对称遵守等于把用户永久困在降级态。
    /// </summary>
    private static void AnnounceSafeModeActive(DshShellForm form, bool explicitRequest = false)
    {
        SafeModeFlow!.ApplyVisibility(true);
        // [issue #25 收口] 唯一通知通道，且这条通知**自带退出动作**。#28-4 的原话是"没有退出通道，
        // 对称遵守等于把用户永久困在降级态"。2026-09-20 补上第二条：标题栏的红色"（安全模式）"标记
        // 现在可点，点它重新调出这张卡——用户用 × 关掉卡片之后不再是死路（以前只能重启一次）。
        var presented = Windows.NoticeCard.Present(form, "dsh 已以安全模式启动",
            "第三方插件已临时禁用（上次会话进入过安全模式，本次启动沿用了它）。\n"
            + "提示：在安全模式下安装的插件，退出安全模式后需要重新安装。",
            // sticky：降级态的提示若自动消失，等于把唯一的退出入口收走（issue #25 的同类陷阱）。
            // 只能由用户点动作退出，或点 × 明确选择"这次先不管"（下次启动仍会再告知）。
            Timeout.InfiniteTimeSpan,
            onAction: () => ExitSafeModeRequested(form),
            actionText: "点击此处退出安全模式并重启",
            kind: ShellLogic.NoticeKind.Urgent,
            // 用户主动点标记要来的：不受 60 秒冷却窗限制（屏幕上已有同一条时仍不叠加）
            explicitRequest: explicitRequest);
        Logger.Info(presented ? "safe-mode notice presented: 第三方插件已临时禁用（点击可退出）"
            : "safe-mode notice not presented; the clickable 标题栏（安全模式）标记是备用入口");
    }

    /// <summary>[issue #28-4] 用户从通知点击退出安全模式：解粘滞 → 以正常 profile 重启服务 → 撤横幅。
    /// 重启核心内部会按新身份重画可见性（Deactivate 后 usedSafeProfile=false）。</summary>
    private static void ExitSafeModeRequested(DshShellForm form)
    {
        if (!SafeMode.IsActive)
        {
            SafeModeFlow!.ApplyVisibility(false); // 状态已不激活（他处已解粘滞）：只清横幅，不白重启
            return;
        }
        SafeMode.Deactivate();
        Trace("SAFEMODE: user exited safe mode via notice → restarting service with normal profile");
        RestartOutOfSafeMode(form);
    }

    /// <summary>
    /// 真正执行"以正常配置重新拉起服务"。独立成方法是为了**退出失败后还能再点一次**：
    /// 粘滞标志在第一次点击时已清除，重试入口若还走 ExitSafeModeRequested，会被
    /// !IsActive 闸门挡回去只清横幅、服务永远停在安全模式。
    /// 失败提示**只有一条通道**：把"重试"并进同一个对话框（重试/取消），不再另发通知卡片——
    /// 一次失败弹两条（模态 + 卡片）正是本轮要消灭的"重复提示"。
    /// </summary>
    private static void RestartOutOfSafeMode(DshShellForm form)
    {
        // 真机 2026-09-20：这段重启实测 20+ 秒，而主窗一直挂着**已经断连的旧页面**，用户两次
        // 把它读成"点了没反应"（原话："没反应，窗口消失了，没有重启启动器"）。窗口不能关——
        // 驻留模式下关窗会连带停掉刚重启好的服务，等于把"再点一次启动器"丢回给用户。
        // 所以点下动作的那一刻就换标题 + 换等待态页面，重启完成后由重启核心导航回真实页面。
        SafeModeFlow?.ApplyTitle(Lifecycle.SafeModeLifecycle.ExitingSafeModeTitle);
        ShowWaitingPage("正在退出安全模式…", "正在停用当前服务，并以你的正常配置（含第三方插件）重新拉起 dsh。请稍候，无需重复点击。");
        _ = Task.Run(async () =>
        {
            var outcome = await RestartCore!.RestartAsync("exit-safe-mode");
            if (outcome is Lifecycle.ServiceRestartCoordinator.Outcome.StartFailed or Lifecycle.ServiceRestartCoordinator.Outcome.NotReady)
            {
                var reason = outcome == Lifecycle.ServiceRestartCoordinator.Outcome.StartFailed
                    ? "无法以正常配置拉起 dsh 服务（E2001）。"
                    : "dsh 服务 60 秒内未就绪（E2004）。";
                try
                {
                    form.BeginInvoke(() =>
                    {
                        Logger.Error($"exit safe mode incomplete: {reason}",
                            outcome == Lifecycle.ServiceRestartCoordinator.Outcome.StartFailed ? ErrorCodes.E2001 : ErrorCodes.E2004);
                        if (E2EMode)
                        {
                            Trace("exit-safe-mode retry prompt suppressed in E2E/probe mode");
                            return;
                        }
                        DialogResult r;
                        try
                        {
                            r = MessageBox.Show(form,
                                reason + "\n\n安全模式的粘滞标志已清除：\n"
                                + "· 点「重试」再拉起一次；\n"
                                + "· 点「取消」则保持现状——重新打开 dsh-launcher 即恢复正常配置。",
                                "退出安全模式未完成", MessageBoxButtons.RetryCancel,
                                MessageBoxIcon.Warning);
                        }
                        catch (Exception ex)
                        {
                            // 对话框本身失败（窗体已关闭等）：留痕，绝不抛进后台任务
                            Logger.Warn("exit-safe-mode retry prompt failed: " + ex.Message);
                            return;
                        }
                        Trace($"exit-safe-mode retry prompt answered: {r}");
                        if (r == DialogResult.Retry) RestartOutOfSafeMode(form);
                    });
                }
                catch { /* 窗体已关闭 */ }
                return;
            }
            if (outcome == Lifecycle.ServiceRestartCoordinator.Outcome.Cancelled) return; // 会话收尾中：不打扰
            try { form.BeginInvoke(() => NavigateMainWebToCurrentServiceUrl()); }
            catch { /* 窗体已关闭 */ }
        });
    }

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
            // [issue #28-2 运行期自愈] 健康运行后服务退出（用户在 DSH 界面点自带重启 / 服务自更新 /
            // 服务崩溃）→ 静默重启服务 + 等新 token 重新导航，绝不弹"启动自检未通过"弹窗。
            monitor.ServiceExitedWhileRunning += code => RestartCore?.OnServiceExited(
                code,
                (exitCode, headline) => { var f = GetMainFormForDialog();
                    if (f is not null) AskRestartDshServiceAfterBootFailure(f, headline); },
                (form, code2, msg) => ShowError(code2, msg, log: false),
                GetMainFormForDialog,
                NavigateMainWebToCurrentServiceUrl);
            // [update-guard] 好符号确认健康 → 快照落"已确认"、解除回滚武装
            monitor.HealthyDetected += () => RollbackFlow?.ConfirmHealthy();
            // 好符号确认健康 → 清零跨会话连续失败计数（2026-08-25 升级询问的复位通道）
            monitor.HealthyDetected += () =>
            {
                try { SafeMode.ResetFailureStreak(); }
                catch (Exception ex) { Logger.Warn("[boot-monitor] failure-streak reset failed: " + ex.Message); }
            };
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
            _ = Task.Run(() => RollbackFlow?.ArmFromPersistedState());
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
            // [F14] 会话已进入退出编排：证据照常落盘留痕，但不再弹询问/自动重启
            //（与收尾链路争抢停启动作属 F14 未定义交错）。
            if (SessionCts.IsCancellationRequested)
            {
                PersistBootFailureEvidence(verdict);
                Trace("boot-monitor: failure handled during shutdown; asks/restarts skipped");
                return;
            }

            // 0) [update-guard/E4003] 回滚闸门：当前运行的是"已应用、未确认健康"的更新版本，
            //    启动自检失败极可能由新版自身或其数据迁移导致 → 不进安全模式/手动重启询问，
            //    直接自动回滚（还原共享数据 + 隔离新运行时）并用旧版重启服务。
            //    （2026-08-23 用户回归：rc.2 迁移 .credentials.yaml 后回退 rc.8 必炸。）
            //    分支判定与事务本体都在 Lifecycle/UpdateRollbackCoordinator（Phase 4 · T5）。
            if (RollbackFlow?.TryHandleBootFailure(verdict) == true) return;

            // 1) 证据落盘：safe-mode-state.json 的 lastFailure 字段（原子写，崩溃/重启仍可查）
            PersistBootFailureEvidence(verdict);

            // 1.5) [issue #28-4] 壳自己刚重启过服务 → 只来自 HTTP 探测回死的证据落在静默窗内时
            // 不计入连续失败、不升级询问。事故形态：自愈重启后账本没跟上 → 进程层 attach 到死
            // pid → 下一次 DSH 内置重启被 HTTP 层判成启动自检失败 → 计数推进 → 询问安全模式 →
            // .dsh-safe 把用户刚装的插件剥掉。进程层/页面层证据（真崩溃签名）永不豁免。
            var sinceShellRestart = RestartCore?.LastShellRestartUtc is { } lastShellRestart
                ? (DateTime.UtcNow - lastShellRestart).TotalSeconds : -1d;
            var httpOnlyEvidence = verdict.Evidence.Count > 0
                && verdict.Evidence.All(e => e.Layer == DshWeb.Lifecycle.BootLayer.Http);
            if (ShellLogic.BootRecoveryPolicy.SuppressLauncherInduced(httpOnlyEvidence, sinceShellRestart,
                    ShellLogic.BootRecoveryPolicy.LauncherInducedQuietSeconds))
            {
                Logger.Warn($"[boot-monitor] suppressing launcher-induced verdict [{verdict.ErrorCode}]: "
                    + $"HTTP-only evidence {sinceShellRestart:F1}s after a shell-initiated service restart "
                    + "(failure not counted, no ask). 若反复出现请查统一日志。");
                return;
            }

            // 连续失败计数推进（Failed 路径恰好一次；吸收态 VerdictUpdated 重写不计数）。
            // 跨会话持久化在 safe-mode.json——事故形态是"每次重开壳都崩"，会话内计数抓不住。
            SafeMode.RegisterBootFailure();

            // 2) 诊断包：静默落 DataDir\diagnostics\（失败仅 Warn，不二次弹窗打扰）
            ExportBootDiagnostics();

            // 3) 分支询问（经 BootRecoveryPolicy 路由，2026-08-25 事故回归）：
            //    - 有插件相关证据（页面坏签名 / 插件崩溃消息 / 日志层插件签名）→ 安全模式；
            //    - 无插件证据但连续失败已达阈值（跨会话计数）→ 升级安全模式（重启必然无效）；
            //    - 其余匿名单次失败 → 问"重启 dsh 服务"（无插件时弹安全模式是误导）。
            var detail = string.Join("\n", verdict.Evidence.Select(e => $"· [{e.Layer}] {e.Summary}"));
            var form = GetMainFormForDialog();
            var ask = ShellLogic.BootRecoveryPolicy.Decide(
                VerdictIndicatesPluginInvolvement(verdict), SafeMode.ConsecutiveBootFailures);
            if (ask == ShellLogic.BootRecoveryPolicy.RecoveryAsk.AskSafeMode)
            {
                var escalated = !VerdictIndicatesPluginInvolvement(verdict);
                var headline = escalated
                    ? $"dsh 启动已连续 {SafeMode.ConsecutiveBootFailures} 次失败（[{verdict.ErrorCode}]）。"
                        + "反复重启对确定性故障无效，建议尝试安全模式。\n\n检测到的证据：\n" + detail
                    : $"{ErrorCodes.Describe(verdict.ErrorCode)}（[{verdict.ErrorCode}]）";
                var askBody = "是否进入安全模式（禁用第三方插件，仅保留 dsh 核心功能）？\n"
                    + "（不会修改你的任何配置文件）\n\n检测到的证据：\n" + detail;
                if (form is not null && form.IsHandleCreated)
                    form.BeginInvoke(() => AskAndMaybeEnterSafeMode(form, headline, askBody));
                else
                    AskAndMaybeEnterSafeMode(form, headline, askBody);
            }
            else
            {
                var headline = $"dsh 启动自检未通过（[{verdict.ErrorCode}]）：{ErrorCodes.Describe(verdict.ErrorCode)}\n"
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
    /// 裁决是否携带插件相关证据（决定弹"安全模式"还是"重启服务"，经
    /// <c>ShellLogic.BootRecoveryPolicy</c> 路由）。三条归因通道（2026-08-25 事故回归扩展）：
    /// 1. 页面层坏签名命中（detail 以 dom[/err[ 开头，见 BootGuard.EvaluatePageProbe）——前端崩溃；
    /// 2. 本会话收到过插件崩溃 WebMessage（WebViewManager.PluginCrashDetected 路径）；
    /// 3. 【新增】日志层证据携带插件加载失败类签名——服务端启动崩溃的唯一文本证据来源；
    /// 4. 【新增】第三方插件在场 + 服务进程异常退出——匿名进程崩溃优先按插件嫌疑处理，
    ///    而不是让用户在注定无效的「重启服务」上循环（事故实测 3 会话 4 次崩溃全被误路由）。
    /// CDP 异常仍不参与判定：核心页面自身异常也会被采集，不足以归因插件（保守防误导）。
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
            if (e.Layer == DshWeb.Lifecycle.BootLayer.Log
                && ShellLogic.BootGuard.LogEvidenceIndicatesPlugin(e.Summary + " " + e.Detail))
                return true;
        }
        if (WebViewManager.LastPluginCrashUtc != default) return true;
        return verdict.Evidence.Any(e =>
                   e.Layer == DshWeb.Lifecycle.BootLayer.Process
                   && e.ErrorCode == ErrorCodes.E2007)
               && ShellLogic.PluginConfig.ProfileHasThirdPartyBundles(WebProfilePackageJsonPath);
    }

    /// <summary>用户 web profile 清单路径（第三方插件在场判定的读取对象；只读不写）。</summary>
    private static string WebProfilePackageJsonPath
        => Path.Combine(DshHomeDir, "profiles", "web", "package.json");

    // ---- [issue #28-2] 运行期服务退出静默自愈 ----

    /// <summary>把动作投递到已建句柄的窗体 UI 线程；窗体缺失或已关闭则跳过（退出竞态是常态）。
    /// 收口此前散落在 6 处的 <c>if (form is not null &amp;&amp; form.IsHandleCreated) ... BeginInvoke</c>
    /// 三行式；原先各处的 <c>catch { }</c> 一并改为留痕，不再静默吞掉投递失败。</summary>
    private static void TryPostToMainForm(Form? form, Action action)
    {
        if (form is null or { IsHandleCreated: false }) return;
        try { form.BeginInvoke(action); }
        catch (Exception ex) { Logger.Warn("UI 投递被拒（窗体可能已关闭）: " + ex.Message); }
    }

    /// <summary>
    /// 把"导航到当前服务 URL"投递到 UI 线程执行。运行期事务（自愈重启 / 安全模式 / 更新回滚）
    /// 都跑在后台线程上，而 CoreWebView2 只允许在 UI 线程访问——直接在后台线程调它会抛
    /// InvalidOperationException，把一条**已经成功**的事务判成失败（真机回滚演练实测抓到：
    /// 回滚完成后页面刷新炸掉 → 走 catch → 监控被停、用户看不到 E4003 结果）。
    /// 窗体不存在/句柄未建时静默跳过：与搬迁前 <c>TryPostToMainForm(...)</c> 的语义逐位一致。
    /// </summary>
    private static void PostNavigateToServiceUrl()
        => TryPostToMainForm(GetMainFormForDialog(), NavigateMainWebToCurrentServiceUrl);

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
                    // [F14] 会话已进入退出编排：不再重启服务（避免退出后拉起无主服务）。
                    if (SessionCts.IsCancellationRequested)
                    {
                        Trace("restart-service(bg): session shutting down; restart skipped");
                        return;
                    }
                    var outcome = await RestartCore!.RestartAsync("restart-service");
                    if (outcome == Lifecycle.ServiceRestartCoordinator.Outcome.Ready)
                    {
                        try { form.BeginInvoke(() => NavigateMainWebToCurrentServiceUrl()); }
                        catch { /* 窗体已关闭 */ }
                        return;
                    }
                    if (outcome == Lifecycle.ServiceRestartCoordinator.Outcome.Cancelled) return; // 会话收尾中：不打扰
                    BootMonitor?.Stop();
                    var (code, message) = outcome == Lifecycle.ServiceRestartCoordinator.Outcome.StartFailed
                        ? (ErrorCodes.E2001, "dsh 服务重启失败（无法拉起服务），请查看统一日志后重新打开 dsh-launcher。")
                        : (ErrorCodes.E2004, "dsh 服务重启后 60 秒内未就绪，请查看统一日志。");
                    try { form.BeginInvoke(() => ShowError(code, message, log: false)); }
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
                    Trace("SAFEMODE: user declined; deactivated sticky safe-mode");
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
            // [F14] 会话已进入退出编排：不再启动安全模式阶梯（避免"窗口已退出、后台还
            // 在停服/拉起"的未定义交错）。
            if (SessionCts.IsCancellationRequested)
            {
                Trace("SAFEMODE(bg): session shutting down; ladder skipped");
                return;
            }
            try
            {
                var ok = SafeModeFlow!.TryEnter(form, DshWeb.Domain.SafeProfileTier.Tier1KeepDeepSeekCore); // L1
                if (!ok)
                {
                    Trace("SAFEMODE(bg): Tier1(L1) failed, falling back to Tier2 (minimal core)");
                    ok = SafeModeFlow!.TryEnter(form, DshWeb.Domain.SafeProfileTier.Tier2Minimal); // L2
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

    /// <summary>启动失败/取消的统一处理（v0.4.1 从 Main 内联块提取，逻辑与原 v0.3.x 一致）。
    /// 返回 true = 已就地武装安全模式（隔离 profile 建好、粘滞标志已置），调用方应当**重跑一次
    /// 启动流水线**；返回 false = 已把失败如实展示给用户，流程收尾。</summary>
    private static bool HandleStartupFailure(SplashForm.Outcome outcome, bool allowSafeModeAsk)
    {
        var logPath = outcome.LogPath;
        // v0.3.0：启动失败时清理"本次拉起但未就绪"的半启动服务（避免残留占端口）
        // 【issue #26】service-exited（就绪前进程已退出）同样需要清理。
        if ((outcome.WaitResult is "logerror" or "timeout"
                or ShellLogic.ServiceReadiness.ServiceExitedVerdict)
            && outcome.ServiceStartedByShell
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
            // [静默失败收口] 首装全局安装失败时 StartService=false 会走通用 E2001 文案
            // （"JS 入口解析失败"）会误导——按 StartupFailurePolicy 改用真实根因 E1012 展示。
            var mapped = ShellLogic.StartupFailurePolicy.MapFirstRunInstallFailure(
                outcome.ErrorCode, _firstRunProvisionError);
            if (mapped is not null)
            {
                ShowError(mapped.Value.Code, mapped.Value.Detail, level: Logger.Level.Error);
                return false;
            }
            ShowError(outcome.ErrorCode, outcome.ErrorDetail ?? "启动失败。",
                level: outcome.ErrorCode == ErrorCodes.E1002 ? Logger.Level.Info : Logger.Level.Error);
            return false;
        }

        var waitResult = outcome.WaitResult ?? "timeout";
        var tail = ShellLogic.ReadLogTail(logPath, 12);
        var tailText = tail.Count == 0 ? "（日志为空或不可读）" : string.Join("\n", tail.Select(l => "  " + l));
        // 归因用的宽窗口（120 行）：node 的报错首行常在 12 行窗口之外，只剩堆栈帧就没法判插件签名
        var wideTail = string.Join("\n", ShellLogic.ReadLogTail(logPath, 120));
        // 【issue #24 配套】logerror 根因常在崩溃栈头部（Node uncaught 转储），尾部 12 行只见其尾：
        // 弹窗补"首条报错线索"（首个命中启动错误标志的行），并给出完整日志路径（对齐 timeout 分支）。
        // 【issue #26】service-exited 同理：进程输出（可能不含错误标志）首行即是根因线索。
        var errorHint = waitResult is "logerror" or ShellLogic.ServiceReadiness.ServiceExitedVerdict
            ? ShellLogic.ServiceReadiness.FirstStartupErrorLine(wideTail)
            : null;
        // 【issue #26】就绪前退出：把退出码与进程首行输出真实展示，取代误导性的"下载慢/网络问题"。
        var serviceExitCode = waitResult == ShellLogic.ServiceReadiness.ServiceExitedVerdict
            ? Managers.ServiceManager.TrackedServiceExitCodeOrMinusOne()
            : -1;
        // 裁决 → 错误码映射沉在 ShellLogic（契约测试锁定），组合根不再重复 switch。
        var code = ShellLogic.ServiceReadiness.MapVerdictErrorCode(waitResult);
        // [真机 T14 缺口修复] 坏插件在**就绪前**把服务打死时，过去只有 E2010 一条死路：安全模式
        // 询问只挂在运行期 E2007 与页面 E1008 上。日志里有模块解析失败 + profile 确实声明了第三方
        // bundle 时，把"禁用插件试试"这个选项给用户；答"是"且隔离 profile 建成 → 返回 true，由调用方
        // **就地重跑一次启动流水线**（真机复测纠正：旧实现只弹一句"请你自己重开"的回执就结束进程，
        // 修完没有留下走得通的路）。三条判据与文案在纯函数层（契约测试锁定），"建 profile → 置标志"
        // 这条事务在 Domain（SafeModeLaunchPolicy）。allowSafeModeAsk 保证一次会话只问一次。
        if (allowSafeModeAsk
            && ShellLogic.StartupFailureRecoveryPolicy.ShouldOfferSafeMode(waitResult, wideTail,
                ShellLogic.PluginConfig.ProfileHasThirdPartyBundles(WebProfilePackageJsonPath))
            && AskEnterSafeModeOnce(null,
                $"dsh 启动失败（[{code}]）：日志显示插件/模块加载报错。",
                ShellLogic.StartupFailureRecoveryPolicy.AskBody)
            && DshWeb.Domain.SafeModeLaunchPolicy.ArmNextLaunch(SafeProfile, SafeMode))
        {
            Logger.Info("SAFEMODE: armed at startup; re-running the startup pipeline with the isolated profile");
            return true;
        }
        // 质量治理 P1-7：用户主动取消不是错误——按 Info 记录，避免污染错误码汇总。
        // 正文（按裁决拼线索/日志尾/退出码）是纯映射，沉在 ShellLogic 并有契约测试。
        ShowError(code, "dsh 服务未能就绪。\n\n" + ShellLogic.ServiceReadiness.StartupFailureBody(
                waitResult, tailText, errorHint, serviceExitCode, logPath),
            level: waitResult == "canceled" ? Logger.Level.Info : Logger.Level.Error);
        return false;
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
            var scale = ShellLogic.DpiScale.Of(form.DeviceDpi);
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
        // Phase 4 · T4：检查与裁决已下沉 DshUpdateManager.CheckForUpdatesAsync +
        // ShellLogic.UpdateNoticeFlowPolicy（纯函数，可脱网契约测试）。
        // 组合根这里只做呈现：把结论映射回用户可见的一次通知，不重复任何判定分支。
        if (SessionUpdates is not Managers.DshUpdateManager updates)
        {
            Logger.Warn("update check skipped: 会话更新引擎未装配");
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await updates.CheckForUpdatesAsync(m => Trace(m));
                if (r.PendingApplyVersion is { } pending)
                    form.BeginInvoke(() => NotifyPendingApply(pending));
                if (r.LauncherSecurityVersion is { } sec)
                {
                    form.BeginInvoke(() => NotifyPending(PendingUpdate.LauncherSecurity, sec,
                        r.LauncherLocalVersion ?? "?"));
                    return; // 安全更新抢占：原实现即在此提前终止，不再评估 dsh
                }
                if (r.DshAvailableVersion is { } latest)
                {
                    Trace($"dsh update {latest} available (local={r.DshLocalVersion ?? "<null>"}); prompting update notice");
                    form.BeginInvoke(() => NotifyPending(PendingUpdate.Dsh, latest, r.DshLocalVersion));
                }
                else if (r.SkipReason is { } why)
                    Trace($"dsh update notice suppressed: {why}");
            }
            catch (Exception ex)
            {
                // 检测失败不再完全静默——至少留 Warn 痕迹（日志失败本身不影响启动）
                Logger.Warn("update check aborted unexpectedly", ctx: new { error = ex.Message });
            }
        });
    }

    /// <summary>下载完成但非"无害扩展名"（可能含可执行代码）时的提示：告知落盘位置，
    /// 不自动打开——防恶意页面触发下载后自动执行本地代码（S2 修复）。</summary>
    private static void NotifyDownloadComplete(string filePath)
        => Windows.NoticeCard.Present(GetMainFormForDialog(), "下载完成",
            "文件已保存：\n" + filePath, TimeSpan.FromSeconds(8));

    private static void NotifyPending(PendingUpdate type, string latest, string? local)
    {
        _pendingUpdate = type;
        _pendingLatest = latest;
        _pendingLocal = local;
        var (title, body) = type == PendingUpdate.LauncherSecurity
            ? (ShellLogic.UpdateNotice.LauncherSecurityTitle, ShellLogic.UpdateNotice.LauncherSecurityBody(latest, local))
            : (ShellLogic.UpdateNotice.DshTitle, ShellLogic.UpdateNotice.DshBody(latest, local));
        // 便携 ZIP：这里是**决策**对话框（要用户点是/否：下不下载、跳不跳过该版本），
        // 不是通知——卡片不承载决策语义，故保留模态形态（v0.4.1 便携版分流不变）。
        if (ShellLogic.RuntimeConfig.IsPortableInstallWithTestOverride())
        {
            ShowPortableUpdateDialog(title, body, type, latest);
            return;
        }
        // [issue #25 收口] 唯一通知通道：自绘卡片。点击 = 原"点击此处在后台下载更新"
        // （OnPendingBalloonClicked），语义与当年 BalloonTipClicked / Toast 激活一致。
        if (Windows.NoticeCard.Present(GetMainFormForDialog(), title, body,
                TimeSpan.FromSeconds(25),      // 驻留 25s，安全更新要让人看到
                () => OnPendingBalloonClicked(null, EventArgs.Empty),
                kind: type == PendingUpdate.LauncherSecurity
                    ? ShellLogic.NoticeKind.Urgent : ShellLogic.NoticeKind.Info))
            Logger.Info($"update notice presented: {title} / {body.Replace("\n", " ")}");
        // 标题栏标记是**状态指示**（卡片会自动收起，错过的人仍要看得出"有更新待处理"），
        // 不构成第二条通知通道。
        ApplyPendingTitleMark(type);
    }

    /// <summary>有待处理更新时的标题栏标记。重复轮询不叠加（同一条标记只上一次）。</summary>
    private static void ApplyPendingTitleMark(PendingUpdate type)
    {
        try
        {
            var owner = GetMainFormForDialog(); // 实时查 OpenForms：关闭中的窗绝不会拿到，无"用后销毁"窗口
            var mark = type == PendingUpdate.LauncherSecurity ? "（有安全更新）" : "（有更新）";
            if (owner.Text.Contains(mark, StringComparison.Ordinal)) return;
            if (owner is DshWeb.Windows.DshShellForm shell && shell.TitleBar is not null)
            {
                shell.TitleBar._titleText += mark;
                shell.TitleBar.Invalidate();
            }
            owner.Text += mark;
            Logger.Info($"update notice state marked on title bar: {mark}");
        }
        catch (Exception ex)
        {
            Logger.Warn("update title mark failed: " + ex.Message);
        }
    }

    /// <summary>
    /// [2026-08-29 便携版通知兜底] 模态弹窗告知更新（便携 ZIP 无系统通知可靠渠道时的唯一
    /// 保证可达通道），按类型分流动作：
    /// - LauncherSecurity：点"是"打开启动器 GitHub Releases 下载页（点"否"仅本次跳过）；
    /// - Dsh：与 PromptDshUpdate 完全同构——确认后经 <see cref="DownloadDshUpdateStaged"/>
    ///   在后台下载、下次重启安装（dsh 更新不引导去下载启动器；拒绝则跳过该版本）。
    /// NotifyPending 经 UI 线程调用，此处可直接 MessageBox；绝不抛出。
    /// </summary>
    private static void ShowPortableUpdateDialog(string title, string body, PendingUpdate type, string latest)
    {
        try
        {
            var form = GetMainFormForDialog();
            try { form?.Activate(); } catch { /* 窗体已关闭则忽略 */ }
            if (type == PendingUpdate.Dsh)
            {
                var r = MessageBox.Show(form,
                    $"检测到 dsh 新版本 {latest}（当前 {_pendingLocal}）。\n是否在后台静默下载，下次重启时自动安装？",
                    "dsh 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                Trace($"portable dsh update ask answered: {r}");
                if (r != DialogResult.Yes)
                {
                    StagedUpdate.MarkSkippedDshVersion(latest); // 与 PromptDshUpdate 拒绝语义一致
                    return;
                }
                _ = Task.Run(() => DownloadDshUpdateStaged(form, latest));
                return;
            }
            var r2 = MessageBox.Show(form,
                title + "\n\n" + body + "\n\n便携版需手动下载更新：\n" + UpdateChecker.LauncherLatestReleaseUrl + "\n\n是否打开下载页？",
                title, MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            Trace($"portable update ask answered: {r2}");
            if (r2 == DialogResult.Yes)
                Managers.WebRuntimeInstaller.OpenExternally(UpdateChecker.LauncherLatestReleaseUrl);
            _pendingUpdate = PendingUpdate.None;
        }
        catch (Exception ex)
        {
            // 弹窗本身失败（窗体关闭/无主窗）：留痕，绝不让通知链路抛进调用方
            Logger.Warn($"portable update dialog failed: {ex.Message}");
        }
    }

    /// <summary>
    /// [2026-09 版本徽标] 标题栏 dsh 版本徽标点击 → 弹出版本信息窗（原生样式）：
    /// dsh 当前/最新版本 + 启动器当前/最新版本 + 启动器下载地址。
    /// 当前版本即时读取（发现层/程序集信息，与更新检查同源）；最新版本由窗体打开后
    /// 异步拉取（UpdateChecker 回退链，失败静默降级为"获取失败"）。弹窗本身绝不抛出
    /// ——版本信息是增值 UI，任何失败都不得反噬主窗。
    /// </summary>
    private static void ShowVersionInfoDialog(Form owner)
    {
        try
        {
            if (owner is null || owner.IsDisposed) return;
            try { owner.Activate(); } catch { /* 窗体未就绪则忽略 */ }
            using var dialog = new DshWeb.Windows.VersionInfoDialog(
                DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime().Version,
                UpdateChecker.CurrentLauncherVersion,
                ResolveDarkMode());
            dialog.ShowDialog(owner);
        }
        catch (Exception ex)
        {
            // 弹窗失败留痕：绝不静默吞掉，也不向版本徽标回调抛异常
            Logger.Warn("version info dialog failed", ctx: new { error = ex.Message });
        }
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
                    $"suppressing balloon. Manual: npm install -g {DshWeb.Domain.DshDiscovery.PackageName}@{version}");
                return;
            }
            // [issue #25 收口] 唯一通知通道：自绘卡片（不依赖托盘、不碰 WPN、不阻塞消息泵）
            var shown = Windows.NoticeCard.Present(GetMainFormForDialog(), "dsh 更新待应用",
                $"dsh {version} 主程序已下载。下次重启启动器后自动安装（需联网解析依赖，预计 1-2 分钟）。",
                TimeSpan.FromSeconds(15));
            if (!shown)
                Logger.Warn("pending-apply notice not presented", ctx: new { version });
        }
        catch (Exception ex)
        {
            Logger.Warn("pending-apply notice failed", ctx: new { error = ex.Message });
        }
    }

    private static void OnPendingBalloonClicked(object? s, EventArgs e)
    {
        var f = GetMainFormForDialog();
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
    private static void PromptDshUpdate(Form form, string latest, string? local)
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
                // [F14] 会话已进入退出编排：放弃"立即重启应用"（pending 保留，下次启动按决策处理）。
                if (SessionCts.IsCancellationRequested)
                {
                    Logger.Info("apply-restart: session shutting down; apply skipped (pending kept)");
                    return;
                }
                StopShellService(); // 停当前服务（含接管/本次拉起的）
                // 重启即应用路径同样优先本地 tarball（不 npx 现场拉主包）；tarball 缺失回退线上
                var pending = StagedUpdate.ReadPending();
                var localTarball = StagedUpdate.LocateTarball(pending.Version, pending.Tarball);
                var installSpec = localTarball ?? $"{DshWeb.Domain.DshDiscovery.PackageName}@{version}";
                // 任务二一致性：--no-audit --no-fund；快源优先、失败沿源序列降级
                string applyErrorTail = "";
                var applySources = Managers.ProcessRunner.GetNpmRegistrySources();
                if (Managers.ProcessRunner.TryNpmOverRegistries(applySources, srcIdx => Managers.ProcessRunner.RunNpmCommand(
                        $"install -g \"{installSpec}\" --no-audit --no-fund" + applySources[srcIdx],
                        out applyErrorTail, SessionCts.Token), "apply-restart", out _))
                {
                    if (SessionCts.IsCancellationRequested)
                    {
                        Logger.Info("apply-restart: session shutting down after install; service restart skipped");
                        return;
                    }
                    StagedUpdate.ClearPending();
                    Logger.Info($"staged dsh update applied (restart): {version}");
                    // [issue #28-4] 复用统一重启核心（ADR-024：拉起只走 StartDshServiceViaIdentity）。
                    // 手写"停-起-等"的旧版本缺 Suspend/Resume 与 PID 账本刷新：更新应用后的服务
                    // 因此脱离进程层监控，下一次运行期退出会被误判成启动自检失败。
                    var restartOutcome = await RestartCore!.RestartAsync("apply-restart");
                    if (restartOutcome == Lifecycle.ServiceRestartCoordinator.Outcome.Cancelled) return; // 会话收尾中
                    if (restartOutcome != Lifecycle.ServiceRestartCoordinator.Outcome.Ready)
                    {
                        var (failCode, failMsg) = restartOutcome == Lifecycle.ServiceRestartCoordinator.Outcome.StartFailed
                            ? (ErrorCodes.E2001, $"dsh {version} 已安装，但服务重启失败（无法拉起服务）。请查看统一日志后重新打开 dsh-launcher。")
                            : (ErrorCodes.E2004, $"dsh {version} 已安装，但重启后 60 秒内未就绪，请查看统一日志。");
                        try { form.BeginInvoke(() => ShowError(failCode, failMsg, log: false)); }
                        catch { /* 窗体已关闭 */ }
                        return;
                    }
                    // 就绪后刷新主窗到"当前服务 URL"：与运行期事务同一入口（投递到 UI 线程 +
                    // web 可用性判断都在 NavigateMainWebToCurrentServiceUrl 内），不再本地抄一份
                    // `if (MainWeb?.CoreWebView2 is not null)` 与吞掉的 catch。
                    PostNavigateToServiceUrl();
                }
                else
                {
                    Logger.Warn("staged dsh update apply (restart) failed: " + applyErrorTail,
                        ErrorCodes.E4002, new { version });
                    try { form.BeginInvoke(() => ShowError(ErrorCodes.E4002,
                        $"dsh {version} 更新安装失败。\n\n可稍后重试，或在命令行手动执行：\nnpm install -g {DshWeb.Domain.DshDiscovery.PackageName}@{version}",
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
        // Phase 4 · T2：暂存构建事务整体迁入 DshUpdateManager.BuildStagedUpdate（含清场、下载、
        // 构建、产物校验、pending 写入与失败时的 tarball 保全 + buildDir 清理）。
        // 组合根这里只剩"把结论变成用户看得见的东西"：标题栏终态、通知卡、E4001 模态。
        _lastBuildUiText = null; // 新构建：重置 UI 合流状态（防上次构建的节流窗口吞掉首帧）
        _lastBuildUiPercent = int.MinValue;
        if (SessionUpdates is not Managers.DshUpdateManager updates)
        {
            Logger.Error("staged update refused: 会话更新引擎未装配", ErrorCodes.E4001);
            UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Failed,
                ShellLogic.UpdateProgress.ComposeTerminalTitleText(success: false, latest, willRetry: false));
            return;
        }

        var o = updates.BuildStagedUpdate(latest, m => Trace(m),
            percent => UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Building,
                $"已构建更新 {percent}%（v{latest}）", percent / 100f),
            () => UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Building,
                $"正在构建更新（v{latest}）...", 0f)); // 初始不确定进度：脉冲态由标题栏定时器驱动

        switch (o.Result)
        {
            case Managers.DshUpdateManager.StagedBuildResult.UnsafeVersion:
                UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Failed,
                    $"更新版本号异常（{latest}），已中止。请稍后重试。", 0f);
                return;

            case Managers.DshUpdateManager.StagedBuildResult.DownloadFailed:
                // [2026-08 回归修复] 失败必须有可见结论：红色终态驻留 + E4001 弹窗
                UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Failed,
                    ShellLogic.UpdateProgress.ComposeTerminalTitleText(success: false, latest, willRetry: false));
                PostStagedModal(form, ErrorCodes.E4001, DownloadFailureText(latest, o.ErrorTail));
                return;

            case Managers.DshUpdateManager.StagedBuildResult.Success:
                var balloon = $"dsh {latest} 已在后台构建完成。下次重启启动器时将自动切换（秒级）。";
                // [issue #25 收口] 唯一通知通道；失败时仍有标题栏 Ready 终态驻留兜着。
                var shown = Windows.NoticeCard.Present(form, "dsh 更新已就绪", balloon, TimeSpan.FromSeconds(8));
                Logger.Info($"update success notification: card={shown}; dwell={BuildTerminalDwellMs}ms");
                UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Ready,
                    ShellLogic.UpdateProgress.ComposeTerminalTitleText(success: true, latest));
                return;

            case Managers.DshUpdateManager.StagedBuildResult.Threw:
                ShowStagedBuildFailure(form, latest, o.Detail ?? "未知异常", o.PreservedForRetry);
                return;

            case Managers.DshUpdateManager.StagedBuildResult.Cancelled:
                // 用户在关窗确认里选了"强制关闭"：会话正在离开，绝不再弹失败模态。
                UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Idle, "", 0f);
                return;

            default: // BuildFailed / MissingManifest / UnresolvableBin：状态已由 Manager 收口，此处只呈现
                ShowStagedBuildFailure(form, latest, BuildFailureReason(o.Result), o.PreservedForRetry);
                return;
        }
        // [T6b] 构建占用状态（BuildInProgress / 取消源）由 DshUpdateManager 自己的 try/finally
        // 负责清零——组合根不再从外面写。终态驻留由 UpdateBuildStatus 的定时器保活约 12s，
        // 因此这里也绝不无条件把标题栏清成 Idle（那会抹掉刚写上的 Failed/Ready 终态）。
    }

    /// <summary>构建失败的用户可读原因（按 Manager 返回的分类给，不再由 UI 猜）。</summary>
    private static string BuildFailureReason(Managers.DshUpdateManager.StagedBuildResult r) => r switch
    {
        Managers.DshUpdateManager.StagedBuildResult.MissingManifest =>
            $"构建产物不完整（缺少 {DshWeb.Domain.DshDiscovery.PackageName} 包清单）",
        Managers.DshUpdateManager.StagedBuildResult.UnresolvableBin =>
            "构建产物不完整（bin 入口无法解析，可能是 dsh 版本布局变更）",
        _ => "底层包管理器（pnpm/npm）构建运行时失败，详见日志",
    };

    /// <summary>下载失败文案：npm 环境缺失与网络失败要给不同的下一步（Phase 5 · F9：命令串走常量）。</summary>
    private static string DownloadFailureText(string latest, string? errorTail)
    {
        var reason = string.IsNullOrWhiteSpace(errorTail)
            ? "底层执行引擎未能启动 Node.js 环境" : errorTail;
        var hint = ShellLogic.NpmHelpers.IsNpmNotFoundError(errorTail)
            ? "未检测到 npm 环境，请确保已安装 Node.js 18+。"
            : "可稍后重试，或手动执行：npm install -g "
                + DshWeb.Domain.DshDiscovery.PackageName + "@" + latest;
        return $"dsh {latest} 下载失败。\n\n原因：{reason}\n\n{hint}";
    }

    /// <summary>把模态错误投递到 UI 线程；窗体已关闭时静默（退出竞态）。</summary>
    private static void PostStagedModal(Form form, string code, string message)
    {
        try { form.BeginInvoke(() => ShowError(code, message, log: false)); }
        catch (Exception ex) { Logger.Warn("staged update modal could not be shown: " + ex.Message); }
    }

    /// <summary>构建类失败的统一 UI 收口（状态迁移已在 DshUpdateManager.PreserveRetryState 完成）。</summary>
    private static void ShowStagedBuildFailure(Form form, string latest, string userReason, bool preserved)
    {
        UpdateBuildStatus(form, CustomTitleBar.BuildStatus.Failed,
            ShellLogic.UpdateProgress.ComposeTerminalTitleText(success: false, latest, willRetry: preserved));
        try
        {
            var noticeShown = Windows.NoticeCard.Present(form, "dsh 更新构建失败",
                $"dsh {latest} 后台构建失败。{(preserved ? "已保留下载，下次启动启动器时自动重试。" : "请稍后重试。")}",
                TimeSpan.FromSeconds(8), kind: ShellLogic.NoticeKind.Urgent);
            Logger.Info($"update failure notification: card={noticeShown}, preserved={preserved}");
        }
        catch (Exception ex)
        {
            // 通知失败不阻断——但留痕（空 catch 违反铁律三），下方 E4001 模态仍会报出来
            Logger.Warn("update failure notice card failed; E4001 dialog still reports it: " + ex.Message);
        }
        PostStagedModal(form, ErrorCodes.E4001,
            "dsh " + latest + " 更新构建失败。\n\n原因：" + userReason + "\n\n"
            + (preserved ? "已保留下载包，下次启动启动器时自动重试。" : "请稍后重试。"));
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
    /// [F4] 服务身份账本查询（组合根注入 ServiceManager）："我们拉起过的 dsh"= 本会话
    /// 内存缓存的 PID ∪ 历史 pid 文件账本（崩溃/接管后依然有效）。仅账本内的 node 进程
    /// 才允许被 Zombie 清理——账本外的 node（用户自己的程序）绝不强杀。
    /// </summary>
    private static bool IsKnownDshServicePid(int pid, int port)
    {
        if (pid <= 0) return false;
        if (pid == _servicePid) return true;
        try
        {
            var f = Managers.ServiceLifecycleOps.PidFilePath(DataDir, port);
            return File.Exists(f)
                   && int.TryParse(File.ReadAllText(f).Trim(), out var recorded)
                   && recorded == pid;
        }
        catch (Exception ex)
        {
            Logger.Warn($"known-pid lookup failed for pid={pid} port={port}: {ex.Message}");
            return false;
        }
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
            SessionApp?.MarkServiceAdoptedByShell(); // 接管即视为壳持有：退出时必须停它
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
    /// 则强制 /f /T；端口释放限时探测 + 占用者处置兜底；杀不干净保留 pid 文件由下次启动清扫。
    /// 实现见 ServiceLifecycleOps.StopService（ADR-024 迁移）。
    /// [issue #28-4] <paramref name="expectSelfRespawn"/> 仅由"运行期自愈重启"传 true：
    /// DSH 内置重启/自更新会让服务自我重新拉起，此时端口上的新进程是**我们自己人**，
    /// 必须接管而不是整树强杀（强杀会打断它正在进行的插件安装）。
    /// </summary>
    private static Managers.ServiceLifecycleOps.StopResult StopShellService(bool expectSelfRespawn = false)
    {
        // [2026-08-29 token 栅栏] 停服即记录"重启前 token"基线：随后的 WaitForFreshServiceToken
        // 以此判断新进程横幅是否已到（每次服务进程重启都会换新 token）。
        _tokenBeforeServiceRestart = _serviceTokenUrl;
        return Managers.ServiceLifecycleOps.StopService(DataDir, Target.Port, Target.Url, _servicePid,
            allowReplacementAdoption: expectSelfRespawn);
    }

    // ---- 关窗/退出异步化（2026-08 用户回归：点关闭后 UI 线程同步停服务卡 1.5s+） ----

    /// <summary>退出编排进行中标志（幂等闸门 + FormClosing 收尾放行）。状态机里对应
    /// <c>LifecycleState.ShuttingDown</c>：本标志管的是"清理只跑一次"这一物理互斥，
    /// 与状态轨迹互补（搬迁台账里登记为"可与状态机合并"的候选）。</summary>
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
        // [F14] 通知全部后台 Task（安全模式阶梯/重启服务/更新重拉）：会话正在收尾。
        try { SessionCts.Cancel(); } catch { /* 预期不会失败 */ }
        // [F13] 关停汇入状态机（Running → ShuttingDown）；非 Running 态由 RequestShutdown
        // 记日志吸收，绝不令退出编排自身失败。
        try { SessionApp?.RequestShutdown(); }
        catch (Exception ex) { Logger.Warn("shutdown: state machine transition failed: " + ex.Message); }
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
                ReadLifetimeMode(), ServerManagedExternally, SessionApp?.ServiceStartedByShell == true,
                WindowManager.Instance.TrayExitRequested);
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
                var dlg = GetMainFormForDialog(); // 更新提示托盘宿主（可能为 null，回退无 owner）
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
        var titleHeight = ShellLogic.DpiScale.Px(32, ShellLogic.DpiScale.Of(form.DeviceDpi));
        form.TitleBar = new CustomTitleBar(form, ResolveDarkMode())
        {
            // 四周 1px 窗口边框（Form.BackColor=边框色）
            Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2, titleHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        form.Controls.Add(form.TitleBar);
        form.HandleCreated += (_, _) => ApplyWindowShadow(form.Handle);
        // 弹窗与主窗共用同一条 chrome 布局规则（DshShellForm.LayoutChrome →
        // WindowGeometry.LayoutChromeRects）。此前这里内联重抄了一遍 32*scale，
        // 同一条规则两份实现 = 迟早一份改一份漏（#28 那轮"一处修一处漏"的同族）。
        form.MainWebView2 = popupWeb;
        popupWeb.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        form.LayoutChrome();
        // 弹窗与主窗同为 DshShellForm：DPI 变化的几何重算由它的 OnDpiChanged 统一负责
        // （此前这里又抄了一份 Rescale+LayoutChrome，且缺窗口尺寸跟随）。
        form.Controls.Add(popupWeb);
        form.FormClosing += (_, _) =>
        {
            try { popupWeb.Dispose(); } catch { /* ignore */ }
        };
        return (form, popupWeb);
    }

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
    private static Icon? TrayWhaleIcon => Windows.WindowIcons.BlueWhaleIcon;









    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_FRAME = 0x0400;
    private const uint RDW_ALLCHILDREN = 0x0080;

    private const int WM_NCACTIVATE = 0x0086;

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
            var icon = Windows.WindowIcons.BlueWhaleIcon;
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
