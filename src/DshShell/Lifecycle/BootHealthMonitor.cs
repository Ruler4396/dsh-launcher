using System.Text.Json;

namespace DshWeb.Lifecycle;

/// <summary>证据来源观察位（ADR-023 多源主动拉取融合）。</summary>
public enum BootLayer
{
    /// <summary>进程追踪者：RecordServicePid 后 attach，Exited/消失 → failed（附 exit code）。</summary>
    Process,
    /// <summary>日志读者：dsh.log 增量扫描 boot 错误签名表，命中 → failed（附命中行）。</summary>
    Log,
    /// <summary>HTTP 探测者：ready 后探测回死（连续未命中）→ failed。</summary>
    Http,
    /// <summary>页面宿主：NavigationCompleted 起 grace 后按间隔 ExecuteScriptAsync 主触发器。</summary>
    Page,
    /// <summary>精确层（可选）：CDP Runtime.exceptionThrown 原文，只采集不判定。</summary>
    Cdp,
}

/// <summary>三态：Pending（监控中）→ Healthy（好符号出现，页面探针停止）/ Failed（吸收态）。</summary>
public enum BootHealthState
{
    Pending,
    Healthy,
    Failed,
}

/// <summary>单条证据（层 + 摘要 + 详情 + 错误码 + UTC 时间）。</summary>
public sealed record BootEvidence(BootLayer Layer, string Summary, string? Detail = null, string? ErrorCode = null)
{
    public DateTime Utc { get; init; } = DateTime.UtcNow;
}

/// <summary>failed 裁决：错误码 + 汇总 + 全部已收集证据（四层融合）。</summary>
public sealed class BootVerdict
{
    public required string ErrorCode { get; init; }
    public required string Summary { get; init; }
    public DateTime Utc { get; init; } = DateTime.UtcNow;
    private readonly List<BootEvidence> _evidence = new();
    public IReadOnlyList<BootEvidence> Evidence => _evidence;
    internal void AddEvidence(BootEvidence e) { lock (_evidence) _evidence.Add(e); }
}

/// <summary>可注入的进程句柄抽象：真实实现包装 Process（Exited 订阅）；Headless 测试用 Fake 直接触发。</summary>
public interface IBootProcessHandle : IDisposable
{
    event EventHandler? Exited;
    bool HasExited { get; }
    /// <summary>已退出时取退出码；不可得（权限/时序）返回 null。</summary>
    int? TryGetExitCode();
}

/// <summary>真实进程句柄：GetProcessById + EnableRaisingEvents。进程已消失时构造立即报告（Exited 同步触发）。</summary>
public sealed class RealProcessHandle : IBootProcessHandle
{
    private readonly System.Diagnostics.Process _process;

    public event EventHandler? Exited;

    public RealProcessHandle(int pid)
    {
        _process = System.Diagnostics.Process.GetProcessById(pid);
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, e) => Exited?.Invoke(this, e);
    }

    public bool HasExited => _process.HasExited;

    public int? TryGetExitCode()
    {
        try { return _process.HasExited ? _process.ExitCode : null; }
        catch { return null; } // 退出码不可得（权限/时序）：证据降级为"exit code unavailable"
    }

    public void Dispose() => _process.Dispose();
}

