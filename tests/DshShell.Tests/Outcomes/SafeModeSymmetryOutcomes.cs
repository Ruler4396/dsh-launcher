using DshWeb;
using DshWeb.Domain;
using DshWeb.Managers;
using DshShell.Tests.Managers;   // FakeRuntime/FakeService/IdentityFixtures（共享 Headless 工装）
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — issue #28-4】安全模式在"启动"与"重启"两条路径上的对称性。
///
/// 不关心内部调用了哪个函数，只验证用户任务级物理不变量：
///   ① 同一份盘上粘滞状态 + 同一份发现身份 → 初始拉起与服务重启拿到**完全相同**的启动命令行
///      （用户报告的反面就是"退出重开插件都在，点 DSH 内置重启插件消失"）；
///   ② 正常模式两侧都不带 --profile；
///   ③ .dsh-safe 里出现非壳生成的内容时，物理目录必须存活（旧实现每次正常启动递归删掉它，
///      等于销毁用户在安全模式期间装的插件）。
/// 真实文件系统交互（测试铁律：不 Mock OS 边界）。
/// </summary>
[Collection("EnvHygiene")]
public class SafeModeSymmetryOutcomes : IDisposable
{
    private const int Port = 3080;
    private readonly string _dshHome;

    public SafeModeSymmetryOutcomes()
    {
        Environment.SetEnvironmentVariable("DSH_WEB_URL", null);   // 外部托管会把 LauncherApp 挤出自拉起分支
        Environment.SetEnvironmentVariable("DSH_VERSION", null);
        _dshHome = Path.Combine(Path.GetTempPath(), $"issue28-symmetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dshHome);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dshHome, recursive: true); } catch { /* 临时目录回收失败不影响结论 */ }
    }

    private SafeModeState StateOnDisk() => new(SafeModeState.DefaultStorePath(_dshHome));

    /// <summary>启动路径：真实 LauncherApp（组合根同款注入）→ FakeService 捕获身份 → 同款参数拼装。</summary>
    private static string BootCommandLine(SafeModeState state, string safeProfileDir)
    {
        var service = new FakeService
        {
            Ready = true,
            PortState = ShellLogic.ServicePortState.Closed,
        };
        var app = new LauncherApp(new FakeRuntime(), service)
        {
            ServiceIdentityDecorator = id => SafeModeLaunchPolicy.Decorate(id, state.IsActive, safeProfileDir),
        };
        Assert.True(app.RunStartupAsync().GetAwaiter().GetResult());
        return ShellLogic.ServiceLaunch.BuildArgs(service.LastStartArgs!.Value.Identity, Port);
    }

    /// <summary>重启路径：StartDshServiceViaIdentity 的同款两步（发现 → 装饰 → 拼装）。</summary>
    private static string RestartCommandLine(SafeModeState state, string safeProfileDir)
    {
        var discovered = IdentityFixtures.Launchable();     // 与 FakeRuntime 返回的是同一份身份
        var identity = SafeModeLaunchPolicy.Decorate(discovered, state.IsActive, safeProfileDir);
        return ShellLogic.ServiceLaunch.BuildArgs(identity, Port);
    }

    [Fact]
    public void Outcome_StickySafeMode_BootAndRestart_IdenticalCommandLine()
    {
        // Given：磁盘上真实存在"上次会话进入过安全模式"的粘滞状态
        var builder = new SafeProfileBuilder(_dshHome);
        Assert.True(builder.Build(SafeProfileTier.Tier1KeepDeepSeekCore));
        StateOnDisk().Activate(SafeProfileTier.Tier1KeepDeepSeekCore);
        var state = StateOnDisk();                    // 重新读盘：确认粘滞态是物理事实而非测试构造
        Assert.True(state.IsActive);

        // When/Then：两条路径的启动命令字节一致，且都带隔离 profile（对称，不再一侧带一侧不带）
        var boot = BootCommandLine(state, builder.SafeProfileDir);
        var restart = RestartCommandLine(state, builder.SafeProfileDir);
        Assert.Equal(restart, boot);
        Assert.Contains("--profile .dsh-safe", boot);
    }

    [Fact]
    public void Outcome_NormalMode_BootAndRestart_NeitherCarriesSafeProfile()
    {
        var builder = new SafeProfileBuilder(_dshHome);
        var state = StateOnDisk();                    // 无 safe-mode.json = 未激活
        Assert.False(state.IsActive);

        var boot = BootCommandLine(state, builder.SafeProfileDir);
        var restart = RestartCommandLine(state, builder.SafeProfileDir);
        Assert.Equal(restart, boot);
        Assert.DoesNotContain("--profile", boot);    // 正常模式：用户的全部插件照旧加载
    }

    [Fact]
    public void Outcome_SafeProfileWithForeignArtifacts_SurvivesNormalModeCleanup()
    {
        // Given：安全模式会话里装了插件 → pnpm 在 .dsh-safe 里实体化 node_modules
        var builder = new SafeProfileBuilder(_dshHome);
        Assert.True(builder.Build(SafeProfileTier.Tier1KeepDeepSeekCore));
        var installed = Path.Combine(builder.SafeProfileDir, "node_modules", "dsh-notification", "index.js");
        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        File.WriteAllText(installed, "// 用户在安全模式期间装的插件");

        // When：下次正常模式启动的清理判定（组合根同款三步）
        var state = StateOnDisk();
        var (onlyArtifacts, manifestUnchanged) = builder.InspectForCleanup(state.Tier);
        var willDelete = ShellLogic.SafeProfileCleanupPolicy.ShouldDelete(
            state.IsActive, onlyArtifacts, manifestUnchanged);

        // Then：绝不删——目录与插件文件物理存活
        Assert.False(onlyArtifacts);
        Assert.False(willDelete);
        Assert.True(Directory.Exists(builder.SafeProfileDir));
        Assert.True(File.Exists(installed));

        // 对照：把非壳产物移走后，同样的判定放行清理（护栏不是"永远不删"）
        Directory.Delete(Path.Combine(builder.SafeProfileDir, "node_modules"), recursive: true);
        var (cleanArtifacts, cleanManifest) = builder.InspectForCleanup(state.Tier);
        Assert.True(ShellLogic.SafeProfileCleanupPolicy.ShouldDelete(
            safeModeActive: false, cleanArtifacts, cleanManifest));
        builder.Cleanup();
        Assert.False(Directory.Exists(builder.SafeProfileDir));
    }

    [Fact]
    public void Outcome_SafeProfileWithEditedManifest_SurvivesNormalModeCleanup()
    {
        // 只有清单被改过（bundles 多了第三方、无 node_modules 实体）也必须存活
        var builder = new SafeProfileBuilder(_dshHome);
        Assert.True(builder.Build(SafeProfileTier.Tier1KeepDeepSeekCore));
        File.WriteAllText(builder.SafeProfilePackageJson,
            """{"name":"dsh-profile-safe","private":true,"dependencies":{"dsh-zh-guide":"file:E:/dsh-plugins/dsh-zh-guide"},"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app","dsh-zh-guide"]}}}""" + "\n");

        var (onlyArtifacts, manifestUnchanged) = builder.InspectForCleanup(SafeProfileTier.Tier1KeepDeepSeekCore);
        Assert.True(onlyArtifacts);
        Assert.False(manifestUnchanged);
        Assert.False(ShellLogic.SafeProfileCleanupPolicy.ShouldDelete(false, onlyArtifacts, manifestUnchanged));
        Assert.True(File.Exists(builder.SafeProfilePackageJson));
    }

    /// <summary>
    /// [真机 T14 缺口修复配套] 启动失败时用户答"是"之后，磁盘上必须同时出现两样东西：
    /// 隔离 profile 与粘滞标志。这条事务此前长在组合根里（一个 22 行的 static 方法），
    /// 顺序对不对、失败时留没留半吊子状态都测不到；搬到 Domain 后第一次可测。
    /// </summary>
    [Fact]
    public void Outcome_ArmNextLaunch_BuildSucceeds_ProfileAndStickyFlagBothOnDisk()
    {
        var builder = new SafeProfileBuilder(_dshHome);
        Assert.True(SafeModeLaunchPolicy.ArmNextLaunch(builder, StateOnDisk()));
        Assert.True(File.Exists(builder.SafeProfilePackageJson));
        var reloaded = StateOnDisk();   // 重新读盘：粘滞态是物理事实，不是测试构造
        Assert.True(reloaded.IsActive);
        Assert.Equal(SafeProfileTier.Tier1KeepDeepSeekCore, reloaded.Tier);
    }

    /// <summary>
    /// 建不出 profile 时**绝不**置标志——否则用户重开只会再撞一次失败（标志把自己锁死）。
    /// 故障注入用真实文件系统：profile 目录位被一个同名文件占住，CreateDirectory 直接抛。
    /// </summary>
    [Fact]
    public void Outcome_ArmNextLaunch_BuildFails_StickyFlagStaysInactive()
    {
        var builder = new SafeProfileBuilder(_dshHome);
        Directory.CreateDirectory(Path.GetDirectoryName(builder.SafeProfileDir)!);
        File.WriteAllText(builder.SafeProfileDir, "这个位置被占了");

        Assert.False(SafeModeLaunchPolicy.ArmNextLaunch(builder, StateOnDisk()));
        Assert.False(StateOnDisk().IsActive);
    }
}
