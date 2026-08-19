namespace DshWeb.Managers;

/// <summary>
/// dsh 服务管理：端口/HTTP 就绪探测与启动决策。
/// 探针以委托注入（默认 ShellLogic.PortOpen / IsHttpReady / GetProcessIdByPort / IsLikelyDshService /
/// KillProcessTree / GetAncestorPids），使超时/就绪/僵尸清理逻辑可 Headless 单测。
/// 服务进程拉起/PID/僵尸清理等 UI 耦合部分留在 Main，后续按 DSH_USE_NEW_LIFECYCLE 切换迁移。
/// </summary>
public sealed class ServiceManager : IServiceManager
{
    private readonly Func<string, int, bool> _tcpProbeSync;
    private readonly Func<string, int, Task<bool>> _tcpProbeAsync;
    private readonly Func<string, System.Net.Http.HttpClient, bool> _httpProbe;
    private readonly Func<int, int> _pidLookup;
    private readonly Func<int, bool> _identityCheck;
    private readonly Func<int, bool> _killProcessTree;
    private readonly Func<int, System.Collections.Generic.List<int>> _ancestors;
    private readonly TimeSpan _pollDelay;
    private readonly TimeSpan _portReleaseTimeout;

    public ServiceManager(
        Func<string, int, bool>? tcpProbe = null,
        Func<string, System.Net.Http.HttpClient, bool>? httpProbe = null,
        TimeSpan? pollDelay = null,
        Func<string, int, Task<bool>>? tcpProbeAsync = null,
        Func<int, int>? pidLookup = null,
        Func<int, bool>? identityCheck = null,
        Func<int, bool>? killProcessTree = null,
        Func<int, System.Collections.Generic.List<int>>? ancestors = null,
        TimeSpan? portReleaseTimeout = null)
    {
        _tcpProbeSync = tcpProbe ?? ShellLogic.PortOpen;
        // 显式注入同步探针时保持其语义（Headless 测试/旧契约）；否则走异步 ConnectAsync，
        // 不再阻塞调用线程（v0.4.2 卡顿修复：同步 TcpClient.Connect 在本机可达 2s）。
        _tcpProbeAsync = tcpProbe is not null
            ? (h, p) => Task.Run(() => tcpProbe(h, p))
            : tcpProbeAsync ?? ((h, p) => ShellLogic.PortOpenAsync(h, p));
        _httpProbe = httpProbe ?? ShellLogic.IsHttpReady;
        _pollDelay = pollDelay ?? TimeSpan.FromSeconds(1);
        _pidLookup = pidLookup ?? ShellLogic.GetProcessIdByPort;
        _identityCheck = identityCheck ?? ShellLogic.IsLikelyDshService;
        _killProcessTree = killProcessTree ?? ShellLogic.KillProcessTree;
        _ancestors = ancestors ?? ShellLogic.GetAncestorPids;
        _portReleaseTimeout = portReleaseTimeout ?? TimeSpan.FromSeconds(2);
    }

    public bool NeedsStart(int port) => !_tcpProbeSync("127.0.0.1", port);

    /// <summary>按 pollDelay 轮询端口+HTTP，超时返回 false。TCP 用 ConnectAsync 异步探测，
    /// HTTP 探测包后台线程（IsHttpReady 内部同步 GetAsync，不占用调用线程）。</summary>
    public async Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var url = $"http://127.0.0.1:{port}";
        var deadline = DateTime.UtcNow + timeout;
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            if (await _tcpProbeAsync("127.0.0.1", port) && await Task.Run(() => _httpProbe(url, http), ct))
                return true; // TCP + HTTP 都已就绪（对应 E2002 超时的成功分支）
            await Task.Delay(_pollDelay, ct).ConfigureAwait(false);
        }
        return false; // 超时（组合根映射 ErrorCodes.E2002）
    }

    /// <summary>
    /// 端口三重验证（任务一）：TCP → 进程身份 → 快速 HTTP。
    /// - TCP 不通 → Closed（需要拉起）；
    /// - TCP 通但占用进程不是 dsh（node）→ Foreign（端口被其他程序占用，快速失败）；
    /// - TCP 通、进程是 node 且 HTTP 就绪 → Healthy（健康运行，跳过拉起）；
    /// - TCP 通、进程是 node 但 HTTP 不通 → Zombie（僵尸服务，清理后重启）。
    /// </summary>
    public ShellLogic.ServicePortState ProbePort(int port, string url)
    {
        if (!_tcpProbeSync("127.0.0.1", port))
            return ShellLogic.ServicePortState.Closed;

        var pid = _pidLookup(port);
        // 进程身份验证：占用端口者必须是我们认识的 dsh 服务（node）进程
        if (pid <= 0 || !_identityCheck(pid))
            return ShellLogic.ServicePortState.Foreign;

        // 快速 HTTP 探测（短超时 3s）：能应答 = 健康；否则判定为僵尸
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        if (_httpProbe(url, http))
            return ShellLogic.ServicePortState.Healthy;

        return ShellLogic.ServicePortState.Zombie;
    }

    /// <summary>
    /// 强杀僵尸进程树（任务一）：先杀监听端口的 node 进程树（taskkill /T /F），再向上杀
    /// cmd/npx 外壳（taskkill /T 只向下，不会结束父外壳），最后等待端口释放（最长 2s）。
    /// 返回是否清理成功（端口最终释放）。</summary>
    public bool KillZombieTree(int port)
    {
        var pid = _pidLookup(port);
        if (pid <= 0)
            return true; // 端口已无占用者（自愈：僵尸进程恰好退出）

        // 杀 node 进程树 + 祖先外壳链（cmd/npx），全部 /T /F 强杀
        var targets = new System.Collections.Generic.HashSet<int> { pid };
        foreach (var ancestor in _ancestors(pid))
            targets.Add(ancestor);
        foreach (var target in targets)
            _killProcessTree(target);

        // 等待端口释放（最长 2s，每 200ms 探测一次）
        var deadline = DateTime.UtcNow + _portReleaseTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!_tcpProbeSync("127.0.0.1", port))
                return true; // 端口已释放
            System.Threading.Thread.Sleep(200);
        }
        return !_tcpProbeSync("127.0.0.1", port);
    }
}
