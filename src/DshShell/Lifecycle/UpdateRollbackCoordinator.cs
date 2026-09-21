namespace DshWeb.Lifecycle;

/// <summary>
/// 更新回滚协调器（臃肿审计 Phase 4 · T5：自 Program.cs 组合根迁出）。
///
/// 【它是什么】dsh 更新应用成功但**新版启动自检失败**时的带补偿事务：
/// 证据落盘 → 挂起健康监控 → 停服 → 还原更新前共享数据 + 隔离新运行时（或尽力降级全局包）→
/// 以旧版重启服务并恢复监控 → 用户可见收口（[E4003]）。停服必须在还原之前：运行时目录被
/// 活着的服务当作工作目录锁住，先隔离就会失败（见 <see cref="RunAsync"/>）。
/// 2026-08-23 用户回归是它的存在理由：新版首启把 <c>.credentials.yaml</c> 单向迁移成新格式后，
/// 回退旧版必读不懂 → 插件树整树加载失败 → "更新失败 = 隔天必炸"。
///
/// 【为什么要离开组合根】审计的结论是纯决策早已沉到 <see cref="ShellLogic"/>，漏下去的是**事务**。
/// 这里迁出的三样东西此前住在 <c>Program</c> 的静态字段/静态方法里：
///   · <c>_updateRollbackArmedVersion</c>——"已应用、未确认健康"的武装标记（跨会话观察期 +
///     一次性消费防回滚循环），本质是流程控制状态，正是铁律要求进状态机的那类；
///   · <c>_preApplyIdentityVersion</c>——从更新引擎**镜像**出来的第二份真相（引擎里已有，
///     组合根抄一遍只是为了让静态方法能读到）；现在直接经委托读引擎；
///   · 90 秒 <c>Thread.Sleep(500)</c> 阻塞轮询——同一条"停服→拉起→等 token→等就绪→重挂监控"
///     事务的第 **二** 份手写实现。现在复用 <see cref="ServiceRestartCoordinator.RestartAsync"/>
///     （T1 的成果），回滚不再自带重启半程，只把自己的状态 <see cref="RollingBackUpdate"/> 投进状态机。
///
/// 全部协作经 <see cref="Dependencies"/> 注入：本类不引用 Program、不引用 Manager 实例，
/// 因此可在 Headless 下测（不起进程、不碰磁盘、不建窗）。
/// </summary>
internal sealed class UpdateRollbackCoordinator
{
    /// <summary>回滚后旧版服务的就绪预算。比常规重启（60s）宽：这条路径要把**更新前的旧版本**
    /// 重新拉起来，而旧版本可能正带着被新版改写过的数据冷启动，慢于平时的自愈重启。</summary>
    public const int RollbackReadyBudgetSeconds = 90;

    /// <summary>组合根注入的协作面（全部为委托；null 检查在装配处由组合根负责）。</summary>
    internal sealed record Dependencies(
        Action<string> Trace,
        Action<BootVerdict> PersistFailureEvidence,
        Action ExportDiagnostics,
        Action SuspendMonitor,
        Action StopMonitor,
        Action StopService,
        Func<string?> DiscoverIdentityVersion,
        Func<string?, string?> UnconfirmedSnapshotVersion,
        Action<string> MarkHealthy,
        Func<string?> PreApplyIdentityVersion,
        Func<string, string, UpdateRollbackResult> RollbackData,
        Func<int, Task<ServiceRestartCoordinator.Outcome>> RestartService,
        Action<string> DowngradeGlobalPackage,
        Action NavigateToServiceUrl,
        Action<string, string> ShowError,
        Func<LifecycleTrigger, bool> TryFireLifecycle);

    private readonly Dependencies _d;

    public UpdateRollbackCoordinator(Dependencies dependencies) => _d = dependencies;

    /// <summary>已武装（更新已应用、尚未确认健康）的版本号；null = 无观察期。</summary>
    public string? ArmedVersion { get; private set; }

    /// <summary>启动自检失败时是否应走回滚（纯决策见
    /// <see cref="ShellLogic.UpdateGuardPolicy.DecideBootFailure"/>）。</summary>
    public bool RollbackArmedOnBootFailure
        => ShellLogic.UpdateGuardPolicy.DecideBootFailure(ArmedVersion)
           == ShellLogic.UpdateGuardPolicy.BootFailureAction.RollbackAndRestart;

