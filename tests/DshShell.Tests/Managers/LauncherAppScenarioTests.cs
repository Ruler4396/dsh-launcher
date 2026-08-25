using DshWeb;
using DshWeb.Lifecycle;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Managers;

/// <summary>
/// LauncherApp 组合根的 Headless 场景测试（维度二，重构后新增）：Fake Manager 驱动生命周期，
/// 不起 UI / 不起 Node / 不进网络。覆盖四个核心场景：
///   1. Happy Path：所有 Manager 成功 → Idle→…→Running，UI 初始化事件被触发；
///   2. Runtime Failure：IRuntimeManager 报 E1004（便携 Node 校验和不匹配）→ Failed + 错误码保留；
///   3. Service Readiness Timeout：HTTP 探测超时 → ShuttingDown + E2002 语义 + 僵尸清理回调被触发；
///   4. WebView2 Crash Recovery：崩溃事件 → 状态机自转移保持 Running（拦截并触发重载，不崩溃）。
/// 全部确定性、毫秒级；断言状态机最终态 + 状态轨迹 + 副作用回调（staleCleanup）。
/// </summary>
[Collection("EnvHygiene")]
    public class LauncherAppScenarioTests
{
    public LauncherAppScenarioTests() => EnvHygiene.ClearHostileEnv();
    // ---------------- Fakes：共享 TestFakes（ADR-024 Identity 契约单一来源） ----------------

    /// <summary>订阅 StateChanged，返回状态轨迹（含最终 Running/初始化事件时序）。</summary>
    private static List<LifecycleState> Trace(LauncherApp app)
    {
        var states = new List<LifecycleState>();
        app.StateChanged += (_, s) => states.Add(s);
        return states;
    }

    // ---------------- 场景 1：Happy Path ----------------

    [Fact]
    public async Task HappyPath_AllManagersSucceed_EndsAtRunning_AndUIInitializedFired()
    {
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        var states = Trace(app);

        Assert.True(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Running, app.State);

        // UI 初始化事件（InitializingUI→Running）确实被驱动：若组合根跳过装配，轨迹缺环即失败
        Assert.Contains(LifecycleState.InitializingUI, states);
        Assert.Contains(LifecycleState.Running, states);
        Assert.Equal(
            new[] { LifecycleState.CheckingInstance, LifecycleState.ResolvingRuntime,
                    LifecycleState.StartingService, LifecycleState.WaitingForReadiness,
                    LifecycleState.InitializingUI, LifecycleState.Running },
            states);
    }

    // ---------------- 场景 2：Runtime Failure（E1004 校验和不匹配） ----------------

    [Fact]
    public async Task RuntimeFailure_E1004_TransitionsToFailed_AndCodePreserved()
    {
        var runtime = new FakeRuntime { Result = RuntimeResolution.Failed(ErrorCodes.E1004, "sha256 mismatch") };
        var app = new LauncherApp(runtime, new FakeService { Ready = true });

        Assert.False(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Failed, app.State); // RuntimeFailed → Failed（非 ShuttingDown，见审查报告）

        // 错误码不再被丢弃：RuntimeResolution 承载 E1004（此前 Failed() 工厂丢码，组合根无法区分
        // "下载失败 E1003"与"校验和不匹配 E1004"——修复见 ManagerInterfaces 注释）。
        Assert.Equal(ErrorCodes.E1004, runtime.Result.ErrorCode);
    }

    // ---------------- 场景 3：Service Readiness Timeout（E2002 + 僵尸清理） ----------------

    [Fact]
    public async Task ReadinessTimeout_TransitionsToShuttingDown_AndStaleCleanupInvoked()
    {
        var cleanedPort = -1;
        var app = new LauncherApp(
            new FakeRuntime(),
            new FakeService { Ready = false }, // HTTP 探测超时
            staleCleanup: port => cleanedPort = port);
        var states = Trace(app);

        Assert.False(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.ShuttingDown, app.State); // ReadinessTimedOut → ShuttingDown
        Assert.Contains(LifecycleState.ShuttingDown, states);

        // 超时清理（Kill 孤儿进程，E2005 语义）在组合根超时分支被触发，端口透传正确
        Assert.Equal(3080, cleanedPort);
    }

    // ---------------- 场景 5：僵尸服务（端口开但 HTTP 死）→ 清理 + 重新拉起 ----------------
    // 根因修复（任务一）：TCP 已开但 HTTP 不通时不再误判"服务健康"傻等 180s——先强杀进程树
    //（taskkill /T /F 含 cmd/npx 外壳），清理成功后再走正常拉起；清理失败快速失败 E2004。

