using System.Drawing;
using DshWeb.Win32;
using Xunit;

namespace DshShell.Tests.Win32;

/// <summary>
/// WindowGeometry 纯函数单测（Step 1，矩阵 G1/G4/G5/G7/G10）。
/// 全部为无 GUI 纯算术覆盖——含负坐标副屏、上下堆叠、150%/100% 异构 DPI 等边界。
/// </summary>
public class WindowGeometryTests
{
    // ---------- ComputeMaximizedMinMaxInfo（G1/G10 多屏物理像素） ----------

    [Fact]
    public void Max_primary_workarea_maps_exactly()
    {
        var work = new Rectangle(0, 0, 1920, 1040); // 物理工作区（主屏）
        var m = WindowGeometry.ComputeMaximizedMinMaxInfo(work);
        Assert.Equal(new Point(1920, 1040), m.MaxSize);
        Assert.Equal(new Point(0, 0), m.MaxPos);
        Assert.Equal(new Point(1920, 1040), m.MaxTrack);
    }

    [Fact]
    public void Max_left_negative_x_secondary_monitor_preserved_exactly()
    {
        // 左侧副屏：物理工作区 X 为负（多屏血泪：逻辑像素陷阱在此丢窗）
        var work = new Rectangle(-1920, 0, 1920, 1040);
        var m = WindowGeometry.ComputeMaximizedMinMaxInfo(work);
        Assert.Equal(new Point(-1920, 0), m.MaxPos);   // 必须精确透传负坐标
        Assert.Equal(new Point(1920, 1040), m.MaxSize); // 尺寸不变
    }

    [Fact]
    public void Max_bottom_stacked_monitor_preserved_exactly()
    {
        // 上下堆叠：Y 为正的副屏
        var work = new Rectangle(0, 1080, 1920, 1040);
        var m = WindowGeometry.ComputeMaximizedMinMaxInfo(work);
        Assert.Equal(new Point(0, 1080), m.MaxPos);
        Assert.Equal(new Point(1920, 1040), m.MaxSize);
    }

    [Fact]
    public void Max_heterogeneous_dpi_workarea_uses_physical_pixels()
    {
        // 150% 缩放副屏：物理工作区 2560x1440（逻辑 1707x960）——喂物理像素，尺寸必须是 2560x1440
        var work = new Rectangle(1920, 0, 2560, 1440);
        var m = WindowGeometry.ComputeMaximizedMinMaxInfo(work);
        Assert.Equal(new Point(2560, 1440), m.MaxSize); // 若按逻辑像素 1707x960 则是回归
        Assert.Equal(new Point(1920, 0), m.MaxPos);
    }

    // ---------- HitTestResizeEdge（G4 边缘缩放 / G5 最大化禁用） ----------

    [Fact]
    public void HitTest_corners_precede_edges()
    {
        var r = new Rectangle(100, 100, 800, 600);
        const int e = 8;
        Assert.Equal(WindowGeometry.HTTOPLEFT, WindowGeometry.HitTestResizeEdge(new Point(100, 100), r, e));
        Assert.Equal(WindowGeometry.HTTOPRIGHT, WindowGeometry.HitTestResizeEdge(new Point(899, 100), r, e));
        Assert.Equal(WindowGeometry.HTBOTTOMLEFT, WindowGeometry.HitTestResizeEdge(new Point(100, 699), r, e));
        Assert.Equal(WindowGeometry.HTBOTTOMRIGHT, WindowGeometry.HitTestResizeEdge(new Point(899, 699), r, e));
    }

    [Fact]
    public void HitTest_edge_middles()
    {
        var r = new Rectangle(100, 100, 800, 600);
        const int e = 8;
        Assert.Equal(WindowGeometry.HTLEFT, WindowGeometry.HitTestResizeEdge(new Point(100, 400), r, e));
        Assert.Equal(WindowGeometry.HTRIGHT, WindowGeometry.HitTestResizeEdge(new Point(899, 400), r, e));
        Assert.Equal(WindowGeometry.HTTOP, WindowGeometry.HitTestResizeEdge(new Point(400, 100), r, e));
        Assert.Equal(WindowGeometry.HTBOTTOM, WindowGeometry.HitTestResizeEdge(new Point(400, 699), r, e));
    }

    [Fact]
    public void HitTest_interior_returns_null()
    {
        var r = new Rectangle(100, 100, 800, 600);
        Assert.Null(WindowGeometry.HitTestResizeEdge(new Point(500, 400), r, 8));
    }

    [Fact]
    public void HitTest_negative_secondary_screen_preserved()
    {
        // 左侧副屏负坐标：窗口矩形与点击点均为负 X，不抛异常、判定正确
        var r = new Rectangle(-1920, 0, 800, 600);
        const int e = 8;
        Assert.Equal(WindowGeometry.HTTOPLEFT, WindowGeometry.HitTestResizeEdge(new Point(-1920, 0), r, e));
        Assert.Equal(WindowGeometry.HTLEFT, WindowGeometry.HitTestResizeEdge(new Point(-1920, 300), r, e));
        Assert.Null(WindowGeometry.HitTestResizeEdge(new Point(-1600, 300), r, e));
    }