    /// <summary>apply 成功 → 武装闸门（本会话内的武装来源）。</summary>
    public void ArmFromAppliedUpdate(string version)
    {
        ArmedVersion = version;
        Logger.Info($"[update-guard] rollback guard armed for v{version}");
    }

    /// <summary>
    /// 跨会话武装：当前身份版本存在"未确认健康"的快照（上次会话应用更新后没走到好符号就
    /// 结束了）→ 本次启动仍在回滚观察期，启动自检失败同样自动回滚。
    /// 含 dsh 身份发现（可能 spawn node 探测），只允许在后台线程调用（装配处已包 Task.Run）。
    /// </summary>
    public void ArmFromPersistedState()
    {
        try
        {
            if (ArmedVersion is not null) return; // 本会话已武装（apply 成功），不覆盖
            var identityVersion = _d.DiscoverIdentityVersion();
            var unconfirmed = _d.UnconfirmedSnapshotVersion(identityVersion);
            if (unconfirmed is null) return;
            ArmedVersion = unconfirmed;
            Logger.Info($"[update-guard] rollback guard armed (cross-session) for v{unconfirmed}");
        }
        catch (Exception ex)
        {
            // 发现链失败属预期内操作失败：降级为不武装，走既有恢复流程
            Logger.Warn("[update-guard] persisted-arm check failed: " + ex.Message);
        }
    }

    /// <summary>[update-guard] 好符号确认：新版本真实跑起来了 → 快照标记健康、解除武装。</summary>
    public void ConfirmHealthy()
    {
        var version = ArmedVersion;
        if (version is null) return;
        ArmedVersion = null; // 先 disarm 再持久化：确认动作自身失败最多回到观察期，不会误回滚
        try
        {
            _d.MarkHealthy(version);
            Logger.Info($"[update-guard] update v{version} confirmed healthy; rollback guard disarmed");
        }
        catch (Exception ex)
        {
            Logger.Warn("[update-guard] healthy-confirm failed: " + ex.Message);
        }
    }

    /// <summary>
    /// 启动自检失败 × 闸门已武装：消费武装标记（**一次性**，无论回滚成败都不重复回滚）并派发
    /// 回滚事务。返回 false = 未武装，调用方继续走既有恢复流程（安全模式/重启询问）。
    ///
    /// 同步前半程（消费标记 + 证据落盘 + 挂起监控）在调用线程完成：这些必须在派发前落地，
    /// 否则数据还原期间 HTTP/页面探针会把"壳主动停机"再判一次启动自检失败（与 T1 的
    /// <c>OnServiceExited</c> 同构）。真正耗时的还原/降级/重启在后台任务里跑。
    /// </summary>
    public bool TryHandleBootFailure(BootVerdict verdict)
    {
        if (!RollbackArmedOnBootFailure) return false;
        var version = ArmedVersion!;
        ArmedVersion = null; // 一次性消费（防回滚循环）
        try
        {
            // 证据先行：失败裁决与诊断包照常落盘，回滚原因可追责
            _d.PersistFailureEvidence(verdict);
            _d.ExportDiagnostics();
            Logger.Error(
                $"[update-rollback] update v{version} failed boot self-check [{verdict.ErrorCode}]; " +
                "rolling back pre-update data and quarantining runtime",
                ErrorCodes.E4003, new { version, code = verdict.ErrorCode });
            _d.SuspendMonitor();
            // [审查 N3 同形状] 拒绝不阻断 saga（上层已判死，回滚是唯一自救路），但必须留痕。
            if (!_d.TryFireLifecycle(LifecycleTrigger.RollbackRequested))
                Logger.Warn("[update-rollback] RollbackRequested refused by state machine; saga proceeds UNREGISTERED");
            _ = Task.Run(() => RunAsync(version, verdict));
            return true;
        }
        catch (Exception ex)
        {
            // 入口自身失败绝不反噬调用方（健康监控线程）；标记已消费，不会再触发回滚风暴
            Logger.Warn("[update-rollback] rollback entry failed: " + ex.Message);
            _d.StopMonitor();
            _d.TryFireLifecycle(LifecycleTrigger.RollbackFailed);
            return true;
        }
    }

