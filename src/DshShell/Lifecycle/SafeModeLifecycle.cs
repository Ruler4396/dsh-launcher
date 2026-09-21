using System.Windows.Forms;
using DshWeb.Domain;
using DshWeb.Windows;

namespace DshWeb.Lifecycle;

/// <summary>
/// 安全模式进/出与验证（臃肿审计 Phase 4 · T3：自 Program.cs 组合根迁出）。
///
/// 【为什么这是最该搬的一个】审计的因果地图铁律要求"带补偿的多步事务进状态机"，而
/// <c>TryStartSafeMode</c> 正是全仓最典型的那条：
/// <c>Build → Activate → Suspend → Stop → Start → WaitToken → Verify → Record → Resume
/// → ApplyVisibility → Reload</c>，且**每一步失败都有各自的反向补偿**（Deactivate + StopMonitor）。
/// 它此前是组合根里的一个 <c>static</c> 方法，于是补偿逻辑、验证等待、标题栏改写、页面刷新
/// 全糊在 114 行里，既不能脱机测，也看不出"哪一步的补偿漏了"。
///
/// 【搬动顺带暴露的一处不一致】验证失败分支原先把标题栏硬写回 <c>"DeepSeek Harness"</c>，
/// 而成功/粘滞分支走 <see cref="ApplyVisibility"/>；同一件事两套写法，正是 #28-4
/// "粘滞激活的启动会话横幅漏显示"那类缺陷的成因。现统一走 ApplyVisibility(false)。
///
/// 验证判据（两阶段）保持不变：① 服务就绪 60s 预算；② 之后 5s 观察窗内
/// <c>WebViewManager.LastPluginCrashUtc</c> 不得推进——签名仍在就说明降级没起作用，
/// 此时必须退回安全状态并如实报失败，绝不谎报"安全模式已生效"。
/// </summary>
internal sealed class SafeModeLifecycle
{
    public const int ReadyBudgetSeconds = 60;
    public const int CrashSignatureObservationSeconds = 5;
    public const string NormalTitle = "DeepSeek Harness";
    public const string SafeModeTitle = "DeepSeek Harness（安全模式）";
    /// <summary>退出安全模式 = 停服 + 以正常配置重新拉起 + 重新导航，真机实测 20+ 秒。
    /// 这期间标题栏挂哪段文字由这里定，别在调用方另写一份。</summary>
    public const string ExitingSafeModeTitle = "DeepSeek Harness（正在退出安全模式…）";
    /// <summary>进入安全模式同样是"停服 + 换 profile 重拉"，同样有 20 秒级的空窗（运行期崩溃
    /// 那条路径答完"是"之后就是这段），所以两侧都给等待态，不只退出侧。</summary>
    public const string EnteringSafeModeTitle = "DeepSeek Harness（正在进入安全模式…）";

    /// <summary>组合根注入的协作面（本类不引用 Program、不引用兄弟 Manager）。</summary>
    internal sealed record Dependencies(
        Action<string> Trace,
        Func<bool> SessionShuttingDown,
        Func<SafeProfileTier, bool> BuildProfile,
        Action<SafeProfileTier> Activate,
        Action Deactivate,
        Func<string> SafeProfileDir,
        Action SuspendMonitor,
        Action StopMonitor,
        Action<int> ResumeMonitor,
        Action StopService,
        Func<bool> StartViaIdentity,
        Action WaitForFreshToken,
        Func<bool> IsReady,
        Action RecordPid,
        Func<int> ResolvePid,
        Func<DateTime> PluginCrashUtc,
        Action NoteShellRestart,
        Action<Action<DshShellForm?>> PostToMainForm,
        Action NavigateToServiceUrl,
        // 把主窗换成壳自绘的等待态（组合根负责投递到 UI 线程；本类可能在后台线程被调用）
        Action<string, string> ShowWaitingPage,
        int Port,
        // 把这次流转投递给生命周期状态机（非法/不适用时返回 false，不抛）
        Func<LifecycleTrigger, bool> TryFireLifecycle);

    private readonly Dependencies _d;
    public SafeModeLifecycle(Dependencies dependencies) => _d = dependencies;

