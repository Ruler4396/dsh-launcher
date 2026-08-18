using Microsoft.Web.WebView2.WinForms;

namespace DshWeb.Managers;

/// <summary>
/// 窗口管理实现：自绘边框/DPI/阴影/主题解析与内部弹窗。
/// 当前为 Program 现有实现的委托（零行为变更）；DshShellForm 本体物理迁移
/// 属于高风险（Win32 消息/焦点特性），待特征开关运行时对比后分步迁入。
/// </summary>
public sealed class WindowManager : IWindowManager
{
    public (Form Form, WebView2 Web) CreatePopup() => DshWeb.Program.CreatePopupForm();
    public void ApplyShadow(IntPtr hwnd) => DshWeb.Program.ApplyWindowShadow(hwnd);
    public bool ResolveDarkMode() => DshWeb.Program.ResolveDarkMode();
}
