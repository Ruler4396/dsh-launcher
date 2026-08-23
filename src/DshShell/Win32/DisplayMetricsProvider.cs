using System;
using System.Drawing;

namespace DshWeb.Win32;

/// <summary>
/// 显示器几何/DPI 指标的抽象（多屏 DPI 修复，矩阵 G1/G10，Task 0 无头测试核心）。
///
/// 设计动机（为什么必须抽象）：
/// 1. **物理 vs 逻辑像素陷阱**：PerMonitorV2 下 WM_GETMINMAXINFO 的 MINMAXINFO 严格要求
///    **物理像素**，而 .NET 的 <see cref="System.Windows.Forms.Screen"/>.WorkingArea 返回
///    **逻辑像素**（96 DPI 基准）。异构 DPI 多屏（尤其负坐标左侧副屏）下，直接把逻辑工作区
///    塞进 MINMAXINFO，会让系统把窗口最大化到一个不存在的物理坐标上 → “窗口飞出屏幕/丢失”。
///    因此必须用原生 MonitorFromWindow + GetMonitorInfo 拿物理像素工作区（rcWork）。
/// 2. **可测试性**：CI（GitHub Actions windows-latest）与本地开发机通常**没有多显示器**，
///    也没有 Windows 11 25H2 异构 DPI 硬件。把这三个 Win32 调用收进接口后，Headless 单测
///    可注入任意“虚拟拓扑”（负坐标副屏、垂直堆叠、150% 缩放等），在无物理硬件下覆盖
///    WM_GETMINMAXINFO 的全部 DPI/坐标边界（见 DisplayMetricsProviderTests）。
///
/// 生产实现 <see cref="Win32DisplayMetricsProvider"/> 逐调用透传真实 Win32 API；
/// 测试实现是内存中的 Fake（见 tests），二者接口相同、零分支替换。
/// </summary>
public interface IDisplayMetricsProvider
{
    /// <summary>
    /// 返回窗口 <paramref name="hwnd"/> 当前所在监视器的物理像素指标（工作区 + 监视器边界 + DPI）。
    /// 若调用失败（句柄无效/系统异常）抛出 <see cref="InvalidOperationException"/>，由
    /// 调用方（WndProc 适配器）兜底回退，绝不让 MAX/MIN 决策用脏数据。
    /// </summary>
    MonitorDpiMetrics GetMonitorMetrics(IntPtr hwnd);

    /// <summary>
    /// 返回窗口所在监视器 DPI 下的系统边框厚度（物理像素，单位 px）。
    /// 由 <see cref="GetSystemMetricsForDpi"/>（nIndex=SM_CXSIZEFRAME/CYSIZEFRAME/
    /// CXPADDEDBORDER）求和得出：水平 = CXSIZEFRAME + CXPADDEDBORDER，
    /// 垂直 = CYSIZEFRAME + CXPADDEDBORDER。用于补偿 WS_THICKFRAME 的最大化外扩。
    /// </summary>
    Size GetWindowFrameSize(IntPtr hwnd, uint dpi);
}

/// <summary>
/// <see cref="IDisplayMetricsProvider"/> 的生产环境实现：逐调用透传原生 Win32 API。
/// 无状态、可复用；不持有窗口句柄生命周期。所有返回均为**物理像素**。
/// </summary>
public sealed class Win32DisplayMetricsProvider : IDisplayMetricsProvider
{
    public MonitorDpiMetrics GetMonitorMetrics(IntPtr hwnd)
    {
        // MonitorFromWindow(MONITOR_DEFAULTTONEAREST)：窗口落到哪块屏就算哪块屏。
        // 这与 Screen.FromHandle 的“最近监视器”语义一致，但返回值是物理像素（无 DPI 折算）。
        var hmon = NativeMethods.MonitorFromWindow(hwnd, Win32Constants.MONITOR_DEFAULTTONEAREST);
        if (hmon == IntPtr.Zero)
            throw new InvalidOperationException("MonitorFromWindow 返回空监视器句柄（窗口可能已销毁）。");

        var mi = new NativeMethods.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(hmon, ref mi))
            throw new InvalidOperationException($"GetMonitorInfo 失败，hMonitor=0x{hmon.ToInt64():X}。");

        // rcWork / rcMonitor 均为物理像素（GetMonitorInfo 契约）。换算成 Rectangle 供纯函数消费。
        var work = new Rectangle(mi.rcWork.Left, mi.rcWork.Top,
            mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
        var mon = new Rectangle(mi.rcMonitor.Left, mi.rcMonitor.Top,
            mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top);

        // GetDpiForWindow：PerMonitorV2 下返回窗口所在监视器的 DPI（随窗口跨屏移动实时变化）。
        // 不能拿全局/主屏 DPI，否则异构缩放副屏的边框厚度会算错。
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        return new MonitorDpiMetrics(work, mon, dpi);
    }

    public Size GetWindowFrameSize(IntPtr hwnd, uint dpi)
    {
        // GetSystemMetricsForDpi：按“窗口所在监视器的 DPI”取物理像素边框厚度。
        // 这是相对旧代码（GetSystemMetrics，全局主屏 DPI）的关键修正——异构 DPI 副屏上
        // 边框厚度必须按该屏 DPI 计算，否则最大化补偿量偏小/偏大，导致边界越界。
        int frameW = NativeMethods.GetSystemMetricsForDpi(Win32Constants.SM_CXSIZEFRAME, dpi);
        int frameH = NativeMethods.GetSystemMetricsForDpi(Win32Constants.SM_CYSIZEFRAME, dpi);
        int padded = NativeMethods.GetSystemMetricsForDpi(Win32Constants.SM_CXPADDEDBORDER, dpi);
        // 每边外扩量 = 边框 + 额外内边距；水平/垂直各算一遍（DWM 最大化在四边都外扩）。
        return new Size(frameW + padded, frameH + padded);
    }
}
