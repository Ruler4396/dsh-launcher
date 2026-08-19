using System.Drawing;
using DshWeb;
using Xunit;

namespace DshShell.Tests.Managers;

/// <summary>
/// IScreenProvider 集成测试（v0.4.0 Headless 化）：注入 Fake 拓扑，验证
/// <see cref="Program.RestoreWindowPosition"/> 经 <see cref="Program.ScreenProvider"/>
/// 正确取屏幕数据并调用 ShellLogic 纯函数——覆盖生产调用点（Program.cs 建窗恢复）的接线。
/// </summary>
public class ScreenProviderIntegrationTests : IDisposable
{
    private readonly DshWeb.Win32.IScreenProvider _original;

    public ScreenProviderIntegrationTests() => _original = Program.ScreenProvider;

    public void Dispose() => Program.ScreenProvider = _original; // 还原，避免污染其他测试

    /// <summary>主屏 4K + 左侧 1080p 副屏（负坐标，物理像素）。</summary>
    private static readonly FakeScreenProvider UhdPlusFhd = new(
        new Rectangle(0, 0, 3840, 2160),          // 主屏 4K
        new Rectangle(-1920, 0, 1920, 1080));     // 左侧 1080p 副屏

    [Fact]
    public void Restore_InsideSecondary_KeepsPosition()
    {
        Program.ScreenProvider = UhdPlusFhd;

        // 窗口记录在副屏（-1500, 200），尺寸 1200x800 → 完全可见 → 坐标不变
        var (x, y) = Program.RestoreWindowPosition(-1500, 200, 1200, 800);
        Assert.Equal((-1500, 200), (x, y));
    }

    [Fact]
    public void Restore_SecondaryDisconnected_FallsBackToPrimaryCenter()
    {
        // 副屏拔掉后仅剩主屏：原副屏坐标 (-1700, 300) 完全越界 → 主屏 4K 居中
        Program.ScreenProvider = new FakeScreenProvider(new Rectangle(0, 0, 3840, 2160));

        var (x, y) = Program.RestoreWindowPosition(-1700, 300, 1200, 800);
        Assert.Equal(((3840 - 1200) / 2, (2160 - 800) / 2), (x, y)); // (1320, 680)
    }

    [Fact]
    public void Restore_OnPrimary_ClampedWithinWorkArea()
    {
        Program.ScreenProvider = UhdPlusFhd;

        // 窗口在主屏但超出右/下边界（任务栏收缩）：钳制进主屏工作区
        var (x, y) = Program.RestoreWindowPosition(3600, 2000, 800, 600);
        Assert.Equal((3840 - 800, 2160 - 600), (x, y)); // (3040, 1560)
    }

    [Fact]
    public void Restore_DpiMixed_150PercentSecondary_StaysOnSecondary()
    {
        // 150% DPI 副屏：物理 1920x1080 工作区，窗口以逻辑坐标记录（仍落在副屏范围）
        Program.ScreenProvider = UhdPlusFhd;

        var (x, y) = Program.RestoreWindowPosition(-1000, 200, 800, 500);
        Assert.Equal((-1000, 200), (x, y)); // 与副屏交集足够 → 保持副屏，不弹回主屏
    }
}
