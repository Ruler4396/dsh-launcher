using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using DshWeb;
using DshWeb.Windows;
using Xunit;
using Xunit.Abstractions;

namespace DshShell.Tests;

/// <summary>
/// 【Regression_Issue28_TrayMenuHighDpi】托盘菜单在高 DPI 屏上"依旧异常"的真实渲染回归（零 Mock）。
///
/// 事故（issue #28 复测第 4 条）：字距问题（#28-1）已修，但报告人 200% 屏上菜单仍然不对——
/// 卡片与电源图标按 s 放大，"退出"两字却按 s² 放大。逐像素量他截图：卡片 225×73（s≈1.9）、
/// 图标墨迹宽 25px（比例正常）、"退"墨迹宽 **48px = 设计值 12·s 的 2.04 倍**。
/// 根因是字号用 <c>GraphicsUnit.Point</c> 且已乘过 <c>_s</c>，绘制 DC 自带的 DPI 又折算一次。
///
/// 为什么这些断言在 96 DPI 的 CI runner 上也成立：渲染画布的分辨率由测试显式指定
/// （<see cref="Bitmap.SetResolution"/>），不依赖宿主显示器——正是生产代码不再依赖 DC DPI 的镜像。
/// </summary>
[Collection("RealOS")]
[Trait("Category", "RealOS")]
public class Regression_Issue28_TrayMenuHighDpi_RealOs
{
    private readonly ITestOutputHelper _out;
    public Regression_Issue28_TrayMenuHighDpi_RealOs(ITestOutputHelper o) => _out = o;

    private sealed record Render(int MaxDarkRun, Rectangle DarkBox, Rectangle RedBox, Rectangle CardBox)
    {
        public int MaxRedRun { get; init; }
    }

    /// <summary>用生产 Draw() 渲染一次，并统计暗色（文字）与红色（电源图标）墨迹范围。</summary>
    private static Render RenderMenu(int deviceDpi, float canvasDpi)
    {
        using var form = new TrayMenuForm(() => { }, deviceDpi);
        var draw = typeof(TrayMenuForm).GetMethod("Draw", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(draw);

        using var bmp = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
        bmp.SetResolution(canvasDpi, canvasDpi);   // 关键：显式给定绘制 DC 的分辨率
        using (var g = Graphics.FromImage(bmp))
        {
            // 必须先铺白底：Format32bppArgb 的全透明像素按预乘读取时 R=G=B=0，
            // 会被"近黑"判据当成文字墨迹（实测整行 136px 全黑 → 断言失真）。
            g.Clear(Color.White);
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            draw!.Invoke(form, new object[] { g });
        }

        var darkCols = new List<int>();
        var darkBox = Rectangle.Empty;
        var redBox = Rectangle.Empty;
        var maxDarkRun = 0;
        var run = 0;
        for (var x = 0; x < bmp.Width; x++)
        {
            var hasDark = false;
            for (var y = 0; y < bmp.Height; y++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R < 90 && c.G < 90 && c.B < 90)
                {
                    hasDark = true;
                    darkBox = darkBox.IsEmpty ? new Rectangle(x, y, 1, 1) : Rectangle.Union(darkBox, new Rectangle(x, y, 1, 1));
                }
                else if (c.R > 150 && c.G < 90 && c.B < 90)
                {
                    redBox = redBox.IsEmpty ? new Rectangle(x, y, 1, 1) : Rectangle.Union(redBox, new Rectangle(x, y, 1, 1));
                }
            }
            if (hasDark) { run++; maxDarkRun = Math.Max(maxDarkRun, run); darkCols.Add(x); }
            else run = 0;
        }
        var card = ShellLogic.TrayMenuLayout.ComputeGeometry(deviceDpi).Content;
        card.Offset(0, 0);
        return new Render(maxDarkRun, darkBox, redBox, card) { MaxRedRun = redBox.Width };
    }

    /// <summary>
    /// 核心断言：同一份菜单在 96 与 192 分辨率画布上，文字墨迹宽度必须**一致**。
    /// 修复前（点数字号）192 画布会渲染出约 2 倍宽的字 → 本用例必红。
    /// </summary>
    [Fact]
    public void RealOs_GlyphInk_IsIndependentOfDrawingContextDpi()
    {
        var on96 = RenderMenu(deviceDpi: 192, canvasDpi: 96f);
        var on192 = RenderMenu(deviceDpi: 192, canvasDpi: 192f);
        _out.WriteLine($"单字最大墨迹宽：96 画布 {on96.MaxDarkRun}px vs 192 画布 {on192.MaxDarkRun}px");

        Assert.True(on96.MaxDarkRun > 0 && on192.MaxDarkRun > 0, "渲染结果里没有文字墨迹（字体缺失/渲染失败）");
        Assert.True(Math.Abs(on96.MaxDarkRun - on192.MaxDarkRun) <= 1,
            $"字号又被绘制 DC 的 DPI 折算了一次（二次缩放复发）：96 画布 {on96.MaxDarkRun}px vs 192 画布 {on192.MaxDarkRun}px");
    }

    /// <summary>字号必须随目标 DPI **线性**放大（旧实现是 s²：192 下约 53px）。</summary>
    [Theory]
    [InlineData(96, 9, 20)]     // 100%：em=13px
    [InlineData(192, 19, 40)]   // 200%：em=27px（旧实现此处约 53px → 必红）
    public void RealOs_GlyphInkWidth_ScalesLinearlyWithTargetDpi(int deviceDpi, int minInk, int maxInk)
    {
        // 按最坏情况取画布 DPI：修复前正是它把二次缩放暴露出来
        var r = RenderMenu(deviceDpi, canvasDpi: deviceDpi);
        _out.WriteLine($"deviceDpi={deviceDpi} 单字最大墨迹宽={r.MaxDarkRun}px（允许 {minInk}–{maxInk}）");
        Assert.InRange(r.MaxDarkRun, minInk, maxInk);
    }

    /// <summary>电源图标必须真实存在、且整体留在白色卡片内（旧回归测试刻意不看图标）。</summary>
    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public void RealOs_PowerIcon_ExistsAndStaysInsideCard(int deviceDpi)
    {
        var r = RenderMenu(deviceDpi, canvasDpi: deviceDpi);
        Assert.True(r.MaxRedRun > 0, "渲染结果里找不到红色电源图标（图标消失）");
        Assert.True(r.RedBox.Left >= r.CardBox.Left && r.RedBox.Right <= r.CardBox.Right
            && r.RedBox.Top >= r.CardBox.Top && r.RedBox.Bottom <= r.CardBox.Bottom,
            $"电源图标越出白色卡片：图标 {r.RedBox} vs 卡片 {r.CardBox}（居中偏移未钳制）");
        Assert.True(r.DarkBox.Right <= r.CardBox.Right,
            $"文字越出白色卡片：文字 {r.DarkBox} vs 卡片 {r.CardBox}");
    }
}
