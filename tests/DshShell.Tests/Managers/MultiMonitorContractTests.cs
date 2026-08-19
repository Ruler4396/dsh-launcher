using System.Drawing;
using DshWeb;
using Xunit;

namespace DshShell.Tests.Managers;

/// <summary>
/// 多显示器 Headless 契约测试（v0.4.0，替代 CI 内核虚拟显示驱动方案）：
/// 对 <see cref="ShellLogic.RestoreWindowPosition"/> 的极端纯函数矩阵。
/// 无真实硬件、无 WinForms 进程级 Screen 缓存依赖——CI 直接以 dotnet test 运行。
///
/// 坐标系约定：全部为**物理像素**（与生产 WinFormsScreenProvider/Screen.WorkingArea 一致）。
/// </summary>
public class MultiMonitorContractTests
{
    private static readonly Rectangle Primary = new(0, 0, 1920, 1080);
    private static readonly Rectangle LeftSecondary = new(-1920, 0, 1920, 1080); // 主屏左侧副屏

    private static (int X, int Y) Restore(
        int x, int y, int w, int h,
        Rectangle primary, params Rectangle[] others)
        => ShellLogic.RestoreWindowPosition(x, y, w, h,
            new[] { primary }.Concat(others).ToArray(), primary);

    // ---------------- Case 1：副屏正常工作，窗口在副屏内 → 坐标不变 ----------------

    [Fact]
    public void WindowInsideSecondary_KeepsPosition()
    {
        // 窗口 (-1800, 100) 尺寸 800x600，完全位于副屏工作区内
        var (x, y) = Restore(-1800, 100, 800, 600, Primary, LeftSecondary);
        Assert.Equal((-1800, 100), (x, y));
    }

    [Fact]
    public void WindowPartiallyOutsideSecondaryLeftEdge_ClampedWithinSecondary()
    {
        // 窗口 (-1950, 100) 尺寸 500x600：与副屏交集 [-1920,-1450] 宽 470（≥120），
        // 与主屏交集 0 → 匹配副屏 → 左钳进副屏（-1950 → -1920）。
        var (x, y) = Restore(-1950, 100, 500, 600, Primary, LeftSecondary);
        Assert.Equal((-1920, 100), (x, y));
    }

    // ---------------- Case 2：副屏拔掉越界容灾 → 回退主屏居中 ----------------

    [Fact]
    public void SecondaryRemoved_OffScreenWindow_CenteredOnPrimary()
    {
        // 原副屏坐标 (-2000, 500) 在主屏坐标系完全不可见（仅剩主屏）
        var (x, y) = Restore(-2000, 500, 800, 600, Primary);
        Assert.Equal(((1920 - 800) / 2, (1080 - 600) / 2), (x, y)); // (560, 240) 主屏居中
    }

    [Fact]
    public void SecondaryRemoved_OffScreenLargeWindow_ClampedIntoPrimary()
    {
        // 窗口比主屏还大（任务栏收缩场景）：居中计算为负 → 钳制回主屏原点
        var (x, y) = Restore(-2000, 500, 2000, 1200, Primary);
        Assert.Equal((0, 0), (x, y)); // 钳到主屏左上角，不出现负坐标
    }

    // ---------------- Case 3：高 DPI 逻辑/物理混用回归 ----------------

    [Fact]
    public void DpiMixed_LogicalCoords_WindowStaysOnSecondary_NotTeleported()
    {
        // 150% DPI 陷阱复现：调用方误传**逻辑坐标**（位置在副屏内、尺寸偏小 1/1.5），
        // 但逻辑坐标仍与副屏物理工作区有足够交集 → 必须保持副屏，不得弹回主屏。
        var logicalX = -1000;            // 物理对应 -1500
        var logicalW = 800 / 1;          // 逻辑尺寸（相对物理偏小）——交集判断按实际传入值
        var (x, y) = Restore(logicalX, 100, logicalW, 400, Primary, LeftSecondary);
        // 与副屏交集 [−1920,0]×[0,1080] 足够 → 保持在副屏坐标系（不弹回主屏）
        Assert.Equal((-1000, 100), (x, y));
    }

    [Fact]
    public void DpiMixed_PhysicalWindowFullyOnSecondary_KeepsPosition()
    {
        // 物理坐标窗口 (-1500, 100) 尺寸 900x600：仅与副屏相交（主屏交集 0）→ 保持副屏不变
        var (x, y) = Restore(-1500, 100, 900, 600, Primary, LeftSecondary);
        Assert.Equal((-1500, 100), (x, y));
    }

    // ---------------- 补充边界：跨屏窗口、任务栏收缩、零尺寸 ----------------

    [Fact]
    public void WindowSpanningBothScreens_ClampedIntoFirstMatch()
    {
        // 窗口横跨两屏（[-1500,0] 伸入主屏 500px）：RestoreWindowPosition 契约按枚举顺序
        // 匹配**第一个**有 ≥120×60 交集的屏幕（生产 Screen.AllScreens 主屏通常在前）→
        // 钳制到主屏工作区。关键不变式：窗口永远不飞出可见屏幕。
        var (x, y) = Restore(-1500, 100, 2000, 600, Primary, LeftSecondary);
        Assert.Equal((0, 100), (x, y)); // 主屏钳制：x=-1500 → 0
    }

    [Fact]
    public void WorkAreaSmallerThanWindow_NoNegativeClamp()
    {
        // 工作区比窗口小（极端任务栏/超小屏）：Math.Max(0, ...) 防止负上限
        var tiny = new Rectangle(0, 0, 300, 200);
        var (x, y) = Restore(10, 10, 800, 600, tiny);
        Assert.Equal((0, 0), (x, y));
    }

    [Fact]
    public void ZeroSizedWindow_FallsBackToPrimaryCenter()
    {
        // 宽高 0 → 内部 Math.Max(,1)：交集 1×1 < 120×60 → 回退主屏居中（不抛异常）
        var (x, y) = Restore(100, 100, 0, 0, Primary);
        Assert.Equal(((1920 - 1) / 2, (1080 - 1) / 2), (x, y)); // (959, 539)
    }
}
