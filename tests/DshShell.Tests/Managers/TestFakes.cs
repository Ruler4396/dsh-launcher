using DshWeb;
using DshWeb.Domain;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Managers;

/// <summary>
/// 环境敏感测试的串行集合：这些类直接驱动 LauncherApp/引擎并读取进程级 DSH_* 环境变量，
/// 必须串行执行以防保存/恢复相互踩踏（xUnit 默认按类并行）。
/// </summary>
[CollectionDefinition("EnvHygiene")]
public sealed class EnvHygieneCollection { }

/// <summary>宿主机环境消毒：开发机 GUI 会话常驻 DSH_WEB_URL（本启动器在跑！）等变量，
/// 会把组合根推入"外部托管"分支、或用版本钩子覆盖物理发现。Headless 测试一律先清除。</summary>
internal static class EnvHygiene
{
    public static void ClearHostileEnv()
    {
        Environment.SetEnvironmentVariable("DSH_WEB_URL", null);
        Environment.SetEnvironmentVariable("DSH_VERSION", null);
    }
}

/// <summary>
/// 测试用身份样例工厂（ADR-024）：Headless 测试统一从这里取合法 DshRuntimeIdentity，
/// 保证"跨模块只传 Identity"的契约在测试侧同样成立（不散装拼路径字符串）。
/// </summary>
public static class IdentityFixtures
{
    /// <summary>最小可直启身份：node.exe × JS 入口齐备（GlobalNpm 形态）。</summary>
    public static DshRuntimeIdentity Launchable(string version = "9.9.9-test")
        => new(DshSource.GlobalNpm,
            NodeExePath: @"C:\fake-tools\node.exe",
            DshEntryJsPath: @"C:\fake-tools\node_modules\@deepseek-ai\dsh\bin.js",
            Version: version);

    /// <summary>自包含运行时身份（RuntimeDir 可从入口推导）。</summary>
    public static DshRuntimeIdentity SelfContained(string version = "9.9.9-test")
        => new(DshSource.SelfContained,
            NodeExePath: @"C:\fake-tools\node.exe",
            DshEntryJsPath: $@"C:\fake-runtimes\{version}\node_modules\@deepseek-ai\dsh\bin.js",
            Version: version);

    /// <summary>无物理安装的兜底身份（NpxCache：无 node/入口，不可直启）。</summary>
    public static DshRuntimeIdentity NpxCache() => new(DshSource.NpxCache, null, null, null);

    /// <summary>安全模式身份（隔离 profile 路径已应用）。</summary>
    public static DshRuntimeIdentity SafeProfiled(string profileDir)
        => Launchable().WithProfile(profileDir);
}

/// <summary>
/// 共享 Fake IRuntimeManager（ADR-024 契约：EnsureRuntimeAsync 返回携带 Identity 的 Resolution）。
/// Headless 场景默认返回可直启身份；失败/异常经属性注入。
/// </summary>
public sealed class FakeRuntime : IRuntimeManager
{
    public RuntimeResolution Result { get; init; } =
        RuntimeResolution.Ready(IdentityFixtures.Launchable());
    public string? Prepend { get; private set; }
    public Exception? ThrowOnEnsure { get; init; }

    public Task<RuntimeResolution> EnsureRuntimeAsync(CancellationToken ct = default)
    {
        if (ThrowOnEnsure is not null) throw ThrowOnEnsure;
        return Task.FromResult(Result);
    }

    public void PrependToPath(string r) => Prepend = r;
}

/// <summary>
/// 共享 Fake IServiceManager（ADR-024 契约：Start(identity,...) 记录调用与身份证据）。
/// 就绪轮询/僵尸清理行为可注入；PollReadiness 缺省映射 Ready→"ready"/否则 "timeout"。
/// </summary>
public sealed class FakeService : IServiceManager
{
    public bool Ready { get; init; }
    public ShellLogic.ServicePortState PortState { get; init; } = ShellLogic.ServicePortState.Healthy;
    public bool KillZombieResult { get; init; } = true;
    public int KillZombieCalls { get; private set; }

    // ---- Start(Identity) 契约证据 ----
    public bool StartResult { get; init; } = true;
    public int StartCalls { get; private set; }
    public (DshRuntimeIdentity Identity, int Port, string? LogPath)? LastStartArgs { get; private set; }

    public bool NeedsStart(int port) => PortState == ShellLogic.ServicePortState.Closed;

    public bool Start(DshRuntimeIdentity identity, int port, string? logPath = null)
    {
        StartCalls++;
        LastStartArgs = (identity, port, logPath);
        return StartResult;
    }

    public string PollReadiness(CancellationToken token, int port, string url, string logPath, bool e2eMode)
        => Ready ? "ready" : "timeout";

    public Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken ct = default)
        => Task.FromResult(Ready);

    public ShellLogic.ServicePortState ProbePort(int port, string url) => PortState;
    public bool KillZombieTree(int port) { KillZombieCalls++; return KillZombieResult; }
}
