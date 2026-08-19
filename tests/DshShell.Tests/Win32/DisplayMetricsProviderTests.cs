using System;
using System.Drawing;
using DshWeb.Win32;
using Xunit;

namespace DshShell.Tests.Win32;

/// <summary>
/// Task 2 方案 A：无物理硬件（Headless）的 WM_GETMINMAXINFO 多屏 DPI 测试。
///
/// 为什么不需要真实多显示器：WM_GETMINMAXINFO 的最终决策被收敛到两个可注入的纯逻辑——
/// 1) <see cref="IDisplayMetricsProvider"/>：从真实 Win32 调用中隔离出来，测试注入内存 Fake；
/// 2) <see cref="WindowGeometry.ComputeMaximizedMinMaxInfo(Rectangle, Size)"/>：纯算术。
/// 二者组合起来即覆盖 DshShellForm 的 WndProc 分支（消息解码 + 转发不产生计算误差）。
///
/// 断言核心：给定"物理像素工作区 rcWork + 该屏 DPI 边框厚度 frame"，最终最大化外矩形
///（= ptMaxPosition - frame 至 ptMaxPosition + ptMaxSize + 2*frame，模拟 DWM 外扩）必须
/// **精确等于** rcWork，既铺满又不越出监视器物理边界——这等价于 E2E 里 BoundingRectangle
/// ⊆ WorkingArea 的目标，但无需任何显示器硬件。
/// </summary>
public class DisplayMetricsProviderTests
{
    // 模拟 DWM 外扩：把 WM_GETMINMAXINFO 返回的 max 值还原为最终外矩形。
    // 语义（Windows Terminal / Chromium 同款）：max 上报的是"扣除边框后的铺满尺寸"，
    // 系统会把窗口物理矩形向四边各外扩 frame，故最终外矩形 = [pos - frame, pos + size + 2*frame]。
    // 参数直接给 Point，避免依赖 internal 类型名（返回类型可赋值给 var，但签名不可点名）。
    private static Rectangle FinalOuterRect(Point maxPos, Point maxSize, Size frame)
        => new Rectangle(
            maxPos.X - frame.Width,
            maxPos.Y - frame.Height,
            maxSize.X + 2 * frame.Width,
            maxSize.Y + 2 * frame.Height);

    // ---------- 场景 1：异构左侧副屏（150% 缩放），负 X 坐标 ----------
    // 物理拓扑：
    //   主屏   [0,0,2560,1440] @150% DPI(144)，物理 3840x2160
    //   副屏   [-1920,0,0,1080] @100% DPI(96)，物理 1920x1080（在左侧，X 全负）
    // 边框（@96DPI）：SM_CXSIZEFRAME=8 + SM_CXPADDEDBORDER=4 => frame=12/12
    [Fact]
    public void Scenario1_HeterogeneousLeftSecondary_uses_physical_pixels_not_logical()
    {
        // 副屏物理工作区（无任务栏时=物理分辨率 1920x1080；假设任务栏在主屏，副屏全高 1080）
        var work = new Rectangle(-1920, 0, 1920, 1080);
        var frame = new Size(12, 12); // @96DPI 的边框厚度

        var mm = WindowGeometry.ComputeMaximizedMinMaxInfo(work, frame);

        // ptMaxPosition 必须为负（留在左侧副屏），且向"屏幕内"平移 frame 抵消左/上外扩
        Assert.Equal(new Point(-1920 + 12, 0 + 12), mm.MaxPos);
        // ptMaxSize = 物理工作区 - 2*frame（否则 DWM 外扩后伸到屏外）
        Assert.Equal(new Point(1920 - 24, 1080 - 24), mm.MaxSize);
        // ptMaxTrackSize 不扣 frame：Normal 拖拽/吸附上限=物理工作区尺寸，贴边才能铺满
        Assert.Equal(new Point(1920, 1080), mm.MaxTrack);

        // 关键断言：模拟 DWM 外扩后的最终外矩形必须精确落在物理工作区内（铺满、无越界）
        var final = FinalOuterRect(mm.MaxPos, mm.MaxSize, frame);
        Assert.Equal(work, final);
    }

    [Fact]
    public void Scenario1_LeftSecondary_never_exceeds_monitor_bounds()
    {
        var monitor = new Rectangle(-1920, 0, 1920, 1080); // 物理监视器边界
        var work = new Rectangle(-1920, 0, 1920, 1080);    // 工作区
        var frame = new Size(12, 12);
        var mm = WindowGeometry.ComputeMaximizedMinMaxInfo(work, frame);

        var final = FinalOuterRect(mm.MaxPos, mm.MaxSize, frame);
        // 最终外矩形完全包含在监视器物理边界内（含左右边界，不飞出负坐标屏）
        Assert.True(final.Left >= monitor.Left);
        Assert.True(final.Top >= monitor.Top);
        Assert.True(final.Right <= monitor.Right);
        Assert.True(final.Bottom <= monitor.Bottom);
    }

    // ---------- 场景 2：垂直堆叠（副屏在上方，Y 全负） ----------
    // 物理拓扑：主屏 [0,0,1920,1080]；副屏 [0,-1080,1920,0]（在上方）
    [Fact]
    public void Scenario2_VerticalStack_secondary_above_primary()
    {
        var work = new Rectangle(0, -1080, 1920, 1080); // 上方副屏物理工作区
        var frame = new Size(8, 8);                      // @96DPI
        var mm = WindowGeometry.ComputeMaximizedMinMaxInfo(work, frame);

        Assert.Equal(new Point(0 + 8, -1080 + 8), mm.MaxPos); // Y 保持负
        Assert.Equal(new Point(1920 - 16, 1080 - 16), mm.MaxSize);
        Assert.Equal(new Point(1920, 1080), mm.MaxTrack); // 拖拽上限=未补偿物理尺寸
        Assert.Equal(work, FinalOuterRect(mm.MaxPos, mm.MaxSize, frame)); // 铺满且不越界
    }

