using DshWeb;
using DshWeb.Lifecycle;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Managers;

/// <summary>
/// LauncherApp 组合根的 Headless 场景测试（维度二，重构后新增）：Fake Manager 驱动生命周期，
/// 不起 UI / 不起 Node / 不进网络。覆盖四个核心启动场景 + 运行期事务轨迹（场景 5，见文件末）：
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

    // ---------------- 场景 1b：运行期关停汇入状态机（F13） ----------------

    [Fact]
    public async Task RequestShutdown_AfterRunning_TransitionsToShuttingDown_F13()    {
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        var states = Trace(app);
        Assert.True(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Running, app.State);

        // 退出编排首行的关停请求必须产生 Running→ShuttingDown 转移（此前全程旁路状态机）。
        Assert.True(app.RequestShutdown());
        Assert.Equal(LifecycleState.ShuttingDown, app.State);
        Assert.Contains(LifecycleState.ShuttingDown, states);
    }

    [Fact]
    public async Task RequestShutdown_Twice_IsIdempotentTransition()
    {
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        await app.RunStartupAsync();
        Assert.True(app.RequestShutdown());
        // ShuttingDown 下再次请求：RequestShutdown 检测非 Running 不再 Fire（幂等，防非法转移）
        Assert.False(app.RequestShutdown());
        Assert.Equal(LifecycleState.ShuttingDown, app.State);
    }

    [Fact]
    public void HandleWebViewCrashed_FromIdle_IsAbsorbedWithoutThrow()
    {
        // 终结/未启动态收到崩溃事件：只记日志吸收，绝不向状态机投递非法转移（Fail-Fast 保留给编程错误）。
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        app.HandleWebViewCrashed();
        Assert.Equal(LifecycleState.Idle, app.State);
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

    // ---------------- 场景 3b：服务就绪前进程退出（issue #26 快速失败，不盲等预算） ----------------

    [Fact]
    public async Task ServiceExitedBeforeReady_FailsFast_TransitionsToShuttingDown_StaleCleanupInvoked()
    {
        var cleanedPort = -1;
        // 与生产同构：组合根把就绪裁决注入 ReadinessProbe（生产 = PollReadiness）；
        // Headless 用 FakeService.PollReadiness 的 ReadinessVerdict 覆盖锁定第五态。
        var service = new FakeService
        {
            PortState = ShellLogic.ServicePortState.Closed, // 需要拉起服务（Start 被调用）
            ReadinessVerdict = ShellLogic.ServiceReadiness.ServiceExitedVerdict,
        };
        var app = new LauncherApp(new FakeRuntime(), service, staleCleanup: port => cleanedPort = port);
        app.ReadinessProbe = ct => Task.FromResult(
            service.PollReadiness(ct, 3080, "http://127.0.0.1:3080", "unused.log", e2eMode: true));
        var states = Trace(app);

        Assert.False(await app.RunStartupAsync());
        // 与 timeout/logerror 同一失败收敛：ReadinessTimedOut → ShuttingDown（状态机语义不变）
        Assert.Equal(LifecycleState.ShuttingDown, app.State);
        Assert.Contains(LifecycleState.ShuttingDown, states);
        // 快速失败语义锁定：WaitResult 是 service-exited（Program 据此映射 E2010 + 进程输出线索），
        // 不再被吞成笼统 timeout；组合根超时清理同样被触发（端口透传正确）。
        Assert.Equal(ShellLogic.ServiceReadiness.ServiceExitedVerdict, app.WaitResult);
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

    // ---------------- [issue #28-3] 健康残留服务：清理重启 vs 沿用接管 ----------------

    [Fact]
    public async Task HealthyLeftover_ShellOwned_RestartsInsteadOfAdopting_28()
    {
        // 端口上已是健康服务 + 策略判定"这是本壳上次会话的残留 + 驻留模式要求服务跟随壳"
        // → 必须就地清理后重新拉起（插件/配置改动才生效，用户报告的"伪重启"根因）
        var service = new FakeService { Ready = true, PortState = ShellLogic.ServicePortState.Healthy };
        var app = new LauncherApp(new FakeRuntime(), service)
        {
            RestartHealthyLeftoverPolicy = () => true,
        };

        Assert.True(await app.RunStartupAsync());
        Assert.Equal(1, service.KillZombieCalls);       // 残留被清理
        Assert.Equal(1, service.StartCalls);            // 随后由本壳重新拉起
        Assert.True(app.ServiceStartedByShell);         // 关停语义随之为"壳拥有"（不残留无主服务）
        Assert.Equal(LifecycleState.Running, app.State);
    }

    [Fact]
    public async Task HealthyLeftover_NotShellOwned_IsAdoptedUnchanged_28()
    {
        // 账本外/常驻模式/外部托管：沿用既有"健康服务不杀也不动"语义（绝不误杀用户自己的 node）
        var service = new FakeService { Ready = true, PortState = ShellLogic.ServicePortState.Healthy };
        var app = new LauncherApp(new FakeRuntime(), service)
        {
            RestartHealthyLeftoverPolicy = () => false,
        };

        Assert.True(await app.RunStartupAsync());
        Assert.Equal(0, service.KillZombieCalls);
        Assert.Equal(0, service.StartCalls);
        Assert.False(app.ServiceStartedByShell);
        Assert.Equal(LifecycleState.Running, app.State);
    }

    [Fact]
    public async Task HealthyLeftover_KillFails_FallsBackToUsableService_28()
    {
        // 清理失败（杀不干净/端口未释放）：保底继续用旧服务，绝不把"可用界面"变成"启不来"
        var service = new FakeService
        {
            Ready = true,
            PortState = ShellLogic.ServicePortState.Healthy,
            KillZombieResult = false,
        };
        var app = new LauncherApp(new FakeRuntime(), service)
        {
            RestartHealthyLeftoverPolicy = () => true,
        };

        Assert.True(await app.RunStartupAsync());
        Assert.Equal(1, service.KillZombieCalls);
        Assert.Equal(0, service.StartCalls);            // 未重新拉起（沿用旧服务）
        Assert.False(app.ServiceStartedByShell);
        Assert.Equal(LifecycleState.Running, app.State);
    }

    // ---------------- [issue #28-4] 启动路径与重启路径共用同一条安全模式 profile 判定 ----------------

    [Fact]
    public async Task StickySafeMode_BootStart_CarriesSameSafeProfileAsRestart_28()
    {
        // 事故：粘滞的 safe-mode.json 只被重启路径读到 —— 启动带全套插件，点 DSH 内置重启插件消失。
        // 现在初始拉起同样经组合根注入的装饰钩子，两侧身份一致。
        var service = new FakeService { Ready = true, PortState = ShellLogic.ServicePortState.Closed };
        var app = new LauncherApp(new FakeRuntime(), service)
        {
            ServiceIdentityDecorator = id => DshWeb.Domain.SafeModeLaunchPolicy.Decorate(
                id, safeModeActive: true, @"C:\Users\x\.dsh\profiles\.dsh-safe"),
        };

        Assert.True(await app.RunStartupAsync());
        var started = service.LastStartArgs;
        Assert.NotNull(started);
        Assert.True(started!.Value.Identity.IsSafeProfile,
            "粘滞安全模式下初始拉起必须带隔离 profile（与重启路径对称）");
        Assert.Equal(@"C:\Users\x\.dsh\profiles\.dsh-safe", started!.Value.Identity.ProfilePath);
    }

    [Fact]
    public async Task NoDecorator_BootStart_IdentityBitForBitUnchanged_28()
    {
        // 未注入钩子（Headless/其它装配方）时逐位不变：本次修复不得改变默认启动身份
        var service = new FakeService { Ready = true, PortState = ShellLogic.ServicePortState.Closed };
        var app = new LauncherApp(new FakeRuntime(), service);

        Assert.True(await app.RunStartupAsync());
        Assert.False(service.LastStartArgs!.Value.Identity.IsSafeProfile);
        Assert.Equal(IdentityFixtures.Launchable(), service.LastStartArgs!.Value.Identity);
    }

    // ---------------- 场景 5：运行期事务经过 LauncherApp 的轨迹 ----------------
    // AGENTS.md 铁律「修改生命周期流转必须在 LauncherAppScenarioTests 补 Headless 测试」。
    // 臃肿审计 Phase 3 给状态机补了 RestartingService / EnteringSafeMode / ExitingSafeMode /
    // ApplyingUpdate / RollingBackUpdate 五个运行态，Phase 4 把三条事务搬进协调器后，
    // 事务是通过 LauncherApp.TryFire 这个**唯一**受控入口投递的——所以轨迹必须在这个缝上断言，
    // 而不是只在 LauncherLifecycle 的单元测试里断言（那测的是表，测不到缝）。

    [Theory]
    [InlineData(LifecycleTrigger.RestartRequested, LifecycleState.RestartingService, LifecycleTrigger.RestartCompleted)]
    [InlineData(LifecycleTrigger.SafeModeEntryRequested, LifecycleState.EnteringSafeMode, LifecycleTrigger.SafeModeEntered)]
    [InlineData(LifecycleTrigger.SafeModeExitRequested, LifecycleState.ExitingSafeMode, LifecycleTrigger.SafeModeExited)]
    [InlineData(LifecycleTrigger.UpdateApplyRequested, LifecycleState.ApplyingUpdate, LifecycleTrigger.UpdateApplied)]
    [InlineData(LifecycleTrigger.RollbackRequested, LifecycleState.RollingBackUpdate, LifecycleTrigger.RollbackCompleted)]
    public async Task RuntimeTransaction_TransitsOutOfRunningAndBack_ThroughAppSeam(
        LifecycleTrigger begin, LifecycleState transientState, LifecycleTrigger end)
    {
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        Assert.True(await app.RunStartupAsync());
        Assert.Equal(LifecycleState.Running, app.State);
        var states = Trace(app);

        Assert.True(app.TryFire(begin), "稳态下开始事务必须被接受");
        Assert.Equal(transientState, app.State);
        Assert.True(app.TryFire(end), "事务完成事件必须把状态送回 Running");
        Assert.Equal(LifecycleState.Running, app.State);
        Assert.Equal(new[] { transientState, LifecycleState.Running }, states);
    }

    /// <summary>
    /// 两个运行期事务不得同时占用状态机：回滚 saga 复用共享重启事务时靠
    /// <c>driveLifecycleState:false</c> 避免争态。这里锁住"缝"的语义——非法嵌套被**吸收**而不是抛，
    /// 否则自愈路径会把自己的事务炸掉（TryFire 与直接 Fire 的区别正在这里）。
    /// </summary>
    [Fact]
    public async Task NestedRuntimeTransaction_IsAbsorbed_AndOriginalTransactionStillCloses()
    {
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        Assert.True(await app.RunStartupAsync());
        Assert.True(app.TryFire(LifecycleTrigger.RollbackRequested));

        Assert.False(app.TryFire(LifecycleTrigger.RestartRequested), "回滚中再投重启必须被吸收");
        Assert.Equal(LifecycleState.RollingBackUpdate, app.State);

        Assert.True(app.TryFire(LifecycleTrigger.RollbackCompleted));
        Assert.Equal(LifecycleState.Running, app.State);
    }

    /// <summary>事务进行中用户关窗必须放行（否则"退出"被瞬时态卡死，退出路径直接抛异常）。</summary>
    [Fact]
    public async Task ShutdownDuringRuntimeTransaction_IsAllowed()
    {
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        Assert.True(await app.RunStartupAsync());
        Assert.True(app.TryFire(LifecycleTrigger.RestartRequested));

        Assert.True(app.TryFire(LifecycleTrigger.ShutdownRequested));
        Assert.Equal(LifecycleState.ShuttingDown, app.State);
        // 已进终态后事务完成事件被吸收：不得把会话从 ShuttingDown 拉回 Running
        Assert.False(app.TryFire(LifecycleTrigger.RestartCompleted));
        Assert.Equal(LifecycleState.ShuttingDown, app.State);
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
