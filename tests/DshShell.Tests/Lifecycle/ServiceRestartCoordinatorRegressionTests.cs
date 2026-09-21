using DshWeb;
using DshWeb.Lifecycle;
using Xunit;

namespace DshShell.Tests.Lifecycle;

/// <summary>
/// [臃肿审计 Phase 4 · T1] 重启预算与升级分支的 Headless 回归（零真实进程：协作面全注入）。
///
/// 锁的是一条真实缺陷：冷却窗若拿"上次**成功**"计时，则一条一直失败的重启链会让
/// <c>now - last &gt; cooldown</c> 永远成立 → 预算每次被判成新 → 尝试数永远停在 1 →
/// "连续 N 次后升级为可见询问"这条分支**不可达**，服务反复起不来时用户看到的现象是
/// 壳无限静默重启、永远不告诉他。修法是把计时锚点移到"上次尝试"，且与预算决策同锁原子完成。
/// </summary>
public class ServiceRestartCoordinatorRegressionTests
{
    private int _restartCalls;
    private int _escalations;

    private ServiceRestartCoordinator Make(bool ready = false, bool shuttingDown = false)
    {
        _restartCalls = 0;
        _escalations = 0;
        var deps = new ServiceRestartCoordinator.Dependencies(
            Trace: _ => { },
            SessionShuttingDown: () => shuttingDown,
            StopService: _ => 0,
            StartViaIdentity: () => (true, false),
            WaitForFreshToken: _ => { },
            IsReady: () => ready,
            RecordPid: () => { },
            ResolvePid: () => 4242,
            ApplySafeModeVisibility: _ => { },
            SuspendMonitor: () => { },
            StopMonitor: () => { },
            ResumeMonitor: _ => { },
            SafeModeActive: () => false,
            Port: 3080,
            TryFireLifecycle: _ => true);
        return new ServiceRestartCoordinator(deps);
    }

    private void Pump(ServiceRestartCoordinator c)
        => c.OnServiceExited(
            1,
            escalateToUserVisibleAsk: (_, _) => _escalations++,
            showError: (_, _, _) => { },
            mainFormProvider: () => null,
            navigateToServiceUrl: () => { });

    /// <summary>连续失败的重启链必须能走到升级——这是本次修复的核心断言。</summary>
    [Fact]
    public void RepeatedFailures_EscalateWithinCooldownWindow()
    {
        var c = Make(ready: false);   // 重启永远不就绪 = 一直失败

        for (var i = 0; i < ServiceRestartCoordinator.MaxRuntimeRestarts; i++)
        {
            Pump(c);
            Assert.Equal(0, _escalations); // 预算内不得打扰用户
        }

        Pump(c); // 第 MaxRuntimeRestarts+1 次
        Assert.Equal(1, _escalations);
    }

    /// <summary>预算内绝不升级；且每次尝试都推进计数（不回到 1）。</summary>
    [Fact]
    public void WithinBudget_NoEscalation_AttemptsAdvance()
    {
        var c = Make(ready: false);
        Pump(c);
        Assert.Equal(1, c.CurrentRestartAttemptsForTest());
        Pump(c);
        Assert.Equal(2, c.CurrentRestartAttemptsForTest());
        Assert.Equal(0, _escalations);
    }

    /// <summary>会话已进入退出编排：一律吸收，既不重启也不升级（否则退出后留下无主服务）。</summary>
    [Fact]
    public void ShuttingDown_AbsorbsSilently()
    {
        var c = Make(ready: false, shuttingDown: true);
        for (var i = 0; i < 6; i++) Pump(c);
        Assert.Equal(0, _escalations);
    }

    [Fact]
    public async Task RestartAsync_ReportsReady_AndStampsSilentWindow()
    {
        var c = Make(ready: true);
        Assert.Null(c.LastShellRestartUtc);
        var outcome = await c.RestartAsync("test");
        Assert.Equal(ServiceRestartCoordinator.Outcome.Ready, outcome);
        Assert.NotNull(c.LastShellRestartUtc); // BootRecoveryPolicy 的静默窗锚点
    }

    [Fact]
    public async Task RestartAsync_StartFailure_ReturnsStartFailedWithoutThrowing()
    {
        var deps = new ServiceRestartCoordinator.Dependencies(
            _ => { }, () => false, _ => 0, () => (false, false), _ => { }, () => false,
            () => { }, () => 0, _ => { }, () => { }, () => { }, _ => { }, () => false, 3080,
            _ => true);
        var c = new ServiceRestartCoordinator(deps);
        Assert.Equal(ServiceRestartCoordinator.Outcome.StartFailed, await c.RestartAsync("test"));
    }

    /// <summary>[审查 N6 2026-09-21] 事务中途抛异常必须折算成 StartFailed 而不是漏出来——
    /// 组合根的"退出安全模式"路径是 fire-and-forget await，异常若不被兜住就是未观察任务异常：
    /// 零留痕、标题栏永停"正在退出安全模式…"（全仓实测无 UnobservedTaskException 钩子）。</summary>
    [Fact]
    public async Task RestartAsync_TransactionThrows_ReturnsStartFailedWithoutThrowing()
    {
        var deps = new ServiceRestartCoordinator.Dependencies(
            _ => { }, () => false, _ => throw new InvalidOperationException("boom (test)"),
            () => (true, false), _ => { }, () => true,
            () => { }, () => 0, _ => { }, () => { }, () => { }, _ => { }, () => false, 3080,
            _ => true);
        var c = new ServiceRestartCoordinator(deps);
        Assert.Equal(ServiceRestartCoordinator.Outcome.StartFailed, await c.RestartAsync("test"));
    }

    /// <summary>[审查 N3 裁决固化] 状态机拒绝 RestartRequested 时重启**照做**（上层事务自持状态
    /// 的在途调用方是合法形状，如退出安全模式），差别只在留痕。此用例把"拒绝≠阻断"钉住，
    /// 防止后来者顺手把它改成 abort 而打断退出安全模式链。</summary>
    [Fact]
    public async Task RestartAsync_RefusedByStateMachine_StillPerformsTheRestart()
    {
        var started = 0;
        var deps = new ServiceRestartCoordinator.Dependencies(
            _ => { }, () => false, _ => 0, () => { started++; return (true, false); }, _ => { }, () => true,
            () => { }, () => 0, _ => { }, () => { }, () => { }, _ => { }, () => false, 3080,
            _ => false);
        var c = new ServiceRestartCoordinator(deps);
        Assert.Equal(ServiceRestartCoordinator.Outcome.Ready, await c.RestartAsync("test"));
        Assert.Equal(1, started);
    }

    /// <summary>
    /// 结构闸：事务与预算不得回流组合根。G1 只挡体量、挡不住"换个名字搬回来"，
    /// 所以这里按符号名钉。
    /// </summary>
    [Fact]
    public void CompositionRootHoldsNoRestartBudgetOrTransaction()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DshShell", "Program.cs"));
        foreach (var banned in new[] { "RestartDshServiceCoreAsync", "ServiceRestartOutcome",
                                        "_runtimeRestartAttempts", "_lastRuntimeRestartAttemptUtc",
                                        "_lastShellRestartUtc", "RestartBudgetSync" })
            Assert.DoesNotContain(banned, src);
        Assert.Contains("RestartCore!.RestartAsync(", src);
        Assert.Contains("Lifecycle.ServiceRestartCoordinator.Outcome.Ready", src);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "DshShell"))) d = d.Parent;
        return d!.FullName;
    }
}
