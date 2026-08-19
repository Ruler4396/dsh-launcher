using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DshWeb.Chrome;
using DshWeb.Win32;
using Microsoft.Web.WebView2.WinForms;

namespace DshWeb.Windows;

/// <summary>
/// 主窗口（Step 6：从 Program.cs 迁出；Step 3 已薄壳化——结构体/常量/P-Invoke 在
/// Win32/NativeMethods.cs，决策在 WindowChromeController，override 只做消息解码+转发）。
/// </summary>
internal sealed class DshShellForm : Form
{
    internal CustomTitleBar? TitleBar;
    internal WebView2? MainWebView2;
    private FormWindowState _lastWindowState = FormWindowState.Normal;

    // Step 3 薄壳化：结构体/常量/P- Invoke 已迁入 Win32/NativeMethods.cs；
    // 决策逻辑下沉 WindowChromeController（Chrome/WindowChromeController.cs）。
    // override 只做消息解码 + 转发（铁律 3）。常量引用 Win32Constants 逐位等价。
    private readonly WindowChromeController _chrome = new();

    // 多屏 DPI 修复（G1/G10）：显示器几何/DPI 指标提供者。默认生产实现直连真实 Win32 API；
    // Headless 单测注入内存 Fake（IDisplayMetricsProvider），即可在无多显示器硬件下覆盖
    // WM_GETMINMAXINFO 的负坐标副屏/异构 DPI 边界（见 Task 2 方案 A）。
    private readonly IDisplayMetricsProvider _display;