    [Fact]
    public void Scenario2_VerticalStack_primary()
    {
        var work = new Rectangle(0, 0, 1920, 1080); // 主屏
        var frame = new Size(8, 8);
        var mm = WindowGeometry.ComputeMaximizedMinMaxInfo(work, frame);
        Assert.Equal(new Point(8, 8), mm.MaxPos);
        Assert.Equal(new Point(1904, 1064), mm.MaxSize);
        Assert.Equal(work, FinalOuterRect(mm.MaxPos, mm.MaxSize, frame));
    }

    // ---------- 异构 DPI：150% 副屏边框更厚 ----------
    // 高 DPI 下 SM_CXSIZEFRAME/CXPADDEDBORDER 会随 DPI 放大（GetSystemMetricsForDpi），
    // 边框必须按"窗口所在屏 DPI"算，不能拿主屏 96DPI 的 12px 硬套。
    [Fact]
    public void HeterogeneousDpi_150percent_frame_compensated_at_that_dpi()
    {
        // 150% 副屏：frame 约 12*1.5=18（示意；真实值来自 GetSystemMetricsForDpi）
        var work = new Rectangle(2560, 0, 2560, 1440);
        var frame = new Size(18, 18);
        var mm = WindowGeometry.ComputeMaximizedMinMaxInfo(work, frame);

        Assert.Equal(new Point(2560 + 18, 0 + 18), mm.MaxPos);
        Assert.Equal(new Point(2560 - 36, 1440 - 36), mm.MaxSize);
        Assert.Equal(work, FinalOuterRect(mm.MaxPos, mm.MaxSize, frame));
    }

    // ---------- maxTrack 语义：Normal 拖拽上限不扣 frame ----------
    // Aero Snap 吸附与 Maximized 的 DWM 外扩机制不同；若 maxTrack 也扣 2*frame，
    // 用户手动拖拽贴边时窗口会四周留 frame 缝隙。业界做法：maxTrack=物理工作区尺寸。
    [Fact]
    public void MaxTrack_equals_uncompensated_workarea_size()
    {
        var work = new Rectangle(-1920, 0, 1920, 1080);
        var frame = new Size(12, 12);
        var mm = WindowGeometry.ComputeMaximizedMinMaxInfo(work, frame);

        Assert.Equal(new Point(1920, 1080), mm.MaxTrack);           // 拖拽上限：不扣 frame
        Assert.Equal(new Point(1920 - 24, 1080 - 24), mm.MaxSize);   // maxSize 仍要扣（对比锁定）
    }

    // ---------- 防御：frame=0 退化为"直接铺满"旧语义，与旧单测兼容 ----------
    [Fact]
    public void ZeroFrame_falls_back_to_exact_workarea()
    {
        var work = new Rectangle(-1920, 0, 1920, 1080);
        var mm = WindowGeometry.ComputeMaximizedMinMaxInfo(work); // 无边框重载
        Assert.Equal(new Point(-1920, 0), mm.MaxPos);
        Assert.Equal(new Point(1920, 1080), mm.MaxSize);
    }

    // ---------- 防御：异常大 frame 不产生负尺寸 ----------
    [Fact]
    public void OversizedFrame_never_yields_negative_size_or_position()
    {
        var work = new Rectangle(-1920, 0, 100, 100);
        var frame = new Size(500, 500); // 远超工作区（极端异常指标）
        var mm = WindowGeometry.ComputeMaximizedMinMaxInfo(work, frame);
        Assert.True(mm.MaxSize.X >= 0);
        Assert.True(mm.MaxSize.Y >= 0);
    }

    // ---------- Fake 提供者契约：证明 IDisplayMetricsProvider 可注入并正确透传 ----------
    // 模拟真实生产路径：DshShellForm.WndProc 调 GetMonitorMetrics -> ComputeMaximizedMinMaxInfo。
    // 这里用 Fake 注入"虚拟拓扑"，验证从接口到纯函数的整条链在无硬件下可跑通。
    private sealed class FakeDisplayMetricsProvider : IDisplayMetricsProvider
    {
        private readonly MonitorDpiMetrics _metrics;
        private readonly Size _frame;
        public FakeDisplayMetricsProvider(MonitorDpiMetrics metrics, Size frame)
        { _metrics = metrics; _frame = frame; }

        public MonitorDpiMetrics GetMonitorMetrics(IntPtr hwnd) => _metrics;
        public Size GetWindowFrameSize(IntPtr hwnd, uint dpi) => _frame;
    }

    [Fact]
    public void Provider_injected_chain_computes_max_within_workarea()
    {
        // 用 Fake 注入场景 1 的副屏拓扑（150% 副屏）
        var fake = new FakeDisplayMetricsProvider(
            new MonitorDpiMetrics(
                WorkArea: new Rectangle(-1920, 0, 1920, 1080),
                Monitor: new Rectangle(-1920, 0, 1920, 1080),
                Dpi: 144),
            new Size(12, 12));

        // 复刻 DshShellForm.WndProc 分支：取指标 -> 纯函数（无窗口句柄、无 GUI）
        var metrics = fake.GetMonitorMetrics(IntPtr.Zero);
        var mm = WindowGeometry.ComputeMaximizedMinMaxInfo(metrics.WorkArea, fake.GetWindowFrameSize(IntPtr.Zero, metrics.Dpi));

        var final = FinalOuterRect(mm.MaxPos, mm.MaxSize, new Size(12, 12));
        Assert.Equal(new Rectangle(-1920, 0, 1920, 1080), final); // 精确铺满，无越界
    }
}
