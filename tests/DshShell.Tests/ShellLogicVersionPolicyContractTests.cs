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
    // ---- git describe 距离尾段（issue #28-1：源码构建被误报"需要安全更新"）----
    [InlineData("0.4.5-6-g15f60daf", "0.4.5", 1)]        // tag 之后第 6 个提交 = 新于该 tag
    [InlineData("0.4.5", "0.4.5-6-g15f60daf", -1)]
    [InlineData("0.4.5-6-g15f60daf", "0.4.5-2-gabcdef12", 1)]  // 同为 dev 构建比提交距离
    [InlineData("0.4.5-6-g15f60daf", "0.4.5-6-g15f60daf", 0)]
    [InlineData("0.4.5-6-g15f60daf-dirty", "0.4.5", 1)]  // 工作区脏标记不改排序
    [InlineData("0.4.5-6-g15f60daf", "0.4.5-rc.1", 1)]   // rc 在发布前，dev 在发布后
    [InlineData("0.4.5-6-g15f60daf", "0.4.6", -1)]       // 核心优先：下一个正式版仍更新
    [InlineData("0.4.6-1-gabcdef12", "0.4.5-9-g12345678", 1)]
    [InlineData("1.0.0-1", "1.0.0", -1)]     // 纯数字 prerelease ≠ dev 距离尾段（无 g<sha>）
    [InlineData("1.0.0-1", "1.0.0-2", -1)]
    [InlineData("1.0.0-g15f60daf", "1.0.0", -1)]  // 缺距离数字 → 按普通 prerelease 处理
    public void CompareVersions_ReturnsExpected(string? a, string? b, int expected)
        => Assert.Equal(expected, Math.Sign(ShellLogic.VersionPolicy.CompareVersions(a, b)));

    // ==================== issue #28-1：本地版本未知时不得判"有安全更新" ====================

    [Theory]
    [InlineData("0.4.3", "0.4.5", true, true)]     // 正常：旧版 + 安全标记 → 提醒
    [InlineData("0.4.5", "0.4.5", true, false)]    // 已最新 → 不提醒
    [InlineData("0.4.6", "0.4.5", true, false)]    // 本地更新 → 不提醒
    [InlineData("0.4.5-6-g15f60daf", "0.4.5", true, false)] // 源码构建新于 release → 不提醒
    [InlineData(null, "0.4.5", true, false)]       // 本地未知 → 静默（修复点：旧实现按 0.0.0 误报）
    [InlineData("", "0.4.5", true, false)]
    [InlineData("   ", "0.4.5", true, false)]
    [InlineData("abc", "0.4.5", true, false)]      // 非法字符串 = 未知，不是 0.0.0
    [InlineData("0.4.3", null, true, false)]       // 远端缺失 → 不提醒（既有语义）
    [InlineData("0.4.3", "0.4.5", false, false)]   // 非安全更新不推送（设计语义）
    public void LauncherSecurityNotice_Matrix(string? current, string? latest, bool isSecurity, bool expected)
        => Assert.Equal(expected, ShellLogic.LauncherUpdateNoticePolicy
            .ShouldNotifyLauncherSecurity(current, latest, isSecurity));

    [Fact]
    public void UpdateChecker_DelegatesToCanonicalComparer_Rc10BeatsRc9()
        => Assert.Equal(1, Math.Sign(UpdateChecker.CompareVersions("0.1.0-rc.10", "0.1.0-rc.9")));

    [Fact]
    public void DshDiscovery_DelegatesToCanonicalComparer_Rc10BeatsRc9()
        => Assert.Equal(1, Math.Sign(
            DshWeb.Domain.DshDiscovery.CompareVersions("0.1.0-rc.10", "0.1.0-rc.9")));

    // ==================== git describe 输出解析（源码构建版本号的唯一来源） ====================

    [Theory]
    [InlineData("v0.4.5\n", "0.4.5")]                          // 正好在 tag 上
    [InlineData("v0.4.5-6-g15f60daf\n", "0.4.5-6-g15f60daf")] // tag 后第 6 个提交（保留距离尾段）
    [InlineData("v0.4.5-6-g15f60daf-dirty", "0.4.5-6-g15f60daf-dirty")]
    [InlineData("0.4.5", "0.4.5")]
    [InlineData("  v0.4.5-6-g15f60daf  ", "0.4.5-6-g15f60daf")]
    public void ParseDescribeOutput_StripsPrefixAndWhitespace(string raw, string expected)
        => Assert.Equal(expected, UpdateChecker.ParseDescribeOutput(raw));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\n")]
    [InlineData("   ")]
    public void ParseDescribeOutput_NoTagsStaysUnknown(string? raw)
        // 浅克隆/blobless/无任何 tag：git 只写 stderr，stdout 为空 → 必须保持"未知"（null），
        // 绝不能凭空造出一个可比较的版本号（issue #28-1 的误报源头）。
        => Assert.Null(UpdateChecker.ParseDescribeOutput(raw));

    [Fact]
    public void ParseDescribeOutput_ResultIsComparableAgainstRelease()
    {
        var dev = UpdateChecker.ParseDescribeOutput("v0.4.5-6-g15f60daf\n");
        Assert.Equal(1, Math.Sign(ShellLogic.VersionPolicy.CompareVersions(dev, "0.4.5")));
    }
}
