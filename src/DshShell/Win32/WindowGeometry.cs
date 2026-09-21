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
    /// 为窗口所在监视器 DPI 下的边框厚度（物理像素）。
    ///
    /// 为什么必须是物理像素：PerMonitorV2 下 <see cref="Screen.FromHandle"/>.WorkingArea 返回
    /// **逻辑像素**，在 150% 缩放副屏上会把工作区算小，导致最大化"铺不满"或"丢窗"（多屏
    /// 血泪：窗口最大化到错误监视器边界）。用 MonitorFromWindow 取最近监视器 + GetMonitorInfo
    /// 拿 rcWork（物理像素），再喂给本函数，消除该陷阱。
    ///
    /// frame 补偿语义（v0.4.2 修正）：
    /// 本函数支持两种调用形态：
    /// - **无 frame（<see cref="ComputeMaximizedMinMaxInfo(Rectangle)"/>，本应用采用）**：
    ///   直接给 rcWork，0px 精确铺满。适用于**去掉了 WS_CAPTION** 的窗口——Windows 对这类
    ///   窗口最大化时**不再向外扩 frame**（ADR-001 已注释并获 CI 实测：e2e-geo 在 e3f2d8d
    ///   引入带 frame 补偿后，最大化矩形从 (0,0,work) 变成 (8,8,work-16) 留 8px 缝隙）。
    /// - **带 frame（本重载）**：补偿"DWM 外扩"——假定最大化时窗口物理矩形向四边各外扩
    ///   frame（ptMaxPos - frame 至 ptMaxPos + ptMaxSize + 2*frame），令其恰等于 rcWork →
    ///   ptMaxPosition = rcWork 左上角 + frame，ptMaxSize = rcWork - 2*frame。仅适用于
    ///   **保留 WS_CAPTION** 的窗口（Windows Terminal / Chromium 自绘标题栏类）。
    ///
    /// ptMaxTrackSize 恒为未补偿物理工作区尺寸：它决定 Normal 状态下用户拖拽窗口边缘
    /// （含 Aero Snap 贴边吸附）能到达的最大尺寸，而 DWM 对 Normal 拖拽/吸附不产生
    /// Maximized 式外扩。若 maxTrack 扣 frame，用户拖到贴边时窗口会比工作区小一圈——
    /// 业界惯例（Windows Terminal /Chromium）是 maxTrack 直接用未补偿的物理工作区尺寸。
    ///
    /// <paramref name="frame"/> 为 0 时本重载退化为无 frame 语义（直接给 rcWork）。
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
        // [审查 N5] 裸 `* dpi / 96.0` 不收 DpiScale 时，dpi≤0（坏驱动/RDP 现场）会把标题栏塌成
        // 0px、WebView 顶到窗口沿——正是台账第 8 条立"≤0 当 96 + 钳制"规则要防的形状。
        var titleH = ShellLogic.DpiScale.Px(32, ShellLogic.DpiScale.Of(dpi));
        var title = new Rectangle(inset, inset,
            Math.Max(0, client.Width - 2 * inset),
            titleH);
        var web = new Rectangle(inset, inset + titleH,
            Math.Max(0, client.Width - 2 * inset),
            Math.Max(0, client.Height - inset - titleH - inset));
        return (title, web);
    }

    /// <summary>
    /// 标题栏拖拽阈值判定（真机 T12 双击最大化失效的修复点）。
    /// 入参：按下点、当前指针点、以按下点为中心的阈值方框（调用方传 SystemInformation.DragSize）。
    /// 返回：是否应当把这次按下交给系统 HTCAPTION 拖拽循环。
    ///
    /// 为什么需要它：自绘标题栏若在 MouseDown 里**无条件** ReleaseCapture + SendMessage
    /// (WM_NCLBUTTONDOWN, HTCAPTION)，就立刻进系统模态拖拽循环——第二次点击被循环吞掉，
    /// WM_LBUTTONDBLCLK 永不产生，<c>MouseDoubleClick</c> 处理器形同虚设，用户双击标题栏
    /// 无法最大化（T12 实测：单屏 96 DPI 同样复现，与 DPI 无关）。加了阈值后，原地点击
    /// 不接管，双击可达；真的拖动时指针必然越框，拖拽照旧。
    /// 阈值框以按下点为中心**四向对称**（半幅 = dragBox/2），负坐标副屏照常成立。
    /// </summary>
    public static bool ShouldStartCaptionDrag(Point downAt, Point nowAt, Size dragBox)
    {
        // dragBox <=0 时退化成"必须真的移动才算拖动"，绝不反过来把原地不动判成拖拽
        var halfW = Math.Max(0, dragBox.Width) / 2;
        var halfH = Math.Max(0, dragBox.Height) / 2;
        return Math.Abs(nowAt.X - downAt.X) > halfW || Math.Abs(nowAt.Y - downAt.Y) > halfH;
    }

    /// <summary>
    /// 手工布局窗口的**最小尺寸**（设计 800×600 @96dpi → 物理像素）。
    /// 为什么必须随 DPI 折算：这些窗体是 FormBorderStyle.None + 手工布局，WinForms 不会替我们
    /// 缩放 <c>MinimumSize</c>——写死 800×600 在 200% 屏上等于允许把窗口缩到设计值的一半，
    /// 标题栏按钮与页面主区直接挤没。dpi ≤ 0 按 96 处理，缩放夹 [0.5, 8]（与其余版式函数同纪律）。
    /// </summary>
    public static Size MinimumWindowSize(int dpi)
    {
        var s = ShellLogic.DpiScale.Of(dpi);
        return new Size((int)Math.Round(800 * s), (int)Math.Round(600 * s));
    }

    /// <summary>自绘标题栏正文字号（设计 9pt @96dpi = 12px）折算成**物理像素**。
    /// 渲染侧必须用 <c>GraphicsUnit.Pixel</c> 消费：Point 单位会被绘制 DC 的 DPI 再折算一次，
    /// 与这里已经乘过的系数叠成 s²（issue #28-3 的根因）。</summary>
    public static int TitleEmPx(int dpi) => EmPx(9.0, dpi);

    /// <summary>设计字号（point）→ 该 DPI 下的物理像素字号；全仓唯一一处 point→px 折算入口。
    /// 各处自绘窗口此前各自写 <c>new Font(family, 8F)</c> 并把几何按 <c>_scale</c> 乘，
    /// 于是"字号被 DC 折算第二次"与"字号没跟着 DPI 长"两种错法都可能发生。</summary>
    public static int EmPx(double designPoint, int dpi)
    {
        var s = ShellLogic.DpiScale.Of(dpi);
        return Math.Max(1, (int)Math.Round(designPoint * 96.0 / 72.0 * s));
    }

    /// <summary>
    /// 跨倍率后窗口该落到的物理矩形：按 newDpi/oldDpi 等比缩放，尽量保持左上角，
    /// 越界则平移回目标屏 <paramref name="targetWork"/> 内。
    ///
    /// 为什么需要它（真机 T11 实测缺口）：主窗从 96 DPI 主屏拖到 168 DPI 副屏后物理尺寸
    /// 仍是 1280x840，而标题栏已经长到 56px——壳只在**启动时**按当时那屏的倍率算过一次
    /// 1280*scale，运行中跨屏（或用户改显示倍率）没人重算，于是页面可用区被静默压掉 43%，
    /// 高倍率屏上"窗口越用越小"。PerMonitorV2 下 WinForms 也不会替手工布局的窗体改尺寸。
    /// 纪律：DPI 数据退化（≤0）一律原样返回；目标屏工作区拿不到（Empty）只放大不搬运，
    /// 绝不把窗口顶到用户找不到的位置。
    /// </summary>
    public static Rectangle RescaleWindowForDpi(Rectangle current, int oldDpi, int newDpi,
        Rectangle targetWork)
    {
        if (oldDpi <= 0 || newDpi <= 0) return current;
        if (oldDpi == newDpi && !targetWork.IsEmpty)
            return ClampIntoWork(current, targetWork);

        var ratio = (double)newDpi / oldDpi;
        var w = Math.Max(1, (int)Math.Round(current.Width * ratio));
        var h = Math.Max(1, (int)Math.Round(current.Height * ratio));
        var moved = new Rectangle(current.X, current.Y, w, h);
        return targetWork.IsEmpty ? moved : ClampIntoWork(moved, targetWork);
    }

    /// <summary>把矩形塞进 <paramref name="work"/>：尺寸先钳到屏大小，再平移回屏内
    /// （先夹右下再夹左上，保证宽高不越界时位置一定合法）。</summary>
    private static Rectangle ClampIntoWork(Rectangle r, Rectangle work)
    {
        var w = Math.Min(r.Width, work.Width);
        var h = Math.Min(r.Height, work.Height);
        var x = Math.Min(r.X, work.Right - w);
        var y = Math.Min(r.Y, work.Bottom - h);
        x = Math.Max(x, work.Left);
        y = Math.Max(y, work.Top);
        return new Rectangle(x, y, w, h);
    }
}
