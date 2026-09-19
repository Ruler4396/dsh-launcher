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

/// <summary>生产默认实现：屏幕**集合**取自 WinForms Screen（唯一能拿到"有几块屏、各自边界"的
/// 公开入口），但每块屏的工作区**数值**一律经 Win32 <c>GetMonitorInfo().rcWork</c> 重取。
///
/// 为什么不能直接 <c>s.WorkingArea</c>：本接口契约是物理像素，而 PerMonitorV2 下 WinForms 的
/// <c>Screen.WorkingArea</c> 返回逻辑像素（96 DPI 基准——与 NativeMethods/WindowGeometry 里
/// G1/G10 那两处注释同一结论）。旧实现直接把逻辑值当物理值返回，缩放屏上窗口位置恢复的
/// 越界判定因此算小一块屏。用"该屏自身左上角"反查 MonitorFromPoint(TONEAREST) 一定落回同一块屏。
/// rcWork 取不到时回退 WinForms 值并 Warn（宁可位置略偏，不可丢窗）。</summary>
public sealed class WinFormsScreenProvider : IScreenProvider
{
    public IReadOnlyList<Rectangle> GetAllWorkingAreas()
    {
        var list = new List<Rectangle>();
        foreach (System.Windows.Forms.Screen s in System.Windows.Forms.Screen.AllScreens)
        {
            var physical = MonitorWorkArea.ForPoint(new Point(s.Bounds.Left, s.Bounds.Top));
            list.Add(physical.IsEmpty ? s.WorkingArea : physical);
        }
        return list;
    }

    public Rectangle PrimaryWorkingArea
    {
        get
        {
            var primary = System.Windows.Forms.Screen.PrimaryScreen;
            if (primary is null) return Rectangle.Empty;
            var physical = MonitorWorkArea.ForPoint(new Point(primary.Bounds.Left, primary.Bounds.Top));
            return physical.IsEmpty ? primary.WorkingArea : physical;
        }
    }
}
