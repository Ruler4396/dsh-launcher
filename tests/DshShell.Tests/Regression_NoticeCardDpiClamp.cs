using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// [臃肿审计 B4] NoticeCardLayout 缺 DPI 钳制——运行期缺陷复现，非风格问题。
///
/// 同族三个布局类各自实现"deviceDpi → 缩放系数 → 物理像素"：SplashLayout.Compute 与
/// TrayMenuLayout.ComputeGeometry 都用 Math.Clamp(dpi/96f, 0.5f, 8f)，唯独
/// NoticeCardLayout.ComputeGeometry 写成裸 dpi / 96f。坏显卡驱动/RDP 会话给出异常
/// deviceDpi 时，另外两个窗口有界、通知卡片没有——用户视角是"通知弹出来是 1px 一条线"
/// 或"整卡跑到屏幕外"。
/// 本文件锁的是**三族同源**这一不变式，而不是某个具体像素值。
/// </summary>
public class Regression_NoticeCardDpiClamp
{
    private static ShellLogic.NoticeCardLayout.Geometry Notice(int dpi)
        => ShellLogic.NoticeCardLayout.ComputeGeometry(dpi);

    /// <summary>钳制上界 8f ⇒ 768dpi(=8×96) 与再高的 dpi 必须给出**完全相同**的几何。
    /// 修复前 Notice(4000) 按 s=41.7 计算，与 Notice(768) 不可能相等 → 本用例红。</summary>
    [Theory]
    [InlineData(769)]
    [InlineData(1000)]
    [InlineData(4000)]
    [InlineData(100_000)]
    [InlineData(int.MaxValue)]
    public void AbsurdDpi_IsClampedToEightX_LikeSiblingLayouts(int absurdDpi)
    {
        var capped = Notice(absurdDpi);
        var ceiling = Notice(768); // 8f × 96

        Assert.Equal(ceiling.Width, capped.Width);
        Assert.Equal(ceiling.TextWidth, capped.TextWidth);
        Assert.Equal(ceiling.Padding, capped.Padding);
        Assert.Equal(ceiling.TitleEmPx, capped.TitleEmPx);
    }

    /// <summary>下界 0.5f ⇒ dpi 极低（驱动给出 1/2 之类）时不得塌成 0 宽度。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    [InlineData(47)]
    public void TinyDpi_StaysWithinLowerClampAndGeometryStaysPositive(int dpi)
    {
        var g = Notice(dpi);
        var floor = Notice(48); // 0.5f × 96

        Assert.True(g.Width > 0, $"Width 必须为正，实测 {g.Width}（dpi={dpi}）");
        Assert.True(g.TitleEmPx >= 1 && g.BodyEmPx >= 1 && g.CloseSize >= 1);
        Assert.Equal(floor.Width, g.Width);
    }

    /// <summary>dpi ≤ 0 视为未知按 1x 处理（与两个兄弟布局的既有语义一致）。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void NonPositiveDpi_FallsBackTo1x(int dpi)
    {
        Assert.Equal(Notice(96).Width, Notice(dpi).Width);
    }

    /// <summary>正常档逐位不变：加钳制不得改动 96/120/144/192 的既有折算结果。</summary>
    [Theory]
    [InlineData(96)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(768)]
    public void NormalDpi_RemainsUnchangedByTheClamp(int dpi)
    {
        var g = Notice(dpi);
        var baseG = Notice(96);
        var s = dpi / 96f;
        Assert.Equal((int)Math.Round(baseG.TextWidth * s), g.TextWidth);
        Assert.Equal(g.TextWidth + 2 * g.Padding + g.AccentWidth + g.Gap, g.Width);
    }
}
