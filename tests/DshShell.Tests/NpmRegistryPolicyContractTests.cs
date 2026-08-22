using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// NpmRegistryPolicy 契约测试（2026-08 用户回归：npmjs 直连不稳导致更新构建失败）。
/// 策略与 RuntimeResolver 的 node 二进制镜像链同源：主源失败 → npmmirror 兜底重试一次。
/// 锁定：attempt=0 用主源（DSH_NPM_MIRROR 或默认）；attempt≥1 切 npmmirror；
/// 主源本来就是 npmmirror 时兜底轮返回空参（避免重复 --registry）。
/// </summary>
public class NpmRegistryPolicyContractTests
{
    private const string Mirror = ShellLogic.NpmRegistryPolicy.FallbackMirror;

    [Fact]
    public void Attempt0_NoEnv_ReturnsEmpty_UseDefaultRegistry()
    {
        var arg = ShellLogic.NpmRegistryPolicy.RegistryArgForAttempt(0, null);
        Assert.Equal("", arg);
    }

    [Fact]
    public void Attempt0_WithEnvMirror_UsesPrimaryMirror()
    {
        var arg = ShellLogic.NpmRegistryPolicy.RegistryArgForAttempt(0, "https://reg.example.com");
        Assert.Equal(" --registry=https://reg.example.com", arg);
    }

    [Fact]
    public void Attempt1_FallsBackToNpmmirror()
    {
        var arg = ShellLogic.NpmRegistryPolicy.RegistryArgForAttempt(1, null);
        Assert.Equal(" --registry=" + Mirror, arg);
    }

    [Fact]
    public void Attempt1_WithPrimaryCustomMirror_StillFallsBackToNpmmirror()
    {
        var arg = ShellLogic.NpmRegistryPolicy.RegistryArgForAttempt(1, "https://reg.example.com");
        Assert.Equal(" --registry=" + Mirror, arg);
    }

    [Fact]
    public void Attempt1_WhenPrimaryIsAlreadyNpmmirror_ReturnsEmptyArg()
    {
        // 主源就是兜底镜像本身（含尾斜杠/大小写差异）→ 兜底轮不得重复追加 --registry
        Assert.Equal("", ShellLogic.NpmRegistryPolicy.RegistryArgForAttempt(1, Mirror));
        Assert.Equal("", ShellLogic.NpmRegistryPolicy.RegistryArgForAttempt(1, Mirror + "/"));
        Assert.Equal("", ShellLogic.NpmRegistryPolicy.RegistryArgForAttempt(1, Mirror.ToUpperInvariant()));
    }

    [Fact]
    public void AttemptsBeyondOne_Idempotent_SameAsAttempt1()
    {
        Assert.Equal(
            ShellLogic.NpmRegistryPolicy.RegistryArgForAttempt(1, null),
            ShellLogic.NpmRegistryPolicy.RegistryArgForAttempt(2, null));
        Assert.Equal(
            ShellLogic.NpmRegistryPolicy.RegistryArgForAttempt(1, Mirror),
            ShellLogic.NpmRegistryPolicy.RegistryArgForAttempt(9, Mirror));
    }
}
