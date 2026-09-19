using DshWeb;
using DshWeb.Lifecycle;
using Xunit;

namespace DshShell.Tests.Lifecycle;

/// <summary>
/// [臃肿审计 Phase 4 · T5] 更新回滚 saga 的 Headless 回归（零真实进程、零磁盘、零窗口）。
///
/// 这条事务此前是组合根里的一个 static 方法 + 两个静态字段，于是三件事同时成立：
/// ① "已应用未确认健康"的武装标记只能以 <c>static string</c> 表达（状态机里当时没有落点）；
/// ② 重启半程被**第二次**手写（含 90 秒 <c>Thread.Sleep</c> 阻塞轮询），与 T1 的事务分叉；
/// ③ 失败出口只弹窗不留状态，回滚后再无任何可观测的收尾。
/// 本类逐条锁住这些不变式：一次性消费、挂起先于还原、降级只在全局路径、
/// 重启复用同一事务且**不**重复投递 RestartRequested、每个失败出口都闭合状态机。
/// </summary>
public class UpdateRollbackCoordinatorRegressionTests
{
    private const string FailedVersion = "0.1.1-rc.2";

    /// <summary>调用序列记录器：既断言"调没调"，也断言"先调谁"（时序缺陷的唯一侦测手段）。</summary>
    private readonly List<string> _seq = new();
    private readonly List<string> _fired = new();
    /// <summary>弹窗完成的信号（弹窗是三条可见路径的最后一步）。</summary>
    private readonly TaskCompletionSource<string> _shown = new(TaskCreationOptions.RunContinuationsAsynchronously);
    /// <summary>无弹窗路径（收尾中 / 事务抛错）的终态信号。</summary>
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private string? _unconfirmedSnapshot = FailedVersion; // 快照里"未确认健康"的版本（跨会话武装源）
    private string? _preApplyVersion = "0.1.1-rc.8";
    private UpdateRollbackResult _rollbackResult =
        new(true, new[] { ".credentials.yaml" }, @"C:\dsh\runtimes\0.1.1-rc.2", Array.Empty<string>());
    private ServiceRestartCoordinator.Outcome _restartOutcome = ServiceRestartCoordinator.Outcome.Ready;
    private int _restartBudgetSeconds;
    private string? _downgradedTo;
    private Exception? _rollbackThrows;
    private bool _markHealthyThrows;

    private UpdateRollbackCoordinator Make()
    {
        _seq.Clear(); _fired.Clear();
        var flow = new UpdateRollbackCoordinator(new UpdateRollbackCoordinator.Dependencies(
            Trace: _ => _seq.Add("trace"),
            PersistFailureEvidence: _ => _seq.Add("evidence"),
            ExportDiagnostics: () => _seq.Add("diagnostics"),
            SuspendMonitor: () => _seq.Add("suspend"),
            StopMonitor: () => _seq.Add("stopMonitor"),
            StopService: () => _seq.Add("stopService"),
            DiscoverIdentityVersion: () => FailedVersion,
            UnconfirmedSnapshotVersion: _ => _unconfirmedSnapshot,
            MarkHealthy: _ => { _seq.Add("markHealthy"); if (_markHealthyThrows) throw new IOException("locked"); },
            PreApplyIdentityVersion: () => _preApplyVersion,
            RollbackData: (_, _) =>
            {
                _seq.Add("rollbackData");
                if (_rollbackThrows is not null) throw _rollbackThrows;
                return _rollbackResult;
            },
            RestartService: budget =>
            {
                _seq.Add("restart");
                _restartBudgetSeconds = budget;
                return Task.FromResult(_restartOutcome);
            },
            DowngradeGlobalPackage: v => { _seq.Add("downgrade"); _downgradedTo = v; },
            NavigateToServiceUrl: () => _seq.Add("navigate"),
            ShowError: (code, msg) => { _seq.Add("showError"); _shown.TrySetResult(code + "|" + msg); },
            TryFireLifecycle: t =>
            {
                _seq.Add("fire:" + t);
                _fired.Add(t.ToString());
                if (t == LifecycleTrigger.RollbackFailed) _closed.TrySetResult(t.ToString());
                return true;
            }));
        // 前置默认：快照里存在"未确认健康"的版本 → 跨会话武装（武装本身不留调用序列，
        // 所以下面各用例断言的 _seq 仍然只包含回滚事务的副作用）。
        if (_unconfirmedSnapshot is not null) flow.ArmFromPersistedState();
        return flow;
    }

    private static BootVerdict Verdict() => new() { ErrorCode = ErrorCodes.E2008, Summary = "test verdict" };