/// <summary>
/// 启动健康融合状态机（ADR-023）：三态（Pending/Healthy/Failed），四观察位主动拉取 + CDP 只采集。
///
/// 触发语义（铁律级，误报防护验收 Task 3）：
/// - 进程层：非零退出/消失 → failed（附 exit code）；壳主动重启窗口（Suspend）期间忽略。
/// - 日志层：就绪后增量日志命中签名表 → failed（附命中行）；只扫监控起点之后的增量。
/// - HTTP 层：ready 后**连续 2 次**探测失败 → failed（单次抖动不判死）。
/// - 页面层（主触发器）：NavigationCompleted 起，grace 后按间隔探针——坏签名一次 → failed；
///   好符号 → Healthy（停止探针）；页面已渲染（Rendered，如 dsh 配置等待界面）→ Healthy（停止探针）；
///   连续 absent_threshold 次缺席 → failed。
/// - 探针自身异常（ExecuteScript 抛错/返回无效）→ 只 Warn，绝不判 failed。
/// - failed 吸收：后续各层证据继续追加（融合视图），但不再重复触发/重复询问。
///
/// 全部探针经委托注入（组合根接线；Headless 测试注入 Fake），自身无 Win32/WebView 依赖。
/// </summary>
public sealed class BootHealthMonitor : IDisposable
{
    private readonly object _sync = new();
    private readonly ShellLogic.BootGuard.BootProfile _profile;
    private readonly string? _logPath;
    private readonly string _httpUrl;
    private readonly Func<string, Task<string?>>? _pageProbe; // 入参=探针脚本，返回原始 JSON
    private readonly Func<int, IBootProcessHandle>? _processHandleFactory;
    private readonly Func<string, bool> _httpProbe; // url → 可达
    private readonly TimeSpan _logPollInterval;
    private readonly TimeSpan _httpPollInterval;
    private readonly Action<string> _trace;

    private BootHealthState _state = BootHealthState.Pending;
    private BootVerdict? _verdict;
    // 判定前到达的采集层证据（如 CDP 异常先于任何 failed）：失败时并入裁决，不丢（S22 实测教训）
    private readonly List<BootEvidence> _earlyEvidence = new();
    private IBootProcessHandle? _processHandle;
    private CancellationTokenSource _cts = new();
    private Task? _logLoop;
    private Task? _httpLoop;
    private Task? _pageLoop;
    private long _logScanOffset;      // 只扫监控起点之后的增量（旧日志不参与判定）
    private int _httpConsecutiveMisses;
    private int _absentStreak;
    private bool _pageArmed;
    private bool _suspended;          // 壳主动重启服务窗口（安全模式切换）：全部判定挂起
    private bool _promptConsumed;     // 每会话仅询问一次（显式状态，非散落 static bool）
    private bool _stopped;
    private bool _processFailureReported; // 进程层失败幂等（Exited 事件与 HasExited 轮询并发防重）

    /// <summary>进入 Failed 时触发**恰好一次**（组合根接线安全模式询问）。</summary>
    public event Action<BootVerdict>? Failed;

    /// <summary>Failed 吸收态下证据追加时触发（组合根重新持久化 safe-mode-state 融合视图）。</summary>
    public event Action<BootVerdict>? VerdictUpdated;

    /// <summary>好符号出现（Healthy）时触发一次（组合根记日志/测试断言）。</summary>
    public event Action? HealthyDetected;

    public BootHealthState State { get { lock (_sync) return _state; } }
    public BootVerdict? Verdict { get { lock (_sync) return _verdict; } }

