using System.Drawing;

namespace DshWeb.Win32;

/// <summary>
/// DshShellForm 窗口几何决策的**纯函数**集合（Step 1，Task 0 行为矩阵 G 类）。
/// 设计约束（铁律 3：Form 留薄壳、逻辑下沉纯函数）：WndProc/OnResize 等 override 只能留在
/// Form 子类上做"消息解码 + 转发"，所有决策计算必须落在这里的可单测纯函数中。
/// 这些函数**不依赖任何 Win32 API / 窗口句柄**，入参由调用方（Form/WndProc 适配器）提供，
/// 因此可在无 GUI 的 xUnit 测试中直接覆盖（含负坐标副屏、异构 DPI 等边界）。
/// </summary>
public static class WindowGeometry
{
    // ---- 命中测试 HT 常量（与 Win32 WM_NCHITTEST 返回值一致，见矩阵 G5） ----
    public const int HTCLIENT = 0x0001;
    public const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    /// <summary>WM_GETMINMAXINFO 决策结果（物理像素）。</summary>
    public readonly record struct MaxMinInfo(Point MaxSize, Point MaxPos, Point MaxTrack);

    /// <summary>
    /// 最大化铺满决策（矩阵 G1/G10）。
    /// 入参 <paramref name="physicalWork"/> 必须是**物理像素**工作区（由适配器用
    /// MonitorFromWindow + GetMonitorInfo(rcWork) 取得）。
    ///
    /// 为什么必须是物理像素：PerMonitorV2 下 <see cref="Screen.FromHandle"/>.WorkingArea 返回
    /// **逻辑像素**，在 150% 缩放副屏上会把工作区算小，导致最大化"铺不满"或"丢窗"（多屏
    /// 血泪：窗口最大化到错误监视器边界）。用 MonitorFromWindow 取最近监视器 + GetMonitorInfo
    /// 拿 rcWork（物理像素），再喂给本函数，消除该陷阱。
    ///
    /// 语义（保持 0.3.5 现状，不做边框补偿）：去 WS_CAPTION 后系统最大化不再外扩，
    /// maxSize = maxTrack = rcWork 尺寸，maxPos = rcWork 左上角 → 0px 精确铺满、不遮任务栏。
    /// </summary>
    public static MaxMinInfo ComputeMaximizedMinMaxInfo(Rectangle physicalWork)
    {
        var size = new Point(physicalWork.Width, physicalWork.Height);
        var pos = new Point(physicalWork.X, physicalWork.Y);
        return new MaxMinInfo(size, pos, size); // maxTrack == maxSize
    }

    /// <summary>
    /// 边缘缩放命中判定（矩阵 G4/G5）。
    /// 入参：屏幕坐标 <paramref name="screenPt"/>（已由 ShellLogic.SplitLParam 拆出 64 位坐标，
    /// 左侧/上方副屏为负坐标）、窗口屏幕矩形 <paramref name="windowRect"/>、边缘宽度
    /// <paramref name="edge"/>（默认 8px）。
    /// 返回：命中区域 HT 代码；未命中返回 null。
    ///
    /// 复刻现 WM_NCHITTEST 判定（含四角优先）。<paramref name="maximized"/> 为 true（最大化）
    /// 时**必须返回 null**——最大化窗口无缩放语义，出现缩放指针是回归（矩阵 G5）。
    /// </summary>
    public static int? HitTestResizeEdge(Point screenPt, Rectangle windowRect, int edge, bool maximized = false)
    {
        if (maximized) return null; // G5：最大化时边缘不出现缩放指针
        var left = screenPt.X < windowRect.Left + edge;
        var right = screenPt.X > windowRect.Right - edge;
        var top = screenPt.Y < windowRect.Top + edge;
        var bottom = screenPt.Y > windowRect.Bottom - edge;
        if (left && top) return HTTOPLEFT;
        if (right && top) return HTTOPRIGHT;
        if (left && bottom) return HTBOTTOMLEFT;
        if (right && bottom) return HTBOTTOMRIGHT;
        if (left) return HTLEFT;
        if (right) return HTRIGHT;
        if (top) return HTTOP;
        if (bottom) return HTBOTTOM;
        return null;
    }

    /// <summary>
    /// 自绘标题栏与 WebView2 客户区布局（矩阵 G7）。
    /// 1px 边框内缩（inset=1）：标题栏占顶部，WebView2 占其余；高度 ≈ 32×DPI/96。
    /// 负值/零尺寸钳制到 0（Math.Max），防止 client 极小或 DPI 异常时出负尺寸矩形。
    /// </summary>
    public static (Rectangle Title, Rectangle Web) LayoutChromeRects(Size client, int dpi)
    {
        const int inset = 1;
        var titleH = (int)Math.Round(32.0 * dpi / 96.0);
        var title = new Rectangle(inset, inset,
            Math.Max(0, client.Width - 2 * inset),
            titleH);
        var web = new Rectangle(inset, inset + titleH,
            Math.Max(0, client.Width - 2 * inset),
            Math.Max(0, client.Height - inset - titleH - inset));
        return (title, web);
    }
}
