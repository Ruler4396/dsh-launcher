using System.Drawing;
using DshWeb;
using DshWeb.Win32;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 【契约测试】版本信息窗版式纯函数（ShellLogic.VersionDialogLayout）。
///
/// 该窗是全仓最后一个"手工绝对定位 + 硬编码 96dpi 像素 + Point 字体 + 无 OnDpiChanged"的窗口：
/// 缩放屏上文字按 s 变宽而列位不动 → 叠列；URL 行与按钮盒子本就重叠。这里把"列不互叠、
/// 内容不出客户端、URL 不与按钮相撞、标题栏高度与主窗同一套规则"钉成契约——
/// 渲染侧（VersionInfoDialog）只消费，不再自己算任何数。
/// </summary>
public class VersionDialogLayoutContractTests
{
    private static ShellLogic.VersionDialogLayout.Geometry G(int dpi)
        => ShellLogic.VersionDialogLayout.Compute(dpi);

    /// <summary>状态列右缘 = 客户端宽 - 内边距（右边界随 DPI 一起长，不是固定 504）。</summary>
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(240)]
    public void Columns_AreOrdered_NoOverlap_AndInsideClient(int dpi)
    {
        var g = G(dpi);
        Assert.True(g.ColNameX < g.ColCurrentX);
        Assert.True(g.ColCurrentX < g.ColLatestX);
        Assert.True(g.ColLatestX < g.ColStatusX);
        // 每列的盒子必须在下一列起点之前结束（叠列就是从这里开始的）
        Assert.True(g.ColNameX + g.NameW <= g.ColCurrentX,
            $"名称列越界：{g.ColNameX}+{g.NameW} > {g.ColCurrentX} (dpi={dpi})");
        Assert.True(g.ColCurrentX + g.CurrentW <= g.ColLatestX,
            $"当前列越界：{g.ColCurrentX}+{g.CurrentW} > {g.ColLatestX} (dpi={dpi})");
        Assert.True(g.ColLatestX + g.LatestW <= g.ColStatusX,
            $"最新列越界：{g.ColLatestX}+{g.LatestW} > {g.ColStatusX} (dpi={dpi})");

        var statusWidth = g.RowWidth - (g.ColStatusX - g.ColNameX);
        Assert.True(statusWidth > 0);
        Assert.Equal(g.ClientWidth - g.Padding, g.ColStatusX + statusWidth);
        Assert.Equal(g.ClientWidth - 2 * g.Padding, g.RowWidth);
    }

    /// <summary>URL 行与按钮不得相交（旧实现给 URL 整行宽，省略号正好压在按钮上）。</summary>
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(240)]
    public void LinkRow_And_Button_DoNotIntersect(int dpi)
    {
        var g = G(dpi);
        var link = new Rectangle(g.ColNameX, g.LinkY, g.LinkWidth, g.LinkHeight);
        var button = new Rectangle(g.ButtonX, g.ButtonY, g.ButtonWidth, g.ButtonHeight);
        Assert.False(link.IntersectsWith(button),
            $"dpi={dpi}：link={link} button={button} 相交");
        Assert.True(button.Right <= g.ClientWidth - g.Padding / 2, "按钮右侧留白不足");
        Assert.True(button.Bottom <= g.ClientHeight, "按钮掉出客户端");
    }

    /// <summary>所有可见内容都在客户端内（含分隔线与第二行）。</summary>
    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public void Content_StaysInsideClient(int dpi)
    {
        var g = G(dpi);
        Assert.True(g.Row1Y > g.TitleHeight, "第一行必须落在自绘标题栏之下");
        Assert.True(g.Row2Y > g.Row1Y + g.RowHeight, "两行不得重叠");
        Assert.True(g.SeparatorY > g.Row2Y + g.RowHeight);
        Assert.True(g.LinkTitleY > g.SeparatorY);
        Assert.True(g.LinkY + g.LinkHeight <= g.ClientHeight);
        Assert.True(g.ButtonY + g.ButtonHeight <= g.ClientHeight);
        Assert.True(g.ClientWidth > 0 && g.ClientHeight > 0);
    }

    /// <summary>字号只在此折算一次（9pt→12px@96），渲染侧必须用 GraphicsUnit.Pixel 消费。</summary>
    [Theory]
    [InlineData(96, 12)]
    [InlineData(120, 15)]
    [InlineData(144, 18)]
    [InlineData(192, 24)]
    public void EmPx_IsScaledExactlyOnce(int dpi, int expected)
        => Assert.Equal(expected, G(dpi).EmPx);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnknownDpi_FallsBackTo96(int dpi)
    {
        var g = G(dpi);
        Assert.Equal(G(96).ClientWidth, g.ClientWidth);
        Assert.Equal(G(96).EmPx, g.EmPx);
    }

    /// <summary>标题栏高度与主窗/弹窗共用同一条规则（WindowGeometry.LayoutChromeRects），
    /// 不再各写一份 32*dpi/96——两处写法分叉过就出现过 #28 那类"一处修一处漏"。</summary>
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void TitleHeight_MatchesTheSharedChromeRule(int dpi)
    {
        var g = G(dpi);
        var (title, _) = WindowGeometry.LayoutChromeRects(
            new Size(g.ClientWidth, g.ClientHeight), dpi);
        Assert.Equal(title.Height, g.TitleHeight);
    }
}
