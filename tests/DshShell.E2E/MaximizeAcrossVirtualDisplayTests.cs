using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.UIA3;
using Xunit;

namespace DshShell.E2E;

/// <summary>
/// Task 2 方案 B：真实 UI 的跨屏最大化 E2E（无物理硬件）。
///
/// 前置：CI/本机已通过 scripts/install-virtual-display.ps1 注入 ≥1 块虚拟副屏，
/// 并用 scripts/Set-VirtualDisplay.ps1 把副屏设为与主屏**不同 DPI**（如 150%），构造
/// 异构 DPI 回归面。本测试：
///   1) 启动真实 DshWeb.exe --ui-probe（无服务探针窗口，见 Program.RunUiProbe）；
///   2) 用原生 GetMonitorInfo 取虚拟副屏的**物理像素** rcWork；
///   3) SetWindowPos 把窗口强行搬到副屏物理坐标；
///   4) WM_SYSCOMMAND + SC_MAXIMIZE 最大化；
///   5) 读 FlaUI 的 BoundingRectangle（物理像素）；
///   6) 断言窗口物理 Bounds 完全落在副屏 rcWork 内，误差 ≤2px。
///
/// 为什么这能在无物理硬件下验证 Bug 修复：虚拟屏 + 异构 DPI 已构造出"物理≠逻辑"的
/// 左/负坐标场景，若 WM_GETMINMAXINFO 仍犯逻辑/物理错位，窗口会最大化到错误坐标而
/// 飞出副屏 → 断言立即失败。这正是修复前在真实 Win11 25H2 上丢失的复现。
///
/// v0.4.0 变更：**CI 不再安装虚拟显示驱动**（受限沙箱无法加载第三方内核驱动），多屏回归
/// 已迁移为 Headless 纯函数/Mock 测试（MultiMonitorContractTests / ScreenProviderIntegrationTests）。
/// 本类保留为**本地调试工具**（需先 install-virtual-display.ps1 注入副屏）：CI 单屏环境
/// 无副屏时自动空跑（守卫），不再因缺驱动而失败。
/// </summary>
[Trait("Category", "RequiresVirtualDisplay")] // CI 默认按 headless filter 排除；本地有副屏时可显式跑
public class MaximizeAcrossVirtualDisplayTests : IAsyncLifetime
{
    private const string ProcessName = "DshWeb";
    private const string WindowTitle = "dsh"; // DshShellForm.Text（探针窗口标题）

    // WM_SYSCOMMAND / 最大化
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MAXIMIZE = 0xF030;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    private Process? _proc;
    private AutomationBase? _automation;

    /// <summary>第二个监视器（索引 1）即第一块虚拟副屏；没有副屏则跳过并提示装驱动。</summary>
    private static Rectangle _secondaryWork = Rectangle.Empty;

