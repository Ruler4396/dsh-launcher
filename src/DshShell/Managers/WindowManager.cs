using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace DshWeb.Managers;

/// <summary>
/// 窗口管理实现：自绘边框/DPI/阴影/主题解析与内部弹窗 + 生命周期（托盘/唤起）。
/// Step 5：托盘生命周期从 Program 迁入（EnsureTrayIcon/ShowMainWindow/托盘气泡/TrayExitRequested），
/// 依赖经委托注入（Program 注入 IsTrayWantedProvider/TrayWhaleIconProvider/TrayExitAction/
/// TrayMenuFactory），解耦 Program 静态字段，行为逐位保持。
///
/// 接线自检（Task5）：Program 注入依赖后调用 <see cref="VerifyDependencies"/>，
/// Debug 断言关键委托已注入（防漏注入导致托盘/唤起静默失效）。
/// </summary>
public sealed class WindowManager : IWindowManager
{
    // ---- Program 注入的依赖委托（解耦 Program 静态实现，行为逐位保持）----
    /// <summary>托盘是否应显示（IsTrayWanted：lifetime 插件+托盘模式，或有待通知更新）。</summary>
    public Func<bool>? IsTrayWantedProvider { get; set; }
    /// <summary>托盘鲸鱼图标（Program 进程级图标缓存，TrayWhaleIcon）。</summary>
    public Func<Icon>? TrayWhaleIconProvider { get; set; }
    /// <summary>托盘"退出"动作（Program：置标志 + 按模式停服务 + Application.Exit）。</summary>
    public Action? TrayExitAction { get; set; }
    /// <summary>托盘菜单创建委托（Program 注入创建 TrayMenuForm——自绘菜单 UI 保留在 Program）。</summary>
    public Func<Action, Form>? TrayMenuFactory { get; set; }

    /// <summary>进程内单例（Program 在 Main 早期创建并注入依赖委托；供托盘/唤起/状态访问）。</summary>
    public static WindowManager Instance { get; set; } = new();

    // ---- 托盘状态（进程级，从 Program 迁入）----
    private NotifyIcon? _trayIcon;
    private bool _trayExitRequested;

    /// <summary>托盘"退出"请求（FormClosing 读：放行真关，不再次隐藏到托盘，矩阵 L1）。</summary>
    public bool TrayExitRequested => _trayExitRequested;

    /// <summary>托盘图标（更新提示/主题切换读用；null=未创建）。</summary>
    internal NotifyIcon? TrayIcon => _trayIcon;

    public (Form Form, WebView2 Web) CreatePopup() => DshWeb.Program.CreatePopupForm();
    public void ApplyShadow(IntPtr hwnd) => DshWeb.Program.ApplyWindowShadow(hwnd);
    public bool ResolveDarkMode() => DshWeb.Program.ResolveDarkMode();

    /// <summary>接线自检（Task5）：Debug 断言关键委托已注入——防漏注入导致托盘/唤起静默失效。</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    public void VerifyDependencies()
    {
        System.Diagnostics.Debug.Assert(IsTrayWantedProvider != null, "IsTrayWantedProvider not injected");
        System.Diagnostics.Debug.Assert(TrayWhaleIconProvider != null, "TrayWhaleIconProvider not injected");
        System.Diagnostics.Debug.Assert(TrayExitAction != null, "TrayExitAction not injected");
    }

