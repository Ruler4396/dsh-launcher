namespace DshWeb;

using DshWeb.Lifecycle;
using DshWeb.Managers;

/// <summary>
/// 组合根 + 统一启动编排（v0.4.2 收尾）：替换 Program.RunStartupPipelineAsync 旧流水线，
/// 启动决策唯一由 LauncherLifecycle 状态机驱动；副作用（维护 IO/拉起服务/就绪探针/僵尸清理）
/// 经委托注入（组合根职责），LauncherApp 自身不引用 Program，杜绝隐式循环依赖。
///
/// Headless 可测：全部 Manager 可注入 Fake，副作用委托缺省 null（跳过），状态轨迹可订阅。
/// </summary>
public sealed class LauncherApp
{
    private readonly IRuntimeManager _runtime;
    private readonly IServiceManager _service;
    private readonly IWebViewManager _webview;
    private readonly IWindowManager _window;
    private readonly ITrayManager _tray;
    private readonly IDshUpdateManager? _updates;
    private readonly string? _serviceLogPath;
    private readonly LauncherLifecycle _lifecycle = new();
    private readonly Action<int>? _staleCleanup;

    // 供 Headless 测试观察状态机的当前状态
    public LifecycleState State => _lifecycle.State;

    /// <summary>状态转移广播（转发内部状态机；Headless 测试订阅以断言轨迹/UI 初始化事件时序）。</summary>
    public event EventHandler<LifecycleState>? StateChanged;

    // ---------------- 副作用委托（组合根注入；null = 跳过，供 Headless 测试） ----------------

    /// <summary>阶段 0：无 UI 的轻量维护 IO（日志轮转/数据迁移/自启落地/延迟应用更新等）。
    /// 入参为取消令牌：npm install -g 应用更新可达 30-60s，用户取消 Splash 时必须能中断（v0.4.0）。
    /// 组合根把 Splash 的 IProgress 桥接进 RunBackgroundMaintenance → ApplyPending →
    /// npm 实时日志逐行上报到 Splash（任务一 UI 联动：缓解"正在安装更新"期间的卡死焦虑）。</summary>
    public Action<CancellationToken>? BackgroundMaintenance { get; set; }

    /// <summary>拉起服务前的僵尸清扫 + 延迟更新应用。</summary>
    public Action? SweepStaleAndApplyUpdate { get; set; }

    /// <summary>首装全局安装失败的用户可见详情（[E1012] 展示用）；null = 未触发或已成功。</summary>
    public string? FirstRunProvisionError =>
        (_updates as DshUpdateManager)?.FirstRunProvisionError;

    /// <summary>
    /// 就绪轮询探针，返回 "ready"/"timeout"/"logerror"/"canceled"（含 dsh.log 错误标志语义）。
    /// 缺省用 <see cref="IServiceManager.WaitReadyAsync"/>（bool 语义 → ready/timeout）。
    /// </summary>
    public Func<CancellationToken, Task<string>>? ReadinessProbe { get; set; }

    /// <summary>就绪超时后的僵尸进程清理回调（Main 接入时注入 Program.SweepStaleServicePid；
    /// Headless 测试注入 Fake 断言"超时清理被触发"——见 LauncherAppScenarioTests）。</summary>
    public Action<int>? StaleCleanup => _staleCleanup;

    // ---------------- 目标服务（env 解析，契约与 ShellLogic.ResolveTarget 一致） ----------------

    /// <summary>DSH_WEB_PORT 覆盖的端口（缺省 3080）。</summary>
    public int Port { get; }

    /// <summary>DSH_WEB_URL 覆盖的服务地址（缺省 http://127.0.0.1:3080）。</summary>
    public string Url { get; }

    /// <summary>设置 DSH_WEB_URL 时视为外部托管（壳不拉起服务，只探测就绪）。</summary>
    public bool ServerManagedExternally { get; }

    // ---------------- 结果表面（RunStartupAsync 返回后供组合根读） ----------------

    public string? LastErrorCode { get; private set; }
    public string? LastErrorDetail { get; private set; }
    public string? WaitResult { get; private set; }
    public bool ServiceStartedByShell { get; private set; }

