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
        _tcpProbeSync = tcpProbe ?? ShellLogic.ServiceReadiness.PortOpen;
        // 显式注入同步探针时保持其语义（Headless 测试/旧契约）；否则走异步 ConnectAsync，
        // 不再阻塞调用线程（v0.4.2 卡顿修复：同步 TcpClient.Connect 在本机可达 2s）。
        _tcpProbeAsync = tcpProbe is not null
            ? (h, p) => Task.Run(() => tcpProbe(h, p))
            : tcpProbeAsync ?? ((h, p) => ShellLogic.ServiceReadiness.PortOpenAsync(h, p));
        _httpProbe = httpProbe ?? ShellLogic.ServiceReadiness.IsHttpReady;
        _pollDelay = pollDelay ?? TimeSpan.FromSeconds(1);
        _pidLookup = pidLookup ?? ShellLogic.ProcessManagement.GetProcessIdByPort;
        _identityCheck = identityCheck ?? ShellLogic.ProcessManagement.IsLikelyDshService;
        _killProcessTree = killProcessTree ?? ShellLogic.ProcessManagement.KillProcessTree;
        _ancestors = ancestors ?? ShellLogic.ProcessManagement.GetAncestorPids;
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
    /// 生产级就绪裁决轮询（自 Program.WaitServiceReady 下沉，逻辑逐位保留）：
    /// TCP+HTTP 探测 + 统一日志错误标志三态（15s 宽限防良性告警误判）+ e2e 20s 上限
    /// + NpxCache 网络回退预算放宽。返回 "ready"/"canceled"/"logerror"/"timeout"。
    /// </summary>
    public string PollReadiness(CancellationToken token, int port, string url, string logPath, bool e2eMode)
    {
        var lastLogCheck = DateTime.MinValue;
        var logErrorSeen = false;
        var logErrorSince = DateTime.MinValue;
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        // 首次运行（dsh 未安装，服务只能经网络下载启动）放宽等待预算 180s → 360s；
        // SelfContained/全局安装维持 180s。
        var networkFallback = DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime().Source
            == DshWeb.Domain.DshSource.NpxCache;
        var pollBudget = ShellLogic.ServiceReadiness.GetPollBudgetSeconds(networkFallback);
        Logger.Info($"poll: networkDownloadFallback={networkFallback}, budget={pollBudget}s");
        for (var i = 0; i < (e2eMode ? 20 : pollBudget); i++)
        {
            if (token.IsCancellationRequested) return "canceled";
            if ((DateTime.UtcNow - lastLogCheck).TotalSeconds >= 5)
            {
                lastLogCheck = DateTime.UtcNow;
                // 主日志被锁时读取 fallback 日志，错误标志检查不失效——两者任一出现启动错误
                // 标志都会触发 15s 宽限期提前退出（诊断盲区消除）。
                var content = ReadTextShared(logPath);
                if (string.IsNullOrWhiteSpace(content)
                    && !string.Equals(logPath, Logger.FallbackPath, StringComparison.OrdinalIgnoreCase))
                {
                    var fb = ReadTextShared(Logger.FallbackPath);
                    if (!string.IsNullOrWhiteSpace(fb)) content = fb;
                }
                if (ShellLogic.ServiceReadiness.LogShowsStartupError(content))
                {
                    if (!logErrorSeen)
                    {
                        logErrorSeen = true;
                        logErrorSince = DateTime.UtcNow;
                        // 日志出现错误标志：不立即判死——启动过程中的良性告警也会命中，
                        // 给 15 秒宽限期；只有持续失败才判定启动出错。
                        Logger.Info("poll: log shows error markers, grace 15s");
                    }
                }
                else
                {
                    logErrorSeen = false; // 日志恢复干净，重置记时
                }
            }
            if (_tcpProbeSync("127.0.0.1", port))
            {
                if (_httpProbe(url, http))
                {
                    Logger.Info("poll: ready (tcp + http)");
                    return "ready"; // TCP + HTTP 都已就绪
                }
                // HTTP 尚未就绪（前端还在启动），继续等
            }
            if (logErrorSeen && DateTime.UtcNow - logErrorSince >= TimeSpan.FromSeconds(15))
            {
                Logger.Info("poll: log error markers persisted 15s, giving up");
                return "logerror";
            }
            // 启动延迟优化：前 8 次快速轮询（200ms），之后 1s 粒度。
            Thread.Sleep(i < 8 ? 200 : 1000);
        }
        Logger.Info($"poll: timeout after {pollBudget}s");
        return "timeout";
    }

    /// <summary>容错读文本（FileShare.ReadWrite——日志可能被服务/cmd 追加句柄锁定）。</summary>
    private static string? ReadTextShared(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch { return null; }
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

    /// <summary>
    /// 【ADR-024 铁律实现】按身份拉起 dsh 服务：node.exe × DshEntryJsPath 直启，
    /// 彻底消灭 cmd.exe / wscript / start-dsh.vbs 中间层（旧 vbs 的三级回退由
    /// 发现层 + 首装链在 Identity 层完成——启动命令不再自行"找 dsh"，只信 Identity）。
    /// 三必须合规：stdout/stderr 重定向 + 异步排空（追加进统一日志）；子进程为长驻服务，
    /// 不设 WaitForExit 超时（超时强杀语义由停止链 KillServiceProcess 承担）。
    /// </summary>
    public bool Start(DshWeb.Domain.DshRuntimeIdentity identity, int port, string? logPath = null)
    {
        // 测试开关：DSH_SERVICE_CMD 指定自定义启动命令（沙盒/E2E 注入）。
        // ADR-021/024：严禁 cmd.exe 包装——SplitCommandLine 拆分后 ProcessStartInfo 直启。
        var testCmd = Environment.GetEnvironmentVariable("DSH_SERVICE_CMD");
        Logger.Info($"DSH_SERVICE_CMD={testCmd ?? "(null)"}");
        if (!string.IsNullOrWhiteSpace(testCmd))
        {
            var split = ShellLogic.ProcessManagement.SplitCommandLine(testCmd);
            if (split is null)
            {
                Logger.Warn($"DSH_SERVICE_CMD unparseable (missing exe or unterminated quote): {testCmd}");
            }
            else
            {
                try
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(split.Value.Exe, split.Value.Args)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                    };
                    ApplyServiceEnvironment(psi, port, logPath);
                    System.Diagnostics.Process.Start(psi);
                    Logger.Info($"service start via DSH_SERVICE_CMD: {split.Value.Exe} {split.Value.Args}");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Warn($"DSH_SERVICE_CMD start failed: {ex.Message}");
                    return false;
                }
            }
        }

        if (!identity.CanLaunchDirectly)
        {
            // 身份要件缺失（NpxCache 未安装 / JS 入口解析失败）：响亮失败，绝不静默落入
            // cmd.exe/npx 冷路径（首装链 EnsureDshInstalled 负责在此之前补齐物理安装）。
            Logger.Error(
                $"identity cannot launch directly (source={identity.Source}, node={identity.NodeExePath ?? "null"}, entry={identity.DshEntryJsPath ?? "null"})",
                ErrorCodes.E2001);
            return false;
        }

        var launchArgs = ShellLogic.ServiceLaunch.BuildArgs(identity, port);
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(identity.NodeExePath!, launchArgs)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = identity.RuntimeDir ?? Path.GetDirectoryName(identity.NodeExePath!),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            ApplyServiceEnvironment(psi, port, logPath);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                Logger.Error("service process failed to start (null)", ErrorCodes.E2001);
                return false;
            }
            PipeServiceOutputToUnifiedLog(p, logPath, identity);
            Logger.Info(identity.IsSafeProfile
                ? $"service start via identity (SAFE profile): node.exe {launchArgs}"
                : $"service start via identity: node.exe {launchArgs}");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("failed to start dsh service: " + ex.Message, ErrorCodes.E2001);
            return false;
        }
    }

    /// <summary>服务子进程环境注入：端口/统一日志/profile 透传（进程级 env 不再被全局污染）。</summary>
    private static void ApplyServiceEnvironment(
        System.Diagnostics.ProcessStartInfo psi, int port, string? logPath)
    {
        psi.EnvironmentVariables["DSH_PORT"] = port.ToString();
        if (!string.IsNullOrWhiteSpace(logPath))
            psi.EnvironmentVariables["DSH_LOG"] = logPath;
    }

    /// <summary>
    /// 服务 stdout/stderr 异步排空并追加到统一日志（替代旧 vbs 的 `cmd >>` 重定向）。
    /// 追加用 FileShare.ReadWrite 打开，与壳 Logger 及读侧探针共存；logPath 为 null 时仅排空丢弃。
    /// </summary>
    private static void PipeServiceOutputToUnifiedLog(
        System.Diagnostics.Process process, string? logPath, DshWeb.Domain.DshRuntimeIdentity identity)
    {
        void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (logPath is null) return;
            try
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(logPath, $"[{DateTime.Now:HH:mm:ss.fff}] [dsh] {line}\n");
            }
            catch
            {
                // 日志句柄瞬时冲突：丢弃该行（服务输出非诊断关键路径），绝不反压子进程
            }
        }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }
}
