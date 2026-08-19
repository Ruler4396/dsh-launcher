using DshWeb;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Managers;

/// <summary>ServiceManager 就绪探测（Headless：用委托探针替代真实 TCP/HTTP）。</summary>
public class ServiceManagerTests
{
    [Fact]
    public void NeedsStart_WhenPortClosed_True()
    {
        var sm = new ServiceManager(tcpProbe: (_, _) => false);
        Assert.True(sm.NeedsStart(3080));
    }

    [Fact]
    public void NeedsStart_WhenPortOpen_False()
    {
        var sm = new ServiceManager(tcpProbe: (_, _) => true);
        Assert.False(sm.NeedsStart(3080));
    }

    [Fact]
    public async Task WaitReady_ReadyImmediately_ReturnsTrue()
    {
        var sm = new ServiceManager(tcpProbe: (_, _) => true, httpProbe: (_, _) => true, pollDelay: TimeSpan.FromMilliseconds(50));
        Assert.True(await sm.WaitReadyAsync(3080, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task WaitReady_Timeout_ReturnsFalse()
    {
        var sm = new ServiceManager(tcpProbe: (_, _) => false, httpProbe: (_, _) => false, pollDelay: TimeSpan.FromMilliseconds(30));
        Assert.False(await sm.WaitReadyAsync(3080, TimeSpan.FromMilliseconds(120)));
    }

    [Fact]
    public async Task WaitReady_Cancelled_ReturnsFalse()
    {
        var sm = new ServiceManager(tcpProbe: (_, _) => false, httpProbe: (_, _) => false, pollDelay: TimeSpan.FromMilliseconds(1000));
        using var cts = new CancellationTokenSource(80);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sm.WaitReadyAsync(3080, TimeSpan.FromSeconds(30), cts.Token));
    }

    // ---------------- 任务一：端口三重验证（TCP + 进程身份 + 快速 HTTP） ----------------

    [Fact]
    public void ProbePort_WhenPortClosed_ReturnsClosed()
    {
        var sm = new ServiceManager(tcpProbe: (_, _) => false);
        Assert.Equal(ShellLogic.ServicePortState.Closed, sm.ProbePort(3080, "http://127.0.0.1:3080"));
    }

    [Fact]
    public void ProbePort_PortOpenAndHttpReady_ReturnsHealthy()
    {
        var sm = new ServiceManager(
            tcpProbe: (_, _) => true,
            httpProbe: (_, _) => true,
            pidLookup: _ => 123,
            identityCheck: _ => true);
        Assert.Equal(ShellLogic.ServicePortState.Healthy, sm.ProbePort(3080, "http://127.0.0.1:3080"));
    }

    [Fact]
    public void ProbePort_PortOpenButHttpFails_NodeOwner_ReturnsZombie()
    {
        // 核心根因场景：TCP 已开（僵尸 node 占着 3080）但 HTTP 无响应 → 判定僵尸而非"健康"
        var sm = new ServiceManager(
            tcpProbe: (_, _) => true,
            httpProbe: (_, _) => false,
            pidLookup: _ => 123,
            identityCheck: _ => true);
        Assert.Equal(ShellLogic.ServicePortState.Zombie, sm.ProbePort(3080, "http://127.0.0.1:3080"));
    }

    [Fact]
    public void ProbePort_PortOpenButHttpFails_NonNodeOwner_ReturnsForeign()
    {
        // 端口被其他程序占用（非 node）：不清理、快速失败 E2004（防误杀）
        var sm = new ServiceManager(
            tcpProbe: (_, _) => true,
            httpProbe: (_, _) => false,
            pidLookup: _ => 999,
            identityCheck: _ => false);
        Assert.Equal(ShellLogic.ServicePortState.Foreign, sm.ProbePort(3080, "http://127.0.0.1:3080"));
    }

    // ---------------- 任务一：僵尸进程树清理（taskkill /T /F 含 cmd/npx 外壳） ----------------

    [Fact]
    public async Task ZombieCleanup_PortOccupiedButHttpFails_KillsProcessTree()
    {
        // 回归：Mock PortOpen=true + HTTP 抛超时 → ProbePort 判定 Zombie；
        // KillZombieTree 触发 taskkill /T /F（杀 node 及其祖先 cmd/npx 外壳），最终端口释放。
        var killed = new List<int>();
        var portOpen = true;
        var sm = new ServiceManager(
            tcpProbe: (_, _) => portOpen,
            httpProbe: (_, _) => false, // HTTP 超时（僵尸特征）
            pidLookup: _ => 123,
            identityCheck: _ => true,
            killProcessTree: pid => { killed.Add(pid); portOpen = false; return true; },
            ancestors: _ => new List<int> { 456, 457 }, // cmd/npx 外壳链
            portReleaseTimeout: TimeSpan.FromMilliseconds(300));

        // ① 三重验证判定僵尸
        Assert.Equal(ShellLogic.ServicePortState.Zombie, sm.ProbePort(3080, "http://127.0.0.1:3080"));

        // ② 清理触发：taskkill /T /F 语义（杀 node + 祖先外壳）
        Assert.True(sm.KillZombieTree(3080));
        Assert.Contains(123, killed);          // node（监听端口的服务进程）
        Assert.Contains(456, killed);          // cmd/npx 外壳
        Assert.Contains(457, killed);
        Assert.True(portOpen is false);        // 端口最终释放
    }

    [Fact]
    public void KillZombieTree_WhenPortAlreadyReleased_ReturnsTrueWithoutKill()
    {
        var killed = new List<int>();
        var sm = new ServiceManager(
            tcpProbe: (_, _) => false, // 端口已无监听（自愈场景）
            pidLookup: _ => 0,
            killProcessTree: pid => { killed.Add(pid); return true; });
        Assert.True(sm.KillZombieTree(3080));
        Assert.Empty(killed); // 无占用者，无需杀任何进程
    }
}