    /// <summary>组合根默认装配（测试可换自身构建的 Manager）。</summary>
    public LauncherApp(
        IRuntimeManager? runtime = null,
        IServiceManager? service = null,
        IWebViewManager? webview = null,
        IWindowManager? window = null,
        ITrayManager? tray = null,
        Action<int>? staleCleanup = null,
        IDshUpdateManager? updates = null,
        string? serviceLogPath = null)
    {
        _runtime = runtime ?? new RuntimeManager();
        _service = service ?? new ServiceManager();
        _webview = webview ?? new WebViewManager();
        _window = window ?? new WindowManager();
        _tray = tray ?? new TrayManager();
        _staleCleanup = staleCleanup;
        _updates = updates;
        _serviceLogPath = serviceLogPath;
        // 契约与 Program.Target 同源（ShellLogic.ResolveTarget）：DSH_WEB_URL → 外部托管；
        // DSH_WEB_PORT → 端口覆盖；缺省 http://127.0.0.1:3080。
        var (url, port) = ShellLogic.RuntimeConfig.ResolveTarget(
            Environment.GetEnvironmentVariable("DSH_WEB_URL"),
            Environment.GetEnvironmentVariable("DSH_WEB_PORT"));
        Url = url;
        Port = port;
        ServerManagedExternally = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSH_WEB_URL"));
        _lifecycle.StateChanged += (_, s) =>
        {
            Logger.Info($"lifecycle: {s}");
            StateChanged?.Invoke(this, s);
        };
    }

    // 五个 Manager 的读取表面（供外部/测试校验装配完整性）
    public IRuntimeManager Runtime => _runtime;
    public IServiceManager Service => _service;
    public IWebViewManager WebView => _webview;
    public IWindowManager Window => _window;
    public ITrayManager Tray => _tray;