    [Fact]
    public void HitTest_maximized_returns_null_always()
    {
        // G5：最大化时边缘不出现缩放指针
        var r = new Rectangle(0, 0, 1920, 1040);
        Assert.Null(WindowGeometry.HitTestResizeEdge(new Point(0, 0), r, 8, maximized: true));
        Assert.Null(WindowGeometry.HitTestResizeEdge(new Point(1919, 0), r, 8, maximized: true));
        Assert.Null(WindowGeometry.HitTestResizeEdge(new Point(0, 1039), r, 8, maximized: true));
    }

    // ---------- LayoutChromeRects（G7 布局） ----------

    [Fact]
    public void Layout_title_height_scales_with_dpi()
    {
        Assert.Equal(32, WindowGeometry.LayoutChromeRects(new Size(1280, 800), 96).Title.Height);
        Assert.Equal(48, WindowGeometry.LayoutChromeRects(new Size(1280, 800), 144).Title.Height); // 150%
        Assert.Equal(64, WindowGeometry.LayoutChromeRects(new Size(1280, 800), 192).Title.Height); // 200%
    }

    [Fact]
    public void Layout_chrome_rects_full_size()
    {
        var (title, web) = WindowGeometry.LayoutChromeRects(new Size(1280, 800), 96);
        Assert.Equal(new Rectangle(1, 1, 1278, 32), title);   // 1px inset 四周
        Assert.Equal(new Rectangle(1, 33, 1278, 766), web);    // 剩余 800-1-32-1=766
    }

    [Fact]
    public void Layout_zero_and_negative_client_clamps_to_zero()
    {
        var (t0, w0) = WindowGeometry.LayoutChromeRects(new Size(0, 0), 96);
        Assert.True(t0.Width >= 0 && t0.Height >= 0 && w0.Width >= 0 && w0.Height >= 0);
        // 极小 client：titleH(32) 超过客户区高时，web 高度钳 0
        var (t1, w1) = WindowGeometry.LayoutChromeRects(new Size(50, 20), 96);
        Assert.Equal(0, w1.Height);
        Assert.True(t1.Width > 0);
    }

    // ---------- ShouldStartCaptionDrag（标题栏拖拽阈值 / 双击最大化可达性） ----------
    // 真机 T12 实测：单屏 96 DPI 下**双击标题栏不最大化**（P1 zoomed=False），而最大化键
    // （P2）正常。根因是 CustomTitleBar.OnMouseDown 无条件进系统 HTCAPTION 拖拽模态循环，
    // 第二次点击被循环吞掉，MouseDoubleClick 永不触发。修法=拖拽阈值：只有指针离开以按下点
    // 为中心的 DragSize 方框才接管拖拽；框内一律不动，双击才有机会走到 OnDoubleClick。

    [Theory]
    [InlineData(0, 0)]     [InlineData(1, 1)]     // 原地/微动（含双击）→ 不启动拖拽
    [InlineData(-2, 2)]                            // 阈值框内四向都不启动
    public void CaptionDrag_inside_threshold_box_does_not_start(int dx, int dy)
        => Assert.False(WindowGeometry.ShouldStartCaptionDrag(
            new Point(500, 300), new Point(500 + dx, 300 + dy), new Size(4, 4)));

    [Theory]
    [InlineData(5, 0)]     [InlineData(-5, 0)]    // 水平越界
    [InlineData(0, 5)]     [InlineData(0, -5)]    // 垂直越界（向上拖是真实用户动作）
    public void CaptionDrag_beyond_threshold_box_starts(int dx, int dy)
        => Assert.True(WindowGeometry.ShouldStartCaptionDrag(
            new Point(500, 300), new Point(500 + dx, 300 + dy), new Size(4, 4)));

    /// <summary>副屏在主屏左侧/上方时按下点是**负坐标**（实测 DISPLAY2 的 rcWork.Y=-193）：
    /// 阈值框必须照常成立，不能用绝对值或正数假设把负坐标当成越界。</summary>
    [Fact]
    public void CaptionDrag_negative_origin_secondary_monitor()
    {
        var down = new Point(-1400, -190);
        Assert.False(WindowGeometry.ShouldStartCaptionDrag(down, new Point(-1401, -189), new Size(4, 4)));
        Assert.True(WindowGeometry.ShouldStartCaptionDrag(down, new Point(-1390, -189), new Size(4, 4)));
    }

    /// <summary>DPI 越高阈值框越该越大（DragSize 是系统给的逻辑值，跨屏后按该屏 DPI 放大），
    /// 否则 200% 屏上"手抖 3px"就被判成拖拽，双击又会失效。</summary>
    [Fact]
    public void CaptionDrag_threshold_scales_with_dpi()
    {
        var box96 = new Size(4, 4);   // 半幅 2
        var box192 = new Size(8, 8);  // 半幅 4（200% 屏上系统给的 DragSize 也跟着放大）
        // 同一位移 3px：96 DPI 已越界，200% DPI 仍在框内
        Assert.True(WindowGeometry.ShouldStartCaptionDrag(new Point(0, 0), new Point(3, 0), box96));
        Assert.False(WindowGeometry.ShouldStartCaptionDrag(new Point(0, 0), new Point(3, 0), box192));
    }

