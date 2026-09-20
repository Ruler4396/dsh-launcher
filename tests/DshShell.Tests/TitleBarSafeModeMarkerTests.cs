using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 标题栏"（安全模式）"标记的分段规则（纯函数 <see cref="ShellLogic.TitleBarText"/>）。
///
/// 【为什么要分段】2026-09-20 用户要求：把"（安全模式）"几个字改成红的，并且**可点**——点它重新
/// 调出右下角那张带退出动作的卡片。理由是他实测到的死路：卡片被 × 关掉之后，只能手动重启一次、
/// 等它再弹一次卡才能退出安全模式。
/// 真实绘制的像素与命中框验证在 <c>Regression_TitleBarSafeModeMarker.RealOs</c>（Category=RealOS）；
/// 这里只锁"哪一段算标记"这条规则。
/// </summary>
public class TitleBarSafeModeMarkerTests
{
    [Fact]
    public void Segments_PlainTitle_IsOneNonMarkerSegment()
    {
        var segs = ShellLogic.TitleBarText.Segments("DeepSeek Harness");
        Assert.Single(segs);
        Assert.Equal("DeepSeek Harness", segs[0].Text);
        Assert.False(segs[0].SafeModeMarker);
    }

    [Fact]
    public void Segments_SafeModeAndUpdateMarkers_OnlyTheSafeModeOneIsRed()
    {
        // 实测过的组合标题：基础标题 + 安全模式横幅 + 待更新标记
        var segs = ShellLogic.TitleBarText.Segments("DeepSeek Harness（安全模式）（有更新）");
        Assert.Equal(3, segs.Count);
        Assert.Equal("DeepSeek Harness", segs[0].Text);
        Assert.False(segs[0].SafeModeMarker);
        Assert.Equal("（安全模式）", segs[1].Text);
        Assert.True(segs[1].SafeModeMarker);
        Assert.Equal("（有更新）", segs[2].Text);
        Assert.False(segs[2].SafeModeMarker);   // 状态指示不是"降级中"，不抢红色也不承诺可点
    }

    [Theory]
    [InlineData("DeepSeek Harness（正在进入安全模式…）")]
    [InlineData("DeepSeek Harness（正在退出安全模式…）")]
    public void Segments_TransitionTitles_AreMarkersToo(string title)
    {
        var segs = ShellLogic.TitleBarText.Segments(title);
        Assert.Contains(segs, s => s.SafeModeMarker);
        Assert.Equal(title, string.Concat(segs.Select(s => s.Text)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DeepSeek Harness（括号没关上")]
    [InlineData("（开头就是标记）")]
    [InlineData("DeepSeek Harness（安全模式）（有更新）（再来一段）")]
    public void Segments_EdgeCases_NeverLoseCharacters(string? title)
        => Assert.Equal(title ?? "", string.Concat(
            ShellLogic.TitleBarText.Segments(title).Select(s => s.Text)));

    /// <summary>多段里可以同时存在多个安全模式标记（都算可点区域，绘制侧取并集）。</summary>
    [Fact]
    public void Segments_MultipleSafeModeSegments_AllMarked()
    {
        var segs = ShellLogic.TitleBarText.Segments("X（安全模式）Y（安全模式已暂停）");
        Assert.Equal(2, segs.Count(s => s.SafeModeMarker));
        Assert.Equal("X（安全模式）Y（安全模式已暂停）", string.Concat(segs.Select(s => s.Text)));
    }
}
