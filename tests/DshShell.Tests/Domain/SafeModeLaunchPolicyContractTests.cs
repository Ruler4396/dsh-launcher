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
}
