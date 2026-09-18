using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// issue #28-4「DSH 内置重启后插件消失」三处决策纯函数契约。
///
/// 事故链（本组测试逐环锁定）：
/// ① 壳主动重启服务后不刷新 PID 账本 → 健康监控 attach 到已死旧 pid → 下一次内置重启被误判
///    为启动自检失败（E2004/E2007）→ 升级询问安全模式 → 粘滞的 .dsh-safe 把第三方插件全剥掉；
/// ② 停服时端口被 dsh 自己重新拉起的新进程占用 → 无条件 taskkill /T /F 全树 → 正在进行的
///    插件安装（npm/pnpm 子进程）被拦腰截断；
/// ③ 正常模式启动无条件递归删除 .dsh-safe → 在安全模式会话里装的插件被物理销毁。
/// </summary>
public class ShellLogicRestartPolicyContractTests
{
    // ==================== ② ServiceRestartPolicy.DecideOccupantReclaim ====================

    private const double Grace = ShellLogic.ServiceRestartPolicy.OccupantAdoptionGraceSeconds;

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void OccupantReclaim_NoOccupant_Kills(int occupantPid)
        // 端口已空：调用方走常规清理分支，这里不给"接管"以免把 0 当活进程
        => Assert.Equal(ShellLogic.ServiceRestartPolicy.OccupantDecision.Kill,
            ShellLogic.ServiceRestartPolicy.DecideOccupantReclaim(occupantPid, rememberedPid: 4711,
                occupantAgeSeconds: 999, replacementHealthy: true, graceSeconds: Grace));

    [Fact]
    public void OccupantReclaim_SameAsRememberedPid_KillsEvenIfHealthy()
        // 端口占用者就是账本里那个进程（优雅终止没生效）→ 必须强杀，绝不"接管"自己刚杀掉的进程
        => Assert.Equal(ShellLogic.ServiceRestartPolicy.OccupantDecision.Kill,
            ShellLogic.ServiceRestartPolicy.DecideOccupantReclaim(4711, rememberedPid: 4711,
                occupantAgeSeconds: 0.1, replacementHealthy: true, graceSeconds: Grace));

    [Fact]
    public void OccupantReclaim_HealthierNewerReplacement_IsAdopted()
    {
        // 核心修复点：dsh 内置重启自己拉起了新进程且已能应答就绪探测 → 接管它，
        // 绝不再 taskkill /T /F 打断它的插件安装子进程树。
        Assert.Equal(ShellLogic.ServiceRestartPolicy.OccupantDecision.AdoptReplacement,
            ShellLogic.ServiceRestartPolicy.DecideOccupantReclaim(9999, rememberedPid: 4711,
                occupantAgeSeconds: 0.4, replacementHealthy: true, graceSeconds: Grace));
    }

    [Fact]
    public void OccupantReclaim_YoungAndNotAnsweringYet_WaitsGrace()
    {
        // 刚拉起、HTTP 还没起来：先等，不立刻杀（杀了就又是截断安装）
        Assert.Equal(ShellLogic.ServiceRestartPolicy.OccupantDecision.WaitGrace,
            ShellLogic.ServiceRestartPolicy.DecideOccupantReclaim(9999, rememberedPid: 4711,
                occupantAgeSeconds: Grace - 0.5, replacementHealthy: false, graceSeconds: Grace));
    }

    [Theory]
    [InlineData(Grace)]          // 恰好到宽限上限
    [InlineData(Grace + 30)]     // 远超
    [InlineData(86400d)]          // 上一台机器留下的旧服务
    public void OccupantReclaim_OldAndNotAnswering_Kills(double age)
        => Assert.Equal(ShellLogic.ServiceRestartPolicy.OccupantDecision.Kill,
            ShellLogic.ServiceRestartPolicy.DecideOccupantReclaim(9999, rememberedPid: 4711,
                occupantAgeSeconds: age, replacementHealthy: false, graceSeconds: Grace));

    [Theory]
    [InlineData(-1d)]    // 年龄读不到（进程已消失/权限）
    [InlineData(-42d)]
    public void OccupantReclaim_UnknownAge_WaitsGraceBeforeKilling(double age)
        // 年龄未知时不立刻杀：宽限到期后仍不应答才走 Kill（保守优先，防误杀正在装插件的进程）
        => Assert.Equal(ShellLogic.ServiceRestartPolicy.OccupantDecision.WaitGrace,
            ShellLogic.ServiceRestartPolicy.DecideOccupantReclaim(9999, rememberedPid: 4711,
                occupantAgeSeconds: age, replacementHealthy: false, graceSeconds: Grace));

    [Fact]
    public void OccupantReclaim_NonPositiveRememberedPid_StillDistinguishes()
    {
        // 账本缺失（_servicePid=0 的接管/崩溃场景）时，健康占用者同样应被接管而非被杀
        Assert.Equal(ShellLogic.ServiceRestartPolicy.OccupantDecision.AdoptReplacement,
            ShellLogic.ServiceRestartPolicy.DecideOccupantReclaim(9999, rememberedPid: 0,
                occupantAgeSeconds: 5, replacementHealthy: true, graceSeconds: Grace));
    }

    // ==================== ③ SafeProfileCleanupPolicy.ShouldDelete ====================

    [Fact]
    public void Cleanup_NormalModeAndPureLauncherArtifacts_Deletes()
        => Assert.True(ShellLogic.SafeProfileCleanupPolicy.ShouldDelete(
            safeModeActive: false, onlyLauncherArtifacts: true, manifestUnchanged: true));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cleanup_WhileSafeModeActive_NeverDeletes(bool onlyArtifacts)
        // 服务此刻正用着这个 profile：删它 = 现场拆掉运行中的服务
        => Assert.False(ShellLogic.SafeProfileCleanupPolicy.ShouldDelete(
            safeModeActive: true, onlyLauncherArtifacts: onlyArtifacts, manifestUnchanged: true));

