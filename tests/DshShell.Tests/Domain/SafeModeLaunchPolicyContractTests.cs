using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests.Domain;

/// <summary>
/// 安全模式身份装饰契约（issue #28-4「DSH 内置重启后插件消失」根因锁定）。
///
/// 【事故】"是否带 --profile .dsh-safe 拉起"的判定此前**只存在于重启路径**
/// （Program.StartDshServiceViaIdentity），初始启动走 LauncherApp 完全不套。同一份粘滞
/// safe-mode.json 下产生用户实测到的诡异不对称：托盘退出重开插件都在，点 DSH 内置重启
/// 插件凭空消失（.dsh-safe 按设计剥离所有非 @deepseek-ai bundle）。
/// 本契约锁定"唯一判定函数"的语义，启动与重启共用它 → 两侧对称。
/// </summary>
public class SafeModeLaunchPolicyContractTests
{
    private const string SafeDir = @"C:\Users\x\.dsh\profiles\.dsh-safe";

    private static DshRuntimeIdentity Global() => new(
        DshSource.GlobalNpm, @"C:\Program Files\nodejs\node.exe",
        @"C:\Users\x\AppData\Roaming\npm\node_modules\@deepseek-ai\dsh\bin\dsh.js", "0.1.5-rc.2");

    [Fact]
    public void Decorate_Active_AppliesSafeProfile()
    {
        var result = SafeModeLaunchPolicy.Decorate(Global(), safeModeActive: true, SafeDir);
        Assert.True(result.IsSafeProfile);
        Assert.Equal(SafeDir, result.ProfilePath);
    }

    [Fact]
    public void Decorate_Inactive_IdentityUntouched()
    {
        var identity = Global();
        var result = SafeModeLaunchPolicy.Decorate(identity, safeModeActive: false, SafeDir);
        Assert.False(result.IsSafeProfile);
        Assert.Same(identity, result); // 未激活时零分配、零改写（正常路径不受影响）
    }

    [Fact]
    public void Decorate_AlreadyDecorated_IsIdempotentAndKeepsExistingProfile()
    {
        // 重复重启不得把已生效的隔离 profile 改写成另一个目录（梯级切换由 SafeMode 状态负责）
        var decorated = Global().WithProfile(SafeDir);
        var result = SafeModeLaunchPolicy.Decorate(decorated, safeModeActive: true, @"D:\other\profile");
        Assert.Same(decorated, result);
        Assert.Equal(SafeDir, result.ProfilePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Decorate_ActiveButProfileUnknown_IdentityUntouched(string? safeProfileDir)
    {
        // 目录未知（构建失败/路径解析异常）时绝不拼出 "--profile " 空参——那是响亮失败，
        // 不是静默降级（异常透明铁律）。
        var identity = Global();
        Assert.Same(identity, SafeModeLaunchPolicy.Decorate(identity, safeModeActive: true, safeProfileDir));
    }

    [Fact]
    public void Decorate_PreservesAllOtherIdentityFacts()
    {
        // 因果地图铁律：装饰只允许改 ProfilePath，Source/NodeExe/入口/版本必须原样传递，
        // 否则重启会落到与启动不同的 dsh 安装上。
        var identity = Global();
        var result = SafeModeLaunchPolicy.Decorate(identity, safeModeActive: true, SafeDir);
        Assert.Equal(identity.Source, result.Source);
        Assert.Equal(identity.NodeExePath, result.NodeExePath);
        Assert.Equal(identity.DshEntryJsPath, result.DshEntryJsPath);
        Assert.Equal(identity.Version, result.Version);
        Assert.Equal(identity.CanLaunchDirectly, result.CanLaunchDirectly);
    }

    // ==================== 粘滞标志与实际目录不一致（真机实测缺陷） ====================

    /// <summary>
    /// 实测：粘滞 safe-mode.json 为 active 但 .dsh-safe 不存在时，直接带 --profile 拉起会被
    /// dsh 硬失败（profile does not exist → exit 1 → 壳 E2002），用户连界面都进不去，
    /// 也就永远点不到"退出安全模式"。故标志在、目录缺 → 必须先重建。
    /// </summary>
    [Theory]
    [InlineData(true, false, true)]    // 粘滞在生效但目录没了 → 重建
    [InlineData(true, true, false)]    // 目录在 → 不用重建
    [InlineData(false, false, false)]  // 本来就不是安全模式 → 无关
    [InlineData(false, true, false)]
    public void NeedsRebuild_OnlyWhenStickyFlagHasNoProfile(bool active, bool exists, bool expected)
        => Assert.Equal(expected, SafeModeLaunchPolicy.NeedsRebuild(active, exists));

    /// <summary>重建失败的兜底必须是"退回正常模式"，而不是"照样带着不存在的 profile 拉起"：
    /// 界面可用但插件被禁用 ≫ 界面根本起不来且无法退出。</summary>
    [Theory]
    [InlineData(false, true)]   // 重建失败 → 退回正常
    [InlineData(true, false)]   // 重建成功 → 继续走安全模式
    public void ShouldFallBackToNormal_OnlyWhenRebuildFailed(bool rebuildSucceeded, bool expected)
        => Assert.Equal(expected, SafeModeLaunchPolicy.ShouldFallBackToNormal(rebuildSucceeded));

    /// <summary>退回正常模式后 Decorate 不得再套 profile（否则兜底形同虚设）。</summary>
    [Fact]
    public void Decorate_AfterFallback_DoesNotAttachProfile()
    {
        var identity = Global();
        var afterFallback = SafeModeLaunchPolicy.Decorate(identity, safeModeActive: false, SafeDir);
        Assert.False(afterFallback.IsSafeProfile);
    }
}
