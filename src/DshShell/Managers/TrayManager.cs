namespace DshWeb.Managers;

/// <summary>
/// 托盘管理实现：托盘图标与主题监听。
/// 当前为 Program 现有实现的委托（零行为变更）；托盘菜单/生命周期切换后续物理迁移。
/// </summary>
public sealed class TrayManager : ITrayManager
{
    public void EnsureTray(Form owner, bool force = false) => WindowManager.Instance.EnsureTrayIcon(owner, force);
    public void RegisterThemeWatcher(Form form) => DshWeb.Program.RegisterThemeWatcher(form);
}
