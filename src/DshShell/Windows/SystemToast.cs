using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace DshWeb.Windows;

/// <summary>
/// 系统 Toast 通知（v0.4.1）：经 WinRT 投影（windows10.0.19041 TFM）直接调用
/// Windows.UI.Notifications，不依赖托盘图标 NotifyIcon。
/// - AUMID "dsh-launcher" 首次使用时注册（DisplayName + 可选 app.ico 图标）；
/// - TryShow 绝不抛出：任何失败（无 Appx 感知/注册表拒绝等）降级 Warn 并返回 false，
///   调用方据此记日志（更新通知不可用时用户仍可从统一日志获知）。
/// XML 内容由 ShellLogic.ToastPolicy.BuildToastXml 统一构造（转义/长度策略单点收口）。
/// </summary>
internal static class SystemToast
{
    private static bool _aumidEnsured;

    /// <summary>尽力显示系统 Toast。返回是否成功（失败仅 Warn，绝不抛出）。</summary>
    internal static bool TryShow(Form? uiOwner, string title, string body, TimeSpan expireAfter, Action? onClick)
    {
        try
        {
            EnsureAumidRegistered();
            var xml = new XmlDocument();
            xml.LoadXml(ShellLogic.ToastPolicy.BuildToastXml(title, body));
            var toast = new ToastNotification(xml)
            {
                ExpirationTime = DateTimeOffset.Now + expireAfter,
            };
            if (onClick is not null)
            {
                var onClickCapture = onClick;
                toast.Activated += (_, _) =>
                {
                    try
                    {
                        // 点击回调封送 UI 线程（Toast 回调在系统线程池）
                        if (uiOwner != null && !uiOwner.IsDisposed && uiOwner.IsHandleCreated && uiOwner.InvokeRequired)
                            uiOwner.BeginInvoke(onClickCapture);
                        else
                            onClickCapture();
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("toast click handler failed", ctx: new { error = ex.Message });
                    }
                };
            }
            ToastNotificationManager.CreateToastNotifier("dsh-launcher").Show(toast);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("system toast failed", ctx: new { error = ex.Message });
            return false;
        }
    }

    /// <summary>首次使用时注册 HKCU AppUserModelId（显示名 + 图标），让 Toast 有身份可挂。</summary>
    private static void EnsureAumidRegistered()
    {
        if (_aumidEnsured) return;
        using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"SOFTWARE\Classes\AppUserModelId\dsh-launcher"))
        {
            key.SetValue("DisplayName", "dsh-launcher");
            try
            {
                var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
                if (File.Exists(iconPath))
                    key.SetValue("IconUri", new Uri(iconPath).ToString());
            }
            catch { /* 图标可选 */ }
        }
        _aumidEnsured = true;
    }
}
