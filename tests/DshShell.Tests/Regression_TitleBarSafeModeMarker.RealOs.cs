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

            // ---- 悬停痕迹（2026-09-20 用户："安全模式能点你得有点痕迹，比如鼠标移上去加下划线"）----
            // 走生产的 OnMouseMove（反射，不为此加测试钩子）：指针进标记 → 红像素必须变多
            // （多出来的就是那条下划线），且命中框不许跟着抖动；指针移开 → 回到基线。
            bar._titleText = "DeepSeek Harness（安全模式）";
            bar.Invalidate();
            using var bmp3 = new Bitmap(900, 32, PixelFormat.Format32bppArgb);
            bar.DrawToBitmap(bmp3, new Rectangle(0, 0, 900, 32));
            var markerNow = bar.GetSafeModeMarkerRect();
            Assert.Equal(marker, markerNow);            // 布局不随悬停移动
            var plain = CountRed(bmp3, markerNow);

            // 类里有私有的 MouseMove 事件处理器，也继承了 Control.OnMouseMove —— GetMethod 不加
            // 参数类型会 AmbiguousMatch。这里取受保护的 OnMouseMove(MouseEventArgs)：它才是
            // "鼠标移动"的真实入口（内部再触发事件走到私有处理器），比直接调私有那个更贴近生产。
            var onMove = typeof(CustomTitleBar).GetMethod("OnMouseMove",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(MouseEventArgs) }, null)!;
            void Move(int px, int py) => onMove.Invoke(bar, new object[]
            { new MouseEventArgs(MouseButtons.None, 0, px, py, 0) });

            Move(markerNow.Left + 3, 16);
            using var bmp4 = new Bitmap(900, 32, PixelFormat.Format32bppArgb);
            bar.DrawToBitmap(bmp4, new Rectangle(0, 0, 900, 32));
            var hovered = CountRed(bmp4, markerNow);
            _out.WriteLine($"red pixels: plain={plain} hovered={hovered}");
            Assert.True(hovered > plain,
                $"悬停没有画出下划线（红像素 {plain} → {hovered}）——用户看不出这里能点");
            Assert.Same(Cursors.Hand, bar.Cursor);

            // 【真机回归 2026-09-20 用户报】鼠标移走后下划线还留着：标题栏的 MouseLeave 清理了
            // _hoverVersion/_hoverMin/Max/Close，但新加的 _hoverSafeMode 漏在里面。
            // 指针离开标题栏后不再有任何 MouseMove 事件，残留状态就永久挂在屏幕上。
            var onLeave = typeof(CustomTitleBar).GetMethod("OnMouseLeave",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                null, new[] { typeof(EventArgs) }, null)!;
            onLeave.Invoke(bar, new object[] { EventArgs.Empty });
            using var bmp6 = new Bitmap(900, 32, PixelFormat.Format32bppArgb);
            bar.DrawToBitmap(bmp6, new Rectangle(0, 0, 900, 32));
            var afterLeave = CountRed(bmp6, markerNow);
            _out.WriteLine($"red pixels after MouseLeave = {afterLeave}（基线 {plain}）");
            Assert.Equal(plain, afterLeave);
            Assert.Equal(Cursors.Default, bar.Cursor);

            Move(6, 16);                                 // 移回主体（非标记）
            using var bmp5 = new Bitmap(900, 32, PixelFormat.Format32bppArgb);
            bar.DrawToBitmap(bmp5, new Rectangle(0, 0, 900, 32));
            Assert.Equal(plain, CountRed(bmp5, markerNow));
            Assert.Equal(Cursors.Default, bar.Cursor);
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
