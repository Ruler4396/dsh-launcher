using System.Drawing;
using System.Threading;
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
        Assert.Equal((int)Math.Round(baseG.AccentWidth * s), g.AccentWidth);
        // 窗口宽 = 文字宽 + 左右内边距 + 色条 + 色条与文字的一个间距
        //（不是再乘一次系数——issue #28-3 的 s² 放大根因）
        Assert.Equal(g.TextWidth + 2 * g.Padding + g.AccentWidth + g.Gap, g.Width);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public void Place_AccentBarSpansFullHeight_AndTextYieldsToIt(int dpi)
    {
        var g = G(dpi);
        var p = ShellLogic.NoticeCardLayout.Place(g, 18, 32, hasAction: true);
        Assert.Equal(g.AccentWidth, p.AccentRect.Width);
        Assert.Equal(p.Height, p.AccentRect.Height);   // 撑满全高，才是一条真正的级别标识
        Assert.Equal(0, p.AccentRect.X);
        // 文字一律从色条右侧开始：色条压住正文 = 视觉上"字被切了一半"
        Assert.True(p.TitleRect.X >= g.AccentWidth + g.Padding);
        Assert.True(p.BodyRect.X >= g.AccentWidth + g.Padding);
        Assert.True(p.ActionRect.X >= g.AccentWidth + g.Padding);
    }

    /// <summary>测量宽度与排版宽度必须同源：否则"按 A 宽换行、按 B 宽绘制"会裁字。</summary>
    [Fact]
    public void MeasureWidths_AreTheWidthsPlaceUses()
    {
        var g = G(96);
        var m = ShellLogic.NoticeCardLayout.MeasureWidths(g);
        var p = ShellLogic.NoticeCardLayout.Place(g, m.TitleWidth, m.BodyWidth, hasAction: true);
        Assert.Equal(m.TitleWidth, p.TitleRect.Width);
        Assert.Equal(m.BodyWidth, p.BodyRect.Width);
        Assert.Equal(m.BodyWidth, p.ActionRect.Width);
    }

    /// <summary>级别 → 提示线索：Urgent 才走警示音/红色条（安全更新、构建失败、安全模式）。</summary>
    [Theory]
    [InlineData(ShellLogic.NoticeKind.Info, false)]
    [InlineData(ShellLogic.NoticeKind.Urgent, true)]
    public void NoticePolicy_WarningCueOnlyForUrgent(ShellLogic.NoticeKind kind, bool expected)
        => Assert.Equal(expected, ShellLogic.NoticePolicy.UseWarningCue(kind));

    /// <summary>
    /// 驻留时长折算：0 = sticky（不自动收起，安全模式降级态用）。
    /// 钳位边界必须有名——调用方传 1 秒会被抬到 3 秒，传 10 分钟会被压到 2 分钟，
    /// 而传 Zero / InfiniteTimeSpan 必须**原样**得到 0，不能被钳成 3 秒：
    /// 那正是"降级态提示 3 秒后自己消失、用户失去退出入口"的旧陷阱。
    /// </summary>
    [Theory]
    [InlineData(0, 0)]                       // TimeSpan.Zero → sticky
    [InlineData(-1, 0)]                      // Timeout.InfiniteTimeSpan(-1ms) → sticky
    [InlineData(-1000, 0)]
    [InlineData(1, 3000)]                    // 低于下限 → 抬到 3s
    [InlineData(25, 25000)]                  // 区间内原样
    [InlineData(120, 120000)]                // 上限
    [InlineData(600, 120000)]                // 超上限 → 压到 120s
    public void NoticePolicy_ResolveExpiryMs(double seconds, int expectedMs)
        => Assert.Equal(expectedMs, ShellLogic.NoticePolicy.ResolveExpiryMs(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void NoticePolicy_InfiniteTimeSpan_IsSticky()
        => Assert.Equal(0, ShellLogic.NoticePolicy.ResolveExpiryMs(Timeout.InfiniteTimeSpan));

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
