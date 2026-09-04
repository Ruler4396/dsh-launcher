using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 契约测试：ShellLogic.CacheInvalidationPolicy.ShouldInvalidate（dsh 版本变更 → 仅磁盘缓存失效决策）。
///
/// 安全铁律（实现必须满足，任何违反=违反"绝不误删用户内容"红线）：
///   K1 无基线（首次运行/壳升级后首跑，webcache-version.json 缺失）→ **绝不清**；
///   K2 当前版本不可判（null/空白，探测失败或未安装）→ **绝不清**；
///   K3 语义版本相同（含空白的归一、v 前缀、build metadata 差异）→ **绝不清**；
///   其余（真实版本差异，含升级与**降级**、预发布差异）→ 清。
/// 比较委托全局唯一比较器 VersionPolicy.CompareVersions（全系统禁止第二套比较器，F1 回归根因）。
/// </summary>
public class CacheInvalidationContractTests
{
    [Theory]
    // ---- K1：无基线 —— 首次激活，绝不清（防误删用户既有缓存） ----
    [InlineData(null, "1.0.0", false)]
    [InlineData("", "1.0.0", false)]
    [InlineData("   ", "1.0.0", false)]
    [InlineData(null, null, false)]
    // ---- K2：当前版本不可判 —— 绝不清 ----
    [InlineData("1.0.0", null, false)]
    [InlineData("1.0.0", "", false)]
    // ---- K3：版本相同 —— 绝不清 ----
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData(" 1.0.0 ", "1.0.0", false)]     // 空白归一（Parse 内 Trim）
    [InlineData("v1.0.0", "1.0.0", false)]      // v 前缀归一（Parse 剥离）
    [InlineData("1.0.0+sha.abc", "1.0.0", false)] // build metadata 不参与比较（F10）
    [InlineData("0.1.1-rc.2", "0.1.1-rc.2", false)]
    // ---- 真实差异 → 清 ----
    [InlineData("1.0.0", "1.0.1", true)]        // 升级
    [InlineData("1.0.1", "1.0.0", true)]        // 降级（内容同样可能变化，不能因方向放行）
    [InlineData("0.1.1-rc.2", "0.1.1", true)]   // 预发布 → 正式（语义不同）
    [InlineData("0.1.0-rc.10", "0.1.0-rc.9", true)] // 预发布数值比较（F1 回归场景：rc.10 > rc.9）
    public void ShouldInvalidate_DecidesBySemanticVersionDifference(string? lastSeen, string? current, bool expected)
        => Assert.Equal(expected, ShellLogic.CacheInvalidationPolicy.ShouldInvalidate(lastSeen, current));
}