    private Task<string> Shown() => _shown.Task.WaitAsync(TimeSpan.FromSeconds(5));
    private Task<string> Closed() => _closed.Task.WaitAsync(TimeSpan.FromSeconds(5));

    // ---- 武装标记的生命周期 ----

    [Fact]
    public void NotArmed_BootFailure_FallsThroughToExistingRecoveryFlow()
    {
        _unconfirmedSnapshot = null; // 无未确认快照 → 既未本会话武装也无跨会话武装
        var flow = Make();
        Assert.False(flow.TryHandleBootFailure(Verdict()));
        Assert.Empty(_seq); // 一行副作用都不该发生：证据落盘属既有恢复流程
    }

    [Fact]
    public async Task ArmedVersion_IsConsumedOnce_NoRollbackStorm()
    {
        var flow = Make();
        Assert.True(flow.TryHandleBootFailure(Verdict()));
        await Shown();
        Assert.False(flow.TryHandleBootFailure(Verdict())); // "失败→重试→再失败"循环被拒
        Assert.Equal(1, _seq.Count(s => s == "rollbackData"));
        Assert.Null(flow.ArmedVersion);
    }

    [Fact]
    public void ArmFromPersistedState_DoesNotOverrideSessionArmed()
    {
        var flow = Make();
        flow.ArmFromAppliedUpdate("1.2.3");
        _unconfirmedSnapshot = "9.9.9";
        flow.ArmFromPersistedState();
        Assert.Equal("1.2.3", flow.ArmedVersion);
    }

    [Fact]
    public void ArmFromPersistedState_ReadsUnconfirmedSnapshotOfCurrentIdentity()
    {
        var flow = Make();
        flow.ArmFromPersistedState();
        Assert.Equal(FailedVersion, flow.ArmedVersion);
        Assert.True(flow.RollbackArmedOnBootFailure);
    }

    [Fact]
    public void ConfirmHealthy_DisarmsBeforePersisting_SoPersistFailureCannotTriggerRollback()
    {
        var flow = Make();
        flow.ArmFromAppliedUpdate(FailedVersion);
        _markHealthyThrows = true;
        flow.ConfirmHealthy(); // 绝不把异常抛给健康监控线程
        Assert.Null(flow.ArmedVersion);
        Assert.False(flow.RollbackArmedOnBootFailure);
    }

    // ---- 同步前半程：证据与挂起必须先于事务体 ----

    [Fact]
    public async Task SuspendHappensBeforeDataRollback_ProbeCannotJudgeShellStoppedService()
    {
        var flow = Make();
        Assert.True(flow.TryHandleBootFailure(Verdict()));
        await Shown();
        var rollback = _seq.IndexOf("rollbackData");
        Assert.True(rollback > 0);
        Assert.InRange(_seq.IndexOf("suspend"), 0, rollback - 1); // 还原期间探针必须已挂起（否则回滚风暴）
        Assert.InRange(_seq.IndexOf("evidence"), 0, rollback - 1);
        Assert.InRange(_seq.IndexOf("diagnostics"), 0, rollback - 1);
        Assert.Equal("fire:RollbackRequested", _seq[_seq.IndexOf("suspend") + 1]);
    }

    [Fact]
    public async Task ServiceStoppedBeforeDataRollback_SoRuntimeDirIsNotLocked()
    {
        // 真机演练抓到的回归（2026-09-19）：搬迁时我丢了"先停服"这一环，于是
        // UpdateDataGuard 的 Directory.Move(runtimes\<version> → quarantine) 报
        // "The process cannot access the file because it is being used by another process"
        // ——服务进程的工作目录就在那个目录里。后果不是报错而是**回滚静默失效**：
        // 数据还原成功、状态机闭合、弹窗说"已隔离出启动发现链"，坏运行时却仍留在
        // DshDiscovery 的扫描路径上，下次启动又被选中。
        var flow = Make();
        Assert.True(flow.TryHandleBootFailure(Verdict()));
        await Shown();
        var rollback = _seq.IndexOf("rollbackData");
        var stop = _seq.IndexOf("stopService");
        Assert.True(stop >= 0, "回滚前必须显式停服（缺调用）");
        Assert.InRange(stop, _seq.IndexOf("suspend") + 1, rollback - 1); // 挂起后、还原前
        Assert.Equal(1, _seq.Count(s => s == "stopService"));
    }

    // ---- 两条安装路径的分岔：隔离 vs 降级 ----

