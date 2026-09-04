using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 契约测试：ShellLogic.PathPolicy.IsSafeVersionSegment（版本串进入删除/移动路径前的白名单）。
///
/// 审计加固背景（2026-09 删除代码全量审计）：更新链路的版本串（registry dist-tag /
/// GitHub tag / pending-update.json / 测试钩子）曾被直接拼进
/// staging\runtime-build-{v}、runtimes\{v} 等路径后再 TryDeleteDir / Directory.Move /
/// File.Delete——含 ".." 或分隔符的版本串可让这些动作脱域误伤用户数据。本白名单保证
/// 任何进入删除/移动动作的版本段都只能是单段安全字符（宁可中止更新，绝不越界）。
/// </summary>
public class PathPolicyContractTests
{
    [Theory]
    // ---- 安全段（真实版本线）----
    [InlineData("1.0.0", true)]
    [InlineData("0.1.2-rc.1", true)]
    [InlineData("0.1.2-rc.1.2+build.5", true)]
    [InlineData("v1.2.3", true)]
    [InlineData("2026.09.04", true)]
    [InlineData("0.0.0-dev", true)]
    // ---- 不安全段：路径穿越/注入/畸形 —— 必须拒绝 ----
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("..", false)]
    [InlineData("1.0.0\\..\\..\\evil", false)]
    [InlineData("1.0.0/../../evil", false)]
    [InlineData("..\\evil", false)]
    [InlineData("C:\\evil", false)]
    [InlineData("1.0.0:80", false)]
    [InlineData("1.0.0?x", false)]
    [InlineData("1.0.0*", false)]
    [InlineData("1.0.0|pipe", false)]
    [InlineData("1.0.0<tag", false)]
    [InlineData("1.0.0>tag", false)]
    [InlineData("1.0.0\"quote", false)]
    [InlineData("1.0.0 space", false)]
    [InlineData("1..0", false)]
    [InlineData(".1.0.0", false)]
    [InlineData("1.0.0.", false)]
    [InlineData("版本.1.0.0", false)]          // 非 ASCII
    [InlineData("1.0\n0", false)]            // 内嵌换行（Trim 只去首尾，中间控制字符必须拒绝）
    public void IsSafeVersionSegment_WhitelistsOnlySingleSafeSegments(string? version, bool expected)
        => Assert.Equal(expected, ShellLogic.PathPolicy.IsSafeVersionSegment(version));

    [Fact]
    public void IsSafeVersionSegment_OverlongVersion_Rejected()
    {
        Assert.False(ShellLogic.PathPolicy.IsSafeVersionSegment(new string('v', 65)));
        Assert.True(ShellLogic.PathPolicy.IsSafeVersionSegment(new string('v', 12)));
    }
}