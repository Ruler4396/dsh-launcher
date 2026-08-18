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
}