    /// <summary>
    /// 驱动一次启动尝试，返回是否进入 Running。
    ///
    /// 线程契约：**可安全地从任意线程调用，且不阻塞调用线程**。本方法内所有可能阻塞的同步
    /// 副作用（维护 IO / 端口探测 / 运行时解析 / 服务拉起）一律包 Task.Run，保证方法在**首个
    /// await 即让出调用线程**——生产路径调用方是 SplashForm.OnShown 的 UI 线程：若此处同步
    /// 执行 TcpClient.Connect（本机可达 2s）等，窗口已显示但 UI 线程无法处理 WM_PAINT → 白屏/
    /// 组件延迟绘制（v0.4.2 回归根因，修复见 BackgroundMaintenance 与 NeedsStart 的 Task.Run）。
    /// 取消（ct）保持冒泡：OperationCanceledException 交由调用方（SplashForm 标记用户取消）。
    /// </summary>
    public async Task<bool> RunStartupAsync(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // ---- 阶段 0：无 UI 的轻量维护 IO（原 Main 同步项）——必须后台执行：内部含同步
        // PortOpen（TcpClient.Connect）、数据迁移与延迟应用更新（npm install -g 可能 30-60s）
        // 等 IO，同步调用会阻塞 UI 线程导致 Splash 白屏。----
        // 取消语义：Task.Run 的 ct 只在任务启动前生效；已运行中的 npm install 由
        // BackgroundMaintenance(ct) 内部转发到 RunNpmCommand → ct.Register Kill 进程树（Splash
        // 取消立即生效，不残留"点取消几十秒后才关"）。----
        // 任务一 UI 联动：后台 ApplyPendingDshUpdate 经组合根桥接把"正在应用更新 (vX)…"与
        // npm 实时日志上报到 Splash（进度流在组合根，见 RunLauncherAppPipelineAsync）。
        progress?.Report("正在准备启动环境…");
        if (BackgroundMaintenance is not null)
        {
            var maintenance = BackgroundMaintenance; // 捕获（组合根可能在方法内换属性）
            await Task.Run(() => maintenance(ct), ct);
        }

        // ---- 任务二 UI 告警：主日志曾被锁（fallback 已触发）→ 启动窗黄色提示（桥接层把
        // "[warn]" 前缀映射为 IsWarn）。避免用户误以为"日志没在写"而惊慌，诊断路径可被发现。----
        if (Logger.FallbackUsed)
        {
            progress?.Report("[warn] 日志文件被占用，部分日志已写入临时目录：" + Logger.FallbackPath);
        }

        // ---- E2E 测试钩子：模拟"后台启动耗时"，跳过真实服务逻辑（tests/DshShell.E2E）。
        // 设 0/缺省 = 正常流水线；设 >0 = 延迟该毫秒数后直接返回就绪。仅测试使用。
        if (TryReadTestDelay(out var delayMs))
        {
            await Task.Delay(delayMs, ct);
            return true;
        }

        _lifecycle.Fire(LifecycleTrigger.StartRequested);
        _lifecycle.Fire(LifecycleTrigger.InstanceConfirmed);

        // ---- ResolvingRuntime：运行时解析（缺 Node 时 Manager 内部先确认再下载，E1002 拒绝）。
        // 同步解析部分（ResolveExisting 读注册表/PATH）包后台，避免阻塞调用线程。----
        progress?.Report("正在准备 Node.js 运行环境…");
        RuntimeResolution rt;
        try
        {
            rt = await Task.Run(() => _runtime.EnsureRuntimeAsync(ct), ct);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不是"运行时失败"，交给调用方
        }
        catch (Exception ex)
        {
            // 非预期异常 = 编程不变式破坏：E9001 留痕，映射 RuntimeFailed（不悬停状态机）
            Logger.Error("runtime resolution crashed: " + ex.Message, ErrorCodes.E9001);
            _lifecycle.Fire(LifecycleTrigger.RuntimeFailed);
            return false;
        }
        if (!rt.Ok || rt.Identity is null)
        {
            // 预期失败（E1002-E1005）：记录具体错误码
            LastErrorCode = rt.ErrorCode ?? ErrorCodes.E1003;
            LastErrorDetail = rt.ErrorDetail ?? ErrorCodes.Describe(LastErrorCode);
            Logger.Error(LastErrorDetail, LastErrorCode);
            _lifecycle.Fire(LifecycleTrigger.RuntimeFailed);
            return false;
        }
        var identity = rt.Identity!;
        _lifecycle.Fire(LifecycleTrigger.RuntimeResolved); // → StartingService

        // ---- 首装链（ADR-024）：身份为 NpxCache（本机无任何物理安装）时经更新引擎
        // npm -g 安装组件，成功后重发现身份。失败响亮 E2001 收口（组合根按
        // StartupFailurePolicy 映射 [E1012] 展示真实根因），绝不静默落 npx 冷路径。----
        if (!ServerManagedExternally
            && identity.Source == DshWeb.Domain.DshSource.NpxCache
            && _updates is not null)
        {
            progress?.Report("正在安装 dsh 组件（首次运行，仅需一次）…");
            var provisioned = await Task.Run(() => _updates.EnsureDshInstalled(identity), ct);
            if (!provisioned)
            {
                LastErrorCode = ErrorCodes.E2001;
                LastErrorDetail = "未能自动安装 dsh 组件（详见统一日志）。";
                Logger.Error(LastErrorDetail, LastErrorCode,
                    new { detail = (_updates as DshUpdateManager)?.FirstRunProvisionError });
                _lifecycle.Fire(LifecycleTrigger.Fatal); // → Failed
                return false;
            }
            identity = DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime(); // 发现链立见新装 shim/版本
        }

        // ---- StartingService：端口三重验证（任务一：TCP + 进程身份 + 快速 HTTP）。
        // 修复根因：仅凭 TCP PortOpen 决定"跳过拉起"会误判僵尸服务（端口开但 HTTP 死）为
        // 健康，导致对半死服务傻等 180s。现在 Zombie → 清理并重启；Foreign → 快速失败 E2004；
        // Healthy → 跳过拉起；Closed → 正常拉起。探针含同步 TcpClient.Connect（本机 2s 级）
        // 与 taskkill，必须后台执行。----
        var portState = ServerManagedExternally
            ? ShellLogic.ServicePortState.Healthy // 外部托管：不拉起、不清理，直接探测就绪
            : await Task.Run(() => _service.ProbePort(Port, Url), ct);

        // Zombie 清理成功 / Closed → 正常拉起；Healthy → 跳过拉起。统一为 bool 决策，
        // 避免 switch 内 goto 穿透到后续语句（HappyPath 测试暴露：break 后落入 StartService 块
        // 二次触发 ServiceStarted → 非法转移 WaitingForReadiness + ServiceStarted）。
        var needsStart = portState switch
        {
            ShellLogic.ServicePortState.Healthy => false,
            ShellLogic.ServicePortState.Zombie => true, // 清理在下面执行后再拉起
            _ => true, // Closed / Foreign 处理见下（Foreign 提前返回）
        };

        if (portState == ShellLogic.ServicePortState.Foreign)
        {
            // 端口被其他程序占用：快速失败提示冲突（不傻等、不误杀无关进程）
            var foreignPid = await Task.Run(() => ShellLogic.ProcessManagement.GetProcessIdByPort(Port), ct);
            Logger.Error($"port {Port} is occupied by a non-dsh process; aborting startup",
                ErrorCodes.E2004, new { port = Port, pid = foreignPid, url = Url });
            LastErrorCode = ErrorCodes.E2004;
            // [F4] Foreign 现含两类：非 node 程序占用 / 账本外的 node（绝不误杀，明确告知用户）。
            LastErrorDetail = $"端口 {Port} 已被其他程序占用（PID {(foreignPid > 0 ? foreignPid.ToString() : "未知")}），且无 dsh HTTP 响应。请释放该端口后重试；若该端口被您自己的 Node.js 程序占用，请先退出它。";
            _lifecycle.Fire(LifecycleTrigger.Fatal); // → Failed
            return false;
        }

        if (portState == ShellLogic.ServicePortState.Zombie)
        {
            // 僵尸服务：TCP 开但 HTTP 不通、占用者是 node → 强杀进程树后重新拉起
            progress?.Report("检测到残留的 dsh 服务，正在清理并重新启动…");
            var cleaned = await Task.Run(() => _service.KillZombieTree(Port), ct);
            if (!cleaned)
            {
                // 清理失败（杀不干净/端口未释放）：快速失败，不让用户傻等 180s
                LastErrorCode = ErrorCodes.E2004;
                LastErrorDetail = $"dsh 服务残留进程无法清理（端口 {Port} 被僵尸进程占用且 HTTP 无响应）。请关闭占用进程后重试。";
                Logger.Error(LastErrorDetail, LastErrorCode, new { port = Port, url = Url });
                _lifecycle.Fire(LifecycleTrigger.Fatal); // StartingService + Fatal → Failed
                return false;
            }
        }

        if (needsStart)
        {
            progress?.Report("正在启动 dsh 服务…");
            // 【ADR-024】服务拉起只信 Identity：node.exe × DshEntryJsPath 直启（Manager 契约），
            // 不再有 wscript/vbs/cmd 中间层。外部托管（ServerManagedExternally）永不进入此分支。
            var startOk = await Task.Run(() =>
            {
                SweepStaleAndApplyUpdate?.Invoke(); // 僵尸清扫 + 延迟更新（IO，后台）
                return _service.Start(identity, Port, _serviceLogPath);
            }, ct);
            if (!startOk)
            {
                LastErrorCode = ErrorCodes.E2001;
                LastErrorDetail = identity.CanLaunchDirectly
                    ? $"dsh 服务启动失败（{Url}）。请查看统一日志。"
                    : $"未找到可用的 dsh 运行时身份（node/JS 入口缺失），无法自动拉起 dsh 服务（{Url}）。";
                Logger.Error(LastErrorDetail, LastErrorCode,
                    new { source = identity.Source.ToString(), entry = identity.DshEntryJsPath });
                _lifecycle.Fire(LifecycleTrigger.Fatal); // StartingService + Fatal → Failed
                return false;
            }
            ServiceStartedByShell = true;
            _lifecycle.Fire(LifecycleTrigger.ServiceStarted);
        }
        else
        {
            _lifecycle.Fire(LifecycleTrigger.ServiceStarted); // Healthy：→ WaitingForReadiness
            progress?.Report("正在检查 dsh 服务…");
        }

        // ---- WaitingForReadiness：轮询 HTTP 就绪（异步探测；取消/超时/日志报错三态）----
        progress?.Report("正在等待 dsh 服务就绪…");
        // [修复] 就绪预算判定所需的 dsh 身份发现挪入后台线程：旧实现在 await 续体（UI 线程）
        // 上同步调 DiscoverCurrentRuntime()——全局安装场景会 spawn node --version 探测（数百 ms
        // ～3s，旧实现更可无限阻塞），Splash 因此冻结（用户回归"点击很久才有窗口"）。
        // 生产路径走组合根注入的 ReadinessProbe（自带后台线程与预算逻辑）；Headless 默认分支
        // 同样把发现+预算计算包进 Task.Run，保证方法在首个 await 即让出调用线程。
        var waitResult = ReadinessProbe is not null
            ? await ReadinessProbe(ct)
            : await Task.Run(async () =>
            {
                var networkFallback = DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime().Source
                    == DshWeb.Domain.DshSource.NpxCache;
                var budget = ShellLogic.ServiceReadiness.GetPollBudgetSeconds(networkFallback);
                return await _service.WaitReadyAsync(Port, TimeSpan.FromSeconds(budget), ct)
                    ? "ready" : "timeout";
            }, ct);
        WaitResult = waitResult;
        if (waitResult != "ready")
        {
            _lifecycle.Fire(LifecycleTrigger.ReadinessTimedOut); // → ShuttingDown
            Logger.Error($"service readiness failed: {waitResult}", ErrorCodes.E2002);
            _staleCleanup?.Invoke(Port); // 超时清理：Kill 孤儿进程（E2005 语义）
            return false;
        }

        // ---- UI 装配（WebView/Window/Tray 由组合根在返回后驱动）→ Running ----
        _lifecycle.Fire(LifecycleTrigger.ServiceReady);   // → InitializingUI
        _lifecycle.Fire(LifecycleTrigger.UIInitialized);  // → Running
        return true;
    }

