using Microsoft.Web.WebView2.WinForms;

namespace DshWeb.Managers;

/// <summary>运行时(Node)管理：解析/下载/校验便携 Node，注入 PATH。剥离自 RuntimeResolver，改为可注入。</summary>
public interface IRuntimeManager
{
    /// <summary>尝试确保可用 Node 环境（PATH/注册表/便携），必要时触发便携下载。返回就绪状态。</summary>
    Task<RuntimeResult> EnsureRuntimeAsync(CancellationToken ct = default);

    /// <summary>把便携 Node 目录前插到进程级 PATH（供子进程继承）。</summary>
    void PrependToPath(string nodeRoot);
}

/// <summary>
/// RuntimeResult.Root 含便携目录时调用 PrependToPath。
/// ErrorCode/ErrorDetail 承载失败语义（E1002-E1005 等）：此前 Failed() 工厂丢弃 code/detail，
/// 组合根只能记"运行时解析失败"而无法区分"校验和不匹配(E1004)/下载失败(E1003)"——修复见
/// LauncherAppScenarioTests.RuntimeFailure_E1004_LogsErrorCode。
/// </summary>
public sealed record RuntimeResult(
    bool Ok, bool Ready, string? NodeExe, bool IsPortable, string? RootDir,
    string? ErrorCode = null, string? ErrorDetail = null)
{
    public static RuntimeResult ReadyNow(string nodeExe) => new(true, true, nodeExe, false, null);
    public static RuntimeResult Portable(RuntimeResolver.NodeEnvironment env) => new(true, false, env.NodeExe, env.IsPortable, env.RootDir);
    public static RuntimeResult Failed(string? code, string? detail) => new(false, false, null, false, null, code, detail);
}

/// <summary>dsh 服务管理：端口/HTTP 就绪探测与启动决策（进程/僵尸/HTTP 探测）。</summary>
public interface IServiceManager
{
    /// <summary>是否需要拉起服务（端口未开）。</summary>
    bool NeedsStart(int port);

    /// <summary>就绪前轮询（端口+HTTP），超时返回 false。探针可注入以便测试。</summary>
    Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>WebView2 初始化/崩溃恢复/权限，并管理与主窗的绑定。</summary>
public interface IWebViewManager
{
    /// <summary>初始化 WebView2（设置/权限/下载/弹窗/崩溃自愈）并绑定到宿主控件。</summary>
    Task InitializeAsync(WebView2 web, string userDataFolder);
}

/// <summary>主窗口/自定义边框/DPI/Win32 消息与主题。</summary>
public interface IWindowManager
{
    /// <summary>创建同源内部弹窗（轻量壳窗口，保留会话）。</summary>
    (Form Form, WebView2 Web) CreatePopup();
    /// <summary>给无边框窗口加 DWM 阴影。</summary>
    void ApplyShadow(IntPtr hwnd);
    /// <summary>解析当前的深色/浅色主题。</summary>
    bool ResolveDarkMode();
}

/// <summary>托盘图标/菜单与主题监听。</summary>
public interface ITrayManager
{
    /// <summary>确保托盘图标存在（按需显示）。</summary>
    void EnsureTray(Form owner, bool force = false);
    /// <summary>注册主题监听（系统/文件变化 → 即时切换）。</summary>
    void RegisterThemeWatcher(Form form);
}