    public BootHealthMonitor(
        ShellLogic.BootGuard.BootProfile profile,
        string? logPath,
        string httpUrl,
        Func<string, Task<string?>>? pageProbe = null,
        Func<int, IBootProcessHandle>? processHandleFactory = null,
        Func<string, bool>? httpProbe = null,
        TimeSpan? logPollInterval = null,
        TimeSpan? httpPollInterval = null,
        Action<string>? trace = null)
    {
        _profile = profile;
        _logPath = logPath;
        _httpUrl = httpUrl;
        _pageProbe = pageProbe;
        _processHandleFactory = processHandleFactory ?? (pid => new RealProcessHandle(pid));
        _httpProbe = httpProbe ?? (url => ShellLogic.ServiceReadiness.IsHttpReady(url, new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) }));
        _logPollInterval = logPollInterval ?? TimeSpan.FromSeconds(2);
        _httpPollInterval = httpPollInterval ?? TimeSpan.FromSeconds(3);
        // 非空默认（避免散落的 ?. 调用）：未注入时走统一日志 Info
        _trace = trace ?? (m => Logger.Info("[boot-monitor] " + m));
    }

    // ---------------- 进程层 ----------------

    /// <summary>RecordServicePid 后调用：attach 进程订阅 Exited。进程已消失 → 立即 failed（附 exit code）。</summary>
    public void AttachProcess(int pid)
    {
        Task.Run(() =>
        {
            try
            {
                IBootProcessHandle? handle = null;
                var factory = _processHandleFactory;
                lock (_sync)
                {
                    if (_stopped || _state == BootHealthState.Failed || pid <= 0 || factory is null) return;
                    handle = factory(pid);
                    _processHandle = handle;
                }
                if (handle is null) return;
                handle.Exited += (_, _) => OnProcessExited(handle);
                // attach 竞态：进程在订阅前已退出（Exited 不会再触发）→ 立即判定
                if (handle.HasExited) OnProcessExited(handle);
                _trace($"process layer attached pid={pid}");
            }
            catch (Exception ex)
            {
                // PID 已被回收/残留（best-effort 解析的 pid 可能已死）等：预期内操作失败，监控绝不弄崩壳。
                // [E2007/E2008 误报根治] 仅 Warn，**不** Report 判死——attach 是监视接线失败，不是崩溃裁决
                // （此前残留 pid 会把整监控打成 E2007 并弹窗：用户实测证据 "进程 attach 失败（pid=4708 不存在）"）。
                // 服务真死由 HTTP 层（E2004 / 连续 2 次 miss）与页面层（缺席阈值 E2008）兜底，检测不丢。
                Logger.Warn($"[boot-monitor] process attach failed pid={pid}: {ex.Message} (monitoring continues via http/page layers)");
            }
        });
    }

    private void OnProcessExited(IBootProcessHandle handle)
    {
        // 幂等：Exited 事件与 HasExited 轮询可能并发命中，进程层失败只报告一次
        lock (_sync)
        {
            if (_stopped || _suspended || _processFailureReported) return;
            _processFailureReported = true;
        }
        var code = handle.TryGetExitCode();
        // 退出码 0 = 优雅退出：正常会话中壳主动停止服务前会 Stop()/Suspend()，
        // 若仍收到 0 退出按可疑处理但降级为 Warn（防误报优先）。
        if (code == 0)
        {
            Logger.Warn("[boot-monitor] service process exited with code 0 while monitored (ignored; intentional stop?)");
            return;
        }
        Report(BootLayer.Process,
            $"dsh 服务进程异常退出（exit code={code?.ToString() ?? "unavailable"}）",
            $"pid exit code={code?.ToString() ?? "unavailable"}", ErrorCodes.E2007);
    }

    // ---------------- 日志层 / HTTP 层（轮询循环） ----------------

    /// <summary>启动日志层 + HTTP 层轮询（页面层由 OnNavigationCompleted 独立武装）。</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_stopped || _logLoop is not null) return;
            _logScanOffset = InitialLogOffset();
            _logLoop = Task.Run(() => LogLoopAsync(_cts.Token));
            _httpLoop = Task.Run(() => HttpLoopAsync(_cts.Token));
            _trace("started (log+http layers)");
        }
    }

    private long InitialLogOffset()
    {
        try { return _logPath is not null && File.Exists(_logPath) ? new FileInfo(_logPath).Length : 0; }
        catch { return 0; }
    }

    private async Task LogLoopAsync(CancellationToken ct)
    {
        var buffer = new System.Text.StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_logPath is not null && File.Exists(_logPath))
                {
                    buffer.Clear();
                    long scanFrom;
                    lock (_sync) scanFrom = _logScanOffset;
                    // ADR-010：共享读（cmd >> 持有写共享）；从上次偏移增量读，旧日志不参与判定
                    var text = await ReadLogIncrementAsync(_logPath, scanFrom, ct);
                    if (text.Length > 0)
                    {
                        lock (_sync) _logScanOffset += System.Text.Encoding.UTF8.GetByteCount(text);
                        foreach (var line in text.Split('\n'))
                        {
                            var trimmed = line.TrimEnd('\r');
                            if (trimmed.Length == 0) continue;
                            // 壳自写条目（E#### 契约）不参与判定：壳的事件已由原生路径处理，
                            // 日志层只判定服务输出（防跨层重复触发，S22 实测教训）
                            if (ShellLogic.BootGuard.IsShellAuthoredLogEntry(trimmed)) continue;
                            if (ShellLogic.BootGuard.MatchBootErrorSignature(trimmed, _profile) is { } marker)
                            {
                                Report(BootLayer.Log, $"服务日志命中启动错误签名「{marker}」",
                                    ShellLogic.BootGuard.Truncate(trimmed, 400), ErrorCodes.E2003);
                                break;
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.Warn("[boot-monitor] log watch iteration failed: " + ex.Message); // 预期内 IO 失败，降级不判死
            }
            try { await Task.Delay(_logPollInterval, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>从指定偏移增量读取日志（共享读；文件被截断/轮转时回退从头读）。</summary>
    private static async Task<string> ReadLogIncrementAsync(string path, long fromOffset, CancellationToken ct)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length <= fromOffset) return string.Empty;
        fs.Seek(Math.Min(fromOffset, fs.Length), SeekOrigin.Begin);
        using var reader = new StreamReader(fs, System.Text.Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    private async Task HttpLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_httpPollInterval, ct);
                var ok = await Task.Run(() => _httpProbe(_httpUrl), ct);
                bool shouldFail;
                lock (_sync)
                {
                    if (_suspended) { _httpConsecutiveMisses = 0; continue; }
                    _httpConsecutiveMisses = ok ? 0 : _httpConsecutiveMisses + 1;
                    // ready 后探测回死：连续 2 次（单次抖动不判死，误报防护）
                    shouldFail = !ok && _httpConsecutiveMisses >= 2 && _state is BootHealthState.Pending or BootHealthState.Healthy;
                }
                if (shouldFail)
                    Report(BootLayer.Http, $"dsh 服务 HTTP 探测回死（{_httpUrl} 连续无响应）",
                        $"consecutive misses={_httpConsecutiveMisses}", ErrorCodes.E2004);
                else if (State == BootHealthState.Failed && !ok)
                    Report(BootLayer.Http, "HTTP 探测仍无响应（failed 后补充证据）", null, null);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Logger.Warn("[boot-monitor] http watch iteration failed: " + ex.Message);
            }
        }
    }

    // ---------------- 页面层（主触发器） ----------------

    /// <summary>NavigationCompleted 后调用：grace 后按间隔探针，直至 Healthy/Failed/Stop。幂等（重复导航不叠加）。</summary>
    public void OnNavigationCompleted()
    {
        lock (_sync)
        {
            if (_stopped || _suspended || _pageArmed || _pageProbe is null) return;
            if (_state == BootHealthState.Healthy) return; // 好符号已确认，探针停止
            _pageArmed = true;
        }
        _trace($"page layer armed (grace={_profile.GraceMs}ms interval={_profile.ProbeIntervalMs}ms threshold={_profile.AbsentThreshold})");
        _pageLoop = Task.Run(() => PageLoopAsync(_cts.Token));
    }

    private async Task PageLoopAsync(CancellationToken ct)
    {
        _trace("page probe loop started");
        try { await Task.Delay(_profile.GraceMs, ct); }
        catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            bool stop;
            lock (_sync)
            {
                stop = _stopped || _suspended || _state != BootHealthState.Pending;
                if (_state == BootHealthState.Healthy) _pageArmed = false; // healthy → 停止探针
            }
            if (stop) return;

            string? raw = null;
            var probeFailed = false;
            try
            {
                var probe = _pageProbe;
                _trace("page probe: dispatching script");
                raw = probe is null ? null : await probe(_profile.BuildProbeScript());
                _trace($"page probe: round done (rawLen={(raw?.Length.ToString() ?? "null")})");
            }
            catch (Exception ex)
            {
                // Task 3 铁律：探针自身异常只记 Warn，不得判 failed（服务进程死掉时 WebView 断连属常态）
                Logger.Warn("[boot-monitor] page probe execution failed (not judging): " + ex.Message);
                raw = null;
                probeFailed = true; // 异常轮不计入缺席（缺席=页面健康但好符号没来）
            }

            if (probeFailed)
            {
                try { await Task.Delay(_profile.ProbeIntervalMs, ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var result = ShellLogic.BootGuard.EvaluatePageProbe(raw, _profile);
            switch (result.Kind)
            {
                case ShellLogic.BootGuard.PageProbeKind.GoodSymbol:
                    MarkHealthy("页面探针确认好符号（__DSH_BOOT__ 就绪）");
                    return;
                case ShellLogic.BootGuard.PageProbeKind.Rendered:
                    // [E2008 误报根治] 页面已渲染出 dsh 自身界面（boot 链未完成，如未配置 API key 的
                    // 欢迎/配置界面）→ 视同健康，停止探针，不判 E2008。
                    MarkHealthy("页面探针确认已渲染（dsh 自带流程/配置等待界面；boot 链未完成不判死）");
                    return;
                case ShellLogic.BootGuard.PageProbeKind.BadSignature:
                    Report(BootLayer.Page, $"页面坏签名命中（前端启动失败）", result.Detail, ErrorCodes.E2008);
                    return;
                case ShellLogic.BootGuard.PageProbeKind.Invalid:
                    // 无效结果（null/解析失败）= 探针异常路径：Warn，不计数不判死
                    Logger.Warn("[boot-monitor] page probe returned invalid result (not judging)");
                    break;
                case ShellLogic.BootGuard.PageProbeKind.Absent:
                    int streak, threshold;
                    bool shouldFail;
                    lock (_sync)
                    {
                        _absentStreak++;
                        streak = _absentStreak;
                        threshold = _profile.AbsentThreshold;
                        shouldFail = _absentStreak >= _profile.AbsentThreshold;
                    }
                    if (shouldFail)
                    {
                        Report(BootLayer.Page, $"好符号连续 {streak} 次缺席（阈值 {threshold}）",
                            result.Detail ?? "good symbol absent", ErrorCodes.E2008);
                        return;
                    }
                    _trace($"page probe: good symbol absent ({streak}/{threshold})");
                    break;
            }

            try { await Task.Delay(_profile.ProbeIntervalMs, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ---------------- CDP 精确层（只采集不判定） ----------------

    /// <summary>CDP Runtime.exceptionThrown 原文入库：只采集，绝不触发状态转移。
    /// 早于 failed 到达时先进缓冲区，失败裁决创建时并入（证据不丢）。</summary>
    public void CollectCdpException(string rawJson)
    {
        lock (_sync)
        {
            if (_stopped) return;
            var evidence = new BootEvidence(BootLayer.Cdp,
                "CDP 捕获页面异常（只采集）", ShellLogic.BootGuard.Truncate(rawJson, 500));
            if (_verdict is { } v) v.AddEvidence(evidence);
            else _earlyEvidence.Add(evidence);
        }
        _trace("cdp exception collected (evidence only)");
    }

    // ---------------- 状态转移核心 ----------------

    /// <summary>报告证据并按层语义判定。Failed 吸收：终态后仅追加证据（并重写落盘视图）。</summary>
    private void Report(BootLayer layer, string summary, string? detail, string? errorCode)
    {
        BootVerdict? newlyFailed = null;
        bool appendedPostFailure = false;
        lock (_sync)
        {
            if (_stopped || _suspended) return;
            var evidence = new BootEvidence(layer, summary, detail, errorCode);
            if (_state == BootHealthState.Failed)
            {
                _verdict?.AddEvidence(evidence); // 吸收态：补充证据不重复触发
                _trace($"evidence appended post-failure layer={layer}: {summary}");
                appendedPostFailure = true;
                newlyFailed = null;
            }
            else if (errorCode is not null)
            {
                _state = BootHealthState.Failed;
                _verdict = new BootVerdict { ErrorCode = errorCode, Summary = summary };
                _verdict.AddEvidence(evidence);
                // 判定前采集的证据（CDP 等）并入裁决：四层融合视图完整
                foreach (var early in _earlyEvidence) _verdict.AddEvidence(early);
                _earlyEvidence.Clear();
                newlyFailed = _verdict;
            }
            else
            {
                _verdict?.AddEvidence(evidence); // 无错误码 = 纯补充证据
            }
        }
        if (newlyFailed is not null)
        {
            Logger.Error($"[boot-monitor] FAILED layer={layer}: {summary}" + (detail is null ? "" : " | " + detail), errorCode);
            try { Failed?.Invoke(newlyFailed); }
            catch (Exception ex) { Logger.Warn("[boot-monitor] Failed handler threw: " + ex.Message); }
        }
        if (appendedPostFailure && VerdictUpdated is not null)
        {
            try { VerdictUpdated.Invoke(_verdict!); } // 融合视图变化 → 组合根重写 safe-mode-state
            catch (Exception ex) { Logger.Warn("[boot-monitor] VerdictUpdated handler threw: " + ex.Message); }
        }
    }

    private void MarkHealthy(string reason)
    {
        bool fire;
        lock (_sync)
        {
            if (_stopped || _suspended || _state != BootHealthState.Pending) return;
            _state = BootHealthState.Healthy;
            _pageArmed = false;
            fire = true;
        }
        if (fire)
        {
            _trace("HEALTHY: " + reason);
            try { HealthyDetected?.Invoke(); } catch (Exception ex) { Logger.Warn("[boot-monitor] HealthyDetected handler threw: " + ex.Message); }
        }
    }

    // ---------------- 会话级询问闸门 / 生命周期控制 ----------------

    /// <summary>每会话仅询问一次：首次返回 true 并消耗闸门，之后恒 false（用户点"否"/已问过均不再弹）。</summary>
    public bool TryConsumeSessionPrompt()
    {
        lock (_sync)
        {
            if (_promptConsumed) return false;
            _promptConsumed = true;
            return true;
        }
    }

    /// <summary>壳主动重启服务（安全模式切换）前的挂起窗口：全部判定暂停，证据不判死。</summary>
    public void Suspend()
    {
        lock (_sync)
        {
            if (_stopped) return;
            _suspended = true;
            _httpConsecutiveMisses = 0;
            _absentStreak = 0;
            _pageArmed = false;
        }
        _trace("suspended (intentional service restart window)");
    }

    /// <summary>重启完成（验证通过）后恢复：清终态回 Pending，重新 attach 新进程；页面层随下次导航重新武装。</summary>
    public void ResumeAfterRestart(int? newPid)
    {
        lock (_sync)
        {
            if (_stopped) return;
            _suspended = false;
            _state = BootHealthState.Pending;
            _verdict = null;
            _httpConsecutiveMisses = 0;
            _absentStreak = 0;
            _pageArmed = false;
        }
        _trace("resumed after service restart");
        if (newPid is > 0) AttachProcess(newPid.Value);
    }

    /// <summary>彻底停止（壳退出/服务被壳停止）：取消全部轮询，之后任何报告被忽略。</summary>
    public void Stop()
    {
        lock (_sync)
        {
            if (_stopped) return;
            _stopped = true;
            _cts.Cancel();
            _processHandle?.Dispose();
        }
        _trace("stopped");
    }

    /// <summary>Dispose 即 Stop（测试/组合根 using 语义）。</summary>
    public void Dispose() => Stop();

    /// <summary>当前全部证据的快照（落盘 safe-mode-state.json / 诊断包用）。</summary>
    public IReadOnlyList<BootEvidence> SnapshotEvidence()
    {
        lock (_sync)
        {
            if (_verdict is null) return Array.Empty<BootEvidence>();
            lock (_verdict.Evidence) return _verdict.Evidence.ToArray();
        }
    }

    /// <summary>把裁决与证据序列化为 safe-mode-state.json 的 lastFailure 结构（层摘要单行、详情截断）。</summary>
    internal static object BuildFailureRecord(BootVerdict verdict)
    {
        var layers = verdict.Evidence.Select(e => new
        {
            layer = e.Layer.ToString(),
            summary = e.Summary,
            detail = e.Detail,
            code = e.ErrorCode,
            utc = e.Utc.ToString("o"),
        }).ToArray();
        return new
        {
            utc = verdict.Utc.ToString("o"),
            code = verdict.ErrorCode,
            summary = verdict.Summary,
            layers,
        };
    }
}