    [Fact]
    public async Task ZombiePort_KillSucceeds_StartServiceInvoked_ThenReadiness()
    {
        var service = new FakeService { PortState = ShellLogic.ServicePortState.Zombie, Ready = true };
        var app = new LauncherApp(new FakeRuntime(), service);
        var states = Trace(app);

        Assert.True(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Running, app.State);
        // 僵尸清理被触发一次；随后按 Identity 直启服务（ServiceManager.Start(identity)，ADR-024）
        Assert.Equal(1, service.KillZombieCalls);
        Assert.Equal(1, service.StartCalls);
        Assert.NotNull(service.LastStartArgs);
        Assert.Contains(LifecycleState.StartingService, states);
    }

    [Fact]
    public async Task ZombiePort_KillFails_TransitionsToFailed_WithE2004_NotTimedOut()
    {
        var service = new FakeService { PortState = ShellLogic.ServicePortState.Zombie, KillZombieResult = false };
        var app = new LauncherApp(new FakeRuntime(), service);

        Assert.False(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Failed, app.State); // 清理失败 → 快速失败，不进入 180s 傻等
        Assert.Equal(ErrorCodes.E2004, app.LastErrorCode);
        Assert.Equal(0, service.StartCalls); // 清理失败绝不拉起
        Assert.Equal(1, service.KillZombieCalls);
    }

    [Fact]
    public async Task ForeignPort_OccupiedByOtherProgram_TransitionsToFailed_WithE2004()
    {
        var service = new FakeService { PortState = ShellLogic.ServicePortState.Foreign };
        var app = new LauncherApp(new FakeRuntime(), service);

        Assert.False(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Failed, app.State);
        Assert.Equal(ErrorCodes.E2004, app.LastErrorCode);
        Assert.Equal(0, service.KillZombieCalls); // 非 dsh 进程 → 不清理（防误杀）
    }

    // ---------------- 场景 4：WebView2 Crash Recovery ----------------

    [Fact]
    public async Task WebViewCrash_WhileRunning_StateStaysRunning_AndEventBroadcast()
    {
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        Assert.True(await app.RunStartupAsync()); // → Running
        var states = Trace(app); // 启动完成后才订阅：只观察崩溃广播

        // 模拟 WebViewManager.ProcessFailed → 组合根 HandleWebViewCrashed（崩溃被拦截）
        app.HandleWebViewCrashed();

        // 应用不崩溃：状态保持 Running，且恰好广播一次自转移（观察者收到后可触发重载副作用）
        Assert.Equal(LifecycleState.Running, app.State);
        var broadcast = Assert.Single(states);
        Assert.Equal(LifecycleState.Running, broadcast);
    }

    // ---------------- 异常边界：Manager 抛异常不得悬停状态机 ----------------

    [Fact]
    public async Task RuntimeManager_ThrowsUnexpected_MapsToRuntimeFailed_NotSuspended()
    {
        var app = new LauncherApp(
            new FakeRuntime { ThrowOnEnsure = new InvalidOperationException("boom") },
            new FakeService { Ready = true });

        // 此前 EnsureRuntimeAsync 异常会直接冒泡、状态机悬停在 ResolvingRuntime；
        // 修复后异常被映射为 RuntimeFailed → Failed（留痕 E9001），调用方拿到确定性结果。
        Assert.False(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Failed, app.State);
    }

    // ---------------- 场景 6：阶段 0 真实文件副作用（SDET 支柱二：不只测状态转移，测系统副作用） ----------------
    // 状态机执行阶段 0 的 BackgroundMaintenance 时，注入的委托应真实写盘 pending-update.json，
    // 断言 RunStartupAsync 后文件真实存在且内容可读——锁定"状态机驱动真实 IO 副作用"链路。

    [Fact]
    public async Task Stage0_BackgroundMaintenance_RealFileSideEffect_CreatesPendingUpdateJson()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true })
        {
            // 阶段 0 注入真实写盘副作用：写入 pending-update.json（等价组合根 RunBackgroundMaintenance
            // 中 HandlePendingUpdateAtStartup 的落盘路径）
            BackgroundMaintenance = _ => StagedUpdate.MarkPending("1.2.3", "deepseek-ai-dsh-1.2.3.tgz"),
        };

        Assert.True(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Running, app.State);

        // 真实文件副作用断言：pending-update.json 真实落盘且内容可解析
        var pendingPath = Path.Combine(tmp.Path, "pending-update.json");
        Assert.True(File.Exists(pendingPath), "状态机阶段 0 应驱动真实落盘 pending-update.json");
        var text = File.ReadAllText(pendingPath);
        Assert.Contains("\"version\":\"1.2.3\"", text);
        Assert.Contains("\"tarball\":\"deepseek-ai-dsh-1.2.3.tgz\"", text);
    }

    /// <summary>每测试用一次性临时目录（与 UpdateFlowContractTests 同风格）。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dsh-launcher-scenario-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
