using DshWeb;
using DshWeb.Lifecycle;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Managers;

/// <summary>LauncherApp 组合根的 Headless 集成测试：Fake Manager 驱动生命周期，不起 UI/Node。
/// 验证"服务就绪 → Running"与"就绪超时 → ShuttingDown"两条核心路径（E2002 映射由组合根承担）。
/// 【ADR-024】Fake 收敛至共享 TestFakes（Identity 契约单一来源）。</summary>
[Collection("EnvHygiene")]
    public class LauncherAppTests
{
    public LauncherAppTests() => EnvHygiene.ClearHostileEnv();
    [Fact]
    public void Composition_AssemblesAllFiveManagers()
    {
        var app = new LauncherApp();
        Assert.NotNull(app.Runtime);
        Assert.NotNull(app.Service);
        Assert.NotNull(app.WebView);
        Assert.NotNull(app.Window);
        Assert.NotNull(app.Tray);
    }

    [Fact]
    public void WindowManager_DelegateSeamsWork()
    {
        // 委托现有实现（零行为变更）：弹窗创建需 UI 上下文，这里只验证可调用表面
        var m = new DshWeb.Managers.WindowManager();
        Assert.IsType<bool>(m.ResolveDarkMode());
    }

    [Fact]
    public async Task ServiceReady_DrivesToRunning()
    {
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        Assert.True(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Running, app.State);
    }

    [Fact]
    public async Task ServiceStart_ReceivesIdentity_FromRuntimeManager()
    {
        // 【ADR-024 契约】IServiceManager.Start 必须收到 IRuntimeManager 产出的同一 Identity 实例，
        // 且端口/日志路径由组合根装配——跨模块零散装字符串。
        var identity = IdentityFixtures.Launchable("1.2.3-test");
        var service = new FakeService { Ready = true, PortState = ShellLogic.ServicePortState.Closed };
        var app = new LauncherApp(
            new FakeRuntime { Result = RuntimeResolution.Ready(identity) }, service,
            serviceLogPath: @"C:\fake\dsh.log");
        Assert.True(await app.RunStartupAsync());

        Assert.Equal(1, service.StartCalls); // 端口 Closed → 拉起一次
        Assert.NotNull(service.LastStartArgs);
        Assert.Equal(identity, service.LastStartArgs!.Value.Identity); // 同一身份实例流动
        Assert.Equal(3080, service.LastStartArgs.Value.Port);
        Assert.Equal(@"C:\fake\dsh.log", service.LastStartArgs.Value.LogPath);
    }

    [Fact]
    public async Task ServiceStartFailure_DrivesToFailed_WithE2001()
    {
        var app = new LauncherApp(new FakeRuntime(),
            new FakeService { Ready = true, StartResult = false, PortState = ShellLogic.ServicePortState.Closed });
        Assert.False(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Failed, app.State);
        Assert.Equal(ErrorCodes.E2001, app.LastErrorCode);
    }

    [Fact]
    public async Task ServiceStartFailure_EntryMissing_DetailShowsProbedPaths_NoVbsWording()
    {
        // issue #24 归因契约：!CanLaunchDirectly 的 E2001 必须带真实探查位置，
        // 且不再出现误导性的"start-dsh.vbs"文案（0.4.x 启动链已无 vbs）。
        var identity = IdentityFixtures.Launchable() with
        {
            DshEntryJsPath = null,
            NodeExePath = @"C:\fake-tools\node.exe",
            EntryProbeFailures = new List<string>
            {
                @"near-shim(missing):D:\my-npm\node_modules\@deepseek-ai\dsh",
                @"shim-content(no-dsh-entry):D:\my-npm\dsh.cmd",
                @"legacy:C:\Users\x\AppData\Roaming\npm\node_modules\@deepseek-ai\dsh",
            }
        };
        var app = new LauncherApp(new FakeRuntime { Result = RuntimeResolution.Ready(identity) },
            new FakeService { Ready = true, StartResult = false, PortState = ShellLogic.ServicePortState.Closed });

        Assert.False(await app.RunStartupAsync());
        Assert.Equal(ErrorCodes.E2001, app.LastErrorCode);
        Assert.DoesNotContain("start-dsh.vbs", app.LastErrorDetail);
        Assert.Contains("已在以下位置探查均未命中", app.LastErrorDetail);
        Assert.Contains(@"D:\my-npm\node_modules\@deepseek-ai\dsh", app.LastErrorDetail);
        Assert.Contains("dsh --version", app.LastErrorDetail);
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
        var app = new LauncherApp(new FakeRuntime { Result = RuntimeResolution.Failed("E1003", "download failed") },
            new FakeService { Ready = true });
        Assert.False(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Failed, app.State);
    }
}
