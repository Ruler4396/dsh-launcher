using System.Drawing;
using System.Runtime.InteropServices;

namespace DshWeb.Win32;

/// <summary>
/// 按物理屏幕坐标反查**该显示器**的有效 DPI。
///
/// 为什么需要它（issue #28-3）：托盘菜单是手工布局 + 手工绘制的自绘窗口，WinForms 不会替它缩放。
/// 旧实现在构造函数里用 <c>CreateGraphics()</c> 采样 DPI，而那一刻窗口 <c>Location</c> 还是 (0,0)
/// → 永远取到主屏缩放；<c>WindowManager.ShowTrayMenu</c> 之后才把菜单定位到光标所在屏。
/// 混屏（主屏 200% + 副屏 100%）下副屏上的菜单尺寸因此整体错一档。
///
/// 回退链：shcore.GetDpiForMonitor(MDT_EFFECTIVE) → user32.GetDpiForSystem → 96。
/// 每一级失败都留痕（异常透明铁律：绝不静默降级成"看起来正常"的尺寸）。
/// </summary>
internal static class MonitorDpi
{
    private const uint MonitorDefaultToNearest = 0x00000002;
    private const int MdEffectiveDpi = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    /// <summary>坐标所在显示器的 DPI；取不到时 96（100%）。</summary>
    internal static int ForPoint(Point p)
    {
        try
        {
            var hMonitor = MonitorFromPoint(new NativePoint { X = p.X, Y = p.Y }, MonitorDefaultToNearest);
            if (hMonitor != IntPtr.Zero
                && GetDpiForMonitor(hMonitor, MdEffectiveDpi, out var dpiX, out _) == 0
                && dpiX > 0)
                return (int)dpiX;
            Logger.Warn($"GetDpiForMonitor returned no dpi for monitor at {p.X},{p.Y}; falling back to system DPI");
        }
        catch (Exception ex) // DllNotFoundException（无 shcore 的老系统）/ EntryPointNotFoundException
        {
            Logger.Warn($"MonitorDpi.ForPoint unavailable at {p.X},{p.Y}: {ex.Message}; falling back to system DPI");
        }

        try
        {
            var sys = GetDpiForSystem();
            if (sys > 0) return (int)sys;
        }
        catch (Exception ex)
        {
            Logger.Warn($"GetDpiForSystem unavailable: {ex.Message}; assuming 96 dpi");
        }
        return 96;
    }
}
