using System.Drawing;
using System.Drawing.Imaging;
using System.Windows.Forms;
using DshWeb.Chrome;
using Xunit;
using Xunit.Abstractions;

namespace DshShell.Tests;

/// <summary>
/// 【真机】标题栏红色"（安全模式）"标记：像素确实是红的、主体不是，且命中矩形罩在红字上。
///
/// 分段规则本身在 <see cref="TitleBarSafeModeMarkerTests"/>（纯函数）锁；这里锁的是**画出来
/// 的样子**与**点得到**——两者都可能与纯函数不一致：字体测量不带 Graphics 会错位（版本徽标当年
/// 就是这条），窗口过窄时标题被省略号裁掉，此时必须**不给命中框**（宁可不可点，也不要点到
/// 看不见的东西）。
///
/// 反向验证过：把标记颜色改成主体色 → 本用例红（标记那段几乎没有红像素）。
/// </summary>
[Trait("Category", "RealOS")]
public class Regression_TitleBarSafeModeMarker
{
    private readonly ITestOutputHelper _out;
    public Regression_TitleBarSafeModeMarker(ITestOutputHelper o) => _out = o;

    [Fact]
    public void RealOs_MarkerIsPaintedRed_AndTheHitRectCoversIt()
        => RunSta(() =>
        {
            using var owner = new Form { Text = "titlebar-marker-probe", ShowInTaskbar = false };
            var bar = new CustomTitleBar(owner, dark: false)
            { Bounds = new Rectangle(0, 0, 900, 32) };
            owner.Controls.Add(bar);
            _ = owner.Handle;                       // 子控件句柄随父窗创建

            bar._titleText = "DeepSeek Harness（安全模式）";
            bar.Invalidate();
            using var bmp = new Bitmap(900, 32, PixelFormat.Format32bppArgb);
            bar.DrawToBitmap(bmp, new Rectangle(0, 0, 900, 32));

            var marker = bar.GetSafeModeMarkerRect();
            Assert.False(marker.IsEmpty, "标题里有（安全模式），却没算出命中矩形");
            _out.WriteLine($"marker rect = {marker}");

            var redInMarker = CountRed(bmp, marker);
            var redInHead = CountRed(bmp, new Rectangle(0, 0, marker.Left, 32));
            Assert.True(redInMarker > 20, $"标记那段几乎没有红色像素（{redInMarker}）——着色没生效");
            Assert.Equal(0, redInHead);              // 主体不许跟着变红
            Assert.True(marker.Contains(marker.Left + marker.Width / 2, 16),
                "命中矩形连自己的中心都不覆盖");

            // 换回没有标记的标题并重画：命中框必须清空（否则留一块看不见的可点区）
            bar._titleText = "DeepSeek Harness";
            bar.Invalidate();
            using var bmp2 = new Bitmap(900, 32, PixelFormat.Format32bppArgb);
            bar.DrawToBitmap(bmp2, new Rectangle(0, 0, 900, 32));
            Assert.True(bar.GetSafeModeMarkerRect().IsEmpty,
                "没有安全模式标记时仍留着命中矩形");
        }, "标题栏安全模式标记绘制");

    /// <summary>数一片区域里"安全模式红"（#D81E06，与通知卡 Urgent 强调条同色）的像素数。</summary>
    private static int CountRed(Bitmap bmp, Rectangle area)
    {
        var n = 0;
        var x0 = Math.Max(0, area.Left);
        var x1 = Math.Min(bmp.Width - 1, area.Right - 1);
        for (var y = 0; y < bmp.Height; y++)
            for (var x = x0; x <= x1; x++)
            {
                var p = bmp.GetPixel(x, y);
                if (p.R >= 190 && p.G <= 70 && p.B <= 60) n++;
            }
        return n;
    }

    private static void RunSta(Action body, string what, int timeoutSeconds = 60)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(timeoutSeconds)), $"{what}：STA 场景超时");
        if (failure is not null) throw new Xunit.Sdk.XunitException($"{what} 抛异常：{failure}");
    }
}
