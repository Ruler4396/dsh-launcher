using System.Drawing;
using System.Runtime.InteropServices;

namespace DshWeb.Win32;

/// <summary>
/// 按物理坐标反查**该监视器的物理像素工作区**（rcWork，已扣除任务栏）。
///
/// 为什么需要它：本仓库的坐标空间约定是"几何决策一律物理像素"（见 WindowGeometry /
/// DisplayMetricsProvider 的注释与 G1/G10 修复），而 WinForms 的 <c>Screen.WorkingArea</c>
/// 在 PerMonitorV2 下是**逻辑像素**（96 DPI 基准）。托盘菜单、通知卡片这类"手工 Location 的
/// 自绘窗口"若拿逻辑工作区去钳物理坐标，缩放屏上就会离图标越来越远、甚至压到任务栏下。
///
/// 失败（句柄为空/调用异常）返回 <see cref="Rectangle.Empty"/>，由调用方决定回退策略并留痕
/// ——绝不返回一个"看起来能用"的假矩形。
/// </summary>
internal static class MonitorWorkArea
{
    private const uint MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);

    /// <summary>点所在监视器的物理工作区；取不到返回 Rectangle.Empty。</summary>
    internal static Rectangle ForPoint(Point pt)
    {
        try
        {
            var hMonitor = MonitorFromPoint(new NativePoint { X = pt.X, Y = pt.Y }, MonitorDefaultToNearest);
            if (hMonitor == IntPtr.Zero)
            {
                Logger.Warn($"MonitorWorkArea.ForPoint: no monitor at {pt.X},{pt.Y}");
                return Rectangle.Empty;
            }
            var mi = new NativeMethods.MONITORINFO
            {
                cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };
            if (!NativeMethods.GetMonitorInfo(hMonitor, ref mi))
            {
                Logger.Warn($"MonitorWorkArea.ForPoint: GetMonitorInfo failed at {pt.X},{pt.Y}");
                return Rectangle.Empty;
            }
            return new Rectangle(mi.rcWork.Left, mi.rcWork.Top,
                mi.rcWork.Right - mi.rcWork.Left, mi.rcWork.Bottom - mi.rcWork.Top);
        }
        catch (Exception ex)
        {
            Logger.Warn("MonitorWorkArea.ForPoint threw: " + ex.Message);
            return Rectangle.Empty;
        }
    }
}