    [Fact]
    public void Cleanup_ForeignArtifactsPresent_NeverDeletes()
        // 目录里出现壳没写过的东西（node_modules/其他文件）= 安全模式期间装过插件
        => Assert.False(ShellLogic.SafeProfileCleanupPolicy.ShouldDelete(
            safeModeActive: false, onlyLauncherArtifacts: false, manifestUnchanged: true));

    [Fact]
    public void Cleanup_ManifestDiverged_NeverDeletes()
        => Assert.False(ShellLogic.SafeProfileCleanupPolicy.ShouldDelete(
            safeModeActive: false, onlyLauncherArtifacts: true, manifestUnchanged: false));

    [Fact]
    public void Cleanup_BothSignalsMissing_NeverDeletes()
        => Assert.False(ShellLogic.SafeProfileCleanupPolicy.ShouldDelete(
            safeModeActive: false, onlyLauncherArtifacts: false, manifestUnchanged: false));

    // ==================== ③b SafeProfileManifestEquivalent（"清单是否仍是壳写的样子"） ====================

    private static readonly IReadOnlyList<string> Core = new[]
    {
        "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app",
    };

    [Fact]
    public void Manifest_Absent_CountsAsUnchanged()
        // 目录里根本没有清单：没有东西可被销毁
        => Assert.True(ShellLogic.SafeProfileCleanupPolicy.SafeProfileManifestEquivalent(null, Core));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Manifest_Empty_CountsAsUnchanged(string json)
        => Assert.True(ShellLogic.SafeProfileCleanupPolicy.SafeProfileManifestEquivalent(json, Core));

    [Fact]
    public void Manifest_ExactlyWhatLauncherWrites_Unchanged()
    {
        var written = """{"name":"dsh-profile-safe","private":true,"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app"]}}}""" + "\n";
        Assert.True(ShellLogic.SafeProfileCleanupPolicy.SafeProfileManifestEquivalent(written, Core));
    }

    [Fact]
    public void Manifest_PluginInstalledInSafeMode_Changed()
    {
        // dsh plugin add 在安全模式会话里装插件后的真实形态：bundles 多一项 + dependencies 段
        const string installed =
            """{"name":"dsh-profile-safe","private":true,"dependencies":{"dsh-notification":"file:E:/dsh-plugins/dsh-notification"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app","dsh-notification"]}}}""";
        Assert.False(ShellLogic.SafeProfileCleanupPolicy.SafeProfileManifestEquivalent(installed, Core));
    }

    [Fact]
    public void Manifest_BundleOrderDrift_Changed()
    {
        const string swapped =
            """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-web-app","@deepseek-ai/dsh-base"]}}}""";
        Assert.False(ShellLogic.SafeProfileCleanupPolicy.SafeProfileManifestEquivalent(swapped, Core));
    }

    [Theory]
    [InlineData("{ this is not valid json ")]
    [InlineData("[]")]
    [InlineData("""{"name":"dsh-profile-safe"}""")] // 壳写的清单必有 dsh.profile.bundles
    public void Manifest_CorruptedOrForeignShape_Changed(string json)
        => Assert.False(ShellLogic.SafeProfileCleanupPolicy.SafeProfileManifestEquivalent(json, Core));

    // ==================== ① BootRecoveryPolicy.SuppressLauncherInduced ====================

    private const int Quiet = ShellLogic.BootRecoveryPolicy.LauncherInducedQuietSeconds;

    [Theory]
    [InlineData(0d)]
    [InlineData(5d)]
    [InlineData((double)Quiet)]
    public void Suppress_HttpOnlyEvidence_InsideQuietWindow(double seconds)
        => Assert.True(ShellLogic.BootRecoveryPolicy.SuppressLauncherInduced(
            httpLayerOnly: true, secondsSinceShellInitiatedRestart: seconds, quietWindowSeconds: Quiet));

    [Theory]
    [InlineData(Quiet + 0.1)]
    [InlineData(60d)]
    public void Suppress_HttpOnlyEvidence_OutsideQuietWindow_NotSuppressed(double seconds)
        => Assert.False(ShellLogic.BootRecoveryPolicy.SuppressLauncherInduced(
            httpLayerOnly: true, secondsSinceShellInitiatedRestart: seconds, quietWindowSeconds: Quiet));

    [Theory]
    [InlineData(-1d)]   // 本会话从未发生过壳主动重启
    [InlineData(-999d)]
    public void Suppress_NoShellInitiatedRestart_NeverSuppresses(double seconds)
        => Assert.False(ShellLogic.BootRecoveryPolicy.SuppressLauncherInduced(
            httpLayerOnly: true, secondsSinceShellInitiatedRestart: seconds, quietWindowSeconds: Quiet));

    [Theory]
    [InlineData(0.5)]
    [InlineData(15d)]
    public void Suppress_ProcessOrPageEvidence_AlwaysEscalates(double seconds)
        // 进程层退出码 / 页面层崩溃签名是"真崩溃"证据，壳主动重启的空窗绝不豁免它们
        // （否则就是把 issue #26/#28-2 的 fail-fast 又关掉）。
        => Assert.False(ShellLogic.BootRecoveryPolicy.SuppressLauncherInduced(
            httpLayerOnly: false, secondsSinceShellInitiatedRestart: seconds, quietWindowSeconds: Quiet));
}
