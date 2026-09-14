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
    [InlineData(1.0)]   // 100% DPI
    [InlineData(1.5)]   // 150%（用户截图所在量级）
    [InlineData(2.0)]   // 200%
    public void Contract_Content_IsHorizontallyCentered(double scale)
    {
        int icon = (int)(18 * scale), gap = (int)(12 * scale);
        int w1 = (int)(14 * scale), w2 = (int)(14 * scale), ls = (int)(2 * scale);
        int itemX = (int)(5 * scale), itemWidth = (int)(106 * scale);
        var place = ShellLogic.TrayMenuLayout.PlaceExitRow(itemX, itemWidth, icon, gap, w1, w2, ls);

        var leftMargin = place.FirstCharX - gap - icon - itemX;
        var rightMargin = (itemX + itemWidth) - (place.SecondCharX + w2);
        Assert.True(Math.Abs(leftMargin - rightMargin) <= 1,
            $"整行须居中：左 {leftMargin}px vs 右 {rightMargin}px（scale={scale}）");
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
}
