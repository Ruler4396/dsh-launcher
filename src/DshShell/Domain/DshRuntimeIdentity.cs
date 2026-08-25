namespace DshWeb.Domain;

/// <summary>dsh 服务的来源类型（ADR-024 收敛为四类；旧 NpmShim 并入 GlobalNpm——
/// %APPDATA%\npm\dsh.cmd 本就是"全局 npm 安装"在 Windows 上的物理形态，单独建模曾制造
/// "检测的 dsh ≠ 启动的 dsh"的身份裂缝）。</summary>
public enum DshSource
{
    /// <summary>Launcher 自管的自包含运行时（后台完整构建，重启原子切换）。最高优先级。</summary>
    SelfContained,
    /// <summary>全局 npm 安装：where dsh 命中，或 %APPDATA%\npm\dsh.cmd shim 存在。</summary>
    GlobalNpm,
    /// <summary>npx 缓存/兜底（本机无任何物理安装，版本不确定）。</summary>
    NpxCache,
    /// <summary>外部托管（DSH_WEB_URL 指向外部服务，壳不管理生命周期）。</summary>
    External,
}

/// <summary>
/// dsh 运行时身份（ADR-024）：系统中"发现、启动、检查、更新"的**唯一真相源**。
///
/// 【核心不变量 I-IDENTITY】：
/// 1. 所有涉及"dsh 是什么版本、用什么命令拉起、要不要更新"的决策，
///    必须基于同一个 DshRuntimeIdentity 实例——严禁各模块自行盲猜版本号字符串。
/// 2. 跨模块交互只传 Identity，不传"版本号字符串 / 包名 / 相对路径"散装数据。
/// 3. 服务启动命令只能由 <see cref="NodeExePath"/> × <see cref="DshEntryJsPath"/> 拼装
///    （node.exe 直启 JS 入口），彻底消灭 cmd.exe / wscript / .cmd shim 中间层（ADR-021 延伸）。
///
/// 发现优先级（DshDiscovery 唯一合法产出点）：
/// 0. SelfContained（launcher 自管，后台构建，原子切换）— 最高优先级
/// 1. DSH_VERSION 环境变量（测试钩子/显式覆盖，覆盖所有 Source 的 Version）
/// 2. DSH_WEB_URL → External
/// 3. where dsh 或 %APPDATA%\npm\dsh.cmd → GlobalNpm
/// 4. npx -y 兜底 → NpxCache
/// </summary>
/// <param name="Source">运行时来源（发现回退链的落点）。</param>
/// <param name="NodeExePath">node.exe 的绝对物理路径；External/未解析时为 null。</param>
/// <param name="DshEntryJsPath">dsh 真实 JS 入口绝对路径（绕过 .cmd shim）；未解析时为 null。</param>
/// <param name="Version">当前物理安装/缓存的版本；NpxCache 未探测到时为 null。</param>
/// <param name="ProfilePath">
/// 服务 profile 路径：正常模式为 null（dsh 默认 web profile）；安全模式（ADR-022 L1/L2）
/// 为隔离 profile 的绝对目录（&lt;dsh-home&gt;\profiles\.dsh-safe）。启动参数只取其目录名
/// （dsh --profile 仅收 name，无分隔符——见 SafeProfileBuilder.SafeProfileName 契约）。
/// </param>
public sealed record DshRuntimeIdentity(
    DshSource Source,
    string? NodeExePath,
    string? DshEntryJsPath,
    string? Version,
    string? ProfilePath = null)
{
    /// <summary>是否为壳管理的本地安装（SelfContained 或 GlobalNpm）。</summary>
    public bool IsLocallyManaged => Source is DshSource.SelfContained or DshSource.GlobalNpm;

    /// <summary>是否具备"node.exe 直启 JS 入口"的全部物理要件（服务拉起的硬前提）。</summary>
    public bool CanLaunchDirectly => NodeExePath is not null && DshEntryJsPath is not null;

    /// <summary>是否以隔离安全 profile 启动（ADR-022）。</summary>
    public bool IsSafeProfile => !string.IsNullOrWhiteSpace(ProfilePath);

    /// <summary>
    /// SelfContained 运行时根目录：从入口路径剥去 node_modules\@deepseek-ai\dsh\ 尾段。
    /// 用作子进程 WorkingDirectory（dsh 相对解析其自身资源）。非 SelfContained 返回 null。
    /// </summary>
    public string? RuntimeDir
    {
        get
        {
            if (Source != DshSource.SelfContained || DshEntryJsPath is null) return null;
            var marker = string.Concat(Path.DirectorySeparatorChar, "node_modules",
                Path.DirectorySeparatorChar, "@deepseek-ai", Path.DirectorySeparatorChar,
                "dsh", Path.DirectorySeparatorChar);
            var idx = DshEntryJsPath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            return idx > 0 ? DshEntryJsPath[..idx] : null;
        }
    }

    /// <summary>返回应用了指定 profile 路径的新身份（不可变，with 语义；启动链使用）。</summary>
    public DshRuntimeIdentity WithProfile(string? profilePath) => this with { ProfilePath = profilePath };
}
