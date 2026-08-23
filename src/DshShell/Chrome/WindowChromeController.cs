using System;
using System.Drawing;
using System.Windows.Forms;
using DshWeb.Win32;

namespace DshWeb.Chrome;

/// <summary>
/// 窗口自绘边框/全屏的决策控制（Step 3 薄壳化，矩阵 G 类）。
/// 铁律 3：CreateParams/WndProc/OnResize 等 override 必须留在 Form 子类，但它们只做
/// "消息解码 + 转发"；所有决策逻辑下沉到本普通类（可单测）与 WindowGeometry 纯函数。
/// 本 controller 由 DshShellForm 持有（WindowManager 经 Program 注入），不持有窗口句柄
/// 生命周期，仅做纯计算与 P/Invoke 调用。
/// </summary>
internal sealed class WindowChromeController
{
    /// <summary>
    /// CreateParams 样式位决策（矩阵 G6：Aero Snap / Win+方向键 / Alt+Space / 任务栏收起）。
    /// 加回 WS_THICKFRAME|WS_MINIMIZEBOX|WS_MAXIMIZEBOX|WS_SYSMENU（FormBorderStyle.None
    /// 默认剥掉）；不加 WS_CAPTION（避免 DWM 最大化时预留原生标题栏空间导致外扩）。
    /// </summary>
    public void ApplyWindowStyle(CreateParams cp)
    {
        cp.Style |= Win32Constants.WS_THICKFRAME | Win32Constants.WS_MINIMIZEBOX
            | Win32Constants.WS_MAXIMIZEBOX | Win32Constants.WS_SYSMENU;
    }

    /// <summary>
    /// 去掉 WS_CAPTION 后，最大化/还原/初次显示等瞬间 DWM 可能短暂按默认非客户区绘制
    /// 一次（看起来像"经典/win98 边框"），直到下一次 WM_NCCALCSIZE/WM_PAINT 才被自定义
    /// 样式覆盖。此处用 SetWindowPos(SWP_FRAMECHANGED) 强推 DWM 立即重新计算非客户区
    ///（重发 WM_NCCALCSIZE），清除该闪影（v0.3.4，矩阵 G3）。
    /// </summary>
    public void ForceNonClientRedraw(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return;
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Win32Constants.SWP_FRAMECHANGED | Win32Constants.SWP_NOMOVE
                | Win32Constants.SWP_NOSIZE | Win32Constants.SWP_NOZORDER
                | Win32Constants.SWP_NOACTIVATE);
        }
        catch { /* 重绘失败不影响功能 */ }
    }
}
