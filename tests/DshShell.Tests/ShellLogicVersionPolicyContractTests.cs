using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// F1 契约锁定：全系统唯一版本比较器（ShellLogic.VersionPolicy）。
/// 背景：DshDiscovery（发现层挑 SelfContained 运行时）与 UpdateChecker（更新检测）曾各持
/// 一套比较器——发现层用序数比较致 rc.10 &lt; rc.9 判反，runtimes\ 多版本共存（apply 不删
/// 旧目录，共存是常态）时永远选中旧版："更新进度 100%、重启后版本没变"。
/// 本 Theory 锁定 prerelease 数值段 / build metadata / 非法输入 fail-open 三类契约；
/// 两个消费方（UpdateChecker/DshDiscovery）的委托正确性由末尾两个直连用例锁定。
/// </summary>
public class ShellLogicVersionPolicyContractTests
{
    [Theory]
    [InlineData("0.3.1", "0.3.0", 1)]
    [InlineData("0.3.0", "0.3.1", -1)]
    [InlineData("0.3.1", "0.3.1", 0)]
    [InlineData("0.3.10", "0.3.9", 1)]       // 语义化：10 > 9，非字符串序
    [InlineData("1.0.0", "0.9.9", 1)]
    [InlineData("0.3.1", null, 1)]           // 远端缺失 → 0.0.0
    [InlineData(null, "0.3.1", -1)]
    [InlineData(null, null, 0)]
    [InlineData("abc", "0.3.1", -1)]         // 非法 → 0.0.0（fail-open，不产生"有新版"误报）
    [InlineData("0.3.1", "abc", 1)]
    // ---- prerelease（F1 的目标场景：发现层曾用序数比较判反）----
    [InlineData("0.1.0-rc.10", "0.1.0-rc.9", 1)]
    [InlineData("0.1.0-rc.9", "0.1.0-rc.10", -1)]
    [InlineData("0.1.0-rc.7", "0.1.0-rc.6", 1)]
    [InlineData("0.1.0-rc.10", "0.1.0-rc.10", 0)]
    [InlineData("0.1.0", "0.1.0-rc.7", 1)]         // 正式版 > prerelease（SemVer 规则）
    [InlineData("0.1.0-rc.1", "0.1.0-alpha.2", 1)] // 字母数字段字典序：rc > alpha
    [InlineData("0.1.0-rc.1", "0.1.0-rc.1.1", -1)] // 段多者更大
    // ---- build metadata 不参与比较（F10）----
    [InlineData("1.2.3+build", "1.2.3", 0)]
    [InlineData("1.2.3+build.1", "1.2.3+build.99", 0)]
    [InlineData("1.2.3-rc.1+build.5", "1.2.3-rc.1+build.9", 0)]
    // ---- 前缀 / 多段容错 ----
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3.4", "1.2.3", 0)]      // 四段：第 4 段宽松忽略
    [InlineData("01.02.03", "1.2.3", 0)]     // 前导零按数值
    public void CompareVersions_ReturnsExpected(string? a, string? b, int expected)
        => Assert.Equal(expected, Math.Sign(ShellLogic.VersionPolicy.CompareVersions(a, b)));

    [Fact]
    public void UpdateChecker_DelegatesToCanonicalComparer_Rc10BeatsRc9()
        => Assert.Equal(1, Math.Sign(UpdateChecker.CompareVersions("0.1.0-rc.10", "0.1.0-rc.9")));

    [Fact]
    public void DshDiscovery_DelegatesToCanonicalComparer_Rc10BeatsRc9()
        => Assert.Equal(1, Math.Sign(
            DshWeb.Domain.DshDiscovery.CompareVersions("0.1.0-rc.10", "0.1.0-rc.9")));
}
