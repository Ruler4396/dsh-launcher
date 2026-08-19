using System.Drawing;

namespace DshWeb.Win32;

/// <summary>
/// 屏幕拓扑抽象（v0.4.0 多显示器 Headless 化）：隔离 WinForms <see cref="System.Windows.Forms.Screen"/>
/// 的进程级缓存依赖，使窗口位置恢复 / 最大化决策可注入任意"假显示器"拓扑测试。
///
/// 生产默认实现 <see cref="WinFormsScreenProvider"/>（包装 Screen.AllScreens / PrimaryScreen）；
/// 测试注入 <see cref="FakeScreenProvider"/>（任意数量、任意分辨率 / DPI 的假屏幕，见
/// tests/DshShell.Tests/Managers/FakeScreenProvider.cs）。
/// 返回值均为**物理像素坐标**（与 ShellLogic 纯函数 / WindowGeometry 的坐标系约定一致）。
/// </summary>
public interface IScreenProvider
{
    /// <summary>全部屏幕的工作区（物理像素）。</summary>
    IReadOnlyList<Rectangle> GetAllWorkingAreas();

    /// <summary>主屏幕工作区（物理像素）；无法解析时返回 Rectangle.Empty。</summary>
    Rectangle PrimaryWorkingArea { get; }
}

/// <summary>生产默认实现：包装 WinForms Screen（进程启动时由系统缓存；本地/CI 单屏即单元素列表）。</summary>
public sealed class WinFormsScreenProvider : IScreenProvider
{
    public IReadOnlyList<Rectangle> GetAllWorkingAreas()
        => System.Windows.Forms.Screen.AllScreens.Select(s => s.WorkingArea).ToList();

    public Rectangle PrimaryWorkingArea
        => System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty;
}
