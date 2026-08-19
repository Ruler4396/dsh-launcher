using System.Drawing;

namespace DshWeb.Win32;

/// <summary>
/// 一次取齐"当前监视器"（窗口所在）的物理像素几何 + DPI 指标，供 WM_GETMINMAXINFO 使用。
/// 统一为**物理像素**（PerMonitorV2 下 WM_GETMINMAXINFO 的 MINMAXINFO 严格要求物理像素，
/// 与 Screen.WorkingArea 的逻辑像素不可混用——逻辑/物理错位正是"最大化丢窗"的根因）。
///
/// 为什么是 public 顶层类型：它是 <see cref="IDisplayMetricsProvider"/> 的返回类型，而该
/// 接口须对外可见（供 CI/Headless 测试注入 Fake）。嵌套在 internal 的 NativeMethods 内会
/// 使有效可访问性降为 internal，导致"返回类型可访问性低于方法"的 CS0050 编译错误。
/// </summary>
public readonly record struct MonitorDpiMetrics(
    Rectangle WorkArea,   // rcWork，物理像素工作区（最大化铺满目标）
    Rectangle Monitor,    // rcMonitor，物理像素监视器边界（窗口不得越出）
    uint Dpi);            // 窗口所在监视器 DPI（如 144=150% 缩放）
