using DshWeb.Lifecycle;
using Xunit;

namespace DshShell.Tests.Lifecycle;

/// <summary>
/// [臃肿审计 Phase 3] 运行期事务的状态机覆盖。
///
/// 为什么这些用例存在：ADR-024 之后 Program.cs 仍回涨 880 行，根因不是没人想下沉，而是
/// LifecycleState 九态**全是启动/关停形状**——更新、安全模式、服务重启在转移表里根本没有落点，
/// 于是它们只能落到 <c>static bool</c> / <c>DateTime.MinValue</c> 哨兵上。本文件把那些流转
/// 变成显式状态与可测不变式：合法路径有断言、**重入必须抛**、退出在事务中途必须放行。
/// </summary>
public class LauncherLifecycleRuntimeTransitionTests
{
    /// <summary>把状态机推到 Running（与 LauncherLifecycleTests 的happy path 同序）。</summary>
    private static LauncherLifecycle ToRunning()
    {
        var lc = new LauncherLifecycle();
        lc.Fire(LifecycleTrigger.StartRequested);
        lc.Fire(LifecycleTrigger.InstanceConfirmed);
        lc.Fire(LifecycleTrigger.RuntimeResolved);
        lc.Fire(LifecycleTrigger.ServiceStarted);
        lc.Fire(LifecycleTrigger.ServiceReady);
        lc.Fire(LifecycleTrigger.UIInitialized);
        Assert.Equal(LifecycleState.Running, lc.State);
        return lc;
    }

    [Theory]
    [InlineData(LifecycleTrigger.RestartRequested, LifecycleState.RestartingService)]
    [InlineData(LifecycleTrigger.SafeModeEntryRequested, LifecycleState.EnteringSafeMode)]
    [InlineData(LifecycleTrigger.SafeModeExitRequested, LifecycleState.ExitingSafeMode)]
    [InlineData(LifecycleTrigger.UpdateApplyRequested, LifecycleState.ApplyingUpdate)]
    [InlineData(LifecycleTrigger.RollbackRequested, LifecycleState.RollingBackUpdate)]
    public void RuntimeTransaction_CanOnlyBeginFromRunning(LifecycleTrigger trigger, LifecycleState expected)
    {
        var lc = ToRunning();
        lc.Fire(trigger);
        Assert.Equal(expected, lc.State);
    }

    [Theory]
    [InlineData(LifecycleTrigger.RestartRequested, LifecycleTrigger.RestartCompleted)]
    [InlineData(LifecycleTrigger.RestartRequested, LifecycleTrigger.RestartFailed)]
    [InlineData(LifecycleTrigger.SafeModeEntryRequested, LifecycleTrigger.SafeModeEntered)]
    [InlineData(LifecycleTrigger.SafeModeExitRequested, LifecycleTrigger.SafeModeExited)]
    [InlineData(LifecycleTrigger.UpdateApplyRequested, LifecycleTrigger.UpdateApplied)]
    [InlineData(LifecycleTrigger.UpdateApplyRequested, LifecycleTrigger.UpdateApplyFailed)]
    [InlineData(LifecycleTrigger.RollbackRequested, LifecycleTrigger.RollbackCompleted)]
    [InlineData(LifecycleTrigger.RollbackRequested, LifecycleTrigger.RollbackFailed)]
    public void RuntimeTransaction_ReturnsToRunning_OnCompletion(LifecycleTrigger begin, LifecycleTrigger end)
    {
        var lc = ToRunning();
        lc.Fire(begin);
        lc.Fire(end);
        Assert.Equal(LifecycleState.Running, lc.State);
    }

    /// <summary>安全模式进入/退出失败 = 服务没起来：必须是 Failed，不能假装还在 Running。</summary>
    [Theory]
    [InlineData(LifecycleTrigger.SafeModeEntryRequested, LifecycleTrigger.SafeModeEntryFailed)]
    [InlineData(LifecycleTrigger.SafeModeExitRequested, LifecycleTrigger.SafeModeExitFailed)]
    public void SafeModeFailure_LandsInFailed(LifecycleTrigger begin, LifecycleTrigger fail)
    {
        var lc = ToRunning();
        lc.Fire(begin);
        lc.Fire(fail);
        Assert.Equal(LifecycleState.Failed, lc.State);
    }

    /// <summary>启动自检失败后仍允许用户选安全模式自救（Failed 是唯一的例外来源态）。</summary>
    [Fact]
    public void SafeModeEntry_IsOfferedAfterBootFailure()
    {
        var lc = new LauncherLifecycle();
        lc.Fire(LifecycleTrigger.StartRequested);
        lc.Fire(LifecycleTrigger.InstanceConfirmed);
        lc.Fire(LifecycleTrigger.RuntimeFailed);
        Assert.Equal(LifecycleState.Failed, lc.State);

        lc.Fire(LifecycleTrigger.SafeModeEntryRequested);
        Assert.Equal(LifecycleState.EnteringSafeMode, lc.State);
    }

