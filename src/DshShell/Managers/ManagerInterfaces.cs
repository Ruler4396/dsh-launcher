namespace DshWeb.Managers;

/// <summary>运行时(Node)管理：解析/下载/校验便携 Node，注入 PATH。剥离自 RuntimeResolver，改为可注入。</summary>
public interface IRuntimeManager
{
    /// <summary>尝试确保可用 Node 环境（PATH/注册表/便携），必要时触发便携下载。返回就绪状态。</summary>
    Task<RuntimeResult> EnsureRuntimeAsync(CancellationToken ct = default);

    /// <summary>把便携 Node 目录前插到进程级 PATH（供子进程继承）。</summary>
    void PrependToPath(string nodeRoot);
}

/// <summary>RuntimeResult.Root 含便携目录时调用 PrependToPath。</summary>
public sealed record RuntimeResult(bool Ok, bool Ready, string? NodeExe, bool IsPortable, string? RootDir)
{
    public static RuntimeResult ReadyNow(string nodeExe) => new(true, true, nodeExe, false, null);
    public static RuntimeResult Portable(RuntimeResolver.NodeEnvironment env) => new(true, false, env.NodeExe, env.IsPortable, env.RootDir);
    public static RuntimeResult Failed(string? code, string? detail) => new(false, false, null, false, null);
}

/// <summary>dsh 服务管理：端口/HTTP 就绪探测与启动决策（进程/僵尸/HTTP 探测）。</summary>
public interface IServiceManager
{
    /// <summary>是否需要拉起服务（端口未开）。</summary>
    bool NeedsStart(int port);

    /// <summary>就绪前轮询（端口+HTTP），超时返回 false。探针可注入以便测试。</summary>
    Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>WebView2 初始化/崩溃恢复/权限（占位接口，后续从 InitWebViewAsync 迁移）。</summary>
public interface IWebViewManager
{
    // Task<WebView2> CreateAsync(CoreWebView2Environment env, string userDataFolder);  // 后续迁移
}

/// <summary>主窗口/自定义边框/DPI/Win32 消息（占位接口，后续迁移 DshShellForm）。</summary>
public interface IWindowManager
{
    // Form CreateMainWindow(WebView2 web);  // 后续迁移
}

/// <summary>托盘图标/菜单（占位接口，后续迁移）。</summary>
public interface ITrayManager
{
    // void EnsureTray(Form owner, ...);  // 后续迁移
}
