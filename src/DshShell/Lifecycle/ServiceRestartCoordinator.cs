using System.Windows.Forms;

namespace DshWeb.Lifecycle;

/// <summary>
/// 运行期服务重启协调器（臃肿审计 Phase 4 · T1：自 Program.cs 组合根迁出）。
///
/// 【为什么要单独成类】审计的结论是：**纯决策早沉到 ShellLogic 了，漏下去的是事务**。
/// 这里的事务有三处共用（安全模式切换、启动自检失败询问、服务运行期退出的静默自愈），
/// 而它此前是组合根里的一个 <c>static</c> 方法 + 一组静态字段，于是：
///   · 重试预算只能靠 <c>_lastRuntimeRestartUtc</c> / <c>_runtimeRestartAttempts</c> 这些
///     组合根静态表达，而状态机里当时根本没有"重启中"这个状态可以进（Phase 3 已补）；
///   · 冷却判定拿"上次**成功**"而不是"上次**尝试**"计时 → 一次失败的重启会把预算清零，
///     "连续 N 次后升级为可见询问"这条分支实际几乎不可达 = 服务反复起不来时无限静默重启；
///   · 裸读-比较-写重置与 <c>Interlocked.Increment</c> 并存 = 竞态。
/// 现在：预算判定是 <see cref="ShellLogic.ServiceRestartPolicy.DecideRestartBudget"/> 纯函数，
/// 状态读写在本类内单锁串行，事务本身经 <see cref="Dependencies"/> 注入，
/// 因此本类可以在 Headless 下被测（不建窗、不起进程）。
///
/// <see cref="Dependencies"/> 有 16 个成员——这个数字本身就是审计想要的答案：
/// 组合根此前是在替一个事务承担 16 项协作。
/// </summary>
internal sealed class ServiceRestartCoordinator
{
    /// <summary>重启结果：只做动作不弹窗，由调用方按错误码精确可见化。</summary>
    public enum Outcome { Ready, StartFailed, NotReady, Cancelled }

    /// <summary>接管"dsh 自行重新拉起的服务"时的 token 横幅等待上限（毫秒）。
    /// 该进程不是本壳拉起的，横幅只有靠 dsh 把子进程 stdout 继承进本壳管道才看得到；
    /// 拿不到就按既有语义回退裸 URL，绝不为此把"已经能用的服务"卡住 15 秒。</summary>
    public const int AdoptedServiceTokenWaitMs = 4000;
    public const int NormalServiceTokenWaitMs = 15000;
    public const int ReadyBudgetSeconds = 60;
    public const int MaxRuntimeRestarts = 3;
    public static readonly TimeSpan RuntimeRestartCooldown = TimeSpan.FromMinutes(10);

    /// <summary>组合根注入的协作面（全部为委托，本类不引用 Program、不引用兄弟 Manager）。</summary>
    internal sealed record Dependencies(
        Action<string> Trace,
        Func<bool> SessionShuttingDown,
        Func<bool, int> StopService,            // expectSelfRespawn → 接管到的新 pid（0=无）
        Func<(bool Ok, bool UsedSafeProfile)> StartViaIdentity,
        Action<int> WaitForFreshToken,          // 入参为等待上限毫秒
        Func<bool> IsReady,
        Action RecordPid,
        Func<int> ResolvePid,
        Action<bool> ApplySafeModeVisibility,
        Action SuspendMonitor,
        Action StopMonitor,
        Action<int> ResumeMonitor,
        Func<bool> SafeModeActive,
        int Port,
        Func<LifecycleTrigger, bool> TryFireLifecycle);

    private readonly Dependencies _d;
    private readonly object _budgetSync = new();
    private DateTime? _lastRuntimeRestartAttemptUtc;
    private int _runtimeRestartAttempts;

    public ServiceRestartCoordinator(Dependencies dependencies) => _d = dependencies;

