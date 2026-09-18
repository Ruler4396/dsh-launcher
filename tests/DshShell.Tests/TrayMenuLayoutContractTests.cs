using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 【契约测试】托盘菜单"退出"条目版式纯函数（issue #28-1）。
/// 不变量：字距严格等于 letterSpacing（不被任何测量/绘制内边距放大）+ 整行在条目内居中。
/// 渲染侧必须配 <c>TextFormatFlags.NoPadding</c> 测量与绘制，本契约才成立
/// （真实像素级回归见 Regression_Issue28_TrayExitRow.RealOs）。
/// </summary>
public class TrayMenuLayoutContractTests
{
    [Theory]
    [InlineData(14, 14, 2)]     // 等宽（NoPadding 实测 10pt "退"/"出" 各 14px）
    [InlineData(15, 13, 2)]     // 不等宽：仍须精确 2px 字距（旧实现按首字宽度推进 → 漂移）
    [InlineData(14, 14, 3)]     // 150% DPI：2 * 1.5 → 3
    [InlineData(18, 22, 4)]     // 200% DPI 异构字宽
    public void Contract_LetterSpacing_IsExact(int firstWidth, int secondWidth, int letterSpacing)
    {
        var place = ShellLogic.TrayMenuLayout.PlaceExitRow(
            itemX: 5, itemWidth: 106, iconSize: 18, gap: 12,
            firstCharWidth: firstWidth, secondCharWidth: secondWidth, letterSpacing: letterSpacing);

        Assert.Equal(letterSpacing, place.SecondCharX - (place.FirstCharX + firstWidth));
        Assert.Equal(18 + 12 + firstWidth + letterSpacing + secondWidth, place.ContentWidth);
    }

    [Theory]
    [InlineData(96)]    // 100% DPI
    [InlineData(144)]   // 150%（用户截图所在量级）
    [InlineData(192)]   // 200%
    [InlineData(240)]   // 250%
    public void Contract_Content_IsHorizontallyCentered(int dpi)
    {
        // 各段宽度全部取自几何纯函数（旧用例把字宽写死 14*scale，正是它掩盖了字号二次缩放）
        var g = ShellLogic.TrayMenuLayout.ComputeGeometry(dpi);
        int w1 = g.EmPx, w2 = g.EmPx;   // CJK 单字 advance ≈ em（真实墨迹宽度由渲染侧实测）
        var place = ShellLogic.TrayMenuLayout.PlaceExitRow(
            g.Item.X, g.Item.Width, g.IconSize, g.Gap, w1, w2, g.LetterSpacing);

        var leftMargin = place.FirstCharX - g.Gap - g.IconSize - g.Item.X;
        var rightMargin = (g.Item.X + g.Item.Width) - (place.SecondCharX + w2);
        Assert.True(Math.Abs(leftMargin - rightMargin) <= 1,
            $"整行须居中：左 {leftMargin}px vs 右 {rightMargin}px（dpi={dpi}）");
    }

    [Fact]
    public void Contract_IconCenter_SitsInsideIconBox()
    {
        var place = ShellLogic.TrayMenuLayout.PlaceExitRow(0, 100, 18, 12, 14, 14, 2);
        // 图标中心 = 内容起点 + 半个图标宽；内容起点 = itemX + 居中偏移
        Assert.Equal(place.FirstCharX - 12 - 9, place.IconCenterX);
    }

    [Fact]
    public void Contract_TextStartsAfterIconAndGap()
    {
        var place = ShellLogic.TrayMenuLayout.PlaceExitRow(0, 100, 18, 12, 14, 14, 2);
        // 首字左边界 = 图标右边界 + gap（旧实现把首字矩形加宽 4px，视觉空档即由此叠加产生）
        Assert.Equal(place.IconCenterX + 9 + 12, place.FirstCharX);
    }

    // ==================== [issue #28-3] 几何纯函数：一律线性缩放，字号只折算一次 ====================

