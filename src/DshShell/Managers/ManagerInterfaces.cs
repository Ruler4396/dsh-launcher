using Microsoft.Web.WebView2.WinForms;

namespace DshWeb.Managers;

/// <summary>
/// 运行时(Node)管理：解析/下载/校验便携 Node，注入 PATH。
/// 【ADR-024 契约】本接口只产出 <see cref="RuntimeResolution"/>——其唯一有效载荷是
/// <see cref="DshWeb.Domain.DshRuntimeIdentity"/>；严禁在 Manager 之间传递散装的
/// "node 路径字符串 / 版本号字符串"（身份必须整体流动）。
/// </summary>
public interface IRuntimeManager
{
    /// <summary>尝试确保可用 Node 环境（PATH/注册表/便携），必要时触发便携下载。
    /// 成功时返回携带完整 dsh 运行时身份的 Resolution。</summary>
    Task<RuntimeResolution> EnsureRuntimeAsync(CancellationToken ct = default);

    /// <summary>把便携 Node 目录前插到进程级 PATH（供子进程继承）。</summary>
    void PrependToPath(string nodeRoot);
}

/// <summary>
/// 运行时解析结果：Ok 时 Identity 必非 null（发现/启动/更新共用同一身份实例）；
/// 失败时 ErrorCode/ErrorDetail 承载语义（E1002-E1005），Identity 为 null。
/// </summary>
public sealed record RuntimeResolution(
    bool Ok,
    DshWeb.Domain.DshRuntimeIdentity? Identity,
    string? ErrorCode = null,
    string? ErrorDetail = null)
{
    public static RuntimeResolution Ready(DshWeb.Domain.DshRuntimeIdentity identity) => new(true, identity);
    public static RuntimeResolution Failed(string? code, string? detail) => new(false, null, code, detail);
}

/// <summary>dsh 服务管理：端口/HTTP 就绪探测与启动决策（进程/僵尸/HTTP 探测）。</summary>
public interface IServiceManager
{
    /// <summary>是否需要拉起服务（端口未开）。</summary>
    bool NeedsStart(int port);

    /// <summary>就绪前轮询（端口+HTTP），超时返回 false。探针可注入以便测试。</summary>
    Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken ct = default);

    /// <summary>
    /// 生产级就绪裁决轮询：TCP+HTTP + 统一日志错误标志三态（ready/canceled/logerror/timeout），
    /// 含首装网络回退预算与快速轮询曲线。由组合根经 ReadinessProbe 注入 LauncherApp。
    /// 【F2/F26】错误标志判定为**增量扫描**（只看入口后新增字节 + 跳过壳自写行）；
    /// delay/间隔/宽限可注入（虚拟时钟驱动），缺省值保持生产行为（Thread.Sleep/5s/15s）。
    /// </summary>
    string PollReadiness(CancellationToken token, int port, string url, string logPath, bool e2eMode,
        Action<TimeSpan>? delay = null, int logCheckIntervalSeconds = 5, int logErrorGraceSeconds = 15);

    /// <summary>端口三重验证（TCP + 进程身份 + 快速 HTTP）：区分健康/僵尸/被占用，供启动决策。</summary>
    ShellLogic.ServicePortState ProbePort(int port, string url);

    /// <summary>强杀僵尸进程树并等待端口释放（taskkill /T /F，含 cmd/npx 外壳）。返回是否清理成功。</summary>
    bool KillZombieTree(int port);

    /// <summary>
    /// 【ADR-024 铁律】按 <see cref="DshWeb.Domain.DshRuntimeIdentity"/> 拉起 dsh 服务：
    /// 启动命令**只能**由 Identity.NodeExePath × Identity.DshEntryJsPath 拼装
    /// （node.exe 直启 JS 入口）——严禁 cmd.exe / wscript / .cmd shim 中间层。
    /// Identity.ProfilePath 非空时以 `--profile &lt;name&gt;` 取代 web 子命令（ADR-022 安全模式）。
    /// 返回 false 表示拉起失败（E2001 语义）。
    /// </summary>
    bool Start(DshWeb.Domain.DshRuntimeIdentity identity, int port, string? logPath = null);
}

/// <summary>
/// 【ADR-024】dsh 更新引擎（跨模块编排唯一入口）：版本比对、pending 更新事务、
/// 首装全局安装——全部基于 <see cref="DshWeb.Domain.DshRuntimeIdentity"/> 决策，
/// 严禁接收"裸版本字符串指令"。
/// </summary>
public interface IDshUpdateManager
{
    /// <summary>基于身份的更新判定：remoteVersion 严格大于 identity.Version 才算有新版。</summary>
    bool NeedsUpdate(DshWeb.Domain.DshRuntimeIdentity local, string? remoteVersion);

    /// <summary>
    /// 首装链：当前身份为 NpxCache（本机无任何物理安装）时执行 npm -g 安装 @deepseek-ai/dsh，
    /// 共享预算策略（ShellLogic.ProvisionPolicy）；成功后失效发现缓存。其余来源直接返回 true。
    /// 失败详情经 <see cref="FirstRunProvisionError"/> 暴露（[E1012] 展示用）。
    /// </summary>
    bool EnsureDshInstalled(DshWeb.Domain.DshRuntimeIdentity current);

    /// <summary>
    /// 应用 pending 更新事务：SelfContained 原子切换（零 npm）/ npm -g 兜底两路径，
    /// 失败按 IsRetryableNpmError 决定 pending 保留或清理。物理终局以
    /// DshDiscovery.DiscoverCurrentRuntime() 重发现为准（FP1 防线）。
    /// </summary>
    void ApplyPending(CancellationToken ct = default, Action<string>? progress = null);

    /// <summary>启动早期待应用更新决策编排（ApplyNow/ClearPending/PromptRestart/None 矩阵接线）。</summary>
    void HandlePendingAtStartup(CancellationToken ct, Action<string>? progress, Func<int, bool> portOpen);

    /// <summary>apply 开始前记录的运行身份版本（npm 回滚降级目标；组合根读取武装 update-guard）。</summary>
    string? PreApplyIdentityVersion { get; }

    /// <summary>apply 成功落地的版本（原子切换/npm 均含）；组合根订阅以武装回滚闸门。</summary>
    event Action<string>? UpdateApplied;

    /// <summary>首装全局安装失败的用户可见详情（E1012 展示用）；null = 未尝试或已成功。</summary>
    string? FirstRunProvisionError { get; }

    /// <summary>更新应用失败通知回调（UI 收口：E4002 弹窗/pending 策略日志）；由组合根装配。</summary>
    Action<string, string>? NotifyApplyFailed { get; set; }

    /// <summary>首装全局安装进度回调（Splash 滚动文案，含 [warn] 降级告警）；由组合根装配。</summary>
    Action<string>? ProvisionProgress { get; set; }
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
