using DshWeb;
using DshWeb.Lifecycle;
using Xunit;

namespace DshShell.Tests.Lifecycle;

/// <summary>
/// 【Regression_Issue28_RuntimeServiceRestart】运行期服务退出的判定语义（Headless 状态机，零真实进程）。
///
/// 事故（issue #28 第 2 点）：用户在 DSH 插件市场装完插件、点 DSH 自带的重启按钮后，node 服务进程
/// 退出 → 旧实现一律按"E2007 启动自检失败"弹"是否重启 dsh 服务"询问框——用户看到的即"必然出现
/// 异常弹窗"。修复：启动自检已通过（Healthy）之后的进程退出改由
/// <see cref="BootHealthMonitor.ServiceExitedWhileRunning"/> 事件上抛，组合根静默自愈
/// （重启服务 + 等新 token 导航）；自检尚未通过的退出仍按 E2007 失败裁决（启动失败必须可见）。
/// </summary>
public class Regression_Issue28_RuntimeServiceRestartTests
{
    private sealed class FakeProcessHandle : IBootProcessHandle
    {
        public bool HasExited { get; private set; }
        public int? ExitCode { get; private set; }
        public event EventHandler? Exited;
        public int? TryGetExitCode() => HasExited ? ExitCode : null;
        public void Exit(int code)
        {
            HasExited = true;
            ExitCode = code;
            Exited?.Invoke(this, EventArgs.Empty);
        }
        public void Dispose() { }
    }

    private static readonly ShellLogic.BootGuard.BootProfile FastProfile = new()
    {
        GraceMs = 40,
        ProbeIntervalMs = 30,
        AbsentThreshold = 3,
        BadSignatures = new[] { "fake-bad-marker" },
    };

