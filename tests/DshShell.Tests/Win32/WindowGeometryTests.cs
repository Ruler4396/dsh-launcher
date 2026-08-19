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
}
