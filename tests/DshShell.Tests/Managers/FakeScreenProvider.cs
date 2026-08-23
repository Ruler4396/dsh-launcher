using System.Drawing;
using DshWeb.Win32;

namespace DshShell.Tests.Managers;

/// <summary>
/// 假屏幕拓扑（Headless 多显示器测试）：注入任意数量、任意分辨率 / 位置的"显示器"工作区，
/// 供 <see cref="IScreenProvider"/> 消费者（Program.RestoreWindowPosition 等）在无真实硬件
/// 环境下做确定性回归。坐标一律为物理像素（与生产 WinFormsScreenProvider 语义一致）。
/// </summary>
public sealed class FakeScreenProvider : IScreenProvider
{
    private readonly IReadOnlyList<Rectangle> _workingAreas;
    private readonly Rectangle _primary;

    public FakeScreenProvider(Rectangle primary, params Rectangle[] secondaries)
    {
        _primary = primary;
        _workingAreas = new[] { primary }.Concat(secondaries).ToArray();
    }

    /// <summary>便捷工厂：主屏 + 左侧副屏（负坐标，150% 异构 DPI 拓扑常用）。</summary>
    public static FakeScreenProvider PrimaryLeftSecondary(
        Size primary = default, Size secondary = default, Size primaryOffset = default)
    {
        var p = primary == default ? new Size(1920, 1080) : primary;
        var s = secondary == default ? new Size(1920, 1080) : secondary;
        return new FakeScreenProvider(
            new Rectangle(primaryOffset.Width, primaryOffset.Height, p.Width, p.Height),
            new Rectangle(-s.Width, 0, s.Width, s.Height)); // 副屏位于主屏左侧
    }

    public IReadOnlyList<Rectangle> GetAllWorkingAreas() => _workingAreas;
    public Rectangle PrimaryWorkingArea => _primary;
}
