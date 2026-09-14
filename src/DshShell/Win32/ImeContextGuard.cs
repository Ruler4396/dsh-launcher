using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshWeb.Win32;

/// <summary>
/// WinForms IME 上下文护栏（2026-09 崩溃根治，issue #28-2 / 版本徽标闪退）。
///
/// 【事故】用户点击标题栏版本徽标 → <see cref="DshWeb.Windows.VersionInfoDialog"/> 打开或关闭的
/// 瞬间进程必崩：事件日志 Application Error 1000 / .NET Runtime 1026，异常码 0xc0000005，
/// 故障模块 coreclr.dll，托管栈固定为
/// <c>ImmSetOpenStatus ← ImeContext.SetOpenStatus ← ImeContext.Disable ← Control.UpdateImeContextMode ←
/// Control.WmSetFocus ← Label.WndProc</c>（另一路是 <c>WmImeKillFocus</c>）。
///
/// 【机理】WinForms 在焦点变化时按控制器的 ImeMode 调用 <c>ImeContext.SetImeStatus</c>；
/// 对 <c>Label</c>/<c>LinkLabel</c>（<c>DefaultImeMode == ImeMode.Disable</c>）它会走到
/// <c>ImeContext.Disable(handle)</c> → <c>IsOpen(handle)</c> → <c>SetOpenStatus(false, handle)</c> →
/// <c>ImmAssociateContext</c>/<c>ImmSetOpenStatus</c>。第三方 IME（本机实测：手心输入法 PalmInput
/// 3.2.9）对这些壳自有窗口返回一个不可用的 HIMC，随后 <c>ImmSetOpenStatus</c> 直接访问违规
/// ——托管层无法 try/catch（native AV，CLR fail-fast，连 E9001 都写不出来）。同类崩溃在
/// .NET/WinForms 生态里长期在案（UnsafeNativeMethods.ImmSetOpenStatus AccessViolation）。
///
/// 【对策】壳自有 WinForms 窗口（含全部子控件）一律不持有 IME 上下文：
/// 在句柄创建时调用 <c>ImmAssociateContext(hwnd, NULL)</c>。此后 WinForms 侧
/// <c>ImeContext.GetImeMode(hwnd) == ImeMode.Disable</c>：
///   · <c>UpdateImeContextMode</c> 里 <c>CurrentImeContextMode == newImeContextMode</c> → 直接短路，不再
///     调用 <c>SetImeStatus</c>（Label 的强制 Disable 路径消失）；
///   · <c>IsOpen(hwnd)</c> 恒 false → <c>Disable()</c> 不再调用 <c>SetOpenStatus</c>；
///   · <c>WmImeKillFocus</c> 的 <c>PropagatingImeMode</c> 初始化结果为 Disable → 静态字段保持
///     Inherit → 模态窗失焦路径整体跳过。
/// 即把"WinForms 期望的 ImeMode.Disable 语义"用一次 ImmAssociateContext 直接落地，绕开
/// 会崩的 ImmSetOpenStatus 中转。页面内的输入法（WebView2/Chromium 自持窗口与 TSF 上下文）
/// 不受影响——Chromium 只在自己的 HWND 上关联输入上下文，与壳的宿主 HWND 无关。
/// </summary>
internal static class ImeContextGuard
{
    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

    /// <summary>已挂护栏的控件（幂等：Harden 可对同一窗体重复调用）。弱引用表，不阻碍回收。</summary>
    private static readonly ConditionalWeakTable<Control, object> Hardened = new();
    private static readonly object Marker = new();

    /// <summary>解绑单个窗口的 IME 上下文（失败只 Warn：绝不因 IME 异常阻断 UI 创建）。</summary>
    internal static void Disassociate(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        try
        {
            ImmAssociateContext(hwnd, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            // imm32 缺失/EntryPoint 异常属极端环境：留痕但不致命（此时 WinForms 自身也无法走 IME 路径）
            Logger.Warn("ime guard: ImmAssociateContext failed", ctx: new { hwnd = hwnd.ToInt64(), error = ex.Message });
        }
    }

    /// <summary>
    /// 给窗体及其现有/后续全部子控件挂上护栏。必须在窗体构造期调用一次（气泡是否已创建句柄无关，
    /// 未创建的句柄会在 HandleCreated 时补齐；后续 ControlAdded 的控件同样自动覆盖）。
    /// </summary>
    internal static void Harden(Control root)
    {
        try
        {
            HardenControl(root);
        }
        catch (Exception ex)
        {
            Logger.Warn("ime guard: harden failed", ctx: new { type = root.GetType().Name, error = ex.Message });
        }
    }

    private static void HardenControl(Control control)
    {
        if (Hardened.TryGetValue(control, out _)) return;
        Hardened.Add(control, Marker);

        if (control.IsHandleCreated) Disassociate(control.Handle);
        control.HandleCreated += (_, _) => Disassociate(control.Handle);
        control.ControlAdded += (_, e) =>
        {
            if (e.Control is { } added) HardenControl(added);
        };
        foreach (Control child in control.Controls) HardenControl(child);
    }
}