    private async Task RunAsync(string version, BootVerdict verdict)
    {
        try
        {
            // 先停服，再还原数据/隔离运行时：服务进程的工作目录就在 runtimes\<version> 里，
            // Windows 会因此锁住该目录 → Directory.Move 报"进程无法访问"，坏运行时留在发现链上，
            // 下次启动又被 DshDiscovery 选中（回滚等于没发生）。迁移前的实现就是
            // Suspend → StopShellService → RollbackAfterFailedUpdate，搬迁时我漏掉了这一环。
            _d.Trace("[update-rollback] stopping service before data rollback");
            _d.StopService();
            _d.Trace("[update-rollback] rolling back pre-update shared data");
            var result = _d.RollbackData(version, $"boot self-check failed [{verdict.ErrorCode}]");

            // npm 全局路径：没有运行时目录可隔离 → 尽力把全局包降回 apply 前版本
            // （--prefer-offline 离线优先；失败透明上报，不阻塞旧版重启——旧版可能本就是全局包）
            var preApply = _d.PreApplyIdentityVersion();
            if (ShellLogic.UpdateGuardPolicy.ShouldDowngradeGlobalPackage(
                    result.QuarantinedRuntimeDir, preApply, version))
            {
                try
                {
                    Logger.Info($"[update-rollback] best-effort npm downgrade to v{preApply}");
                    _d.DowngradeGlobalPackage(preApply!);
                }
                catch (Exception ex)
                {
                    Logger.Warn($"[update-rollback] npm downgrade threw (continuing): {ex.Message}");
                }
            }

            // 停服 → 身份驱动拉起旧版 → 等新 token → 等就绪 → 记账本/PID → 重挂监控：
            // 整段复用 T1 的重启事务（driveLifecycleState=false：本 saga 自持状态）。
            var outcome = await _d.RestartService(RollbackReadyBudgetSeconds);
            if (outcome == ServiceRestartCoordinator.Outcome.Cancelled)
            {
                // 会话已进入退出编排：不再弹窗，但状态必须离开瞬时态
                _d.TryFireLifecycle(LifecycleTrigger.RollbackFailed);
                return;
            }
            if (outcome == ServiceRestartCoordinator.Outcome.StartFailed)
            {
                _d.StopMonitor();
                _d.TryFireLifecycle(LifecycleTrigger.RollbackFailed);
                _d.ShowError(ErrorCodes.E4003,
                    $"dsh 更新 v{version} 启动自检失败，数据已自动回滚，但服务重启失败，请查看统一日志后重新打开 dsh-launcher。");
                return;
            }
            if (outcome == ServiceRestartCoordinator.Outcome.NotReady)
            {
                _d.StopMonitor(); // 服务状态未知，停止监控防误报
                _d.TryFireLifecycle(LifecycleTrigger.RollbackFailed);
                _d.ShowError(ErrorCodes.E4003,
                    $"dsh 更新 v{version} 启动自检失败，数据已自动回滚，但旧版服务 "
                    + $"{RollbackReadyBudgetSeconds} 秒内未就绪，请查看统一日志。");
                return;
            }

            _d.NavigateToServiceUrl();
            _d.TryFireLifecycle(LifecycleTrigger.RollbackCompleted);
            _d.ShowError(ErrorCodes.E4003,
                $"dsh 更新 v{version} 启动自检失败，已自动回滚。\n\n" +
                $"· 已还原更新前的配置数据（{(result.DataRestored ? string.Join("、", result.RestoredFiles) : "无需还原/快照缺失")}）\n" +
                $"· 新版本运行时{(result.QuarantinedRuntimeDir is null ? "无（npm 路径已尽力降级）" : "已隔离出启动发现链")}\n" +
                "· 服务正以旧版本重新启动。\n\n" +
                "如需排查新版问题，请携带统一日志与 update-guard\\rollback-history.jsonl 反馈。");
            Logger.Info($"[update-rollback] completed for v{version}; service restored on previous version");
        }
        catch (Exception ex)
        {
            Logger.Warn("[update-rollback] threw: " + ex.Message);
            _d.StopMonitor();
            _d.TryFireLifecycle(LifecycleTrigger.RollbackFailed);
        }
    }
}
