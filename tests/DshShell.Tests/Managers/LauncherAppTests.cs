using DshWeb;
using DshWeb.Lifecycle;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Managers;

/// <summary>LauncherApp 组合根的 Headless 集成测试：Fake Manager 驱动生命周期，不起 UI/Node。
/// 验证"服务就绪 → Running"与"就绪超时 → ShuttingDown"两条核心路径（E2002 映射由组合根承担）。</summary>
public class LauncherAppTests
{
    private sealed class FakeRuntime : IRuntimeManager
    {
        public RuntimeResult Result { get; init; } = RuntimeResult.ReadyNow("node.exe");
        public string? Prepend { get; private set; }
        public Task<RuntimeResult> EnsureRuntimeAsync(CancellationToken ct = default) => Task.FromResult(Result);
        public void PrependToPath(string r) => Prepend = r;
    }

    private sealed class FakeService : IServiceManager
    {
        public bool Ready { get; init; } = true;
        public bool NeedsStart(int port) => false;             // 端口已开，走就绪路径
        public Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(Ready);
    }

    [Fact]
    public async Task ServiceReady_DrivesToRunning()
    {
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        Assert.True(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Running, app.State);
    }

    [Fact]
    public async Task ReadinessTimeout_DrivesToShuttingDown_AndFalse()
    {
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = false });
        Assert.False(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.ShuttingDown, app.State); // 组合根在此映射 ErrorCodes.E2002
    }

    [Fact]
    public async Task RuntimeFailed_DrivesToFailed()
    {
        var app = new LauncherApp(new FakeRuntime { Result = RuntimeResult.Failed("E1003", "download failed") },
            new FakeService { Ready = true });
        Assert.False(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Failed, app.State);
    }
}
