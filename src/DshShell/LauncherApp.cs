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
    private readonly LauncherLifecycle _lifecycle = new();
    private readonly Action<int>? _staleCleanup;

    // 供 Headless 测试观察状态机的当前状态
    public LifecycleState State => _lifecycle.State;

    /// <summary>状态转移广播（转发内部状态机；Headless 测试订阅以断言轨迹/UI 初始化事件时序）。</summary>
    public event EventHandler<LifecycleState>? StateChanged;

    // ---------------- 副作用委托（组合根注入；null = 跳过，供 Headless 测试） ----------------

    /// <summary>阶段 0：无 UI 的轻量维护 IO（日志轮转/数据迁移/自启落地等）。</summary>
    public Action? BackgroundMaintenance { get; set; }

    /// <summary>拉起服务前的僵尸清扫 + 延迟更新应用。</summary>
    public Action? SweepStaleAndApplyUpdate { get; set; }

    /// <summary>拉起 dsh 服务（wscript start-dsh.vbs）。返回 false 表示拉起失败（E2001）。</summary>
    public Func<bool>? StartService { get; set; }

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
        Action<int>? staleCleanup = null)
    {
        _runtime = runtime ?? new RuntimeManager();
        _service = service ?? new ServiceManager();
        _webview = webview ?? new WebViewManager();
        _window = window ?? new WindowManager();
        _tray = tray ?? new TrayManager();
        _staleCleanup = staleCleanup;
        // 契约与 Program.Target 同源（ShellLogic.ResolveTarget）：DSH_WEB_URL → 外部托管；
        // DSH_WEB_PORT → 端口覆盖；缺省 http://127.0.0.1:3080。
        var (url, port) = ShellLogic.ResolveTarget(
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
        // PortOpen（TcpClient.Connect）、数据迁移与延迟更新应用（npm install -g 可能 30-60s）
        // 等 IO，同步调用会阻塞 UI 线程导致 Splash 白屏。----
        progress?.Report("正在准备启动环境…");
        if (BackgroundMaintenance is not null)
            await Task.Run(BackgroundMaintenance, ct);

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
        RuntimeResult rt;
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
        if (!rt.Ok)
        {
            // 预期失败（E1002-E1005）：记录具体错误码（此前 Failed() 丢码、且误用 Ready 判定）
            LastErrorCode = rt.ErrorCode ?? ErrorCodes.E1003;
            LastErrorDetail = rt.ErrorDetail ?? ErrorCodes.Describe(LastErrorCode);
            Logger.Error(LastErrorDetail, LastErrorCode);
            _lifecycle.Fire(LifecycleTrigger.RuntimeFailed);
            return false;
        }
        _lifecycle.Fire(LifecycleTrigger.RuntimeResolved); // → StartingService

        // ---- StartingService：端口已开/外部托管 → 直接探测；否则清扫+拉起。
        // NeedsStart 内部是同步 TcpClient.Connect（本机 2s 级），必须后台执行。----
        var portOpen = await Task.Run(
            () => ServerManagedExternally || !_service.NeedsStart(Port), ct);
        if (portOpen)
        {
            _lifecycle.Fire(LifecycleTrigger.ServiceStarted); // → WaitingForReadiness
            progress?.Report("正在检查 dsh 服务…");
        }
        else
        {
            progress?.Report("正在启动 dsh 服务…");
            var startOk = await Task.Run(() =>
            {
                SweepStaleAndApplyUpdate?.Invoke(); // 僵尸清扫 + 延迟更新（IO，后台）
                return StartService is null || StartService(); // 未注入（Headless）视为成功
            }, ct);
            if (!startOk)
            {
                LastErrorCode = ErrorCodes.E2001;
                LastErrorDetail = $"未找到 start-dsh.vbs，无法自动拉起 dsh 服务（{Url}）。";
                Logger.Error(LastErrorDetail, LastErrorCode);
                _lifecycle.Fire(LifecycleTrigger.Fatal); // StartingService + Fatal → Failed
                return false;
            }
            ServiceStartedByShell = true;
            _lifecycle.Fire(LifecycleTrigger.ServiceStarted);
        }

        // ---- WaitingForReadiness：轮询 HTTP 就绪（异步探测；取消/超时/日志报错三态）----
        progress?.Report("正在等待 dsh 服务就绪…");
        var waitResult = ReadinessProbe is not null
            ? await ReadinessProbe(ct)
            : await _service.WaitReadyAsync(Port, TimeSpan.FromSeconds(180), ct)
                ? "ready" : "timeout";
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
    /// 而非让应用整体崩溃。WebViewManager.ProcessFailed → 本方法由组合根接线。
    /// </summary>
    public void HandleWebViewCrashed() => _lifecycle.Fire(LifecycleTrigger.WebViewCrashed);

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