    /// <summary>上次"壳主动重启服务并确认就绪"的时刻（UTC）；null = 本会话从未发生过。
    /// 供 <see cref="ShellLogic.BootRecoveryPolicy.SuppressLauncherInduced"/> 取静默窗。</summary>
    public DateTime? LastShellRestartUtc { get; private set; }

    /// <summary>登记一次"壳主动重启并已确认就绪"（安全模式进入 / 回滚后重启这两条
    /// 不经本类 RestartAsync 的路径也要登记，否则 BootRecoveryPolicy 的静默窗会漏判，
    /// 把壳自己引发的重启当成启动自检失败去问用户）。</summary>
    public void NoteShellRestart() => LastShellRestartUtc = DateTime.UtcNow;

    /// <summary>
    /// 健康运行期服务退出的自愈入口（<c>BootHealthMonitor.ServiceExitedWhileRunning</c> 回调，
    /// 触发线程 = 进程事件/轮询线程）。非阻塞、幂等：立即挂起监控（服务已死，HTTP/页面探针
    /// 随后必然 miss，不能让它们把"运行期重启"判成启动自检失败），后台重启并等新 token 导航。
    /// 失败或连续超限才升级为可见提示。
    /// </summary>
    public void OnServiceExited(int? exitCode, Action<int?, string> escalateToUserVisibleAsk,
        Action<Form, string, string> showError, Func<Form?> mainFormProvider,
        Action navigateToServiceUrl)
    {
        try
        {
            ShellLogic.ServiceRestartPolicy.RestartBudget budget;
            int attempt;
            var nowUtc = DateTime.UtcNow;
            lock (_budgetSync)
            {
                (budget, attempt) = ShellLogic.ServiceRestartPolicy.DecideRestartBudget(
                    _lastRuntimeRestartAttemptUtc, _runtimeRestartAttempts, nowUtc,
                    RuntimeRestartCooldown, MaxRuntimeRestarts, _d.SessionShuttingDown());
                _runtimeRestartAttempts = attempt;
                // 计时锚点必须是"上次**尝试**"而不是"上次成功"：只在成功分支写时间戳时，
                // 一条失败的重启链会让 now-last 永远大于冷却窗 → 每次都被判成新预算 →
                // 升级询问永远不可达、服务反复起不来时无限静默重启。
                if (budget == ShellLogic.ServiceRestartPolicy.RestartBudget.QuietRestart)
                    _lastRuntimeRestartAttemptUtc = nowUtc;
            }
            if (budget == ShellLogic.ServiceRestartPolicy.RestartBudget.AbsorbShuttingDown) return;

            if (budget == ShellLogic.ServiceRestartPolicy.RestartBudget.EscalateToUser)
            {
                // 反复退出（例如插件本身让服务起不来）：静默循环毫无意义，交回用户可见的询问
                var form = mainFormProvider();
                var headline = $"dsh 服务在运行中反复退出（已自动重启 {MaxRuntimeRestarts} 次，"
                    + $"最近退出码 {exitCode?.ToString() ?? "未知"}）。";
                Logger.Warn("[runtime-restart] quiet restart budget exhausted; escalating to visible ask",
                    ErrorCodes.E2007);
                escalateToUserVisibleAsk(exitCode, headline);
                return;
            }

            _d.SuspendMonitor();
            _d.Trace($"[runtime-restart] service exit detected (exit code={exitCode?.ToString() ?? "unknown"}); "
                + $"attempt {attempt}/{MaxRuntimeRestarts}");

            _ = Task.Run(async () =>
            {
                try
                {
                    // [issue #28-4] 只有这条路径允许"接管自我重新拉起的新进程"：DSH 内置重启/自更新
                    // 的实现就是服务进程自我退出并再拉起，端口上的新进程是我们自己的服务。
                    var outcome = await RestartAsync("runtime-restart", expectSelfRespawn: true);
                    if (outcome != Outcome.Ready)
                    {
                        if (outcome == Outcome.Cancelled) return; // 会话收尾中：不打扰
                        _d.StopMonitor(); // 服务状态未知，停止监控防误报
                        var (code, message) = outcome == Outcome.StartFailed
                            ? (ErrorCodes.E2001, "dsh 服务在运行中退出，自动重启失败（无法拉起服务）。请查看统一日志后重新打开 dsh-launcher。")
                            : (ErrorCodes.E2004, "dsh 服务在运行中退出，自动重启后 60 秒内未就绪。请查看统一日志。");
                        var form = mainFormProvider();
                        if (form is not null) showError(form, code, message);
                        return;
                    }
                    _d.Trace("[runtime-restart] service restarted and ready");
                    navigateToServiceUrl();
                }
                catch (Exception ex)
                {
                    Logger.Warn("[runtime-restart] threw: " + ex.Message);
                    _d.StopMonitor();
                }
            });
        }
        catch (Exception ex)
        {
            // 自愈入口本身失败绝不反噬调用方（进程事件线程）
            Logger.Warn("[runtime-restart] entry failed: " + ex.Message);
        }
    }

