using DshWeb;
using DshWeb.Domain;
using DshWeb.Lifecycle;
using DshWeb.Windows;
using Xunit;

namespace DshShell.Tests.Lifecycle;

/// <summary>
/// [臃肿审计 Phase 4 · T3] 安全模式进入的补偿路径与状态机登记（Headless，零真实进程）。
///
/// 这条流转是"带补偿的多步事务"最典型的形态（Build → Activate → Suspend → Stop → Start →
/// WaitToken → Verify → Record → Resume → 横幅 → Reload），每个失败出口都必须留下**一致**的
/// 反向补偿。审计要点：验证失败时若谎报成功，用户看到的正是 #28-4 的"插件凭空消失且毫无解释"。
/// </summary>
public class SafeModeLifecycleTests
{
    private readonly List<string> _fired = new();
    private readonly List<string> _ui = new();
    private int _deactivations, _stops, _activations, _posts;
    private bool _shuttingDown;
    private bool _ready = true;
    private bool _buildOk = true;
    private bool _startOk = true;
    private DateTime _crashUtc = new(2026, 1, 1);
    private int _crashReads;
    private bool _crashAdvancesAfterFirstRead;
    // [审查 N3] 两个新旋钮：状态机拒绝 Requested / 停服中途抛异常
    private bool _refuseEntryRequest;
    private bool _stopThrows;

    private SafeModeLifecycle Make()
    {
        _fired.Clear(); _ui.Clear(); _posts = 0;
        _deactivations = _stops = _activations = 0;
        _refuseEntryRequest = false; _stopThrows = false;
        return new SafeModeLifecycle(new SafeModeLifecycle.Dependencies(
            Trace: _ => { },
            SessionShuttingDown: () => _shuttingDown,
            BuildProfile: _ => _buildOk,
            Activate: _ => _activations++,
            Deactivate: () => _deactivations++,
            SafeProfileDir: () => "/tmp/.dsh-safe",
            SuspendMonitor: () => { },
            StopMonitor: () => _stops++,
            ResumeMonitor: _ => { },
            StopService: () => { _ui.Add("stop-service"); if (_stopThrows) throw new InvalidOperationException("boom (test)"); },
            StartViaIdentity: () => _startOk,
            WaitForFreshToken: () => { },
            IsReady: () => _ready,
            RecordPid: () => { },
            ResolvePid: () => 999,
            PluginCrashUtc: () =>
                _crashAdvancesAfterFirstRead && ++_crashReads > 1
                    ? _crashUtc.AddMinutes(1) : _crashUtc,
            NoteShellRestart: () => { },
            PostToMainForm: _ => _posts++,
            NavigateToServiceUrl: () => { },
            // 等待态由组合根投递到 UI 线程；这里只记录"有没有给、什么时候给"
            ShowWaitingPage: (headline, _) => _ui.Add("waiting:" + headline),
            Port: 3080,
            TryFireLifecycle: t =>
            {
                if (t == LifecycleTrigger.SafeModeEntryRequested && _refuseEntryRequest) return false;
                _fired.Add(t.ToString()); return true;
            }));
    }

    /// <summary>
    /// 真机 2026-09-20 用户反馈：停服→重拉这 20 秒里界面挂着已断连的旧页面，被读成"点了没反应"。
    /// 所以等待态必须**在停服之前**给出（停服之后才有空窗），而不是等重启完成才动。
    /// </summary>
    [Fact]
    public void TryEnter_ShowsWaitingState_BeforeTheServiceIsStopped()
    {
        Assert.True(Make().TryEnter(null!, SafeProfileTier.Tier1KeepDeepSeekCore));
        Assert.Equal(new[] { "waiting:正在进入安全模式…", "stop-service" }, _ui);
    }

    /// <summary>拉不起服务时，等待态/横幅必须被撤回——绝不能把"正在进入安全模式…"留在一个
    /// 其实没进安全模式的窗口上（谎报状态是本项目反复踩的那一类缺陷）。</summary>
    [Fact]
    public void TryEnter_StartFailure_RestoresVisibility_InsteadOfLeavingTheWaitingBanner()
    {
        _startOk = false;
        Assert.False(Make().TryEnter(null!, SafeProfileTier.Tier1KeepDeepSeekCore));
        Assert.True(_posts >= 1, $"失败出口未重画可见性（PostToMainForm 调用 {_posts} 次）");
    }

    /// <summary>成功进入：登记 Requested → Entered，且绝不 Deactivate（状态必须留在激活）。</summary>
    [Fact]
    public void Success_FiresRequestedThenEntered_AndStaysActivated()
    {
        var ok = Make().TryEnter(null!, SafeProfileTier.Tier1KeepDeepSeekCore);
        Assert.True(ok);
        Assert.Equal(new[] { "SafeModeEntryRequested", "SafeModeEntered" }, _fired);
        Assert.Equal(1, _activations);
        Assert.Equal(0, _deactivations);
    }