    /// <summary>报告人 200% 屏实测：卡片/图标按 s 放大而文字按 s² 放大（墨迹宽 = 设计值 2.04 倍）。
    /// 本组用例把"字号折算只发生一次"钉成契约——任何把 dpi 乘两遍的实现都会在这里变红。</summary>
    [Theory]
    [InlineData(96, 100)]
    [InlineData(120, 125)]
    [InlineData(144, 150)]
    [InlineData(168, 175)]
    [InlineData(192, 200)]    // 报告人截图所在量级
    [InlineData(240, 250)]
    public void Contract_Geometry_ScalesLinearlyWithDpi(int dpi, int expectedPercent)
    {
        var g = ShellLogic.TrayMenuLayout.ComputeGeometry(dpi);
        var s = dpi / 96f;

        Assert.Equal(expectedPercent, g.ScalePercent);
        Assert.Equal((int)Math.Round(ShellLogic.TrayMenuLayout.DesignMenuWidth * s), g.Content.Width);
        Assert.Equal((int)Math.Round(ShellLogic.TrayMenuLayout.DesignMenuHeight * s), g.Content.Height);
        Assert.Equal((int)Math.Round(ShellLogic.TrayMenuLayout.DesignIconBox * s), g.IconSize);
        Assert.Equal((int)Math.Round(ShellLogic.TrayMenuLayout.DesignLetterSpacing * s), g.LetterSpacing);
        // 字号：10pt 在 96dpi 下 = 13.33px，再按 dpi 线性放大——**只此一次**
        Assert.Equal((int)Math.Round(ShellLogic.TrayMenuLayout.DesignEmPt * 96.0 / 72.0 * s), g.EmPx);

        // 比例不变量（缩放不变）：二次缩放会让文字相对卡片变大，这里必红
        var ratioAt100 = (double)ShellLogic.TrayMenuLayout.ComputeGeometry(96).EmPx
            / ShellLogic.TrayMenuLayout.ComputeGeometry(96).Content.Width;
        Assert.True(Math.Abs((double)g.EmPx / g.Content.Width - ratioAt100) < 0.02,
            $"字号/卡宽比例随 DPI 漂移：{g.EmPx}/{g.Content.Width} vs 100% 基线 {ratioAt100:F4}");
    }

    [Theory]
    [InlineData(0)]      // 取不到显示器 DPI
    [InlineData(-96)]
    public void Contract_Geometry_UnknownDpi_FallsBackTo100Percent(int dpi)
        => Assert.Equal(ShellLogic.TrayMenuLayout.ComputeGeometry(96), ShellLogic.TrayMenuLayout.ComputeGeometry(dpi));

    [Fact]
    public void Contract_Geometry_ScaleIsClampedBothWays()
    {
        // 旧实现 Math.Max(1f, …) 只向上夹：100% 副屏（混屏）会被主屏缩放放大。
        var tiny = ShellLogic.TrayMenuLayout.ComputeGeometry(24);      // 25%
        Assert.Equal(50, tiny.ScalePercent);
        var huge = ShellLogic.TrayMenuLayout.ComputeGeometry(96 * 40); // 4000%
        Assert.Equal(800, huge.ScalePercent);
    }

    [Fact]
    public void Contract_Geometry_FormAndItemAreConsistent()
    {
        var g = ShellLogic.TrayMenuLayout.ComputeGeometry(192);
        Assert.Equal(g.Content.Width + g.ShadowMargin * 2, g.FormWidth);
        Assert.Equal(g.Content.Height + g.ShadowMargin * 2, g.FormHeight);
        Assert.True(g.Item.Left >= g.Content.Left && g.Item.Right <= g.Content.Right
            && g.Item.Top >= g.Content.Top && g.Item.Bottom <= g.Content.Bottom,
            "条目矩形必须整体落在白色卡片内（命中区与绘制区同源，不得各算一份）");
    }

    [Fact]
    public void Contract_OverflowingContent_KeepsIconInsideCard()
    {
        // 高 DPI 下实测字宽超过条目宽：居中偏移会算成负数，旧实现把图标画到卡片外。
        var place = ShellLogic.TrayMenuLayout.PlaceExitRow(
            itemX: 10, itemWidth: 60, iconSize: 36, gap: 24,
            firstCharWidth: 53, secondCharWidth: 53, letterSpacing: 4);
        Assert.True(place.ContentWidth > 60, "前置条件：内容确实超宽");
        Assert.Equal(10 + 36 / 2, place.IconCenterX);   // 左对齐到条目起点，图标中心不越出卡片
        Assert.True(place.IconCenterX - 18 >= 10, "图标左缘必须在条目矩形内");
        Assert.Equal(place.FirstCharX + 53 + 4, place.SecondCharX); // 字距契约在溢出时同样成立
    }
}