    /// <summary>把监控推到 Healthy：页面探针返回好符号（与生产 BootGuard 契约一致的最短路径）。</summary>
    private static async Task WaitHealthyAsync(BootHealthMonitor m, int timeoutMs = 5000)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.HealthyDetected += () => tcs.TrySetResult(true);
        m.OnNavigationCompleted();
        Assert.True(await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task,
            $"monitor did not become healthy within {timeoutMs}ms");
    }

    /// <summary>页面探针的好符号载荷（与生产探针协议一致：good/text/err 三段）。</summary>
    private static string GoodSymbolProbeJson => "{\"good\":true,\"text\":\"__DSH_BOOT__ 0.1.5-rc.1\",\"err\":\"\"}";

    [Fact]
    public async Task HealthyService_Exit_RaisesRuntimeRestart_NotBootFailure()
    {
        var handle = new FakeProcessHandle();
        using var m = new BootHealthMonitor(
            FastProfile, null, "http://127.0.0.1:1",
            pageProbe: _ => Task.FromResult<string?>(GoodSymbolProbeJson),
            processHandleFactory: _ => handle,
            httpProbe: _ => true,
            logPollInterval: TimeSpan.FromMilliseconds(30),
            httpPollInterval: TimeSpan.FromMilliseconds(30));

        int? runtimeExit = null;
        var runtimeTcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.ServiceExitedWhileRunning += code => { runtimeExit = code; runtimeTcs.TrySetResult(code); };
        var failedFired = false;
        m.Failed += _ => failedFired = true;

        m.AttachProcess(4242);
        await WaitHealthyAsync(m);

        handle.Exit(1); // 用户点了 DSH 自带的重启按钮：服务进程退出
        var exitCode = await Task.WhenAny(runtimeTcs.Task, Task.Delay(3000)) == runtimeTcs.Task
            ? await runtimeTcs.Task
            : throw new Xunit.Sdk.XunitException("Healthy 之后的服务退出未触发 ServiceExitedWhileRunning");

        Assert.Equal(1, exitCode);
        Assert.Equal(1, runtimeExit);
        Assert.False(failedFired, "运行期服务退出绝不能再走启动自检失败（E2007）弹窗路径");
        Assert.Null(m.Verdict);
    }

    [Fact]
    public async Task HealthyService_ZeroExit_AlsoRaisesRuntimeRestart()
    {
        // DSH 自身重启可能优雅退出（code 0）；只要自检已通过就必须自愈，不能静默留在死服务上
        var handle = new FakeProcessHandle();
        using var m = new BootHealthMonitor(
            FastProfile, null, "http://127.0.0.1:1",
            pageProbe: _ => Task.FromResult<string?>(GoodSymbolProbeJson),
            processHandleFactory: _ => handle,
            httpProbe: _ => true,
            logPollInterval: TimeSpan.FromMilliseconds(30),
            httpPollInterval: TimeSpan.FromMilliseconds(30));

        var runtimeTcs = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.ServiceExitedWhileRunning += code => runtimeTcs.TrySetResult(code);
        m.AttachProcess(7);
        await WaitHealthyAsync(m);

        handle.Exit(0);
        Assert.True(await Task.WhenAny(runtimeTcs.Task, Task.Delay(3000)) == runtimeTcs.Task,
            "Healthy 之后的 code=0 退出同样必须触发运行期自愈");
        Assert.Equal(0, await runtimeTcs.Task);
    }

    [Fact]
    public async Task BeforeHealthy_NonZeroExit_StillJudgedAsBootFailure()
    {
        // 回归护栏：启动自检未通过时的进程退出必须保持既有 E2007 失败裁决（启动失败可见）
        var handle = new FakeProcessHandle();
        using var m = new BootHealthMonitor(
            FastProfile, null, "http://127.0.0.1:1",
            pageProbe: _ => Task.FromResult<string?>(null),
            processHandleFactory: _ => handle,
            httpProbe: _ => false,
            logPollInterval: TimeSpan.FromMilliseconds(50),
            httpPollInterval: TimeSpan.FromMilliseconds(50));

        var failed = new TaskCompletionSource<BootVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeFired = false;
        m.Failed += v => failed.TrySetResult(v);
        m.ServiceExitedWhileRunning += _ => runtimeFired = true;

        m.AttachProcess(99);
        await Task.Delay(60);
        handle.Exit(9);

        Assert.True(await Task.WhenAny(failed.Task, Task.Delay(3000)) == failed.Task);
        Assert.Equal("E2007", (await failed.Task).ErrorCode);
        Assert.False(runtimeFired, "未 healthy 的退出不得走运行期自愈");
    }

    [Fact]
    public async Task ResumeAfterRestart_ReArmsProcessExitObservation()
    {
        // 幂等闸门必须复位：否则第 2 次"点 DSH 重启"永远不会被观测到（静默自愈只生效一次）
        var first = new FakeProcessHandle();
        var second = new FakeProcessHandle();
        var handles = new Queue<FakeProcessHandle>(new[] { first, second });
        using var m = new BootHealthMonitor(
            FastProfile, null, "http://127.0.0.1:1",
            pageProbe: _ => Task.FromResult<string?>(GoodSymbolProbeJson),
            processHandleFactory: _ => handles.Count > 0 ? handles.Dequeue() : new FakeProcessHandle(),
            httpProbe: _ => true,
            logPollInterval: TimeSpan.FromMilliseconds(30),
            httpPollInterval: TimeSpan.FromMilliseconds(30));

        var exits = new List<int?>();
        var secondExit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.ServiceExitedWhileRunning += code =>
        {
            lock (exits) exits.Add(code);
            if (exits.Count >= 2) secondExit.TrySetResult(true);
        };

        m.AttachProcess(11);
        await WaitHealthyAsync(m);
        first.Exit(2);
        await Task.Delay(150);

        // 组合根重启完成 → ResumeAfterRestart 重挂新进程
        m.ResumeAfterRestart(12);
        await WaitHealthyAsync(m);
        second.Exit(3);

        Assert.True(await Task.WhenAny(secondExit.Task, Task.Delay(3000)) == secondExit.Task,
            "ResumeAfterRestart 后第 2 次服务退出未被观测（_processFailureReported 未复位？）");
        lock (exits) Assert.Equal(new int?[] { 2, 3 }, exits);
    }
}