    /// <summary>
    /// 壳主动重启 dsh 服务并等到就绪（安全模式切换 / 启动自检失败询问 / 运行期自愈 / 更新回滚
    /// 四处共用）：Suspend（重启窗口内不判死）→ 停服 → 身份驱动拉起 → 等新 token → 就绪等待 →
    /// ResumeAfterRestart（重挂进程层；页面层随 Reload 的 NavigationCompleted 重新武装）。
    /// 只做动作不弹窗：返回结果枚举，由调用方按错误码精确可见化。
    /// <paramref name="driveLifecycleState"/> = false 供**上层事务自持状态**的调用方使用
    ///（回滚 saga 有自己的 RollingBackUpdate 状态，重启只是它的子步骤，不该再投 RestartRequested）。
    /// </summary>
    public async Task<Outcome> RestartAsync(string reason, bool expectSelfRespawn = false,
        int readyBudgetSeconds = ReadyBudgetSeconds, bool driveLifecycleState = true)
    {
        // [审查 N6 2026-09-21] 本事务在组合根被 fire-and-forget await（"退出安全模式"路径原先
        // lambda 首 await 不在任何 try 内）：不在这里兜住异常，抛错=未观察任务异常，零留痕、
        // 标题栏永停"正在退出安全模式…"。异常按既有语义折算成 StartFailed（E2001 可见化）。
        try
        {
            return await RestartUncheckedAsync(reason, expectSelfRespawn, readyBudgetSeconds, driveLifecycleState);
        }
        catch (Exception ex)
        {
            Logger.Error($"{reason}: restart transaction threw", ErrorCodes.E2001, new { ex = ex.ToString() });
            if (driveLifecycleState) _d.TryFireLifecycle(LifecycleTrigger.RestartFailed);
            return Outcome.StartFailed;
        }
    }

