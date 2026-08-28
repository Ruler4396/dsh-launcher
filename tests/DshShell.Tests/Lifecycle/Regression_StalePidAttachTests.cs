using DshWeb;
using DshWeb.Lifecycle;
using Xunit;

namespace DshShell.Tests.Lifecycle;

/// <summary>
/// [Regression / Category=RealOS] 残留 service pid：真实进程已消亡后，RealProcessHandle 构造
/// （Process.GetProcessById）抛错 → BootHealthMonitor 的 AttachProcess 必须只 Warn、绝不判死
/// （[E2007/E2008 误报根治]，用户实测证据 "进程 attach 失败（pid=4708 不存在）"）。
/// 零 Mock：真实拉起 node 子进程 → 真实杀掉 → 待其从系统彻底消失 → 用残留 pid 走真实默认工厂。
/// </summary>
public class Regression_StalePidAttachTests
{
    [Fact]
    [Trait("Category", "RealOS")]
    public async Task Regression_StaleServicePid_Attach_RealOs_NeverFailsMonitor()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("node", "--eval \"setTimeout(()=>{}, 60000)\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        };

        using var child = System.Diagnostics.Process.Start(psi);
        Assert.NotNull(child);
        var stalePid = child.Id;
        child.Kill(); // 真实强杀 → pid 成为残留
        child.WaitForExit();

        // 消除 Kill→系统消失 竞态：等到 GetProcessById 对该 pid 抛错（进程从系统彻底注销）再 attach
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var confirmedGone = false;
        while (DateTime.UtcNow < deadline)
        {
            try { using var _ = System.Diagnostics.Process.GetProcessById(stalePid); }
            catch (ArgumentException) { confirmedGone = true; break; }
            await Task.Delay(50);
        }
        Assert.True(confirmedGone, $"stale pid {stalePid} did not leave the OS within 5s");

        // 真实默认工厂（RealProcessHandle）→ GetProcessById(stalePid) 抛错 → attach 失败路径
        var m = new BootHealthMonitor(
            new ShellLogic.BootGuard.BootProfile { GraceMs = 50, ProbeIntervalMs = 40, AbsentThreshold = 3 },
            logPath: null,
            httpUrl: "http://127.0.0.1:1");
        using (m)
        {
            var failedFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            m.Failed += _ => failedFired.TrySetResult();
            m.AttachProcess(stalePid);
            await Task.Delay(400);
            Assert.False(failedFired.Task.IsCompleted, "stale pid attach must NEVER judge failed");
            Assert.Equal(BootHealthState.Pending, m.State);
        }
    }
}