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
    /// MonitorFromWindow + GetMonitorInfo(rcWork) 取得），<paramref name="frame"/>
    /// 为窗口所在监视器 DPI 下的边框厚度（物理像素），用于补偿 WS_THICKFRAME 的 DWM 外扩。
    ///
    /// 为什么必须是物理像素：PerMonitorV2 下 <see cref="Screen.FromHandle"/>.WorkingArea 返回
    /// **逻辑像素**，在 150% 缩放副屏上会把工作区算小，导致最大化"铺不满"或"丢窗"（多屏
    /// 血泪：窗口最大化到错误监视器边界）。用 MonitorFromWindow 取最近监视器 + GetMonitorInfo
    /// 拿 rcWork（物理像素），再喂给本函数，消除该陷阱。
    ///
    /// 为什么需要边框补偿（本修复的核心）：
    /// 窗口保留 WS_THICKFRAME（Aero Snap/边缘缩放依赖，见 WindowChromeController.ApplyWindowStyle），
    /// 去 WS_CAPTION 后系统不会为原生标题栏预留空间，但 **DWM 在最大化时仍会把窗口物理矩形
    /// 向四周外扩 frame 像素**（左/上各 -frame，右/下各 +frame，即四边都外扩）。若 WM_GETMINMAXINFO
    /// 直接把 rcWork 设为 ptMaxSize/ptMaxPosition，最终窗口矩形会比工作区大 frame 像素，
    /// 落在任务栏上 / 伸出屏幕外——这正是多屏（尤其左侧负坐标副屏）下"窗口飞出屏幕"的残留根因。
    ///
    /// 补偿做法（与 Windows Terminal / Chromium 自绘标题栏同款）：
    ///   最终外矩形 = [ptMaxPos - frame, ptMaxPos + ptMaxSize + 2*frame]
    /// 令其恰等于工作区 → ptMaxPosition = rcWork 左上角 + frame，
    ///                      ptMaxSize     = rcWork 尺寸 - 2*frame（每边各收一个 frame）。
    /// frame 用 GetSystemMetricsForDpi(SM_CXSIZEFRAME/CYSIZEFRAME/CXPADDEDBORDER) 按
    /// "窗口所在监视器 DPI" 取，异构缩放副屏才不会算错（取全局 DPI 会偏大/偏小）。
    ///
    /// ptMaxTrackSize 是唯一**不扣 frame** 的字段：它决定 Normal 状态下用户拖拽窗口边缘
    /// （含 Aero Snap 贴边吸附）能到达的最大尺寸，而 DWM 对 Normal 拖拽/吸附**不会**像
    /// Maximized 那样额外向四周外扩 frame。若 maxTrack 也按 maxSize 扣 2*frame，用户手动
    /// 拖到贴边时窗口会比工作区小一圈（四周留 frame 缝隙）——业界惯例（Windows Terminal
    /// /Chromium）是 maxTrack 直接用未补偿的物理工作区尺寸。
    ///
    /// <paramref name="frame"/> 为 0 时退化为旧语义（直接给 rcWork，0px 铺满、不遮任务栏），
    /// 供无需外扩补偿的调用方/旧单测保持兼容。
    /// </summary>
    public static MaxMinInfo ComputeMaximizedMinMaxInfo(Rectangle physicalWork, Size frame)
    {
        // 补偿量钳制到非负：极端 DPI/异常指标下绝不产生负尺寸窗口（防御）。
        var fx = Math.Max(0, frame.Width);
        var fy = Math.Max(0, frame.Height);
        var width = Math.Max(0, physicalWork.Width - 2 * fx);
        var height = Math.Max(0, physicalWork.Height - 2 * fy);

        // 左上角向"屏幕内"（工作区右下方向）平移 frame，抵消 DWM 向左上外扩的 frame。
        var pos = new Point(physicalWork.X + fx, physicalWork.Y + fy);
        var size = new Point(width, height);

        // ptMaxTrackSize 不扣 frame：Normal 拖拽/吸附不触发 DWM 最大化外扩，若扣 2*frame，
        // 用户贴边拖拽时窗口会比工作区小一圈（四周留 frame 缝隙）。直接用物理工作区尺寸
        //（业界 Windows Terminal / Chromium 同款）。钳制到非负仅作防御。
        var track = new Point(
            Math.Max(0, physicalWork.Width),
            Math.Max(0, physicalWork.Height));

        return new MaxMinInfo(size, pos, track);
    }

    /// <summary>无边框补偿的兼容重载（等价于 <see cref="ComputeMaximizedMinMaxInfo(Rectangle, Size)"/>
    /// 传 Size.Empty）——旧单测/无需外扩补偿的场景保持可用。</summary>
    public static MaxMinInfo ComputeMaximizedMinMaxInfo(Rectangle physicalWork)
        => ComputeMaximizedMinMaxInfo(physicalWork, Size.Empty);

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
