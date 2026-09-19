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
    /// <summary>运行期服务重启中（自愈或用户确认）：瞬时态，完成/失败均回 Running。</summary>
    RestartingService,
    /// <summary>进入安全模式的事务进行中（建 profile→激活→停服→拉起→验证）。</summary>
    EnteringSafeMode,
    /// <summary>退出安全模式的事务进行中。</summary>
    ExitingSafeMode,
    /// <summary>更新应用/暂存构建事务进行中。</summary>
    ApplyingUpdate,
    /// <summary>更新回滚 saga 进行中（还原数据 → 隔离/降级 → 旧版重启 → 重挂监控）。</summary>
    RollingBackUpdate,
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

    // ---- 运行期事务（臃肿审计 Phase 3：此前这些流转在状态机表里根本没有落点，
    //      只能落到 Program.cs 的静态标志上——那正是 static bool 控流程的成因）----
    /// <summary>请求重启服务（自愈或用户确认）。</summary>
    RestartRequested,
    /// <summary>重启完成且已确认就绪。</summary>
    RestartCompleted,
    /// <summary>重启失败：回到 Running 并由调用方升级为可见询问（服务可残留旧态，但应用不终结）。</summary>
    RestartFailed,
    SafeModeEntryRequested,
    SafeModeEntered,
    SafeModeEntryFailed,
    SafeModeExitRequested,
    SafeModeExited,
    SafeModeExitFailed,
    UpdateApplyRequested,
    UpdateApplied,
    UpdateApplyFailed,
    /// <summary>启动自检失败 × 更新回滚闸门已武装 → 开始回滚 saga。</summary>
    RollbackRequested,
    /// <summary>回滚完成且旧版服务已就绪。</summary>
    RollbackCompleted,
    /// <summary>回滚后旧版没能起来/没就绪：界面仍在，错误由调用方可见化。</summary>
    RollbackFailed,
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

        // ---- 运行期事务：一律"从稳态出发、回到稳态"，瞬时态之间的重入**故意不入表**——
        //      重启中再来一次重启 = 编程错误，交给 Fire 的 Fail-Fast 抛错暴露。
        //      调用方须照 HandleWebViewCrashed / RequestShutdown 的先例先判态再 Fire。----
        [(LifecycleState.Running, LifecycleTrigger.RestartRequested)] = LifecycleState.RestartingService,
        [(LifecycleState.RestartingService, LifecycleTrigger.RestartCompleted)] = LifecycleState.Running,
        // 重启失败不终结应用（界面还在、用户会看到询问），但绝不能留在 RestartingService
        [(LifecycleState.RestartingService, LifecycleTrigger.RestartFailed)] = LifecycleState.Running,

        [(LifecycleState.Running, LifecycleTrigger.SafeModeEntryRequested)] = LifecycleState.EnteringSafeMode,
        // 启动自检失败后用户可选安全模式：Failed 是唯一允许"再起来"的非稳态来源
        [(LifecycleState.Failed, LifecycleTrigger.SafeModeEntryRequested)] = LifecycleState.EnteringSafeMode,
        [(LifecycleState.EnteringSafeMode, LifecycleTrigger.SafeModeEntered)] = LifecycleState.Running,
        [(LifecycleState.EnteringSafeMode, LifecycleTrigger.SafeModeEntryFailed)] = LifecycleState.Failed,

        [(LifecycleState.Running, LifecycleTrigger.SafeModeExitRequested)] = LifecycleState.ExitingSafeMode,
        [(LifecycleState.ExitingSafeMode, LifecycleTrigger.SafeModeExited)] = LifecycleState.Running,
        [(LifecycleState.ExitingSafeMode, LifecycleTrigger.SafeModeExitFailed)] = LifecycleState.Failed,

        [(LifecycleState.Running, LifecycleTrigger.UpdateApplyRequested)] = LifecycleState.ApplyingUpdate,
        [(LifecycleState.ApplyingUpdate, LifecycleTrigger.UpdateApplied)] = LifecycleState.Running,
        [(LifecycleState.ApplyingUpdate, LifecycleTrigger.UpdateApplyFailed)] = LifecycleState.Running,

        // 更新回滚 saga：启动自检失败时应用仍是 Running（服务起来过又被判死），故只从稳态出发。
        [(LifecycleState.Running, LifecycleTrigger.RollbackRequested)] = LifecycleState.RollingBackUpdate,
        // 回滚失败不终结应用（界面还在、用户会看到 E4003），但绝不能停在 RollingBackUpdate
        [(LifecycleState.RollingBackUpdate, LifecycleTrigger.RollbackCompleted)] = LifecycleState.Running,
        [(LifecycleState.RollingBackUpdate, LifecycleTrigger.RollbackFailed)] = LifecycleState.Running,

        // 事务进行中用户关窗必须放行（否则"退出"被瞬时态卡死）；不入表即抛错，那会变成退出路径崩溃
        [(LifecycleState.RestartingService, LifecycleTrigger.ShutdownRequested)] = LifecycleState.ShuttingDown,
        [(LifecycleState.EnteringSafeMode, LifecycleTrigger.ShutdownRequested)] = LifecycleState.ShuttingDown,
        [(LifecycleState.ExitingSafeMode, LifecycleTrigger.ShutdownRequested)] = LifecycleState.ShuttingDown,
        [(LifecycleState.ApplyingUpdate, LifecycleTrigger.ShutdownRequested)] = LifecycleState.ShuttingDown,
        [(LifecycleState.RollingBackUpdate, LifecycleTrigger.ShutdownRequested)] = LifecycleState.ShuttingDown,
    };

    /// <summary>
    /// 触发一次转移；Fatal 为任意非终结态的全局逃生口（→ Failed），非法转移抛错（Fail-fast）。
    /// [F17] 转移日志补全四要素中的三要素：旧状态 → 触发源 → 新状态（时间戳由 Logger 补）。
    /// 旧实现只记新状态，排障时无法回答"谁把它变成 Running/ShuttingDown"。
    /// </summary>
    /// <summary>
    /// 该触发源在当前状态下是否是【合法】转移（不改状态、不抛错）。
    /// 运行期事务的入口用它先判再投：非稳态时记日志吸收，而不是向状态机投递非法转移
    /// 把自己的退出/自愈路径炸掉——Fail-Fast 语义完整保留给真正的编程错误
    /// （直接调 Fire 仍然会抛）。与 RequestShutdown / HandleWebViewCrashed 同法。
    /// </summary>
    public bool CanFire(LifecycleTrigger trigger)
        => trigger == LifecycleTrigger.Fatal
           || (_state is not (LifecycleState.Failed or LifecycleState.ShuttingDown)
               && Table.ContainsKey((_state, trigger)));

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