    [Fact]
    public async Task SelfContainedPath_QuarantinesRuntime_AndNeverTouchesGlobalPackage()
    {
        var flow = Make();
        Assert.NotNull(_rollbackResult.QuarantinedRuntimeDir);
        Assert.True(flow.TryHandleBootFailure(Verdict()));
        var shown = await Shown();
        Assert.Null(_downgradedTo);
        Assert.DoesNotContain("downgrade", _seq);
        Assert.Contains("已隔离出启动发现链", shown);
        Assert.Contains(".credentials.yaml", shown);
    }

    [Fact]
    public async Task NpmPath_NothingToQuarantine_DowngradesGlobalPackageToPreApplyVersion()
    {
        var flow = Make();
        _rollbackResult = new UpdateRollbackResult(false, Array.Empty<string>(), null, Array.Empty<string>());
        Assert.True(flow.TryHandleBootFailure(Verdict()));
        var shown = await Shown();
        Assert.Equal(_preApplyVersion, _downgradedTo);
        Assert.Contains("无（npm 路径已尽力降级）", shown);
        Assert.Contains("无需还原/快照缺失", shown);
    }

    [Fact]
    public async Task NpmPath_UnknownPreApplyVersion_SkipsPointlessReinstall()
    {
        var flow = Make();
        _rollbackResult = new UpdateRollbackResult(true, new[] { "x" }, null, Array.Empty<string>());
        _preApplyVersion = null;
        Assert.True(flow.TryHandleBootFailure(Verdict()));
        await Shown();
        Assert.Null(_downgradedTo);
    }

    // ---- 重启半程：复用 T1 事务，且状态归回滚自己管 ----

    [Fact]
    public async Task RestartReusesSharedTransaction_WithRollbackBudget_AndWithoutRestartState()
    {
        var flow = Make();
        Assert.True(flow.TryHandleBootFailure(Verdict()));
        await Shown();
        Assert.Equal(UpdateRollbackCoordinator.RollbackReadyBudgetSeconds, _restartBudgetSeconds);
        Assert.Equal(1, _seq.Count(s => s == "restart"));
        // 关键：回滚自持状态，绝不再投 RestartRequested/Completed（那会与 RollingBackUpdate 争状态机）
        Assert.Equal(new[] { "RollbackRequested", "RollbackCompleted" }, _fired);
        Assert.Contains("navigate", _seq);
        Assert.DoesNotContain("stopMonitor", _seq);
    }

    [Fact]
    public async Task StartFailed_StopsMonitor_ClosesState_AndReportsE4003()
    {
        var flow = Make();
        _restartOutcome = ServiceRestartCoordinator.Outcome.StartFailed;
        Assert.True(flow.TryHandleBootFailure(Verdict()));
        var shown = await Shown();
        Assert.Contains("stopMonitor", _seq);
        Assert.Equal(new[] { "RollbackRequested", "RollbackFailed" }, _fired);
        Assert.StartsWith(ErrorCodes.E4003 + "|", shown);
        Assert.Contains("但服务重启失败", shown);
        Assert.DoesNotContain("navigate", _seq);
    }

    [Fact]
    public async Task NotReady_StopsMonitor_AndReportsBudgetInSeconds()
    {
        var flow = Make();
        _restartOutcome = ServiceRestartCoordinator.Outcome.NotReady;
        Assert.True(flow.TryHandleBootFailure(Verdict()));
        var shown = await Shown();
        Assert.Contains("stopMonitor", _seq);
        Assert.Equal(new[] { "RollbackRequested", "RollbackFailed" }, _fired);
        Assert.Contains("90 秒内未就绪", shown);
    }

    [Fact]
    public async Task SessionShuttingDown_AbsorbsWithoutDialog_ButClosesState()
    {
        var flow = Make();
        _restartOutcome = ServiceRestartCoordinator.Outcome.Cancelled;
        Assert.True(flow.TryHandleBootFailure(Verdict()));
        await Closed();
        Assert.DoesNotContain("showError", _seq);
        Assert.Equal(new[] { "RollbackRequested", "RollbackFailed" }, _fired);
    }

    [Fact]
    public async Task RollbackDataThrows_StopsMonitor_ClosesState_AndNeverRestarts()
    {
        var flow = Make();
        _rollbackThrows = new UnauthorizedAccessException("snapshot locked");
        Assert.True(flow.TryHandleBootFailure(Verdict()));
        await Closed();
        Assert.Contains("stopMonitor", _seq);
        Assert.Equal(new[] { "RollbackRequested", "RollbackFailed" }, _fired);
        Assert.DoesNotContain("restart", _seq); // 数据还原失败绝不继续拉起
    }
}
