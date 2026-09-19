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
    private int _deactivations, _stops, _activations;
    private bool _shuttingDown;
    private bool _ready = true;
    private bool _buildOk = true;
    private bool _startOk = true;
    private DateTime _crashUtc = new(2026, 1, 1);
    private int _crashReads;
    private bool _crashAdvancesAfterFirstRead;

    private SafeModeLifecycle Make()
    {
        _fired.Clear(); _deactivations = _stops = _activations = 0;
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
            StopService: () => { },
            StartViaIdentity: () => _startOk,
            WaitForFreshToken: () => { },
            IsReady: () => _ready,
            RecordPid: () => { },
            ResolvePid: () => 999,
            PluginCrashUtc: () =>
                _crashAdvancesAfterFirstRead && ++_crashReads > 1
                    ? _crashUtc.AddMinutes(1) : _crashUtc,
            NoteShellRestart: () => { },
            PostToMainForm: _ => { },
            NavigateToServiceUrl: () => { },
            Port: 3080,
            TryFireLifecycle: t => { _fired.Add(t.ToString()); return true; }));
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

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "DshShell"))) d = d.Parent;
        return d!.FullName;
    }
}
