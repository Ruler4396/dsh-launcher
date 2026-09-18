using System.Drawing;
using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// [issue #25 收口] 通知卡片版式契约（纯函数 ShellLogic.NoticeCardLayout）。
/// 与 SplashLayoutContractTests / TrayMenuLayoutContractTests 同一纪律：设计基准 → 物理像素
/// 的折算、段间**恰好一个 Gap**（0 重叠、0 空隙）、越界钳制，全部在纯函数层钉死，
/// 绘制侧（Windows/NoticeCard.cs）只消费不计算。
/// </summary>
public class NoticeCardLayoutContractTests
{
    private static ShellLogic.NoticeCardLayout.Geometry G(int dpi)
        => ShellLogic.NoticeCardLayout.ComputeGeometry(dpi);

    [Theory]
    [InlineData(96, 1.0f)]
    [InlineData(120, 1.25f)]
    [InlineData(144, 1.5f)]
    [InlineData(192, 2.0f)]
    public void Geometry_ScalesLinearlyWithDpi(int dpi, float s)
    {
        var g = G(dpi);
        var baseG = G(96);
        Assert.Equal((int)Math.Round(baseG.TextWidth * s), g.TextWidth);
        Assert.Equal((int)Math.Round(baseG.Padding * s), g.Padding);
        Assert.Equal((int)Math.Round(baseG.CloseSize * s), g.CloseSize);
        // 窗口宽 = 文字宽 + 左右内边距（不是再乘一次系数——issue #28-3 的 s² 放大根因）
        Assert.Equal(g.TextWidth + 2 * g.Padding, g.Width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Geometry_UnknownDpi_FallsBackTo1x(int dpi)
    {
        Assert.Equal(G(96).Width, G(dpi).Width);
        Assert.True(G(dpi).CornerRadius >= 1);   // 任何折算都不产生 0 尺寸（Region 会抛）
    }

    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public void Place_WithoutAction_LeavesNoTrailingGap(int dpi)
    {
        var g = G(dpi);
        var p = ShellLogic.NoticeCardLayout.Place(g, 18, 32, hasAction: false);
        Assert.True(p.ActionRect.IsEmpty);
        // 底部只剩一个 Padding：多算的 Gap/ActionHeight 会变成卡片下方的空白条
        Assert.Equal(g.Padding + 18 + g.Gap + 32 + g.Padding, p.Height);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Place_SectionsHaveExactlyOneGap_NoOverlapNoSeam(int dpi)
    {
        var g = G(dpi);
        var p = ShellLogic.NoticeCardLayout.Place(g, 18, 32, hasAction: true);
        Assert.Equal(p.TitleRect.Bottom + g.Gap, p.BodyRect.Top);
        Assert.Equal(p.BodyRect.Bottom + g.Gap, p.ActionRect.Top);
        Assert.Equal(18, p.TitleRect.Height);
        Assert.Equal(p.ActionRect.Height, g.ActionHeight);
        // 三段都在窗口内（× 也不例外）——超出就是被 Region 裁掉的"看不见的按钮"
        Assert.True(p.ActionRect.Bottom <= p.Height);
        Assert.True(p.CloseRect.Bottom <= p.Height);
        Assert.True(p.BodyRect.Right <= p.Width - g.Padding);
    }

    [Fact]
    public void Place_TitleYieldsWidthToCloseButton()
    {
        var g = G(96);
        var p = ShellLogic.NoticeCardLayout.Place(g, 18, 32, hasAction: false);
        // 标题不得伸到 × 底下：让出 CloseSize + 一个 Gap
        Assert.True(p.TitleRect.Right + g.Gap <= p.CloseRect.Left);
        Assert.Equal(p.CloseRect.Right, g.Width - g.Padding);
    }

    [Fact]
    public void Place_HeightNeverClipsCloseButton()
    {
        var g = G(96);
        // 极矮的文字（例如空 body）时，× 仍要完整落在窗口内
        var p = ShellLogic.NoticeCardLayout.Place(g, 1, 1, hasAction: false);
        Assert.True(p.Height >= p.CloseRect.Bottom + g.Padding);
    }

    [Fact]
    public void PlaceAtBottomRight_AnchorsToWorkAreaCornerWithMargin()
    {
        var g = G(96);
        var wa = new Rectangle(0, 0, 1920, 1040);   // 1080 高、40 任务栏
        var (x, y) = ShellLogic.NoticeCardLayout.PlaceAtBottomRight(wa, g, 400, 120);
        Assert.Equal(1920 - 400 - g.ScreenMargin, x);
        Assert.Equal(1040 - 120 - g.ScreenMargin, y);
    }

    [Fact]
    public void PlaceAtBottomRight_RespectsNonOriginWorkArea()
    {
        // 副屏在工作区负坐标一侧：定位必须以 workArea 为基准，不能用 (0,0)
        var g = G(96);
        var wa = new Rectangle(-1920, 0, 1920, 1040);
        var (x, y) = ShellLogic.NoticeCardLayout.PlaceAtBottomRight(wa, g, 400, 120);
        Assert.InRange(x, wa.Left + g.ScreenMargin, wa.Right - 400 - g.ScreenMargin);
        Assert.Equal(wa.Bottom - 120 - g.ScreenMargin, y);
    }

    [Fact]
    public void PlaceAtBottomRight_TinyWorkArea_ClampsInside()
    {
        var g = G(96);
        var wa = new Rectangle(0, 0, 300, 100);   // 比卡片（400×120）还小
        var (x, y) = ShellLogic.NoticeCardLayout.PlaceAtBottomRight(wa, g, 400, 120);
        // 卡片塞不进工作区：贴左上内边距，绝不给出负偏移（窗口跑到工作区外就再也关不掉了）
        Assert.Equal(g.ScreenMargin, x);
        Assert.Equal(g.ScreenMargin, y);
    }

    [Fact]
    public void PlaceAtBottomRight_FitsBothDimensions_StillAnchorsBottomRight()
    {
        // 反向保险：只有一维放不下时，另一维仍须贴右下（别把钳制写成"整张卡片挪到左上"）
        var g = G(96);
        var wa = new Rectangle(0, 0, 300, 600);
        var (x, y) = ShellLogic.NoticeCardLayout.PlaceAtBottomRight(wa, g, 400, 120);
        Assert.Equal(g.ScreenMargin, x);
        Assert.Equal(600 - 120 - g.ScreenMargin, y);
    }
}
