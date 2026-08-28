using DshWeb;
using DshWeb.Lifecycle;
using Xunit;

namespace DshShell.Tests.Lifecycle;

/// <summary>
/// BootHealthMonitor Headless 融合状态机测试（ADR-023）：Fake 进程句柄 + 注入探针，
/// 锁定触发语义铁律——进程非零退出/日志增量命中/HTTP 连续 2 次 miss/页面坏签名或缺席阈值
/// → failed 恰好一次；探针异常与无效结果绝不判死；Suspend 窗口全屏蔽；failed 吸收态只追加证据。
/// </summary>
public class BootHealthMonitorTests
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
        GraceMs = 50,
        ProbeIntervalMs = 40,
        AbsentThreshold = 3,
        BadSignatures = new[] { "bootstrap facade is missing", "fake-bad-marker" },
    };

    private static async Task<BootVerdict> WaitFailedAsync(BootHealthMonitor m, int timeoutMs = 5000)
    {
        if (m.Verdict is { } existing) return existing;
        var tcs = new TaskCompletionSource<BootVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Failed += v => tcs.TrySetResult(v);
        Assert.True(await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)) == tcs.Task || m.Verdict != null,
            $"monitor did not fail within {timeoutMs}ms");
        return m.Verdict ?? await tcs.Task;
    }

    // ---------------- 进程层 ----------------

    [Fact]
    public async Task ProcessLayer_NonZeroExit_FailedWithExitCodeEvidence()
    {
        var handle = new FakeProcessHandle();
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1", null, _ => handle);
        var failedTask = WaitFailedAsync(m);
        m.AttachProcess(123);
        await Task.Delay(80);
        handle.Exit(1);
        var verdict = await failedTask;
        Assert.Equal("E2007", verdict.ErrorCode);
        Assert.Contains(verdict.Evidence, e => e.Layer == BootLayer.Process && e.Detail!.Contains("1"));
    }

    [Fact]
    public async Task ProcessLayer_ZeroExit_TreatedAsIntentionalStop_NotFailed()
    {
        var handle = new FakeProcessHandle();
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1", null, _ => handle);
        m.AttachProcess(123);
        await Task.Delay(80);
        handle.Exit(0);
        await Task.Delay(150);
        Assert.Equal(BootHealthState.Pending, m.State);
    }

    [Fact]
    public async Task ProcessLayer_AttachToDeadPid_FailsImmediately_WithExitCode()
    {
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1", null, _ =>
        {
            var h = new FakeProcessHandle();
            h.Exit(3);
            return h;
        });
        var failedTask = WaitFailedAsync(m);
        m.AttachProcess(77);
        var verdict = await failedTask;
        Assert.Equal("E2007", verdict.ErrorCode);
        Assert.Contains("3", verdict.Evidence.First(e => e.Layer == BootLayer.Process).Detail);
    }

    [Fact]
    public async Task ProcessLayer_AttachFactoryThrows_WarnsOnly_NeverFails()
    {
        // [E2007/E2008 误报根治] 残留/失效 pid（RealProcessHandle 构造时 GetProcessById 抛错）＝
        // attach 监视接线失败，不再是崩溃裁决：绝不触发 Failed（此前会以 E2007 判死并弹窗——
        // 用户实测证据 "进程 attach 失败（pid=4708 不存在）"）。服务真死由 HTTP/页面层兜底。
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1",
            processHandleFactory: _ => throw new ArgumentException("no process with id 4708"));
        var failedFired = new TaskCompletionSource();
        m.Failed += _ => failedFired.TrySetResult();
        m.AttachProcess(4708);
        await Task.Delay(250);
        Assert.False(failedFired.Task.IsCompleted, "attach failure must NEVER judge failed");
        Assert.Equal(BootHealthState.Pending, m.State);
    }

    // ---------------- 日志层 ----------------

    [Fact]
    public async Task LogLayer_MarkerInIncrement_FailsWithMatchedLine()
    {
        var log = Path.Combine(Path.GetTempPath(), $"bootmon-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(log, "old pre-existing npm ERR! line (must NOT judge)\n");
        try
        {
            using var m = new BootHealthMonitor(FastProfile, log, "http://127.0.0.1:1",
                logPollInterval: TimeSpan.FromMilliseconds(40));
            var failedTask = WaitFailedAsync(m);
            m.Start();
            await Task.Delay(120);
            await File.AppendAllTextAsync(log, "[plugin] plugin load failed: Cannot find module 'dsh-notification'\n");
            var verdict = await failedTask;
            Assert.Equal("E2003", verdict.ErrorCode);
            Assert.Contains(verdict.Evidence, e => e.Layer == BootLayer.Log
                && e.Summary.Contains("plugin load failed")
                && e.Detail!.Contains("Cannot find module 'dsh-notification'"));
        }
        finally { File.Delete(log); }
    }

    [Fact]
    public async Task LogLayer_PreExistingErrors_NeverJudge()
    {
        var log = Path.Combine(Path.GetTempPath(), $"bootmon-{Guid.NewGuid():N}.log");
        await File.WriteAllTextAsync(log, "npm ERR! historical garbage from previous run\n");
        try
        {
            using var m = new BootHealthMonitor(FastProfile, log, "http://127.0.0.1:1",
                logPollInterval: TimeSpan.FromMilliseconds(40));
            m.Start();
            await Task.Delay(300);
            Assert.Equal(BootHealthState.Pending, m.State);
        }
        finally { File.Delete(log); }
    }

    // ---------------- HTTP 层 ----------------

    [Fact]
    public async Task HttpLayer_TwoConsecutiveMisses_Fails()
    {
        var misses = 0;
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1",
            httpProbe: _ => { misses++; return false; },
            httpPollInterval: TimeSpan.FromMilliseconds(40));
        var failedTask = WaitFailedAsync(m);
        m.Start();
        Assert.Equal("E2004", (await failedTask).ErrorCode);
        Assert.True(misses >= 2, $"expected >=2 probes before failing, got {misses}");
    }

    [Fact]
    public async Task HttpLayer_SingleTransientMiss_DoesNotFail()
    {
        var firstProbe = true;
        var profile = new ShellLogic.BootGuard.BootProfile { GraceMs = 60000, AbsentThreshold = 1000 };
        using var m = new BootHealthMonitor(profile, null, "http://127.0.0.1:1",
            httpProbe: _ =>
            {
                if (firstProbe) { firstProbe = false; return false; }
                return true;
            },
            httpPollInterval: TimeSpan.FromMilliseconds(40));
        m.Start();
        await Task.Delay(350);
        Assert.Equal(BootHealthState.Pending, m.State);
    }

    // ---------------- 页面层 ----------------

    [Fact]
    public async Task PageLayer_DomBadSignature_RequiresAbsentThresholdToFail_CarriesSuspectEvidence()
    {
        // 2026-08 回归：DOM 文本坏签名降级为 Absent，需连续 AbsentThreshold(3) 轮才判死，
        // 证据不丢（detail 携带 dom-suspect 原文），摘要改为"好符号连续 N 次缺席"。
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1",
            _ => Task.FromResult("{\"good\":false,\"text\":\"Plugin crash: window.__ModuleLoader__ bootstrap facade is missing\",\"err\":\"\"}"));
        var failedTask = WaitFailedAsync(m);
        m.OnNavigationCompleted();
        var verdict = await failedTask;
        Assert.Equal("E2008", verdict.ErrorCode);
        var page = Assert.Single(verdict.Evidence, e => e.Layer == BootLayer.Page);
        Assert.Contains("缺席", page.Summary);
        Assert.NotNull(page.Detail);
        Assert.Contains("dom-suspect[", page.Detail);
        Assert.Contains("bootstrap facade is missing", page.Detail);
    }

    [Fact]
    public async Task PageLayer_DomBadSignature_FirstRoundStaysPending_AntiFalsePositive()
    {
        // 抗误报：DOM 坏签名首轮（远不足 AbsentThreshold）必须仍 Pending，绝不误判 E2008。
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1",
            _ => Task.FromResult("{\"good\":false,\"text\":\"only a hidden node says bootstrap facade is missing\",\"err\":\"\"}"));
        m.Start();
        m.OnNavigationCompleted();
        await Task.Delay(90); // ≪ 3 轮（ProbeIntervalMs=40 → ~2 轮）
        Assert.Equal(BootHealthState.Pending, m.State);
    }

    [Fact]
    public async Task PageLayer_ErrBadSignature_StillFailsImmediately()
    {
        // err 原文坏签名仍一票否决（S22"捕获原文"硬要求，抗误报仅限 DOM 文本层）。
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1",
            _ => Task.FromResult("{\"good\":false,\"text\":\"ok\",\"err\":\"Uncaught: bootstrap facade is missing\"}"));
        var failedTask = WaitFailedAsync(m);
        m.OnNavigationCompleted();
        var verdict = await failedTask;
        Assert.Equal("E2008", verdict.ErrorCode);
        var page = Assert.Single(verdict.Evidence, e => e.Layer == BootLayer.Page);
        // err 原文坏签名走 BadSignature 一票路径：摘要含 "坏签名"，错误码 E2008
        // （BootHealthMonitor 的 BadSignature 分支不把探针 detail 透传进 Summary，故只校验摘要关键字）。
        Assert.Contains("坏签名", page.Summary);
        Assert.Equal("E2008", page.ErrorCode);
    }

    [Fact]
    public async Task PageLayer_GoodSymbolAfterAbsences_Healthy_NoFalsePositive_S23Shape()
    {
        var calls = 0;
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1", _ =>
        {
            calls++;
            return Task.FromResult(calls <= 2
                ? "{\"good\":false,\"text\":\"loading\",\"err\":\"\"}"
                : "{\"good\":true,\"text\":\"DeepSeek Harness\",\"err\":\"\"}");
        });
        var healthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.HealthyDetected += () => healthy.TrySetResult();
        m.OnNavigationCompleted();
        Assert.True(await Task.WhenAny(healthy.Task, Task.Delay(4000)) == healthy.Task,
            "good symbol after absences should turn Healthy");
        Assert.Equal(BootHealthState.Healthy, m.State);
        Assert.True(calls >= 2);
        var callsAtHealthy = Volatile.Read(ref calls);
        await Task.Delay(150);
        Assert.Equal(callsAtHealthy, Volatile.Read(ref calls)); // Healthy 后探针停止
    }

    [Fact]
    public async Task PageLayer_AbsentThresholdExceeded_Fails()
    {
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1",
            _ => Task.FromResult("{\"good\":false,\"text\":\"blank page\",\"err\":\"\"}"));
        var failedTask = WaitFailedAsync(m);
        m.OnNavigationCompleted();
        var verdict = await failedTask;
        Assert.Equal("E2008", verdict.ErrorCode);
        Assert.Contains("缺席", verdict.Summary);
        Assert.Contains("3", verdict.Summary); // 阈值=FastProfile.AbsentThreshold
    }

    [Fact]
    public async Task PageLayer_RenderedContent_Healthy_ProbesStop()
    {
        // [E2008 误报根治] 页面已渲染出 dsh 自身界面（good=false，boot 链未完成，如未配置 API key 的
        // 欢迎/配置界面）→ Rendered → Healthy，探针停止，绝不 E2008 弹窗。
        var calls = 0;
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1", _ =>
        {
            calls++;
            return Task.FromResult(
                "{\"good\":false,\"text\":\"欢迎使用 DeepSeek Harness —— 请先配置你的模型提供方 API Key 后即可开始使用。设置入口在右上角齿轮图标，也可以从这里打开帮助文档与示例。\",\"err\":\"\"}");
        });
        var healthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        m.HealthyDetected += () => healthy.TrySetResult();
        var failedFired = new TaskCompletionSource();
        m.Failed += _ => failedFired.TrySetResult();
        m.OnNavigationCompleted();
        Assert.True(await Task.WhenAny(healthy.Task, Task.Delay(4000)) == healthy.Task,
            "rendered config-waiting page should turn Healthy");
        Assert.Equal(BootHealthState.Healthy, m.State);
        Assert.False(failedFired.Task.IsCompleted);
        var callsAtHealthy = Volatile.Read(ref calls);
        await Task.Delay(150);
        Assert.Equal(callsAtHealthy, Volatile.Read(ref calls)); // Healthy 后探针停止
    }

    [Fact]
    public async Task PageLayer_ShortTextBelowRenderedThreshold_StillAbsent_FailsAfterThreshold()
    {
        // 渲染豁免不削弱慢启动/白屏保护：innerText 低于 RenderedMinTextChars（空白/纯加载页）
        // 仍计票缺席 → E2008。
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1",
            _ => Task.FromResult("{\"good\":false,\"text\":\"Loading...\",\"err\":\"\"}"));
        var failedTask = WaitFailedAsync(m);
        m.OnNavigationCompleted();
        var verdict = await failedTask;
        Assert.Equal("E2008", verdict.ErrorCode);
        Assert.Contains("缺席", verdict.Summary);
    }

    [Fact]
    public async Task PageProbe_ThrowsEveryTime_WarnOnly_NeverFails_Task3Guard()
    {
        var attempts = 0;
        var profile = new ShellLogic.BootGuard.BootProfile { GraceMs = 20, ProbeIntervalMs = 30, AbsentThreshold = 2 };
        using var m = new BootHealthMonitor(profile, null, "http://127.0.0.1:1", _ =>
        {
            attempts++;
            throw new InvalidOperationException("ExecuteScriptAsync exploded");
        });
        var failedFired = new TaskCompletionSource();
        m.Failed += _ => failedFired.TrySetResult();
        m.OnNavigationCompleted();
        await Task.Delay(400);
        Assert.True(Volatile.Read(ref attempts) >= 3, "probe should be retried repeatedly");
        Assert.False(failedFired.Task.IsCompleted, "probe exceptions must NEVER judge failed (Task 3)");
        Assert.Equal(BootHealthState.Pending, m.State);
    }

    [Fact]
    public async Task PageProbe_InvalidResults_NeverFails_Task3Guard()
    {
        var flip = false;
        var profile = new ShellLogic.BootGuard.BootProfile { GraceMs = 20, ProbeIntervalMs = 30, AbsentThreshold = 2 };
        using var m = new BootHealthMonitor(profile, null, "http://127.0.0.1:1", _ =>
        {
            flip = !flip;
            return Task.FromResult(flip ? null : "{garbage");
        });
        var failedFired = new TaskCompletionSource();
        m.Failed += _ => failedFired.TrySetResult();
        m.OnNavigationCompleted();
        await Task.Delay(300);
        Assert.False(failedFired.Task.IsCompleted);
        Assert.Equal(BootHealthState.Pending, m.State);
    }

    // ---------------- 吸收态 / 闸门 / 生命周期 ----------------

    [Fact]
    public async Task Failed_IsAbsorbing_SecondLayerAppendsEvidence_EventFiresOnce()
    {
        var handle = new FakeProcessHandle();
        int fireCount = 0, updatedCount = 0;
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1",
            processHandleFactory: _ => handle,
            httpProbe: _ => false,
            httpPollInterval: TimeSpan.FromMilliseconds(300));
        var failedTask = WaitFailedAsync(m);
        m.Failed += _ => Interlocked.Increment(ref fireCount);
        m.VerdictUpdated += _ => Interlocked.Increment(ref updatedCount);
        m.Start();
        m.AttachProcess(42);
        await Task.Delay(120);
        handle.Exit(9);
        var verdict = await failedTask;
        Assert.Equal("E2007", verdict.ErrorCode);
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline && !verdict.Evidence.Any(e => e.Layer == BootLayer.Http))
            await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref fireCount)); // Failed 恰好一次
        Assert.Equal(BootHealthState.Failed, m.State);
        var layers = verdict.Evidence.Select(e => e.Layer).ToList();
        Assert.Contains(BootLayer.Process, layers);
        Assert.Contains(BootLayer.Http, layers);       // 吸收态补充证据
        Assert.True(Volatile.Read(ref updatedCount) >= 1);
    }

    [Fact]
    public void PromptGate_ExactlyOncePerSession()
    {
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1");
        Assert.True(m.TryConsumeSessionPrompt());
        Assert.False(m.TryConsumeSessionPrompt());
        Assert.False(m.TryConsumeSessionPrompt());
    }

    [Fact]
    public async Task SuspendWindow_ShieldsAllLayers_ResumeRearmsAndReattaches()
    {
        var handles = new List<FakeProcessHandle>();
        var serviceUp = true;
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1",
            processHandleFactory: _ =>
            {
                var h = new FakeProcessHandle();
                handles.Add(h);
                return h;
            },
            httpProbe: _ => serviceUp);
        var failedTask = WaitFailedAsync(m);
        m.AttachProcess(1);
        await Task.Delay(80);
        m.Suspend();               // 壳主动重启窗口：全部判定挂起
        serviceUp = false;
        handles[0].Exit(1);
        await Task.Delay(150);
        Assert.Equal(BootHealthState.Pending, m.State);
        serviceUp = true;
        m.ResumeAfterRestart(2);   // 重挂新进程
        await Task.Delay(100);
        Assert.Equal(2, handles.Count);
        handles[1].Exit(5);
        var verdict = await failedTask;
        Assert.Equal("E2007", verdict.ErrorCode);
        Assert.Contains("5", verdict.Evidence.First(e => e.Layer == BootLayer.Process).Detail);
    }

    [Fact]
    public async Task Stop_PreventsAnyFurtherTransition()
    {
        var handle = new FakeProcessHandle();
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1", null, _ => handle);
        var fired = new TaskCompletionSource();
        m.Failed += _ => fired.TrySetResult();
        m.Stop();
        handle.Exit(1);
        await Task.Delay(150);
        Assert.False(fired.Task.IsCompleted);
        Assert.Equal(BootHealthState.Pending, m.State);
    }

    // ---------------- CDP 只采集层 ----------------

    [Fact]
    public async Task CdpException_CollectOnly_NeverJudges_ButJoinsFusionView()
    {
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1");
        m.CollectCdpException("{\"exceptionDetails\":{\"text\":\"Uncaught TypeError\"}}");
        Assert.Equal(BootHealthState.Pending, m.State);

        using var m2 = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1", null, _ =>
        {
            var h = new FakeProcessHandle();
            h.Exit(2);
            return h;
        });
        var failedTask = WaitFailedAsync(m2);
        m2.AttachProcess(9);
        Assert.Equal("E2007", (await failedTask).Evidence.Single(e => e.Layer == BootLayer.Process).ErrorCode);
        m2.CollectCdpException("{\"exceptionDetails\":{\"text\":\"late exception\"}}");
        m2.CollectCdpException("{\"exceptionDetails\":{\"text\":\"another\"}}");
        var cdp = m2.SnapshotEvidence().Where(e => e.Layer == BootLayer.Cdp).ToList();
        Assert.Equal(2, cdp.Count);
        Assert.All(cdp, e => Assert.Null(e.ErrorCode)); // CDP 只采集，不带错误码
    }

    [Fact]
    public async Task CdpException_BeforeFailure_PreservedInVerdict_EarlyEvidenceNotLost()
    {
        var handle = new FakeProcessHandle();
        using var m = new BootHealthMonitor(FastProfile, null, "http://127.0.0.1:1",
            null, _ => handle, _ => false);
        var failedTask = WaitFailedAsync(m);
        m.CollectCdpException("{\"exceptionDetails\":{\"text\":\"Uncaught TypeError\",\"exception\":{\"description\":\"bootstrap facade is missing\"}}}");
        m.AttachProcess(42);
        handle.Exit(5);
        var verdict = await failedTask;
        Assert.Equal("E2007", verdict.ErrorCode);
        Assert.Contains(verdict.Evidence, e => e.Layer == BootLayer.Cdp && e.Detail!.Contains("facade is missing"));
    }
}