    private async Task<Outcome> RestartUncheckedAsync(string reason, bool expectSelfRespawn,
        int readyBudgetSeconds, bool driveLifecycleState)
    {
        _d.SuspendMonitor();
        // [审查 N3 同形状] 被状态机拒绝不阻断动作（退出安全模式等上层事务自持状态是合法在途），
        // 但必须留痕："事务在跑、状态机失明"的窗口是本仓反复踩的盲区；调用方都在稳态时此 Warn 永不亮。
        if (driveLifecycleState && !_d.TryFireLifecycle(LifecycleTrigger.RestartRequested))
            Logger.Warn($"{reason}: RestartRequested refused by state machine; restart proceeds UNREGISTERED");
        _d.Trace($"{reason}: stopping service");
        var adoptedPid = _d.StopService(expectSelfRespawn);

        if (_d.SessionShuttingDown())
        {
            _d.Trace($"{reason}: session shutting down; service restart skipped");
            return Outcome.Cancelled;
        }

        bool usedSafeProfile;
        if (adoptedPid > 0)
        {
            // [issue #28-4] 端口已被"更新的、已应答的"dsh 服务占据 = 被停进程自我重新拉起
            // （DSH 内置重启/自更新的实现方式）。接管它，不再重复拉起——重复拉起只会再制造
            // 一次端口争抢，而旧实现在此把它的整棵子进程树（含正在装插件的 npm/pnpm）强杀干净。
            usedSafeProfile = _d.SafeModeActive();
            _d.Trace($"{reason}: adopted self-respawned service pid={adoptedPid}; start skipped");
        }
        else
        {
            var (startOk, safeProfile) = _d.StartViaIdentity();
            usedSafeProfile = safeProfile;
            _d.Trace($"{reason}: identity-driven start returned {startOk}");
            if (!startOk)
            {
                Logger.Error($"dsh 服务重启失败（{reason}）", ErrorCodes.E2001);
                if (driveLifecycleState) _d.TryFireLifecycle(LifecycleTrigger.RestartFailed);
                return Outcome.StartFailed;
            }
        }

        // [2026-08-29 token 栅栏] 新进程横幅到位后再刷新。接管场景必须缩短等待：横幅只有在
        // dsh 把自己子进程的 stdout 继承进本壳管道时才拿得到；拿不到就快速回退裸 URL，
        // 不能让"已经可用的服务"空等满 15s（用户观感=点了重启卡住）。
        _d.WaitForFreshToken(adoptedPid > 0 ? AdoptedServiceTokenWaitMs : NormalServiceTokenWaitMs);

        var deadline = DateTime.UtcNow.AddSeconds(readyBudgetSeconds);
        while (DateTime.UtcNow < deadline && !_d.SessionShuttingDown() && !_d.IsReady())
            await Task.Delay(500);
        if (_d.SessionShuttingDown()) return Outcome.Cancelled;
        if (!_d.IsReady())
        {
            Logger.Error($"{reason}: service not ready within {readyBudgetSeconds}s after restart", ErrorCodes.E2004);
            if (driveLifecycleState) _d.TryFireLifecycle(LifecycleTrigger.RestartFailed);
            return Outcome.NotReady;
        }

        // [issue #28-4 根因链断点] 账本与内存 PID 必须指向**新进程**，否则：
        // ① 下面的 ResumeAfterRestart 会 attach 到已死的旧 pid（进程层监控就此失明）；
        // ② 下一次 DSH 内置重启不再被识别为"运行期退出"，而被 HTTP 层判成 E2004 启动自检失败
        //    → RegisterBootFailure → 询问进安全模式 → 粘滞的 .dsh-safe 把用户刚装的插件剥掉。
        _d.RecordPid();
        LastShellRestartUtc = DateTime.UtcNow; // 静默窗锚点：本壳主动重启并已确认就绪
        var pid = _d.ResolvePid();
        if (pid <= 0)
            Logger.Warn($"{reason}: service pid unresolved after restart; boot monitor resumes WITHOUT "
                + "process layer (http/page layers still armed)", ErrorCodes.E2005, new { port = _d.Port });
        _d.ResumeMonitor(pid);
        // [issue #28-4] 重启后安全模式横幅必须跟上：此前只有 TryStartSafeMode 会写标题，
        // 经重启路径降级成 .dsh-safe 时用户看到的是"插件凭空消失且毫无解释"。
        _d.ApplySafeModeVisibility(usedSafeProfile);
        if (driveLifecycleState) _d.TryFireLifecycle(LifecycleTrigger.RestartCompleted);
        return Outcome.Ready;
    }

    /// <summary>Headless 测试缝：读当前连续尝试计数。</summary>
    internal int CurrentRestartAttemptsForTest() { lock (_budgetSync) return _runtimeRestartAttempts; }
}