    /// <summary>
    /// 两阶段验证：服务就绪 + 崩溃签名消失。任一条不满足即返回 false（不谎报成功）。
    /// </summary>
    public bool WaitVerified()
    {
        // 阶段一：readiness。[F14] 退出编排启动后立即放弃等待（验证已无意义，且不与收尾争抢停启链路）。
        var deadline = DateTime.UtcNow.AddSeconds(ReadyBudgetSeconds);
        while (DateTime.UtcNow < deadline && !_d.SessionShuttingDown() && !_d.IsReady())
            Task.Delay(500).Wait();
        if (_d.SessionShuttingDown()) return false;
        if (!_d.IsReady())
        {
            Logger.Error($"safe mode verification: service not ready within {ReadyBudgetSeconds}s",
                ErrorCodes.E1011);
            return false;
        }

        // 阶段二：崩溃签名消失（观察窗内不得有新的插件崩溃消息）
        var baseline = _d.PluginCrashUtc();
        var observeDeadline = DateTime.UtcNow.AddSeconds(CrashSignatureObservationSeconds);
        while (DateTime.UtcNow < observeDeadline && !_d.SessionShuttingDown())
        {
            if (_d.PluginCrashUtc() > baseline)
            {
                Logger.Error("safe mode verification: plugin crash signature still present", ErrorCodes.E1011);
                return false;
            }
            Task.Delay(300).Wait();
        }
        _d.Trace("SAFEMODE: verification OK (ready + crash signature absent)");
        return true;
    }

    /// <summary>
    /// [issue #28-4] 安全模式可见性收口：标题栏横幅按"实际用于拉起进程的那份身份"开关。
    /// 此前它只在 TryStartSafeMode 内部设置——粘滞激活的启动会话、以及所有经身份降级成
    /// .dsh-safe 的重启路径全部漏网，用户实测到的现象就是"插件凭空消失，界面上没有任何解释"。
    /// </summary>
    public void ApplyVisibility(bool safeProfileActive)
        => ApplyTitle(safeProfileActive ? SafeModeTitle : NormalTitle);

    /// <summary>
    /// 标题栏文字的唯一所有者。<see cref="ApplyVisibility"/> 只是它对"安全模式/正常"两种
    /// 状态的封装；重启进行中的第三种文案（<see cref="ExitingSafeModeTitle"/>）也走这里，
    /// 避免第二处直接改 <c>form.Text</c>。
    /// </summary>
    public void ApplyTitle(string title) => _d.PostToMainForm(form =>
    {
        if (form is null) return;
        if (form.TitleBar is not null) form.TitleBar._titleText = title;
        form.Text = title;
        form.TitleBar?.Invalidate();
    });

