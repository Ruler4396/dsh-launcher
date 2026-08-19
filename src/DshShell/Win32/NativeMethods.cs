using System;
using System.Runtime.InteropServices;

namespace DshWeb.Win32;

/// <summary>
/// DshShellForm 自绘边框/全屏所需的 Win32 常量（Step 3 薄壳化：从 Program.cs 纯搬迁）。
/// 保留语义注释（矩阵 G1/G3/G5/G6），常量值逐位不变。
/// </summary>
internal static class Win32Constants
{
    // ---- 窗口消息 ----
    public const int WM_GETMINMAXINFO = 0x0024;
    public const int WM_NCHITTEST = 0x0084;
    // Aero Snap（拖到屏幕边缘的半屏/最大化、Win+方向键）依赖 WS_CAPTION|WS_THICKFRAME
    // 样式位；FormBorderStyle.None 会把它们剥掉（0.1.10 自绘标题栏后贴边失效的根因）。
    // 方案：样式位加回来，再用 WM_NCCALCSIZE 吃掉原生框架预留区，观感仍是全自绘无边框
    //（Chromium / Windows Terminal 同款做法）。
    public const int WM_NCCALCSIZE = 0x0083;
    public const int WM_NCACTIVATE = 0x0086;
    public const int WM_NCPAINT = 0x0085;

    // ---- CreateParams 样式位（G6 Aero Snap/Win+方向键/Alt+Space/任务栏收起）----
    // 不用 WS_CAPTION（含 WS_BORDER|WS_DLGFRAME）：去掉后 DWM 在最大化时不再为原生
    // 标题栏/边框预留非客户区空间，配合 WM_NCCALCSIZE 返回 0，系统不会把窗口向外扩展，
    // WM_GETMINMAXINFO 直接设工作区即精确铺满（消除 4px 内缩间隙）。
    // 保留 WS_THICKFRAME（Aero Snap/Win+方向键/边缘缩放）、WS_MINIMIZEBOX/MAXIMIZEBOX
    //（任务栏收起/Alt+Space 项）、WS_SYSMENU（Alt+Space 系统菜单）。
    public const int WS_THICKFRAME = 0x00040000;
    public const int WS_MINIMIZEBOX = 0x00020000;
    public const int WS_MAXIMIZEBOX = 0x00010000;
    public const int WS_SYSMENU = 0x00080000;

    // ---- WM_NCHITTEST 命中区域（G4/G5）----
    public const int HTCLIENT = 0x0001;
    public const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
    public const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    // ---- 边缘缩放判定宽度（G4）----
    public const int ResizeEdge = 8;

    // ---- SetWindowPos 标志（G3 ForceNonClientRedraw）----
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOMOVE = 0x0002;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_FRAMECHANGED = 0x0020;

    // ---- MonitorFromWindow 标志（G1/G10 物理像素工作区）----
    public const uint MONITOR_DEFAULTTONEAREST = 2; // 取最近监视器（副屏窗口归属判定）
}

/// <summary>
/// DshShellForm 所需的 Win32 结构体与 P/Invoke（Step 3 薄壳化：从 Program.cs 纯搬迁）。
/// </summary>
internal static class NativeMethods
{
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    // Step 1 多屏修复（G1/G10）P/Invoke：物理像素工作区来源。
    // 为什么必须物理像素：PerMonitorV2 下 Screen.FromHandle.WorkingArea 是逻辑像素，
    // 150% 缩放副屏会把工作区算小 → 最大化铺不满/丢窗。MonitorFromWindow+GetMonitorInfo
    // 拿 rcWork（物理像素）喂 ComputeMaximizedMinMaxInfo 消除陷阱（矩阵 G1/G10）。
    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;      // 物理像素工作区（最大化铺满目标）
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NCCALCSIZE_PARAMS
    {
        public RECT rgrc0;
        public RECT rgrc1;
        public RECT rgrc2;
        public IntPtr lppos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }
}
