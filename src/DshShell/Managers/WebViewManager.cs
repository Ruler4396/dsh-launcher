using Microsoft.Web.WebView2.WinForms;

namespace DshWeb.Managers;

/// <summary>
/// WebView2 管理实现。当前托管 Program.InitWebViewAsync 的委托（零行为变更，仅暴露接口边界）；
/// 待 DSH_USE_NEW_LIFECYCLE 运行时对比校验后，再逐步把内部逻辑物理迁入本类。
/// </summary>
public sealed class WebViewManager : IWebViewManager
{
    public Task InitializeAsync(WebView2 web, string userDataFolder)
        => DshWeb.Program.InitWebViewAsync(web, userDataFolder);
}