    /// <summary>
    /// 构造函数。核心路径在 Program.cs 用 <see cref="IDisplayMetricsProvider"/> 显式注入；
    /// 缺省参数退化为生产实现 <see cref="Win32DisplayMetricsProvider"/>（--ui-probe 等场景）。
    /// </summary>
    internal DshShellForm(IDisplayMetricsProvider? display = null)
    {
        _display = display ?? new Win32DisplayMetricsProvider();
        // 修复 0xc0000005（ImmSetOpenStatus 访问违规崩溃）：WinForms 对宿主 WebView2 的 IME
        // 状态管理在输入法活跃时偶发无效 HIMC 句柄导致崩溃（用户 20:53 更新后主窗崩溃真凶）。
        // 本应用页面输入法由 WebView2（Chromium）内部处理，Form 无需 WinForms IME 介入。
        // 重写 ImeMode + 控件级 ImeMode.Disable（Program 建窗处）双保险，跳过 ImeContext。
        ImeMode = ImeMode.Disable;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            // Step 3 薄壳：样式位决策转发到 controller（矩阵 G6）。
            // 加回 WS_THICKFRAME|WS_MINIMIZEBOX|WS_MAXIMIZEBOX|WS_SYSMENU：恢复 Aero Snap
            // /Win+方向键/任务栏收起/Alt+Space 系统菜单（FormBorderStyle.None 默认剥掉）。
            // 不加 WS_CAPTION：避免 DWM 在最大化时预留原生标题栏空间导致窗口外扩。
            // 原生边框观感由 WM_NCCALCSIZE 返回 0 去除，自绘标题栏 + 1px 边框不变。
            var cp = base.CreateParams;
            _chrome.ApplyWindowStyle(cp);
            return cp;
        }
    }

    internal void ToggleFullscreen()
    {
        // F11 = 最大化/还原切换，标题栏始终保留（不再隐藏标题栏——
        // 之前"全屏模式隐藏标题栏"反复造成"标题栏消失"困扰）。
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal : FormWindowState.Maximized;
        DshWeb.Program.Trace($"ToggleFullscreen: WindowState={WindowState}");
    }

    /// <summary>
    /// 统一重算自绘标题栏与 WebView2 的客户区布局（1px 边框内缩；
    /// 全屏时无边框、标题栏隐藏、WebView2 铺满）。供 DpiChanged / OnResize 复用，
    /// 避免各路径手写布局不一致导致标题栏错位（按钮消失）。
    /// </summary>
    internal void LayoutChrome()
    {
        if (TitleBar is null || MainWebView2 is null) return;
        // 标题栏始终可见（F11 = 最大化/还原，不再隐藏标题栏）
        // Step 1 纯函数下沉（G7）：布局决策在 WindowGeometry.LayoutChromeRects（inset=1、
        // titleH=round(32*dpi/96)、负值钳 0），此处只应用结果，避免各路径手写不一致。
        var (title, web) = DshWeb.Win32.WindowGeometry.LayoutChromeRects(ClientSize, DeviceDpi);
        TitleBar.Bounds = title;
        MainWebView2.Bounds = web;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 自愈：标题栏必须始终可见——防止最大化/还原等路径把标题栏弄丢（按钮消失）。
        if (TitleBar is not null && !TitleBar.Visible)
            TitleBar.Visible = true;
        LayoutChrome();
        // 强制整条标题栏重绘，清除 Aero Snap 拖动/最大化动画留下的按钮残留
        TitleBar?.Invalidate();
        // 仅当窗口状态（最大化/还原/最小化）真正变化时才强推 DWM 重算非客户区；
        // 拖动缩放期间 WM_SIZE 高频触发，若每次 SetWindowPos(SWP_FRAMECHANGED) 会反复
        // 重算框架，造成卡顿/闪烁（v0.3.4）。
        if (WindowState != _lastWindowState)
        {
            _lastWindowState = WindowState;
            ForceNonClientRedraw();
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ForceNonClientRedraw();
    }

    /// <summary>
    /// 去掉 WS_CAPTION 后，最大化/还原/初次显示等瞬间 DWM 可能短暂按默认非客户区绘制
    /// 一次（看起来像"经典/win98 边框"），直到下一次 WM_NCCALCSIZE/WM_PAINT 才被自定义
    /// 样式覆盖。此处用 SetWindowPos(SWP_FRAMECHANGED) 强推 DWM 立即重新计算非客户区
    /// （重发 WM_NCCALCSIZE），清除该闪影（v0.3.4）。
    /// </summary>
    internal void ForceNonClientRedraw()
    {
        // Step 3 薄壳：决策/调用转发到 controller（矩阵 G3 闪影清除）
        _chrome.ForceNonClientRedraw(Handle);
    }

    protected override void WndProc(ref Message m)
    {
        // Step 3 薄壳：override 保留（WinForms 硬约束），只做消息解码 + 转发；
        // 常量/结构体/P-Invoke 已迁入 Win32/NativeMethods.cs，行为逐位不变。
        switch (m.Msg)
        {
            case Win32Constants.WM_NCCALCSIZE:
                // wParam=TRUE：返回 0 即声明"客户区 = 整个窗口矩形"（吃掉系统
                // 标题栏/边框预留，自绘标题栏照常占据客户区顶部）。
                // 注意：不能在此钳制 rgrc0（会让客户区比窗口小，DWM 在残留区画
                // 原生标题栏"多出一栏"），也不能返回 WVR_* 标志（DWM 客户区计算
                // 错乱导致内容大面积消失）。最大化铺满由 WM_GETMINMAXINFO 负责。
                if (m.WParam != IntPtr.Zero)
                {
                    m.Result = IntPtr.Zero;
                    return;
                }
                base.WndProc(ref m);
                return;
            case Win32Constants.WM_NCACTIVATE:
                // 不吞掉则 DefWindowProc 用经典 NC 渲染器画 Win98 式标题栏（见 ADR-003）；
                // 本窗口 NC 全自绘，吞掉并返回 1（声明已处理激活态重绘）。
                ForceNonClientRedraw();
                m.Result = (IntPtr)1; // 1：已处理激活态重绘
                return;
            case Win32Constants.WM_NCPAINT:
                // 非客户区绘制：1px 边框由 Form.BackColor + 客户区内缩自绘，不画原生框架（ADR-003）。
                m.Result = IntPtr.Zero;
                return;
            case Win32Constants.WM_GETMINMAXINFO:
            {
                var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(m.LParam);
                // 多屏 DPI 修复（G1/G10）：物理像素工作区。
                // 经 IDisplayMetricsProvider 拿"窗口所在监视器"的物理像素指标（MonitorFromWindow
                // + GetMonitorInfo 取 rcWork，GetDpiForWindow 取该屏 DPI），替代 Screen.FromHandle
                // 的逻辑像素陷阱（150% 副屏把工作区算小 → 丢窗）。
                // 决策全在纯函数 ComputeMaximizedMinMaxInfo，此处只做"取指标 + 转发"（铁律 3）。
                //
                // v0.4.2 回归修复：**不再做 frame（DWM 外扩）补偿**。本窗口在 CreateParams 中
                // 去掉了 WS_CAPTION——Windows 对去 WS_CAPTION 的窗口最大化时**不再向外扩 frame**
                //（ADR-001 旧注释 + e2e-geo CI 实测：e3f2d8d 引入 GetWindowFrameSize 补偿后，
                // 最大化矩形从 (0,0,work) 变成 (8,8,work-16)，四周留 8px 缝隙）。直接给物理
                // 工作区即 0px 精确铺满；ptMaxTrackSize 同为物理工作区（Normal 贴边拖拽上限，
                // 不扣 frame，业界 Windows Terminal / Chromium 同款）。
                // 带 frame 的补偿重载仅适用于保留 WS_CAPTION 的窗口（那里系统最大化才外扩）。
                try
                {
                    var metrics = _display.GetMonitorMetrics(Handle);
                    var mm = DshWeb.Win32.WindowGeometry.ComputeMaximizedMinMaxInfo(metrics.WorkArea);
                    mmi.ptMaxSize = new NativeMethods.POINT { X = mm.MaxSize.X, Y = mm.MaxSize.Y };
                    mmi.ptMaxPosition = new NativeMethods.POINT { X = mm.MaxPos.X, Y = mm.MaxPos.Y };
                    mmi.ptMaxTrackSize = new NativeMethods.POINT { X = mm.MaxTrack.X, Y = mm.MaxTrack.Y };
                }
                catch (InvalidOperationException)
                {
                    // 指标获取失败（窗口已销毁/监视器异常）：保留系统默认 MINMAXINFO，绝不
                    // 用脏数据覆盖——宁可最大化行为回退系统默认，也不产生"飞出屏幕"的错误坐标。
                }
                Marshal.StructureToPtr(mmi, m.LParam, false);
                m.Result = IntPtr.Zero;
                return;
            }
            case Win32Constants.WM_NCHITTEST:
                base.WndProc(ref m);
                if (m.Result == (IntPtr)Win32Constants.HTCLIENT)
                {
                    // 64 位屏幕坐标：左侧/上方副屏为负坐标，LParam.ToInt32() 会抛 OverflowException
                    //（B1）。正确拆位：低 16 位有符号 = X，高 16 位有符号 = Y。
                    var (x, y) = DshWeb.ShellLogic.SplitLParam(m.LParam.ToInt64());
                    var pt = new Point(x, y);
                    var r = RectangleToScreen(ClientRectangle);
                    // Step 1 纯函数下沉（G4/G5）：决策在 WindowGeometry.HitTestResizeEdge，
                    // 最大化返回 null（边缘不出现缩放指针）。行为与旧内联判定逐位一致。
                    var ht = DshWeb.Win32.WindowGeometry.HitTestResizeEdge(
                        pt, r, Win32Constants.ResizeEdge, maximized: WindowState == FormWindowState.Maximized);
                    if (ht is not null) m.Result = (IntPtr)ht.Value;
                }
                return;
            default:
                base.WndProc(ref m);
                return;
        }
    }
}