    public async Task InitializeAsync()
    {
        _secondaryWork = GetMonitorWorkArea(1);
        // 无副屏（CI 单屏）→ _secondaryWork.IsEmpty，测试内守卫空跑；本地先跑
        // install-virtual-display.ps1 + Set-VirtualDisplay.ps1 注入后此值非空才真正验证。
        if (_secondaryWork.IsEmpty) return;

        // 1. 启动真实 exe（--ui-probe：不拉服务、导航 about:blank，仅验证窗口几何行为）
        var exe = LocateDshWebExe();
        _proc = Process.Start(new ProcessStartInfo(exe, "--ui-probe")
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe)
        });
        _automation = new UIA3Automation();
        await Task.Delay(1500); // 等窗口创建 + WebView2 初始化
    }

    public async Task DisposeAsync()
    {
        _automation?.Dispose();
        if (_proc is { HasExited: false })
        {
            _proc.Kill(entireProcessTree: true);
            await _proc.WaitForExitAsync();
        }
        _proc?.Dispose();
    }

    [Fact]
    public async Task Maximize_on_virtual_secondary_screen_stays_within_working_area()
    {
        if (_secondaryWork.IsEmpty) return; // 无副屏守卫：本地调试专用，CI 单屏自动空跑

        // 找到主窗口（按进程 id + 类名过滤，避开系统子窗口）
        // 注：UseWindowsForms 注入 System.Windows.Forms.Application 全局 using，这里显式用
        // FlaUI.Core.Application 消除二义性（跨屏最大化断言依赖 FlaUI 的 BoundingRectangle）。
        var app = FlaUI.Core.Application.Attach(_proc!.Id);
        var window = WaitForMainWindow(app, WindowTitle);
        Assert.NotNull(window);
        var hwnd = window.Properties.NativeWindowHandle.Value;
        Assert.NotEqual(IntPtr.Zero, hwnd);

        // 2. 先移到主屏，保证起点归一（便于复现跨屏路径）
        MoveTo(hwnd, GetMonitorWorkArea(0), new Size(400, 300));

        // 3. 用 SetWindowPos 把窗口强行搬到副屏物理坐标（左/上 = 副屏工作区原点）
        SetWindowPos(hwnd, IntPtr.Zero, _secondaryWork.X, _secondaryWork.Y, 400, 300,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_NOSIZE);
        await Task.Delay(300);
        Assert.True(IsOnMonitor(hwnd, _secondaryWork),
            $"窗口未落到副屏：{GetWindowRect(hwnd)} 不在 {_secondaryWork}");

        // 4. 模拟点击最大化按钮：发送 WM_SYSCOMMAND SC_MAXIMIZE
        PostMessage(hwnd, WM_SYSCOMMAND, SC_MAXIMIZE, IntPtr.Zero);
        await Task.Delay(800); // 等系统完成最大化重排

        // 5. 读 FlaUI 的 BoundingRectangle（物理像素）
        var bounds = window.BoundingRectangle;

        // 6. 断言：物理 Bounds 完全包含在副屏 rcWork 内，误差 ≤2px（与 e2e geo 探针同容差）
        const int tolerance = 2;
        Assert.True(bounds.Left >= _secondaryWork.Left - tolerance,
            $"左越界：left={bounds.Left} 要求 ≥ {_secondaryWork.Left - tolerance}");
        Assert.True(bounds.Top >= _secondaryWork.Top - tolerance,
            $"上越界：top={bounds.Top} 要求 ≥ {_secondaryWork.Top - tolerance}");
        Assert.True(bounds.Right <= _secondaryWork.Right + tolerance,
            $"右越界：right={bounds.Right} 要求 ≤ {_secondaryWork.Right + tolerance}");
        Assert.True(bounds.Bottom <= _secondaryWork.Bottom + tolerance,
            $"下越界：bottom={bounds.Bottom} 要求 ≤ {_secondaryWork.Bottom + tolerance}");
    }

    // ---------------- Win32 辅助 ----------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    private delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, ref RECT r, IntPtr lp);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr rc, MonitorEnumProc cb, IntPtr lp);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMon, ref MONITORINFO mi);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

    /// <summary>取第 index 块监视器的物理像素工作区（rcWork）。index=0 主屏，1.. 副屏。</summary>
    private static Rectangle GetMonitorWorkArea(int index)
    {
        var result = Rectangle.Empty;
        var i = 0;
        // 枚举回调签名须与 MonitorEnumProc 一致（含 ref RECT 参数），否则 CS1676 编译失败
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMon, _, ref _, _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            GetMonitorInfo(hMon, ref mi);
            if (i++ == index)
                result = Rectangle.FromLTRB(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static Rectangle GetWindowRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static bool IsOnMonitor(IntPtr hwnd, Rectangle work)
    {
        var r = GetWindowRect(hwnd);
        return r.Left >= work.Left && r.Top >= work.Top;
    }

    private static void MoveTo(IntPtr hwnd, Rectangle work, Size size)
        => SetWindowPos(hwnd, IntPtr.Zero, work.X, work.Y, size.Width, size.Height, SWP_NOZORDER);

    /// <summary>定位 DshWeb.exe（优先测试运行目录旁的构建产物）。</summary>
    private static string LocateDshWebExe()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "DshWeb.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DshShell", "bin", "Debug", "net10.0-windows", "DshWeb.exe")
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        throw new FileNotFoundException("未找到 DshWeb.exe，请先编译 src/DshShell/DshShell.csproj。");
    }

    private Window? WaitForMainWindow(FlaUI.Core.Application app, string title)
    {
        // 按标题 + 顶层窗口找主窗（避免匹配子窗口）。GetAllTopLevelWindows 需传 AutomationBase。
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var w in app.GetAllTopLevelWindows(_automation!))
            {
                var name = w.Name ?? string.Empty;
                if (name.Contains(title, StringComparison.OrdinalIgnoreCase))
                    return w;
            }
            Thread.Sleep(500);
        }
        return null;
    }
}
