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
    private readonly Func<int, int, bool> _knownServicePid;
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
        TimeSpan? portReleaseTimeout = null,
        Func<int, int, bool>? knownServicePid = null)
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
        // [F4] 服务身份账本（组合根注入：本会话拉起 + pid 文件账本）。缺省恒 true = 旧行为
        // （凡 node 皆可管理）；生产注入真实账本后，"账本外的 node"不再被判 Zombie 强杀。
        _knownServicePid = knownServicePid ?? new Func<int, int, bool>((_, _) => true);
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
    /// 【F2 修复】错误标志检查改为**增量扫描**：只判定 PollReadiness 入口之后新增的字节，
    /// 且跳过壳自写行（"code":"E####" JSONL）——历史日志中的良性网络词（ECONNRESET 等
    /// dsh 运行期合法输出）不再跨会话污染，消除"慢启动 >15s 即误判 E2003 并误杀服务"。
    /// 【F26 可测试性】休眠/检查间隔/宽限全部由虚拟时钟驱动（累加注入 delay 的步长），
    /// 测试注入 no-op delay 即压缩到毫秒级；生产缺省 delay=Thread.Sleep、间隔 5s、宽限
    /// 15s，行为与旧实现等价（e2e 20 轮预算下宽限按比例缩到 2s，保证 logerror 在 e2e
    /// 预算内可达）。
    /// </summary>
    public string PollReadiness(CancellationToken token, int port, string url, string logPath, bool e2eMode,
        Action<TimeSpan>? delay = null, int logCheckIntervalSeconds = 5, int logErrorGraceSeconds = 15)
    {
        var delaySync = delay ?? (static d => Thread.Sleep(d));
        var graceMs = (e2eMode ? 2 : logErrorGraceSeconds) * 1000;
        var checkEveryMs = logCheckIntervalSeconds * 1000.0;
        var lastLogCheckMs = double.NegativeInfinity;
        var logErrorSeen = false;
        var logErrorSinceMs = 0.0;
        var virtualMs = 0.0;
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        // 首次运行（dsh 未安装，服务只能经网络下载启动）放宽等待预算 180s → 360s；
        // SelfContained/全局安装维持 180s。
        var networkFallback = DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime().Source
            == DshWeb.Domain.DshSource.NpxCache;
        var pollBudget = ShellLogic.ServiceReadiness.GetPollBudgetSeconds(networkFallback);
        Logger.Info($"poll: networkDownloadFallback={networkFallback}, budget={pollBudget}s");
        // [F2] 增量扫描起点：入口时已存在的字节（历史/上一会话内容）永不参与判定
        var mainOffset = InitialLogLength(logPath);
        var fallbackPath = Logger.FallbackPath;
        var fallbackOffset = string.IsNullOrEmpty(fallbackPath) ? -1 : InitialLogLength(fallbackPath);
        for (var i = 0; i < (e2eMode ? 20 : pollBudget); i++)
        {
            if (token.IsCancellationRequested) return "canceled";
            if (virtualMs - lastLogCheckMs >= checkEveryMs)
            {
                lastLogCheckMs = virtualMs;
                // [F2] 增量读（FileShare.ReadWrite 共享读）；主日志无新增时回退日志同样增量兜底——
                // 两者任一在**本轮新增**内容中出现启动错误标志都会触发宽限期提前退出。
                var (content, next) = ReadLogIncrementShared(logPath, mainOffset);
                mainOffset = next;
                if (string.IsNullOrWhiteSpace(content) && fallbackOffset >= 0
                    && !string.Equals(logPath, fallbackPath, StringComparison.OrdinalIgnoreCase))
                {
                    var (fb, fbNext) = ReadLogIncrementShared(fallbackPath!, fallbackOffset);
                    fallbackOffset = fbNext;
                    content = fb;
                }
                // 壳自写行（E#### JSONL）不参与判定（与 BootHealthMonitor 日志层同一契约）
                if (content is not null && ShowsStartupErrorIncrement(content))
                {
                    if (!logErrorSeen)
                    {
                        logErrorSeen = true;
                        logErrorSinceMs = virtualMs;
                        // 日志出现错误标志：不立即判死——启动过程中的良性告警也会命中，
                        // 宽限窗口（生产 15s）内持续存在才判定启动出错。
                        Logger.Info("poll: log shows error markers, grace started");
                    }
                }
                // [F2] 旧实现的"日志恢复干净则重置记时"分支随全量扫描一并移除：统一日志为
                // 追加型，历史标志永不消失，增量语义下"seen 即宽限起点"与旧行为等价。
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
            if (logErrorSeen && virtualMs - logErrorSinceMs >= graceMs)
            {
                Logger.Info("poll: log error markers persisted grace, giving up");
                return "logerror";
            }
            // 启动延迟优化：前 8 次快速轮询（200ms），之后 1s 粒度（虚拟时钟累加）。
            var stepMs = i < 8 ? 200 : 1000;
            delaySync(TimeSpan.FromMilliseconds(stepMs));
            virtualMs += stepMs;
        }
        Logger.Info($"poll: timeout after {pollBudget}s");
        return "timeout";
    }

    /// <summary>本轮新增内容是否命中启动错误标志（逐行；只认壳管道转发的服务行 +
    /// 跳过壳自写行——F2/F6：壳的 E1012 等错误文案内嵌 npm tail，任何整段匹配都会误伤）。</summary>
    private static bool ShowsStartupErrorIncrement(string content)
        => content.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Any(l => l.Length > 0
                      && ShellLogic.BootGuard.IsServicePipedLogLine(l)
                      && !ShellLogic.BootGuard.IsShellAuthoredLogEntry(l)
                      && ShellLogic.ServiceReadiness.LogShowsStartupError(l));

    private static long InitialLogLength(string? path)
    {
        try { return path is not null && File.Exists(path) ? new FileInfo(path).Length : 0; }
        catch { return 0; }
    }

    /// <summary>
    /// 日志增量读取（共享读，F2 配套）：只返回 fromOffset 之后的新增文本，无新增返回 null；
    /// 文件被截断/轮转（长度小于起点）时回退从头读——本轮新增内容仍参与判定。
    /// 偏移以 UTF-8 字节数推进（ReadToEnd 全量消费，往返字节数精确）。
    /// </summary>
    internal static (string? Text, long NextOffset) ReadLogIncrementShared(string path, long fromOffset)
    {
        try
        {
            if (!File.Exists(path)) return (null, fromOffset);
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = fromOffset;
            if (fs.Length < start) start = 0; // 截断/轮转：从头读（内容仍是本轮会话的新增）
            if (fs.Length <= start) return (null, start);
            fs.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
            var text = reader.ReadToEnd();
            return (text, start + System.Text.Encoding.UTF8.GetByteCount(text));
        }
        catch { return (null, fromOffset); }
    }

    /// <summary>
    /// 端口三重验证（任务一）：TCP → 进程身份 → 快速 HTTP。
    /// - TCP 不通 → Closed（需要拉起）；
    /// - TCP 通但占用进程不是 dsh（node）→ Foreign（端口被其他程序占用，快速失败）；
    /// - TCP 通、进程是 node 且 HTTP 就绪 → Healthy（健康运行，跳过拉起）；
    /// - TCP 通、进程是 node 但 HTTP 不通 → Zombie（僵尸服务，清理后重启）。
    /// [F4 账本优先] HTTP 不通时先查服务身份账本（组合根注入：本会话拉起 + pid 文件）：
    /// 账本内 → Zombie（自愈清理，仅杀我们自己记录过的服务）；**账本外 → Foreign**
    /// （绝不强杀用户自己的 node 程序——旧行为凡 node 即杀，误杀面实测在案）。
    /// 注：账本外但 HTTP 就绪的 node 仍判 Healthy——健康服务不杀也不动（无账本的
    /// 健康残留属 F19 场景，由启动链 TryAdoptOrphanService 兜底认领）。
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
        var httpOk = _httpProbe(url, http);
        if (!httpOk && !_knownServicePid(pid, port))
        {
            Logger.Warn($"port {port} occupied by unknown node pid={pid} without dsh HTTP; treating as foreign (not killing)", ErrorCodes.E2004, new { pid, port });
            return ShellLogic.ServicePortState.Foreign;
        }
        return httpOk
            ? ShellLogic.ServicePortState.Healthy
            : ShellLogic.ServicePortState.Zombie;
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
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8,
                    };
                    ApplyServiceEnvironment(psi, port, logPath);
                    var p = System.Diagnostics.Process.Start(psi);
                    if (p is null)
                    {
                        Logger.Warn("DSH_SERVICE_CMD process failed to start (null)");
                        return false;
                    }
                    // 与 identity 路径同构：输出进统一日志 + token 横幅解析（沙盒/E2E 行为对齐生产）
                    PipeServiceOutputToUnifiedLog(p, logPath, identity, port);
                    TrackServiceProcess(p);
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
            // 【2026-08-25 P0 回归修复】此前为 `using var p`：Start 返回时立即 Dispose 进程对象，
            // PipeServiceOutputToUnifiedLog 刚挂上的 stdout/stderr 异步排空随句柄释放而失效——
            // 服务输出从此从未落统一日志（连健康启动的 "dsh web: ..." 都消失），日志层签名表
            // 全程失明，插件崩溃堆栈丢失，安全模式归因链断裂。Dispose 只释放本地句柄、绝不杀
            // 进程，故改为静态追踪：下次启动替换时才释放旧对象。
            var p = System.Diagnostics.Process.Start(psi);
            if (p is null)
            {
                Logger.Error("service process failed to start (null)", ErrorCodes.E2001);
                return false;
            }
            PipeServiceOutputToUnifiedLog(p, logPath, identity, port);
            TrackServiceProcess(p);
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

    // ---- 服务进程对象追踪（2026-08-25 P0 修复的构件，见 Start 内注释）----
    private static readonly object ServiceProcessGate = new();
    private static System.Diagnostics.Process? _trackedServiceProcess;

    /// <summary>
    /// 追踪本次拉起的服务进程对象，替换并释放上一个（Dispose 不杀进程，仅释放句柄；
    /// 若旧进程仍在跑，其输出排空本就该随替换终止）。线程安全。
    /// </summary>
    private static void TrackServiceProcess(System.Diagnostics.Process p)
    {
        lock (ServiceProcessGate)
        {
            var old = _trackedServiceProcess;
            _trackedServiceProcess = p;
            if (old is null) return;
            try { old.Dispose(); }
            catch { /* 句柄已失效：释放失败可安全忽略（预期内操作失败） */ }
        }
    }

    /// <summary>
    /// 服务 stdout/stderr 异步排空并追加到统一日志（替代旧 vbs 的 `cmd >>` 重定向）。
    /// 追加用共享打开（FileShare.ReadWrite）与壳 Logger 及读侧探针共存；logPath 为 null 时仅排空丢弃。
    /// 【2026-08-25 回归加固】两路管道线程可能同时到达成串输出（崩溃堆栈正是如此）：
    /// 此前裸 File.AppendAllText（share=Read，第二写者必失败）并发即丢行且无重试——
    /// 现以写锁串行化 + 共享写打开 + 有界重试，杜绝同刻多行互踩。
    /// 【2026-08-29 token 栅栏】逐行经 ShellLogic.ServiceOutput 解析 dsh ≥0.1.2 的
    /// `dsh web: …/?token=…` 启动横幅，命中经 <see cref="ServiceTokenUrlObserved"/> 上抛
    /// （组合根跟随导航；本类不碰 UI）。注意事件为静态：Start 的两条路径（identity /
    /// DSH_SERVICE_CMD）分属不同实例，通道必须类级才能全量覆盖。
    /// </summary>
    private static readonly object UnifiedLogAppendGate = new();

    /// <summary>观察到位移：解析出带 token 的 web URL（dsh ≥0.1.2 信任栅栏；0.1.1 无此横幅不触发）。</summary>
    public static event Action<string>? ServiceTokenUrlObserved;

    private static void RaiseServiceTokenUrl(string url)
    {
        try { ServiceTokenUrlObserved?.Invoke(url); }
        catch (Exception ex)
        {
            // 订阅方（组合根）异常绝不反压管道排空线程
            Logger.Warn($"service token url observer threw: {ex.Message}");
        }
    }

    private static void PipeServiceOutputToUnifiedLog(
        System.Diagnostics.Process process, string? logPath,
        DshWeb.Domain.DshRuntimeIdentity identity, int port)
    {
        void Append(string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            if (ShellLogic.ServiceOutput.TryExtractTokenUrl(line, port, out var tokenUrl))
            {
                Logger.Info($"service token url observed (web trust fence): {tokenUrl}");
                RaiseServiceTokenUrl(tokenUrl);
            }
            if (logPath is null) return;
            var rendered = $"[{DateTime.Now:HH:mm:ss.fff}] [dsh] {line}";
            lock (UnifiedLogAppendGate)
            {
                try
                {
                    var dir = Path.GetDirectoryName(logPath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                }
                catch { /* 目录创建失败由下方写入重试路径统一处理 */ }
                // 有界重试：与壳 Logger / 读侧探针的瞬时句柄冲突不再永久丢行
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                        using var writer = new StreamWriter(fs);
                        writer.WriteLine(rendered);
                        return;
                    }
                    catch (IOException) when (attempt < 9)
                    {
                        Thread.Sleep(20); // 预期内瞬时冲突：短退避后重试
                    }
                    catch (Exception ex)
                    {
                        // [F24] 持续冲突/路径级失败：放弃该行（绝不反压子进程），但必须留痕——
                        // 丢的可能是服务崩溃堆栈（插件归因证据源，LogEvidenceIndicatesPlugin 依赖）。
                        Logger.Warn($"dropped service log line after retries (evidence loss): {rendered} ({ex.Message})");
                        return;
                    }
                }
            }
        }
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 【E2003 诊断归因（issue #24 配套）】服务进程退出码直接落统一日志：E2003 弹窗只有
        // 日志尾 12 行，崩溃栈（Node uncaught 转储）的退出事实与退出码必须可查。
        // 正常停止（壳 Kill）也会触发——统一以 Info 记录（不带错误码），避免污染诊断汇总。
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => LogServiceProcessExit(process);
        if (process.HasExited) LogServiceProcessExit(process);
    }

    private static void LogServiceProcessExit(System.Diagnostics.Process process)
    {
        try
        {
            var code = process.ExitCode;
            lock (UnifiedLogAppendGate)
            {
                Logger.Info($"service process exited (code={code})");
            }
        }
        catch
        {
            // 进程对象已失效（退出码不可读）：崩溃堆栈行仍留在统一日志，无额外归因可补
        }
    }
}
