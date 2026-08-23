namespace DshWeb.Domain;

/// <summary>dsh 服务的来源类型。</summary>
public enum DshSource
{
    /// <summary>Launcher 自管的自包含运行时（后台完整构建，重启原子切换）。最高优先级。</summary>
    SelfContained,
    /// <summary>全局 npm 安装（npm install -g @deepseek-ai/dsh）：where dsh 成功。</summary>
    GlobalNpm,
    /// <summary>npm 全局 shim 目录（%APPDATA%\npm\dsh.cmd，PATH 未包含时的回退）。</summary>
    NpmShim,
    /// <summary>npx 缓存（npx -y @deepseek-ai/dsh，未全局安装时的兜底）。</summary>
    NpxCache,
    /// <summary>外部托管（DSH_WEB_URL 指向外部服务，壳不管理生命周期）。</summary>
    External,
}

/// <summary>服务启动时使用的 profile 模式（安全模式重构 ADR-022）。</summary>
public enum DshProfileMode
{
    /// <summary>正常模式：用用户 web profile（默认）。</summary>
    Normal,
    /// <summary>安全模式：用隔离空 profile（<c>DSH_HOME/profiles/.dsh-safe</c>），剥离第三方插件。</summary>
    Safe,
}

/// <summary>
/// dsh 运行时身份：统一"发现、启动、检查、更新"的唯一身份抽象。
///
/// 【核心不变量】：系统中所有涉及"dsh 是什么版本、用什么命令启动"的决策，
/// 必须基于同一个 DshRuntimeIdentity 实例。严禁各模块自行 blind-guess（盲猜）。
///
/// 发现优先级：
/// 0. SelfContained（launcher 自管，后台构建，原子切换）— 最高优先级
/// 1. DSH_VERSION 环境变量（测试钩子/显式覆盖）
/// 2. DSH_WEB_URL → External
/// 3. where dsh → GlobalNpm
/// 4. %APPDATA%\npm\dsh.cmd → NpmShim
/// 5. npx -y → NpxCache
/// </summary>
public sealed record DshRuntimeIdentity(
    DshSource Source,
    string? ExecutablePath,      // 运行时根目录或 dsh.cmd 的物理路径
    string InvocationCommand,    // 实际用于启动的命令
    string? InstalledVersion,    // 当前物理安装/缓存的版本
    string PackageName,          // "@deepseek-ai/dsh"
    DshProfileMode Profile = DshProfileMode.Normal  // 服务 profile 模式（安全模式重构 ADR-022）
)
{
    /// <summary>是否为壳管理的本地安装（SelfContained 或 GlobalNpm 或 NpmShim）。</summary>
    public bool IsLocallyManaged => Source is DshSource.SelfContained or DshSource.GlobalNpm or DshSource.NpmShim;

    /// <summary>是否需要版本检测（NpxCache 时版本不确定，需从 npm registry 比较）。</summary>
    public bool VersionDetectable => InstalledVersion is not null;

    /// <summary>自包含运行时的根目录（仅 SelfContained 类型有效）。</summary>
    public string? RuntimeDir => Source == DshSource.SelfContained ? ExecutablePath : null;

    /// <summary>是否为安全模式启动（Profile == Safe）。</summary>
    public bool IsSafe => Profile == DshProfileMode.Safe;
}
