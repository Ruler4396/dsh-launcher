using System;
using System.Runtime.InteropServices;
using DshWeb;

namespace DshWeb.Managers;

/// <summary>
/// F11 全屏的系统级低级键盘钩子（WH_KEYBOARD_LL）。
/// 在 OS 层捕获按键，不依赖 WinForms 消息队列 / 焦点 / WebView2 浏览器进程——
/// 这是对物理 F11 最可靠、跨重启稳定的方案（v0.3.4）。
/// </summary>
internal sealed class F11LowLevelHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private readonly Action _toggle;
    private readonly Func<bool> _isForeground;
    private IntPtr _hook;
    private readonly LowLevelKeyboardProc _proc; // 保持委托存活，防 GC

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string lpModuleName);
    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    /// <summary>
    /// F11 全屏判定（纯函数，可单测）：低级键盘钩子回调中，nCode≥0 且主窗口在前台
    /// 且为 F11(0x7A) 的按下/系统按下，即应切换全屏并吞掉该键。
    /// </summary>
    internal static bool ShouldHandleF11Hook(int nCode, IntPtr wParam, uint vkCode, bool isForeground)
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_SYSKEYDOWN = 0x0104;
        const int VK_F11 = 0x7A;
        return nCode >= 0 && isForeground
            && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN)
            && vkCode == VK_F11;
    }

    public F11LowLevelHook(Action toggle, Func<bool> isForeground)
    {
        _toggle = toggle;
        _isForeground = isForeground;
        _proc = HookProc;
        _hook = SetWindowsHookExW(WH_KEYBOARD_LL, _proc, GetModuleHandleW("DshWeb.exe"), 0);
        if (_hook == IntPtr.Zero)
            Logger.Warn("F11 low-level keyboard hook install failed");
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam is (IntPtr)0x0100 or (IntPtr)0x0104)
        {
            var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (ShouldHandleF11Hook(nCode, wParam, info.vkCode, _isForeground()))
            {
                // [审查 N21 2026-09-21] LL 钩子回调由系统在 LowLevelHooksTimeout 内等返回：
                // 内联做窗口切换（→布局→WebView2 Resize）慢一次就被 Windows **静默摘钩**
                // （此后 F11 永久失效且 _hook 非零、无报错）。toggle 的投递义务在接线方
                // （组合根 BeginInvoke 进 UI 队列后返回），钩子线程零工作。
                _toggle();
                return (IntPtr)1; // 吞掉 F11，阻止继续分发
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}