    /// <summary>
    /// 重入必须抛：重启中再来一次重启 / 建安全模式中再来一次建安全模式，都是"两个事务并行
    /// 抢同一个服务进程"的前置条件——过去它表现为 issue #28 的重复拉起与误杀。
    /// </summary>
    [Theory]
    [InlineData(LifecycleTrigger.RestartRequested)]
    [InlineData(LifecycleTrigger.SafeModeEntryRequested)]
    [InlineData(LifecycleTrigger.SafeModeExitRequested)]
    [InlineData(LifecycleTrigger.UpdateApplyRequested)]
    [InlineData(LifecycleTrigger.RollbackRequested)]
    public void ReentrantTransaction_Throws_AndStateUnchanged(LifecycleTrigger trigger)
    {
        var lc = ToRunning();
        lc.Fire(trigger);
        var during = lc.State;

        var ex = Assert.Throws<InvalidOperationException>(() => lc.Fire(trigger));
        Assert.Contains("非法生命周期转移", ex.Message);
        Assert.Equal(during, lc.State);
    }

    /// <summary>
    /// 回滚 saga 自持状态：它复用的重启事务不得再投 RestartRequested（否则两个事务抢同一个
    /// 状态机）。这条锁住 ServiceRestartCoordinator.RestartAsync(driveLifecycleState:false) 的前提。
    /// </summary>
    [Fact]
    public void NestedRestartTrigger_RollbackStateMachine_RejectsIt()
    {
        var lc = ToRunning();
        lc.Fire(LifecycleTrigger.RollbackRequested);
        Assert.False(lc.CanFire(LifecycleTrigger.RestartRequested));
        Assert.Throws<InvalidOperationException>(() => lc.Fire(LifecycleTrigger.RestartRequested));
        Assert.Equal(LifecycleState.RollingBackUpdate, lc.State);
    }

    /// <summary">完成事件不得在未开事务时投递（防止"没重启却报重启完成"的假轨迹）。</summary>
    [Theory]
    [InlineData(LifecycleTrigger.RestartCompleted)]
    [InlineData(LifecycleTrigger.SafeModeEntered)]
    [InlineData(LifecycleTrigger.UpdateApplied)]
    [InlineData(LifecycleTrigger.RollbackCompleted)]
    public void CompletionWithoutActiveTransaction_Throws(LifecycleTrigger trigger)
    {
        var lc = ToRunning();
        Assert.Throws<InvalidOperationException>(() => lc.Fire(trigger));
        Assert.Equal(LifecycleState.Running, lc.State);
    }

    /// <summary>
    /// 事务进行中用户关窗必须放行到 ShuttingDown——否则"退出"会被一个瞬时态卡死，
    /// 而 Fire 的 Fail-Fast 会让退出路径直接抛异常（用户看到的是点关闭没反应/崩溃）。
    /// </summary>
    [Theory]
    [InlineData(LifecycleTrigger.RestartRequested)]
    [InlineData(LifecycleTrigger.SafeModeEntryRequested)]
    [InlineData(LifecycleTrigger.SafeModeExitRequested)]
    [InlineData(LifecycleTrigger.UpdateApplyRequested)]
    [InlineData(LifecycleTrigger.RollbackRequested)]
    public void ShutdownDuringTransaction_IsAllowed(LifecycleTrigger begin)
    {
        var lc = ToRunning();
        lc.Fire(begin);
        lc.Fire(LifecycleTrigger.ShutdownRequested);
        Assert.Equal(LifecycleState.ShuttingDown, lc.State);
    }

    /// <summary>每条运行期转移都要进 StateChanged 广播（轨迹是排障的唯一线索）。</summary>
    [Fact]
    public void RuntimeTransitions_AreAllBroadcast()
    {
        var lc = ToRunning();
        var seen = new List<LifecycleState>();
        lc.StateChanged += (_, s) => seen.Add(s);

        lc.Fire(LifecycleTrigger.RestartRequested);
        lc.Fire(LifecycleTrigger.RestartCompleted);

        Assert.Equal(new[] { LifecycleState.RestartingService, LifecycleState.Running }, seen);
    }

    /// <summary>Fatal 逃生口对新增瞬时态同样有效（不能出现"卡在 ApplyingUpdate 里 Fatal 不掉"）。</summary>
    [Theory]
    [InlineData(LifecycleTrigger.RestartRequested)]
    [InlineData(LifecycleTrigger.SafeModeEntryRequested)]
    [InlineData(LifecycleTrigger.UpdateApplyRequested)]
    [InlineData(LifecycleTrigger.RollbackRequested)]
    public void Fatal_FromEveryRuntimeTransactionState_ReachesFailed(LifecycleTrigger begin)
    {
        var lc = ToRunning();
        lc.Fire(begin);
        lc.Fire(LifecycleTrigger.Fatal);
        Assert.Equal(LifecycleState.Failed, lc.State);
    }
}