    /// <summary>退化输入：阈值 <=0 时不得把"原地不动"也判成拖拽（那会原样复刻双击失效）。</summary>
    [Fact]
    public void CaptionDrag_zero_threshold_still_ignores_no_movement()
        => Assert.False(WindowGeometry.ShouldStartCaptionDrag(new Point(10, 10), new Point(10, 10), new Size(0, 0)));

    // ---------- RescaleWindowForDpi（跨倍率后窗口物理尺寸跟随，真机 T11 实测缺口） ----------
    // T11 实测：主窗从 96 DPI 主屏拖到 168 DPI 副屏后，物理尺寸仍是 1280x840（标题栏却从
    // 32 长到 56）——壳只在**启动时**按当时那屏的倍率算过一次 1280*scale，运行中跨屏就没人
    // 重算，于是可用内容区被静默压掉 43%。修法：DpiChanged 时按 new/old 等比放大，左上角
    // 尽量保持，越界则平移回目标屏 rcWork 内。

    /// <summary>本机实测拓扑：96→168（175%）副屏 rcWork=(1920,-193) 2560x1530。</summary>
    [Fact]
    public void Rescale_up_to_175_monitor_grows_and_stays_inside()
    {
        var work = new Rectangle(1920, -193, 2560, 1530);
        var got = WindowGeometry.RescaleWindowForDpi(new Rectangle(2560, 152, 1280, 840), 96, 168, work);
        Assert.Equal(2240, got.Width);   // 1280 * 168/96
        Assert.Equal(1470, got.Height);  // 840 * 168/96
        Assert.True(got.Right <= work.Right);
        Assert.True(got.Bottom <= work.Bottom);
        Assert.True(got.X >= work.Left && got.Y >= work.Top);
    }

    /// <summary>反向 168→96：等比缩回，且不得越过目标屏左上角（副屏 rcWork.Y 是负的）。</summary>
    [Fact]
    public void Rescale_down_to_100_monitor_shrinks_and_clamps()
    {
        var work = new Rectangle(68, 0, 1852, 1080);
        var got = WindowGeometry.RescaleWindowForDpi(new Rectangle(2240, -133, 2240, 1470), 168, 96, work);
        Assert.Equal(1280, got.Width);
        Assert.Equal(840, got.Height);
        Assert.True(got.X >= work.Left && got.Y >= work.Top);
        Assert.True(got.Right <= work.Right && got.Bottom <= work.Bottom);
    }

    [Fact]
    public void Rescale_same_dpi_is_identity()
        => Assert.Equal(new Rectangle(300, 200, 1280, 840),
            WindowGeometry.RescaleWindowForDpi(new Rectangle(300, 200, 1280, 840), 96, 96,
                new Rectangle(0, 0, 1920, 1080)));

    /// <summary>放大后比目标屏还大（小屏 4K 拖到 1080p 副屏）：钳到工作区尺寸，绝不留越界。</summary>
    [Fact]
    public void Rescale_clamps_to_smaller_target_monitor()
    {
        var work = new Rectangle(1920, 0, 1280, 720);
        var got = WindowGeometry.RescaleWindowForDpi(new Rectangle(0, 0, 1280, 840), 96, 192, work);
        Assert.Equal(1280, got.Width);
        Assert.Equal(720, got.Height);
        Assert.Equal(work, got);
    }

    /// <summary>DPI 数据退化（0/负）时**不得**把窗口缩成 0 或搬到未知位置：原样返回。</summary>
    [Theory]
    [InlineData(0, 168)] [InlineData(96, 0)] [InlineData(-1, 96)]
    public void Rescale_garbage_dpi_is_no_op(int oldDpi, int newDpi)
        => Assert.Equal(new Rectangle(10, 20, 800, 600),
            WindowGeometry.RescaleWindowForDpi(new Rectangle(10, 20, 800, 600), oldDpi, newDpi,
                new Rectangle(0, 0, 1920, 1080)));

    /// <summary>目标屏 rcWork 拿不到（Rectangle.Empty）时只做等比放大，不做钳位搬运——
    /// 用空矩形当"屏幕"会把窗口顶到 (0,0) 外，用户直接找不到窗口。</summary>
    [Fact]
    public void Rescale_empty_workarea_skips_clamp_but_still_grows()
    {
        var got = WindowGeometry.RescaleWindowForDpi(new Rectangle(100, 100, 1000, 800), 96, 192, Rectangle.Empty);
        Assert.Equal(2000, got.Width);
        Assert.Equal(1600, got.Height);
        Assert.Equal(100, got.X);
        Assert.Equal(100, got.Y);
    }
}
