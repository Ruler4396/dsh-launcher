using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// VersionInfoPolicy 契约测试（2026-09 新增功能：TitleBar dsh 版本徽标 + 版本信息弹窗展示策略）。
/// 锁定三条契约：
/// 1. 徽标/版本展示文本统一在此合成（UI 层零拼字符串，防标题栏与弹窗展示规则漂移）；
/// 2. 比较结论委托全系统唯一比较器 ShellLogic.VersionPolicy（严禁另起炉灶）；
/// 3. 失败占位不误导用户："获取失败/无法获取最新版本" 绝不冒充 "已是最新"。
/// </summary>
public class VersionInfoPolicyContractTests
{
    // ---------- 徽标文本 ----------

    [Theory]
    [InlineData("0.1.0-rc.7", "v0.1.0-rc.7")]
    [InlineData("v0.1.0-rc.7", "v0.1.0-rc.7")]   // 已带 v 前缀不重复
    [InlineData("V1.2.3", "v1.2.3")]             // 大写 V 归一为小写 v
    [InlineData("1.2.3", "v1.2.3")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void ComposeTitleBarBadge_Normalizes(string? input, string expected)
        => Assert.Equal(expected, ShellLogic.VersionInfoPolicy.ComposeTitleBarBadge(input));

    // ---------- 当前/最新版本展示文本 ----------

    [Theory]
    [InlineData("0.1.0-rc.7", "v0.1.0-rc.7")]
    [InlineData("", "未知")]
    [InlineData(null, "未知")]
    public void FormatCurrent_RendersVOrUnknown(string? current, string expected)
        => Assert.Equal(expected, ShellLogic.VersionInfoPolicy.FormatCurrent(current));

    [Theory]
    [InlineData("0.1.0-rc.8", "v0.1.0-rc.8")]
    [InlineData("", "获取失败")]
    [InlineData(null, "获取失败")]
    public void FormatLatest_RendersVOrFetchFailed(string? latest, string expected)
        => Assert.Equal(expected, ShellLogic.VersionInfoPolicy.FormatLatest(latest));

    [Fact]
    public void FormatLatest_CustomFallback_Honored()
        => Assert.Equal("检查中…", ShellLogic.VersionInfoPolicy.FormatLatest(null, "检查中…"));

    // ---------- 比较结论（委托 VersionPolicy） ----------

    [Theory]
    [InlineData("0.1.0-rc.7", "0.1.0-rc.8", ShellLogic.VersionInfoPolicy.Relation.NewerAvailable)]
    [InlineData("0.1.0-rc.8", "0.1.0-rc.8", ShellLogic.VersionInfoPolicy.Relation.UpToDate)]
    [InlineData("0.1.0-rc.9", "0.1.0-rc.8", ShellLogic.VersionInfoPolicy.Relation.UpToDate)]
    [InlineData("0.1.0-rc.10", "0.1.0-rc.9", ShellLogic.VersionInfoPolicy.Relation.UpToDate)] // SemVer 数值段
    [InlineData(null, "0.1.0-rc.8", ShellLogic.VersionInfoPolicy.Relation.NewerAvailable)]   // 本地未知→提示可更新
    [InlineData("", "0.1.0-rc.8", ShellLogic.VersionInfoPolicy.Relation.NewerAvailable)]
    [InlineData("0.1.0-rc.7", null, ShellLogic.VersionInfoPolicy.Relation.Unknown)]          // 远端不可知
    [InlineData(null, null, ShellLogic.VersionInfoPolicy.Relation.Unknown)]
    public void CompareCurrentToLatest_DelegatesVersionPolicy(
        string? current, string? latest, ShellLogic.VersionInfoPolicy.Relation expected)
        => Assert.Equal(expected, ShellLogic.VersionInfoPolicy.CompareCurrentToLatest(current, latest));

    // ---------- 状态行文案 ----------

    [Fact]
    public void FormatRelation_UpToDate_SaysLatest()
        => Assert.Equal("已是最新", ShellLogic.VersionInfoPolicy.FormatRelation(
            ShellLogic.VersionInfoPolicy.Relation.UpToDate, "0.1.0-rc.8"));

    [Fact]
    public void FormatRelation_NewerAvailable_IncludesVersion()
        => Assert.Equal("有新版本 v0.1.0-rc.8", ShellLogic.VersionInfoPolicy.FormatRelation(
            ShellLogic.VersionInfoPolicy.Relation.NewerAvailable, "0.1.0-rc.8"));

    [Fact]
    public void FormatRelation_Unknown_DoesNotClaimLatest()
        => Assert.Equal("无法获取最新版本", ShellLogic.VersionInfoPolicy.FormatRelation(
            ShellLogic.VersionInfoPolicy.Relation.Unknown, null));

    [Fact]
    public void FormatRelation_Unknown_CustomText_Honored()
        => Assert.Equal("检查中…", ShellLogic.VersionInfoPolicy.FormatRelation(
            ShellLogic.VersionInfoPolicy.Relation.Unknown, null, "检查中…"));
}