    /// <summary>
    /// WebView 渲染崩溃事件入口：状态机自转移（Running→Running）广播"崩溃已被拦截并触发重载"，
    /// 而非让应用整体崩溃。WebViewManager.ProcessFailed → 本方法由组合根接线（F13）。
    /// 终结/关停态（ShuttingDown/Failed）下崩溃事件无意义——记日志吸收，不触发转移
    /// （避免向状态机投递非法转移触发其 Fail-Fast）。
    /// </summary>
    public void HandleWebViewCrashed()
    {
        var state = _lifecycle.State;
        if (state is LifecycleState.Running or LifecycleState.InitializingUI)
            _lifecycle.Fire(LifecycleTrigger.WebViewCrashed);
        else
            Logger.Info($"webview crash while {state}; state machine self-transition skipped");
    }

    /// <summary>
    /// [F13] 运行期关停请求进入状态机：Running → ShuttingDown（组合根 BeginShutdownAsync 首行调用）。
    /// 此前退出编排完全绕过状态机（ShutdownRequested 触发器零调用），Running 期会话状态由
    /// Program 静态字段组表达——本方法是"关停汇入集中转移入口"的接线点。
    /// 非 Running 态（Splash 流水线中途的罕见退出请求）不转移、仅记日志：状态机 Fail-Fast
    /// 语义保留给编程错误，运行期正常关停不应令退出编排自身崩溃。
    /// </summary>
    public bool RequestShutdown()
    {
        var state = _lifecycle.State;
        if (state != LifecycleState.Running)
        {
            Logger.Info($"shutdown requested while {state}; state machine transition skipped");
            return false;
        }
        _lifecycle.Fire(LifecycleTrigger.ShutdownRequested);
        return true;
    }

    private bool TryReadTestDelay(out int ms)
    {
        ms = 0;
        if (Environment.GetEnvironmentVariable("DSH_TEST_SPLASH_DELAY_MS") is { } raw
            && int.TryParse(raw, out var v) && v >= 0)
        {
            ms = v;
            return true;
        }
        return false;
    }
}