    /// <summary>
    /// 尝试进入指定梯级的安全模式。返回 false 表示未生效（已就地完成补偿：退回非激活态、
    /// 停监控、清横幅），调用方据此决定是否降级到下一个梯级或告知用户。
    /// </summary>
    public bool TryEnter(DshShellForm form, SafeProfileTier tier)
    {
        try
        {
            if (_d.SessionShuttingDown())
            {
                _d.Trace("SAFEMODE(bg): session shutting down; tier attempt skipped");
                return false;
            }
            _d.Trace($"SAFEMODE(bg): building tier {tier}");
            // 进入安全模式是"带补偿的多步事务"，必须先经状态机登记：Phase 3 加的
            // EnteringSafeMode 状态此前无人投递，等于状态机对这条流转仍是盲的。
            // [审查 N3 2026-09-21] 被状态机否决（返回 false = 别的事务在途/已终结）时这一跳
            // 必须整个跳过："状态机是唯一真相源"的意思就是它说不行就不行——此前忽略返回值
            // 照常建 profile/停服/拉起，事务跑在状态机的盲区里。
            if (!_d.TryFireLifecycle(LifecycleTrigger.SafeModeEntryRequested))
            {
                _d.Trace("SAFEMODE(bg): state machine refused SafeModeEntryRequested; tier attempt skipped");
                return false;
            }
            if (!_d.BuildProfile(tier))
            {
                Logger.Error($"safe mode disabled: failed to build tier {tier} profile", ErrorCodes.E1010);
                // Requested 已经投递过，这里必须投递 EntryFailed，否则状态机会被留在
                // EnteringSafeMode 这个瞬时态里——下一次投递任何运行期触发都会抛非法转移。
                _d.TryFireLifecycle(LifecycleTrigger.SafeModeEntryFailed);
                return false;
            }
            _d.Activate(tier);
            _d.Trace($"SAFEMODE: activated tier {tier}, safe profile={_d.SafeProfileDir()}");

            // 安全模式用隔离 profile 启动：SafeMode.IsActive → Identity.WithProfile(.dsh-safe)，
            // 启动命令由 ServiceLaunch.BuildArgs 注入根级 --profile（ADR-022/024）。
            // 从这一步起服务会被停掉、页面必然断连——先把"正在发生什么"显示出来，再动手
            // （真机 2026-09-20 用户反馈：这段空窗里界面挂着旧页面，被读成"点了没反应"）。
            ApplyTitle(EnteringSafeModeTitle);
            _d.ShowWaitingPage("正在进入安全模式…",
                "正在停用当前服务，并改用只保留 dsh 核心功能的隔离 profile 重新拉起。你的任何配置文件都不会被修改。");
            _d.SuspendMonitor(); // ADR-023：壳主动重启服务 = 判定挂起窗口
            _d.Trace("SAFEMODE(bg): stopping service");
            _d.StopService();
            _d.Trace("SAFEMODE(bg): StopShellService returned");
            if (_d.SessionShuttingDown())
            {
                // [F14] 停服后、拉起前发现退出编排已启动：不再重启服务（否则壳退出后
                // 反而拉起一个无人管理的新 dsh 进程）。
                _d.StopMonitor();
                _d.Deactivate();
                _d.Trace("SAFEMODE(bg): session shutting down; service restart skipped");
                _d.TryFireLifecycle(LifecycleTrigger.SafeModeEntryFailed);
                return false;
            }
            if (!_d.StartViaIdentity())
            {
                _d.StopMonitor(); // 重启失败且不再有服务可监视
                _d.Deactivate();
                // 等待态是我们在停服前挂上去的：拉不起来就必须把标题与横幅一并撤回，
                // 绝不能把"正在进入安全模式…"留在一个并没有在安全模式的窗口上。
                ApplyVisibility(false);
                _d.TryFireLifecycle(LifecycleTrigger.SafeModeEntryFailed);
                return false;
            }
            // [2026-08-29 token 栅栏] 等新进程横幅到位再刷新，消灭"重启空窗期导航 → 错误页驻留"竞态
            _d.WaitForFreshToken();
            if (!WaitVerified())
            {
                // 安全模式未真正生效：退出安全状态、恢复窗口原样（不谎报成功）
                _d.StopMonitor(); // 两级阶梯都失败 → 不再有受监视的健康服务
                _d.Deactivate();
                ApplyVisibility(false);
                _d.TryFireLifecycle(LifecycleTrigger.SafeModeEntryFailed);
                return false;
            }

            // —— 只有真正通过双重观测（readiness + 崩溃签名消失）才标注安全模式横幅 ——
            // [issue #28-4] 账本必须改指这次新拉起的服务：否则下一次运行期退出会被误判成
            // 启动自检失败（E2004/E2007），进而升级询问安全模式——正是本条缺陷的自我强化链。
            _d.RecordPid();
            _d.NoteShellRestart();
            // ADR-023：恢复监控（清终态回 Pending、attach 新进程；页面层随下方 Reload 的
            // NavigationCompleted 重新武装）——安全模式下的服务同样受崩溃检测保护。
            var safePid = _d.ResolvePid();
            if (safePid <= 0)
                Logger.Warn("SAFEMODE: service pid unresolved after safe-mode start; boot monitor resumes "
                    + "WITHOUT process layer (http/page layers still armed)", ErrorCodes.E2005,
                    new { port = _d.Port });
            _d.ResumeMonitor(safePid);
            _d.TryFireLifecycle(LifecycleTrigger.SafeModeEntered);
            ApplyVisibility(true);
            _d.PostToMainForm(_ => _d.NavigateToServiceUrl());
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("safe mode start exception: " + ex.Message);
            _d.StopMonitor(); // 重启流程异常中断：服务状态未知，停止监控防误报
            _d.Deactivate();
            ApplyVisibility(false);
            // [审查 N3] 异常出口此前独缺这一投——其余失败出口都闭合事务。不投则状态机
            // 永久滞留 EnteringSafeMode 瞬时态（:142 注释自证后果）。Requested 未投成功时
            // 这一投会被拒绝（TryFire 不抛），无副作用。
            _d.TryFireLifecycle(LifecycleTrigger.SafeModeEntryFailed);
            return false;
        }
    }
}
