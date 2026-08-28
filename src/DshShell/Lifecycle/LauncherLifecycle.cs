namespace DshWeb.Lifecycle;

/// <summary>启动/退出生命周期状态（ADR-008：显式状态机替代 Main 面条隐式状态）。</summary>
public enum LifecycleState
{
    Idle,
    CheckingInstance,
    ResolvingRuntime,
    StartingService,
    WaitingForReadiness,
    InitializingUI,
    Running,
    ShuttingDown,
    Failed,
}

/// <summary>驱动生命周期转移的事件。</summary>
public enum LifecycleTrigger
{
    StartRequested,
    InstanceConfirmed,
    RuntimeResolved,
    RuntimeFailed,
    ServiceStarted,
    ServiceReady,
    ReadinessTimedOut,
    UIInitialized,
    WebViewCrashed,
    ShutdownRequested,
    Fatal,
}

/// <summary>
/// 纯内存生命周期状态机（与 UI/IO/线程解耦，可 Headless 单测）。
/// 只做"状态+触发 → 目标状态"的显式映射与 <see cref="StateChanged"/> 广播；
/// 副作用（服务拉起、运行时解析、建窗）由组合根 LauncherApp 依状态驱动。
/// 非法转移直接抛错（Fail-fast），让藏在 Main 面条代码里的隐式分支变成可测的不变式。
/// </summary>
public sealed class LauncherLifecycle
{
    private LifecycleState _state = LifecycleState.Idle;

    /// <summary>状态变化事件（携带新状态，供 UI/编排层驱动副作用）。</summary>
    public event EventHandler<LifecycleState>? StateChanged;

    public LifecycleState State => _state;

    // 显式转移表：缺省即非法转移
    private static readonly Dictionary<(LifecycleState, LifecycleTrigger), LifecycleState> Table = new()
    {
        [(LifecycleState.Idle, LifecycleTrigger.StartRequested)] = LifecycleState.CheckingInstance,

        [(LifecycleState.CheckingInstance, LifecycleTrigger.InstanceConfirmed)] = LifecycleState.ResolvingRuntime,
        [(LifecycleState.CheckingInstance, LifecycleTrigger.ShutdownRequested)] = LifecycleState.ShuttingDown,

        [(LifecycleState.ResolvingRuntime, LifecycleTrigger.RuntimeResolved)] = LifecycleState.StartingService,
        [(LifecycleState.ResolvingRuntime, LifecycleTrigger.RuntimeFailed)] = LifecycleState.Failed,

        [(LifecycleState.StartingService, LifecycleTrigger.ServiceStarted)] = LifecycleState.WaitingForReadiness,
        [(LifecycleState.StartingService, LifecycleTrigger.Fatal)] = LifecycleState.Failed,

        [(LifecycleState.WaitingForReadiness, LifecycleTrigger.ServiceReady)] = LifecycleState.InitializingUI,
        [(LifecycleState.WaitingForReadiness, LifecycleTrigger.ReadinessTimedOut)] = LifecycleState.ShuttingDown,

        [(LifecycleState.InitializingUI, LifecycleTrigger.UIInitialized)] = LifecycleState.Running,
        // WebView 渲染崩溃发生在 UI 初始化完成前（CoreWebView2 已建立、UIInitialized 未触发）：
        // 与 Running 同语义——崩溃被壳拦截自愈，不是终结事件（F13 接线时的合法事件面收敛）。
        [(LifecycleState.InitializingUI, LifecycleTrigger.WebViewCrashed)] = LifecycleState.InitializingUI,

        [(LifecycleState.Running, LifecycleTrigger.ShutdownRequested)] = LifecycleState.ShuttingDown,
        [(LifecycleState.Running, LifecycleTrigger.Fatal)] = LifecycleState.Failed,
        // WebView 渲染进程崩溃：被拦截并触发重载（组合根 HandleWebViewCrashed），自转移保持
        // Running——崩溃不会终结应用（这是"崩溃自愈而非崩溃"语义的状态机表达，测试见
        // LauncherLifecycleTests.WebViewCrash_WhileRunning_StaysRunning_WithEvent）。
        [(LifecycleState.Running, LifecycleTrigger.WebViewCrashed)] = LifecycleState.Running,

        [(LifecycleState.ShuttingDown, LifecycleTrigger.ShutdownRequested)] = LifecycleState.ShuttingDown, // 幂等：收尾可再次确认
    };

    /// <summary>
    /// 触发一次转移；Fatal 为任意非终结态的全局逃生口（→ Failed），非法转移抛错（Fail-fast）。
    /// [F17] 转移日志补全四要素中的三要素：旧状态 → 触发源 → 新状态（时间戳由 Logger 补）。
    /// 旧实现只记新状态，排障时无法回答"谁把它变成 Running/ShuttingDown"。
    /// </summary>
    public void Fire(LifecycleTrigger trigger)
    {
        if (trigger == LifecycleTrigger.Fatal)
        {
            // 全局逃生口：任意非终结态 → Failed；已终结（Failed/ShuttingDown）则幂等忽略
            if (_state is not (LifecycleState.Failed or LifecycleState.ShuttingDown))
            {
                var fatalFrom = _state;
                _state = LifecycleState.Failed;
                Logger.Info($"lifecycle: {fatalFrom} --Fatal--> Failed");
                StateChanged?.Invoke(this, _state);
            }
            return;
        }
        if (!Table.TryGetValue((_state, trigger), out var next))
            throw new InvalidOperationException(
                $"非法生命周期转移: {_state} + {trigger}");

        var from = _state;
        _state = next;
        Logger.Info($"lifecycle: {from} --{trigger}--> {next}");
        StateChanged?.Invoke(this, _state);
    }
}
