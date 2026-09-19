using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// [臃肿审计 Phase 4 · T4] 后台更新检查的通知裁决契约（纯函数，脱网可测）。
///
/// 这四条规则此前埋在组合根 126 行的任务体里，与网络调用混写，只能靠读代码确认。
/// 其中"launcher 安全更新命中即终止"和"三条去重跳过"的顺序都有历史事故背景
/// （更新死循环：下载成功 → pending → 重开又弹），所以逐条钉死。
/// </summary>
public class UpdateNoticeFlowContractTests
{
    private const string L = "0.4.6";   // launcher 远端
    private const string LL = "0.4.5";  // launcher 本地
    private const string D = "0.1.3";   // dsh 远端

    private static ShellLogic.UpdateNoticeFlowPolicy.Outcome Decide(
        string? pending = null, bool launcherNotify = false, string? launcherVersion = null,
        string? launcherLocal = LL, string? latest = null, string? local = "0.1.2",
        int newer = 1, bool stagedThisSession = false, int latestVsSkipped = 1, int pendingVsLatest = -1)
        => ShellLogic.UpdateNoticeFlowPolicy.Decide(
            pending, launcherNotify, launcherVersion, launcherLocal, latest, local,
            newer, stagedThisSession, latestVsSkipped, pendingVsLatest);

    [Fact]
    public void NothingPendingAndNoNetwork_ResultIsSilent()
    {
        var r = Decide();
        Assert.Null(r.PendingApplyVersion);
        Assert.Null(r.LauncherSecurityVersion);
        Assert.Null(r.DshAvailableVersion);
    }

    [Fact]
    public void PendingVersion_IsReportedEvenWhenNothingElseFires()
        => Assert.Equal("0.1.3", Decide(pending: "0.1.3").PendingApplyVersion);

    /// <summary>launcher 安全更新抢占：给出安全更新槽位，且 dsh 槽位必须为空（原实现提前 return）。</summary>
    [Fact]
    public void LauncherSecurity_PreemptsAndSuppressesDshNotice()
    {
        var r = Decide(pending: "0.1.2", launcherNotify: true, launcherVersion: L,
                       latest: D, newer: 1);
        Assert.Equal(L, r.LauncherSecurityVersion);
        Assert.Null(r.DshAvailableVersion);
        Assert.Equal("0.1.2", r.PendingApplyVersion); // pending 提示不受抢占影响（先记后判）
    }

    [Fact]
    public void LauncherShouldNotNotify_DoesNotPreempt()
        => Assert.Equal(D, Decide(launcherNotify: false, launcherVersion: L, latest: D).DshAvailableVersion);

    /// <summary>launcher 命中但本地版本未知：文案必须是 "?"，不得显示成空。</summary>
    [Fact]
    public void UnknownLauncherLocal_IsRenderedAsQuestionMark()
        => Assert.Equal("?", Decide(launcherNotify: true, launcherVersion: L, launcherLocal: null)
            .LauncherLocalVersion);

    [Fact]
    public void NoLatestVersion_SkipsWithReason()
    {
        var r = Decide(latest: null);
        Assert.Null(r.DshAvailableVersion);
        Assert.Equal("no-latest", r.SkipReason);
    }

    [Fact]
    public void UpToDate_SkipsWithReason()
        => Assert.Equal("up-to-date", Decide(latest: D, newer: 0).SkipReason);

    /// <summary>
    /// 更新死循环回归（根因 C）：已暂存的 pending >= 检测到的 latest 时不得再弹"有更新"。
    /// </summary>
    [Fact]
    public void AlreadyPendingCoveringLatest_Skips()
    {
        var r = Decide(pending: D, latest: D, newer: 1, pendingVsLatest: 0);
        Assert.Null(r.DshAvailableVersion);
        Assert.Equal("already-pending", r.SkipReason);
        Assert.Equal(D, r.PendingApplyVersion); // 但"待应用"提示仍要给
    }

    [Fact]
    public void AlreadyStagedThisSession_Skips()
        => Assert.Equal("already-staged-session",
            Decide(latest: D, stagedThisSession: true).SkipReason);

    /// <summary>用户拒绝过的版本不再提示；更高版本仍提示（latestVsSkipped &gt; 0）。</summary>
    [Fact]
    public void SkippedByUser_SkipsOnlyForThatVersion()
    {
        Assert.Equal("skipped-by-user", Decide(latest: D, latestVsSkipped: 0).SkipReason);
        Assert.Equal(D, Decide(latest: D, latestVsSkipped: 1).DshAvailableVersion);
    }

    /// <summary>本地没有任何 dsh（local 为 null）时必须提示——首装/被卸载场景。</summary>
    [Fact]
    public void UnknownLocalDsh_StillPrompts()
    {
        var r = Decide(latest: D, local: null, newer: 1);
        Assert.Equal(D, r.DshAvailableVersion);
        Assert.Null(r.DshLocalVersion);
    }

    /// <summary>
    /// 三条跳过规则的先后必须与历史实现一致：pending 覆盖 &gt; 本会话已下载 &gt; 用户跳过。
    /// 三条同时成立时只有第一条能胜出——顺序变了就会让用户看到不同的静默理由，
    /// 也就改变了"哪条留痕进日志"的可归因性。
    /// </summary>
    [Fact]
    public void SkipReasons_PreserveOriginalOrdering()
    {
        // 三条全成立 → pending 规则先命中
        Assert.Equal("already-pending", Decide(
            pending: D, latest: D, pendingVsLatest: 1,
            stagedThisSession: true, latestVsSkipped: 0).SkipReason);

        // 去掉 pending 规则（pending 不覆盖 latest）→ 会话已下载规则命中
        Assert.Equal("already-staged-session", Decide(
            pending: D, latest: D, pendingVsLatest: -1,
            stagedThisSession: true, latestVsSkipped: 0).SkipReason);

        // 再去掉会话规则 → 用户跳过规则命中
        Assert.Equal("skipped-by-user", Decide(
            pending: D, latest: D, pendingVsLatest: -1,
            stagedThisSession: false, latestVsSkipped: 0).SkipReason);
    }
}
