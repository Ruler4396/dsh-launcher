using System.Reflection;
using DshWeb;
using DshWeb.Chrome;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 版本信息功能】标题栏 dsh 版本徽标 → 原生版本信息窗 的跨模块不变量
/// （因果链：Program 启动发现 dsh 版本 → 写入 CustomTitleBar._dshVersion → 徽标渲染 →
/// 点击 VersionClick → VersionInfoDialog 展示 dsh/启动器当前+最新 + 启动器下载地址）。
///
/// 只关心系统的最终物理不变量，不关心各模块内部实现：
/// 1. 组合根把"发现层原始版本号"喂给 CustomTitleBar（UI 不自行探测版本）；
/// 2. 展示文本由 ShellLogic.VersionInfoPolicy 单点合成（不散落各窗体）；
/// 3. 启动器下载地址单一事实源（UpdateChecker.LauncherLatestReleaseUrl 与仓库常量一致）。
/// </summary>
public class VersionInfoOutcomes
{
    [Fact]
    public void Outcome_TitleBar_ExposesVersionBadgeAndClickHook()
    {
        // 契约：CustomTitleBar 暴露"版本徽标字段 + 点击回调 + 命中矩形"三项装配入口。
        // 字段存在性即"UI 有能力渲染可点击徽标"的物理证据（组合根装配位）。
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        Assert.NotNull(typeof(CustomTitleBar).GetField("_dshVersion", flags));
        Assert.NotNull(typeof(CustomTitleBar).GetField("_versionRect", flags));
        Assert.NotNull(typeof(CustomTitleBar).GetField("VersionClick", flags));
    }

    [Fact]
    public void Outcome_TitleBarBadge_InitialState_Empty()
    {
        // 未发现 dsh 版本（初始值 / NpxCache 版本未知）时徽标为空 → 不渲染不点击，
        // 绝不显示误导性的假版本号
        Assert.Equal("", ShellLogic.VersionInfoPolicy.ComposeTitleBarBadge(null));
    }

    [Fact]
    public void Outcome_LauncherDownloadUrl_SingleSourceOfTruth()
    {
        // 下载地址 = https://github.com/{LauncherRepo}/releases/latest（与仓库常量一致）
        Assert.Equal(
            "https://github.com/" + UpdateChecker.LauncherRepo + "/releases/latest",
            UpdateChecker.LauncherLatestReleaseUrl);
        Assert.StartsWith("https://github.com/", UpdateChecker.LauncherLatestReleaseUrl);
        Assert.EndsWith("/releases/latest", UpdateChecker.LauncherLatestReleaseUrl);
    }

    [Fact]
    public void Outcome_VersionTexts_ComposedByPurePolicy_NotByUi()
    {
        // 标题栏徽标 / 弹窗"当前、最新"列文案全部由 ShellLogic 纯函数合成：
        // 组合根与窗体只喂原始版本号，UI 零拼字符串（防各处格式化漂移）
        Assert.Equal("v0.1.0-rc.7", ShellLogic.VersionInfoPolicy.ComposeTitleBarBadge("0.1.0-rc.7"));
        Assert.Equal("v0.1.0-rc.7", ShellLogic.VersionInfoPolicy.FormatCurrent("0.1.0-rc.7"));
        Assert.Equal("v0.1.0-rc.8", ShellLogic.VersionInfoPolicy.FormatLatest("0.1.0-rc.8"));
        Assert.Equal("获取失败", ShellLogic.VersionInfoPolicy.FormatLatest(null));
    }

    [Fact]
    public void Outcome_RelationStatus_MatchesUpdateCheckSemantics()
    {
        // 弹窗"状态"列与更新检查同语义：本地未知 + 远端已知 = 有新版可装（不误报"已是最新"）；
        // 远端不可知 = 无法获取（不得假称最新）
        Assert.Equal(ShellLogic.VersionInfoPolicy.Relation.NewerAvailable,
            ShellLogic.VersionInfoPolicy.CompareCurrentToLatest(null, "0.1.0-rc.8"));
        Assert.Equal(ShellLogic.VersionInfoPolicy.Relation.Unknown,
            ShellLogic.VersionInfoPolicy.CompareCurrentToLatest("0.1.0-rc.7", null));
    }
}