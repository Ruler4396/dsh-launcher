using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using DshWeb.Win32;
using Xunit;
using Xunit.Abstractions;

namespace DshShell.Tests;

/// <summary>
/// 【Regression_MonitorDpiAngular】按点取 DPI 的采样器必须返回显示器的 **有效** DPI。
///
/// 事故（issue #25 通知卡片"还是不够显眼"排查中发现）：shcore 的 MONITOR_DPI_TYPE 里
/// MDT_EFFECTIVE_DPI = 0，而 1 是 MDT_ANGULAR_DPI —— 面板的**物理角 DPI**。
/// MonitorDpi 把 1 当成了 "EFFECTIVE"，于是本机（1920×1080 @100%，物理约 89 DPI）上
/// ForPoint 恒返回 89，卡片按 s=0.93 整体缩小一档（设计 445×? → 实测 413×77）。
/// 偏差取决于面板尺寸，换机器就换一档，100% 缩放下肉眼完全看不出——所以它活过了 #28-3
/// 那轮高 DPI 修复和所有截图对照。
///
/// 两条断言的分工：① 常量级契约在任意机器（含 CI）上都能钉死，因为它是"与平台 SDK 的
/// 字面值对齐"问题；② 真机交叉核对用同一个 shcore 调用的正确 dpiType 再取一次，
/// 在"物理 DPI ≠ 有效 DPI"的机器上能直接抓到回归（本机 89 vs 96）。
/// </summary>
[Collection("RealOS")]
[Trait("Category", "RealOS")]
public class Regression_MonitorDpiAngular_RealOs
{
    private const uint MONITOR_DEFAULTTONEAREST = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct native_point { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(native_point pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    private readonly ITestOutputHelper _out;
    public Regression_MonitorDpiAngular_RealOs(ITestOutputHelper o) => _out = o;

    /// <summary>MONITOR_DPI_TYPE 的字面值是与 SDK 的契约，写错就是静默错档：
    /// 0 = EFFECTIVE（含用户缩放），1 = ANGULAR（物理），2 = DESKTOP。</summary>
    [Fact]
    public void ShcoreDpiType_Constant_IsEffectiveNotAngular()
    {
        var field = typeof(MonitorDpi).GetField("MdEffectiveDpi",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var value = Assert.IsType<int>(field!.GetValue(null));
        Assert.True(value == 0,
            $"GetDpiForMonitor 的 dpiType 必须是 MDT_EFFECTIVE_DPI=0，当前 {value}"
            + "（1 是 MDT_ANGULAR_DPI=面板物理 DPI，会让所有自绘窗口整体错一档）");
    }

    [Fact]
    public void ForPoint_MonitorCenter_MatchesEffectiveDpi()
    {
        var area = Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
        Assert.False(area.IsEmpty, "RealOS：本用例需要一块真实显示器");
        var center = new Point(area.Left + area.Width / 2, area.Top + area.Height / 2);
        var hMonitor = MonitorFromPoint(new native_point { X = center.X, Y = center.Y },
            MONITOR_DEFAULTTONEAREST);
        Assert.NotEqual(IntPtr.Zero, hMonitor);

        Assert.Equal(0, GetDpiForMonitor(hMonitor, 0, out var effective, out _));
        Assert.Equal(0, GetDpiForMonitor(hMonitor, 1, out var angular, out _));
        var sampled = MonitorDpi.ForPoint(center);
        _out.WriteLine($"center={center} effective={effective} angular={angular} ForPoint={sampled}");

        Assert.Equal((int)effective, sampled);
        // 物理 DPI 与有效 DPI 相等的机器（虚拟机/标准面板）上，上一条断言抓不到回归——
        // 这里显式留痕，让"这台机器测不出该缺陷"可查，而不是给个假绿。
        if (angular == effective)
            _out.WriteLine($"NOTE: 本机 angular==effective=={angular}，交叉核对无区分度");
        else
            Assert.NotEqual((int)angular, sampled);
    }
}
