using DshWeb.Lifecycle;
using Xunit;

namespace DshShell.Tests.Lifecycle;

/// <summary>
/// LauncherLifecycle 的 Headless 测试：不启动 UI / 不启动 Node 进程，只驱动纯状态机，
/// 验证启动、超时、失败、非法转移等路径——这是 Main 面条代码最缺、最易回归的层
///（ADR-008 回归护栏，重构接线前先锁定行为）。
/// </summary>
public class LauncherLifecycleTests
{
    /// <summary>驱动 happy path 到 Running，返回过程中依次到过的状态。</summary>
    private static List<LifecycleState> DriveToRunning(LauncherLifecycle fs)
    {
        var states = new List<LifecycleState>();
        fs.StateChanged += (_, s) => states.Add(s);

        fs.Fire(LifecycleTrigger.StartRequested);      // Idle -> CheckingInstance
        fs.Fire(LifecycleTrigger.InstanceConfirmed);   // -> ResolvingRuntime
        fs.Fire(LifecycleTrigger.RuntimeResolved);     // -> StartingService
        fs.Fire(LifecycleTrigger.ServiceStarted);      // -> WaitingForReadiness
        fs.Fire(LifecycleTrigger.ServiceReady);        // -> InitializingUI
        fs.Fire(LifecycleTrigger.UIInitialized);       // -> Running
        return states;
    }

    [Fact]
    public void HappyPath_EndsAtRunning_WithExpectedOrder()
    {
        var fs = new LauncherLifecycle();
        var order = DriveToRunning(fs);

        Assert.Equal(LifecycleState.Running, fs.State);
        Assert.Equal(
            new[]
            {
                LifecycleState.CheckingInstance, LifecycleState.ResolvingRuntime,
                LifecycleState.StartingService, LifecycleState.WaitingForReadiness,
                LifecycleState.InitializingUI, LifecycleState.Running,
            },
            order);
    }

    [Fact]
    public void ReadinessTimeout_TransitionsToShuttingDown()
    {
        var fs = new LauncherLifecycle();
        // 到 WaitingForReadiness
        fs.Fire(LifecycleTrigger.StartRequested);
        fs.Fire(LifecycleTrigger.InstanceConfirmed);
        fs.Fire(LifecycleTrigger.RuntimeResolved);
        fs.Fire(LifecycleTrigger.ServiceStarted);
        Assert.Equal(LifecycleState.WaitingForReadiness, fs.State);

        // 模拟就绪探测超时 → ShuttingDown（组合根在此映射 E2002，状态机只管状态）
        fs.Fire(LifecycleTrigger.ReadinessTimedOut);
        Assert.Equal(LifecycleState.ShuttingDown, fs.State);
    }

    [Fact]
    public void RuntimeFailed_TransitionsToFailed()
    {
        var fs = new LauncherLifecycle();
        fs.Fire(LifecycleTrigger.StartRequested);
        fs.Fire(LifecycleTrigger.InstanceConfirmed);
        fs.Fire(LifecycleTrigger.RuntimeFailed);

        Assert.Equal(LifecycleState.Failed, fs.State);
    }

    [Fact]
    public void FatalFromWaiting_TransitionsToFailed()
    {
        var fs = new LauncherLifecycle();
        fs.Fire(LifecycleTrigger.StartRequested);
        fs.Fire(LifecycleTrigger.InstanceConfirmed);
        fs.Fire(LifecycleTrigger.RuntimeResolved);
        fs.Fire(LifecycleTrigger.ServiceStarted);
        fs.Fire(LifecycleTrigger.Fatal);

        Assert.Equal(LifecycleState.Failed, fs.State);
    }

    [Fact]
    public void ShuttingDown_IsIdempotent()
    {
        var fs = new LauncherLifecycle();
        DriveToRunning(fs);
        fs.Fire(LifecycleTrigger.ShutdownRequested);   // Running -> ShuttingDown
        fs.Fire(LifecycleTrigger.ShutdownRequested);   // 幂等确认
        Assert.Equal(LifecycleState.ShuttingDown, fs.State);
    }

    [Fact]
    public void IllegalTransition_Throws_AndStateStays()
    {
        var fs = new LauncherLifecycle();
        // 从未 start 就 fire ServiceReady → 非法
        Assert.Throws<InvalidOperationException>(() => fs.Fire(LifecycleTrigger.ServiceReady));
        Assert.Equal(LifecycleState.Idle, fs.State); // 转移失败不改变状态
    }

    // ---- 维度二场景 4：WebView2 崩溃恢复（状态机层）----

    [Fact]
    public void WebViewCrash_WhileRunning_StaysRunning_WithEvent()
    {
        var fs = new LauncherLifecycle();
        DriveToRunning(fs);

        var saw = 0;
        fs.StateChanged += (_, s) => { if (s == LifecycleState.Running) saw++; };
        fs.Fire(LifecycleTrigger.WebViewCrashed); // Running + WebViewCrashed → Running（自转移）

        Assert.Equal(LifecycleState.Running, fs.State); // 崩溃被拦截，不终结应用
        Assert.Equal(1, saw);                           // 广播一次（副作用挂载点）
    }

    [Fact]
    public void WebViewCrash_FromNonRunning_Throws()
    {
        var fs = new LauncherLifecycle();
        // 崩溃事件只在 Running 态合法（其他态触发是编程错误 → Fail-fast）
        Assert.Throws<InvalidOperationException>(() => fs.Fire(LifecycleTrigger.WebViewCrashed));
    }
}