    /// <summary>
    /// 确保托盘图标存在（v0.3.0 起按需显示：仅装了 lifetime 插件或存在待通知更新时创建）。
    /// 左键单击 → ShowMainWindow（先 SW_RESTORE 再 Activate，L2）；右键 → 托盘菜单。
    /// </summary>
    public void EnsureTrayIcon(Form form, bool force = false)
    {
        if (_trayIcon is not null) return;
        if (!force && !(IsTrayWantedProvider?.Invoke() ?? false)) return;
        try
        {
            var tray = new NotifyIcon
            {
                // 托盘背景多为深色，固定用白色鲸鱼（深色鲸鱼看不清）
                Icon = TrayWhaleIconProvider?.Invoke() ?? SystemIcons.Application,
                Text = "dsh-launcher",
                Visible = true,
            };
            // 左键单击：窗口置顶显示；右键：弹出自绘托盘菜单（浅色毛玻璃层，仅"退出"）。
            tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowMainWindow(form);
                else if (e.Button == MouseButtons.Right) ShowTrayMenu();
            };
            _trayIcon = tray;
        }
        catch (Exception ex)
        {
            // 质量治理 P2-8：托盘创建失败不再静默——记录原因（更新/待应用通知会因此丢失）
            DshWeb.Logger.Warn("tray icon creation failed; balloon notifications will be lost", ctx: new { error = ex.Message });
        }
    }

    /// <summary>
    /// 在鼠标位置弹出托盘菜单（自绘浅色毛玻璃层，仅"退出"，动作经 TrayExitAction 注入）。
    /// 屏幕边界自适应：左/上越界翻转到鼠标另一侧，仍越界贴工作区边缘。
    /// </summary>
    private void ShowTrayMenu()
    {
        try
        {
            var exitAction = TrayExitAction ?? (() => Application.Exit());
            if (TrayMenuFactory is null) { exitAction(); return; } // 无工厂（注入缺失）则直接退出
            var menu = TrayMenuFactory(exitAction);
            var pt = Cursor.Position;
            var wa = Screen.FromPoint(pt).WorkingArea;
            var loc = new Point(pt.X - menu.Width + 12, pt.Y - menu.Height - 6);
            if (loc.X < wa.Left) loc.X = pt.X + 12;
            if (loc.Y < wa.Top) loc.Y = pt.Y + 6;
            if (loc.X + menu.Width > wa.Right) loc.X = wa.Right - menu.Width;
            if (loc.Y + menu.Height > wa.Bottom) loc.Y = wa.Bottom - menu.Height;
            menu.Location = loc;
            menu.Show();
        }
        catch { /* 菜单显示失败不影响壳 */ }
    }

    /// <summary>
    /// 托盘唤起主窗口（L2）：先 Show，最小化 → SW_RESTORE 再 Activate（否则 Activate 无效）；
    /// WebView2 隐藏期崩溃/长隐藏(>5min) → 延迟重载（W6/W7）。
    /// </summary>
    public void ShowMainWindow(Form form)
    {
        if (!form.Visible) form.Show();
        if (form.WindowState == FormWindowState.Minimized)
        {
            // 最小化 → 还原（SW_RESTORE），否则 Activate 无效，窗口不会出现
            DshWeb.Program.ShowWindowNative(form.Handle, 9); // 9 = SW_RESTORE
            form.WindowState = FormWindowState.Normal;
        }
        form.Activate();
        // WebView2 在窗口隐藏期间可能出问题导致恢复后白屏：
        // - 渲染/GPU 进程崩溃（ProcessFailed 已置标志）→ 延迟重载页面恢复
        // - 隐藏超过 5 分钟，渲染进程可能被系统回收（无崩溃事件）→ 强制重载兜底
        // 隐藏状态下 Reload 无效；且刚显示时立即 Reload 与 WebView2 的可见性处理
        // 存在竞态（实测隐藏→恢复→立即 Reload 后进程崩溃），必须延迟执行。
        var longHidden = WebViewManager.HiddenSince != DateTime.MinValue
            && DateTime.Now - WebViewManager.HiddenSince >= TimeSpan.FromMinutes(5);
        if (WebViewManager.RecoveryNeeded || longHidden)
        {
            WebViewManager.RecoveryNeeded = false;
            WebViewManager.HiddenSince = DateTime.MinValue;
            _ = TryReloadWebViewDeferred(form); // fire-and-forget：不等待结果
        }
    }

    /// <summary>
    /// 延迟重载主窗口 WebView2 页面（隐藏/显示后的崩溃恢复）。延迟 500ms 等窗口
    /// 可见性处理完成；期间窗口若再次隐藏/关闭则放弃本次重载并留待下次恢复（标志复位）。
    /// </summary>
    private static async Task TryReloadWebViewDeferred(Form form)
    {
        try
        {
            await Task.Delay(500);
            if (form.IsDisposed || !form.Visible || WebViewManager.MainWeb is { IsDisposed: true })
            {
                // 窗口又隐藏/关闭了：下次恢复窗口时再处理
                WebViewManager.RecoveryNeeded = true;
                return;
            }
            if (WebViewManager.MainWeb?.CoreWebView2 is not null)
            {
                DshWeb.Program.Trace("tray restore: reloading webview after process failure (deferred)");
                WebViewManager.MainWeb.CoreWebView2.Reload();
            }
        }
        catch
        {
            // 重载失败静默（页面可能已自行恢复）
        }
    }

    /// <summary>托盘气泡通知（下载完成等）。</summary>
    public void ShowBalloonTip(int timeout, string title, string body, ToolTipIcon icon)
    {
        try
        {
            if (_trayIcon is null) return;
            _trayIcon.ShowBalloonTip(timeout, title, body, icon);
        }
        catch { /* 气泡失败忽略 */ }
    }

    /// <summary>真实退出时释放托盘。</summary>
    public void DisposeTray()
    {
        _trayIcon?.Dispose();
        _trayIcon = null;
    }
}