    /// <summary>
    /// profile 构建失败：不得 Activate，且**必须闭合状态机事务**。
    /// Requested 在 Build 之前就已投递（事务确实开始了），所以失败出口若只 return 而不投
    /// EntryFailed，状态机会被留在 EnteringSafeMode 这个瞬时态里 —— 之后任何运行期触发
    /// 都会抛非法转移。这条是我先写错、被本用例抓出来的。
    /// </summary>
    [Fact]
    public void BuildFailure_DoesNotActivate_ButClosesTheTransaction()
    {
        var m = Make();
        _buildOk = false;
        Assert.False(m.TryEnter(null!, SafeProfileTier.Tier1KeepDeepSeekCore));
        Assert.Equal(0, _activations);
        Assert.Equal(new[] { "SafeModeEntryRequested", "SafeModeEntryFailed" }, _fired);
    }

    /// <summary>拉起失败必须退回非激活并停监控——否则留下"自称安全模式却没有服务"的状态。</summary>
    [Fact]
    public void StartFailure_CompensatesByDeactivatingAndStoppingMonitor()
    {
        var m = Make();
        _startOk = false;
        Assert.False(m.TryEnter(null!, SafeProfileTier.Tier2Minimal));
        Assert.Equal(1, _deactivations);
        Assert.Equal(1, _stops);
        Assert.Contains("SafeModeEntryFailed", _fired);
        Assert.DoesNotContain("SafeModeEntered", _fired);
    }

    /// <summary>
    /// 验证失败（就绪但插件崩溃签名仍在）：必须 Deactivate + 停监控 + 登记 EntryFailed。
    /// 这条就是"不谎报成功"的机器化表达。
    /// </summary>
    [Fact]
    public void VerificationFailure_NeverClaimsSuccess()
    {
        // 两条判据分别单独验证，避免为了造失败而真等 60s/5s 预算
        var clean = Make();
        Assert.True(clean.WaitVerified(), "无新崩溃签名时应判定通过");

        var m = Make();
        _crashAdvancesAfterFirstRead = true; // 观察窗内出现新的插件崩溃 → 阶段二判失败
        Assert.False(m.TryEnter(null!, SafeProfileTier.Tier1KeepDeepSeekCore));
        Assert.Equal(1, _deactivations);
        Assert.Contains("SafeModeEntryFailed", _fired);
    }

    /// <summary>会话收尾：守卫先于 Requested，因此真的什么都不投递、不 Build、不 Activate。</summary>
    [Fact]
    public void ShuttingDown_SkipsBeforeAnySideEffect()
    {
        var m = Make();
        _shuttingDown = true;
        Assert.False(m.TryEnter(null!, SafeProfileTier.Tier1KeepDeepSeekCore));
        Assert.Equal(0, _activations);
        Assert.Empty(_fired);
    }

    /// <summary>验证的两条判据都必须真实存在（缺一条就变成"只要端口开着就报安全模式成功"）。</summary>
    [Fact]
    public void Verification_UsesBothReadinessAndCrashSignature()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DshShell", "Lifecycle", "SafeModeLifecycle.cs"));
        Assert.Contains("PluginCrashUtc()", src);
        Assert.Contains("IsReady()", src);
        Assert.Contains("CrashSignatureObservationSeconds", src);
    }

    /// <summary>结构闸：这条带补偿事务不得回流组合根。</summary>
    [Fact]
    public void CompositionRootHoldsNoSafeModeTransaction()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DshShell", "Program.cs"));
        foreach (var banned in new[] { "TryStartSafeMode", "WaitSafeModeVerified", "static void ApplySafeModeVisibility" })
            Assert.DoesNotContain(banned, src);
        Assert.Contains("SafeModeFlow!.TryEnter(", src);
        Assert.Contains("SafeModeFlow!.ApplyVisibility(", src);
    }

    /// <summary>[审查 N3 2026-09-21] 事务中途抛异常也必须闭合状态机事务（投 EntryFailed）。
    /// 审查实测：其余失败出口都有这一投，唯独 catch 漏了 → 状态机永久滞留 EnteringSafeMode
    /// 瞬时态，下一次任何运行期触发都是非法转移。</summary>
    [Fact]
    public void ExceptionMidTransaction_StillClosesTheStateMachineTransaction()
    {
        var m = Make();
        _stopThrows = true;
        Assert.False(m.TryEnter(null!, SafeProfileTier.Tier1KeepDeepSeekCore));
        Assert.Equal(new[] { "SafeModeEntryRequested", "SafeModeEntryFailed" }, _fired);
        Assert.Equal(1, _deactivations);
        Assert.Equal(1, _stops); // StopMonitor 的反向补偿照常发生
    }

    /// <summary>[审查 N3] 状态机拒绝 Requested = 这一跳整个跳过：不停服、不建等待态、不激活。
    /// "状态机唯一真相源"的机器化表达——此前忽略返回值照常跑事务，事务落在状态机盲区。</summary>
    [Fact]
    public void RefusedEntryRequest_SkipsTheWholeTransaction_WithoutSideEffects()
    {
        var m = Make();
        _refuseEntryRequest = true;
        Assert.False(m.TryEnter(null!, SafeProfileTier.Tier1KeepDeepSeekCore));
        Assert.Equal(0, _activations);
        Assert.Empty(_ui);      // 无"stop-service"、无等待态
        Assert.Empty(_fired);   // 被拒绝的投递不落账
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "DshShell"))) d = d.Parent;
        return d!.FullName;
    }
}
