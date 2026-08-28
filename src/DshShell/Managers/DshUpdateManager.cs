namespace DshWeb.Managers;

/// <summary>
/// dsh 更新引擎（ADR-024：跨模块更新编排唯一入口，实现 <see cref="IDshUpdateManager"/>）。
///
/// 职责（自 Program.cs 外科手术式迁出，逐行保持原语义）：
/// - 首装全局安装（NpxCache 身份 → npm install -g，共享预算 ProvisionPolicy）；
/// - 启动早期待应用更新决策编排（ApplyNow / ClearPending / PromptRestart 矩阵接线）；
/// - pending 更新事务：SelfContained 原子切换（零 npm）/ npm -g 兜底；
/// - staging 缓存治理。
///
/// 【身份铁律】所有版本判定基于 DshDiscovery.DiscoverCurrentRuntime() 的
/// DshRuntimeIdentity——严禁散装"版本号字符串"跨模块传递。
/// 铁律边界：本类绝不触碰 Form / Toast / 标题栏状态——UI 反馈全部由调用方经回调驱动。
/// </summary>
public sealed class DshUpdateManager : IDshUpdateManager
{
    private readonly string _dataDir;
    private readonly int _port;

    /// <summary>apply 开始前记录的运行身份版本（npm 全局路径回滚时的降级目标）。</summary>
    public string? PreApplyIdentityVersion { get; private set; }

    /// <summary>apply 成功落地的版本（原子切换/npm 均含）；组合根订阅以武装 update-guard 回滚闸门。</summary>
    public event Action<string>? UpdateApplied;

    /// <summary>首装全局安装失败的用户可见详情（E1012 展示用）；null = 未尝试或已成功。</summary>
    public string? FirstRunProvisionError { get; private set; }

    /// <summary>更新应用失败通知回调（UI 收口：E4002 弹窗 + pending 策略）；由组合根装配。</summary>
    public Action<string, string>? NotifyApplyFailed { get; set; }

    /// <summary>首装全局安装进度回调（Splash 滚动文案，含 [warn] 降级告警）；由组合根装配。</summary>
    public Action<string>? ProvisionProgress { get; set; }

    /// <summary>PromptRestart 决策回调（服务在跑且版本不一致时；组合根据此在主窗就绪后询问一次）。</summary>
    public Action<string>? DeferRestartPrompt { get; set; }

    public DshUpdateManager(string dataDir, int port = 3080)
    {
        _dataDir = dataDir;
        _port = port;
    }

    /// <summary>基于身份的更新判定：remoteVersion 严格大于 identity.Version 才算有新版。</summary>
    public bool NeedsUpdate(DshWeb.Domain.DshRuntimeIdentity local, string? remoteVersion)
        => remoteVersion is not null && UpdateChecker.CompareVersions(remoteVersion, local.Version) > 0;

    // ==================== 首装链（原 Program.TryEnsureGlobalDshInstalled） ====================

    /// <summary>
    /// 首装（本机无任何可用 dsh）改为 npm 全局安装 @deepseek-ai/dsh（2026-09 用户决策）：
    /// 单次安装、复用 npm 缓存。成功后失效发现缓存，交 Identity 启动链直启。
    /// 失败响亮返回 false（详情经 FirstRunProvisionError 以 [E1012] 覆盖展示），绝不静默落 npx。
    /// 总预算/单次上限由 <see cref="ShellLogic.ProvisionPolicy"/> 纯函数决策（契约测试锁定）；
    /// 每个降级边界（换源）经 progress 回调发黄色告警文案。测试/无头/外部托管跳过；沙盒默认跳过，
    /// DSH_TEST_ALLOW_GLOBAL_INSTALL=1 显式放行演练。
    /// </summary>
    public bool EnsureDshInstalled(DshWeb.Domain.DshRuntimeIdentity current)
    {
        if (Environment.GetEnvironmentVariable("DSH_NO_UI") == "1"
            || Environment.GetEnvironmentVariable("DSH_E2E") == "1"
            || !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSH_SERVICE_CMD")))
            return true; // 测试钩子：服务命令外部给定，交由原路径
        if (ShellLogic.RuntimeConfig.IsSandboxMode
            && !string.Equals(Environment.GetEnvironmentVariable("DSH_TEST_ALLOW_GLOBAL_INSTALL"), "1",
                StringComparison.OrdinalIgnoreCase))
            return true;

        if (current.Source != DshWeb.Domain.DshSource.NpxCache)
            return true; // 已有 SelfContained / 全局 dsh，无需安装

        var sw = System.Diagnostics.Stopwatch.StartNew();
        ProvisionProgress?.Invoke("正在获取 dsh 最新版本…");
        string? version;
        using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            version = UpdateChecker.FetchLatestDshVersionAsync(http).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(version))
        {
            // 版本解析失败不直接放弃：registry 直连可达时 @latest 仍可装；真断网则安装步报错收口
            Logger.Warn("first-run global install: cannot resolve latest version; trying @latest tag");
            version = "latest";
        }
        var spec = $"{DshWeb.Domain.DshDiscovery.PackageName}@{version}";
        ProvisionProgress?.Invoke($"正在全局安装 dsh {version}（首次运行，仅需一次，约 1-3 分钟）…");
        Logger.Info($"first-run global install: npm install -g {spec}");

        var sources = ProcessRunner.GetNpmRegistrySources();
        var lastTail = "";
        for (var i = 0; i < sources.Length; i++)
        {
            var timeoutMs = ShellLogic.ProvisionPolicy.RemainingInstallTimeoutMs(
                elapsedMs: sw.ElapsedMilliseconds, totalBudgetMs: 600000, perAttemptCapMs: 420000);
            if (timeoutMs < ShellLogic.ProvisionPolicy.MinAttemptMs)
            {
                Logger.Warn($"first-run global install: shared budget exhausted before source #{i}");
                break;
            }
            if (i > 0) // 降级边界必须可见：黄色告警（旧实现静默换源，用户只见"卡住"）
                ProvisionProgress?.Invoke(
                    $"[warn] 安装源失败，切换备用源重试…（{i}/{sources.Length}，已用 {sw.Elapsed.TotalSeconds:F0}s/预算 {ShellLogic.ProvisionPolicy.TotalBudgetSeconds}s）");
            if (ProcessRunner.RunNpmCommand(
                    $"install -g \"{spec}\" --no-audit --no-fund" + sources[i],
                    out var tail,
                    progress: s => ProvisionProgress?.Invoke(s),
                    timeoutMs: (int)timeoutMs))
            {
                Logger.Info($"first-run global install succeeded via source #{i} (dsh {version})");
                DshWeb.Domain.DshDiscovery.InvalidateCache(); // 发现链立即可见新装的 shim/版本
                return true;
            }
            lastTail = tail;
            Logger.Warn($"first-run global install failed on source #{i}: {tail}");
        }

        FirstRunProvisionError = "npm 全局安装失败（所有镜像源均不可用或共享预算耗尽）。" +
            (string.IsNullOrWhiteSpace(lastTail) ? "" : "\n最后错误：\n" + lastTail);
        Logger.Error(FirstRunProvisionError, ErrorCodes.E1012, new { spec });
        ProvisionProgress?.Invoke("[warn] dsh 组件自动安装失败，本次启动已停止（详见统一日志）。");
        return false;
    }

    // ==================== 启动早期决策（原 Program.HandlePendingUpdateAtStartup） ====================

    /// <summary>
    /// 启动早期待应用更新决策（v0.4.0 T2，纯函数矩阵 U2 的接线）：
    /// - ApplyNow：服务未运行 → 直接应用（可取消）；
    /// - ClearPending：运行版本 == 待应用版本 → 清账（历史残留）；
    /// - PromptRestart：服务在跑且版本不一致 → 经 DeferRestartPrompt 上抛（主窗就绪后一次性询问）；
    /// - None：无 pending。端口开着时绝不静默跳过（根因 A 修复）。
    /// </summary>
    public void HandlePendingAtStartup(CancellationToken ct, Action<string>? progress, Func<int, bool> portOpen)
    {
        // 测试钩子（DSH_TEST_FAKE_APPLY=1）：E2E 模拟"确认更新→重启→应用"全流程。
        if (Environment.GetEnvironmentVariable("DSH_TEST_FAKE_APPLY") == "1")
        {
            ApplyPending(ct, progress);
            return;
        }
        var (pendingVersion, _, _, _, runtimeDir) = StagedUpdate.ReadPending();
        if (string.IsNullOrWhiteSpace(pendingVersion)) return;

        // [INVARIANT] SelfContained 构建完成后，必须无条件执行 Apply，无论端口是否被占用。
        // 旧服务仍在运行（端口开）时，强杀后执行原子切换。
        if (!string.IsNullOrWhiteSpace(runtimeDir) && Directory.Exists(runtimeDir))
        {
            // [2026-08-22 回归·源完整性门禁] 半成品目录一旦搬出即事故。不完整则本次跳过
            // 强制应用，保留 pending 等构建方重写或用户重新点更新，绝不搬运。
            if (!StagedUpdate.IsSourceRuntimeComplete(runtimeDir, pendingVersion!))
            {
                Logger.Warn($"[Apply] pending runtimeDir exists but INCOMPLETE ({runtimeDir}); skipping forced apply this launch", ErrorCodes.E4002);
                progress?.Invoke($"检测到未完成的更新产物 v{pendingVersion}，已跳过自动应用；请重新点击更新。");
                return;
            }
            Logger.Info($"[Apply] SelfContained runtime ready: {runtimeDir}, forcing apply");
            if (portOpen(_port))
            {
                Logger.Info($"[Apply] Port {_port} occupied, killing old service before apply");
                KillServiceOnPort(_port);
            }
            ApplyPending(ct, progress);
            return;
        }

        // 旧版 pending（无 runtimeDir）：按决策矩阵处理（运行身份经统一发现层读取）
        var runningIdentity = DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime();
        var action = ShellLogic.LifecycleDecisions.ResolvePendingUpdateAction(
            pendingExists: true,
            portOpen: portOpen(_port),
            runningVersion: runningIdentity.Version,
            pendingVersion: pendingVersion);
        Logger.Info($"[Apply] Decision: {action}, portOpen={portOpen(_port)}, running={runningIdentity.Version ?? "<null>"}, pending={pendingVersion}");
        switch (action)
        {
            case ShellLogic.PendingUpdateAction.ApplyNow:
                ApplyPending(ct, progress);
                break;
            case ShellLogic.PendingUpdateAction.ClearPending:
                StagedUpdate.ClearPending();
                Logger.Info($"[Apply] Cleared stale pending: {pendingVersion} already running");
                break;
            case ShellLogic.PendingUpdateAction.PromptRestart:
                DeferRestartPrompt?.Invoke(pendingVersion!);
                Logger.Info($"[Apply] Deferred: {pendingVersion} pending while service running; will prompt on main window");
                break;
        }
    }

    /// <summary>强杀占用指定端口的 dsh 服务进程（Apply 前清理旧服务）。逻辑自 Program 迁出不变。</summary>
    private void KillServiceOnPort(int port)
    {
        try
        {
            var pid = ShellLogic.ProcessManagement.GetProcessIdByPort(port);
            if (pid <= 0) return;
            Logger.Info($"[Apply] Killing old service PID={pid} on port {port}");
            if (ShellLogic.ProcessManagement.KillServiceProcess(pid, port))
            {
                // 等待端口释放（最长 2 秒）
                var deadline = DateTime.UtcNow.AddSeconds(2);
                while (DateTime.UtcNow < deadline &&
                       ShellLogic.ProcessManagement.GetProcessIdByPort(port) > 0)
                    Thread.Sleep(100);
                Logger.Info($"[Apply] Port {port} released: {ShellLogic.ProcessManagement.GetProcessIdByPort(port) <= 0}");
            }
            else
            {
                Logger.Warn($"[Apply] Failed to kill PID={pid}, proceeding with apply anyway");
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Apply] KillServiceOnPort error: {ex.Message}");
        }
    }

    // ==================== 更新事务（原 Program.ApplyPendingDshUpdate） ====================

    /// <summary>
    /// 应用已下载的 dsh 新版（原子目录切换，重启零 npm 解析）。
    ///
    /// 【核心不变量 I1】：重启路径严禁执行任何 npm install。
    /// 【核心不变量 I2】：重启路径严禁发起任何网络请求。
    ///
    /// 新流程：后台构建已将完整运行时写入 staging/runtime-build-{version}/，
    /// 此处仅做原子目录移动（同盘 rename，秒级）+ 清理 pending。
    /// 失败不阻塞启动（继续用旧版本）。
    /// </summary>
    public void ApplyPending(CancellationToken ct = default, Action<string>? progress = null)
    {
        var (version, _, _, _, runtimeDir) = StagedUpdate.ReadPending();
        if (string.IsNullOrWhiteSpace(version)) return;

        // [Evidence-1] Apply 开始
        Logger.Info($"[Apply] Start: pending exists, target version={version}, runtimeDir={runtimeDir ?? "null"}, port={_port}");

        // [update-guard] apply 前记录运行身份版本（npm 路径回滚的降级目标）+ 共享数据版本首拍
        PreApplyIdentityVersion ??= DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime().Version;
        UpdateDataGuard.SnapshotBeforeApply(version);

        // 测试钩子
        if (Environment.GetEnvironmentVariable("DSH_TEST_FAKE_APPLY") == "1")
        {
            StagedUpdate.ClearPending();
            Logger.Info($"[Apply] Result: fake apply (test hook), pending cleared");
            return;
        }

        progress?.Invoke($"正在应用更新 (v{version})…");

        // ---- 路径 A：SelfContained 原子切换（零 npm，秒级） ----
        if (!string.IsNullOrWhiteSpace(runtimeDir) && Directory.Exists(runtimeDir))
        {
            // [2026-08-22 回归·源完整性门禁] 半成品绝不搬运（搬了既产生坏目标，
            // 又被未退出构建进程的句柄锁死后续删除/移动）。不完整时保留现场，
            // 明确告知重试路径，不走 npm 降级（源已坏，降级只会再失败一次）。
            if (!StagedUpdate.IsSourceRuntimeComplete(runtimeDir, version))
            {
                Logger.Warn($"[Apply] SelfContained source INCOMPLETE ({runtimeDir}), refusing to move", ErrorCodes.E4002);
                StagedUpdate.MarkApplyFailed();
                NotifyApplyFailedInternal(version, "构建产物未就绪（可能已被新构建重置或中断）。请重新点击更新，完成构建后重启应用。");
                return;
            }

            var runtimesDir = Path.Combine(_dataDir, "runtimes");
            Directory.CreateDirectory(runtimesDir);
            var targetDir = Path.Combine(runtimesDir, version);

            // [Evidence-2] 目标幂等准备 + 原子切换。
            // [2026-08-22 回归] 有效同版本目标 → AlreadyApplied 幂等短路；无效目标 → 备份挪走
            // （失败会带真实异常信息上抛，不再静默）。
            Logger.Info($"[Apply] Executing: Directory.Move({runtimeDir}, {targetDir})");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var prepareAction = StagedUpdate.PrepareTargetForApply(targetDir, version);
                if (prepareAction != ShellLogic.StagedApplyPolicy.ExistingTargetAction.AlreadyApplied)
                    Directory.Move(runtimeDir, targetDir);
                sw.Stop();

                // [Evidence-3] 切换成功（AlreadyApplied 视为重复应用同版本，同样成功收尾）
                Logger.Info($"[Apply] Result: atomic swap success (prepare={prepareAction}), Duration={sw.ElapsedMilliseconds}ms, target={targetDir}");

                // [F23] 立即清账：Move 成功与 ClearPending 之间是崩溃窗口——旧顺序下窗口内
                // 崩溃会让下次启动把 pending.runtimeDir 判为失效而重跑 npm install（对已
                // 应用版本做一次 1-2 分钟的无谓网络安装）。清账前移后窗口收窄到微秒级；
                // 回滚观察期不受影响（update-guard 的跨会话武装基于快照 manifest，与
                // pending 文件无关）。
                StagedUpdate.ClearPending();

                // [update-guard] 武装回滚闸门：新版启动自检失败 → 自动还原数据 + 隔离运行时
                UpdateApplied?.Invoke(version);

                CleanupStagingCache();

                // [Evidence-4] 清理确认
                var pendingAfter = StagedUpdate.ReadPending().Version;
                Logger.Info($"[Apply] Cleanup: pending after apply = {(pendingAfter ?? "null")} (expected null)");

                progress?.Invoke(prepareAction == ShellLogic.StagedApplyPolicy.ExistingTargetAction.AlreadyApplied
                    ? $"更新 v{version} 已就绪（目标已是同版本，跳过重复切换）。"
                    : $"更新 v{version} 已应用完成（原子切换，零 npm）。");

                // 【ADR-024 FP1 终极防线】npm/搬移"成功" ≠ Identity 已变：以发现层重发现为准记录终局。
                LogPostApplyIdentity(version);
            }
            catch (Exception ex)
            {
                sw.Stop();
                Logger.Error($"[Apply] Result: atomic swap FAILED, Duration={sw.ElapsedMilliseconds}ms, Error={ex.Message}", ErrorCodes.E4002);
                StagedUpdate.MarkApplyFailed();
                NotifyApplyFailedInternal(version, $"原子切换失败: {ex.Message}");
            }
            return;
        }

        // ---- 路径 B：npm install 路径（兼容旧版 pending / 构建失败降级） ----
        Logger.Info($"[Apply] Falling back to npm install path (no SelfContained runtime)");
        var localTarball = StagedUpdate.LocateTarball(version, null);
        var useLocal = localTarball is not null;
        var text = useLocal
            ? $"正在应用更新 (v{version})…（本地安装，可能需要几分钟）"
            : $"正在应用更新 (v{version})…（需要在线下载，可能需要几分钟）";
        progress?.Invoke(text);

        if (ct.IsCancellationRequested)
        {
            Logger.Info("[Apply] Canceled before npm install");
            return;
        }

        var installSpec = localTarball ?? $"{DshWeb.Domain.DshDiscovery.PackageName}@{version}";
        var npmArgs = $"install -g \"{installSpec}\" --prefer-offline --no-audit --no-fund";

        // [Evidence-2] 执行 npm install（快源优先，失败沿源序列降级）
        Logger.Info($"[Apply] Executing: npm {npmArgs}");
        var npmSw = System.Diagnostics.Stopwatch.StartNew();
        string errorTail = "";
        var applySources = ProcessRunner.GetNpmRegistrySources();
        if (ProcessRunner.TryNpmOverRegistries(applySources, srcIdx => ProcessRunner.RunNpmCommand(
                npmArgs + applySources[srcIdx], out errorTail, ct, progress), "apply-pending", out _))
        {
            npmSw.Stop();
            // [Evidence-3] npm 成功
            Logger.Info($"[Apply] Result: npm success, Duration={npmSw.ElapsedMilliseconds}ms");

            // [update-guard] 武装回滚闸门：新版启动自检失败 → 自动还原数据 + 尽力降级全局包
            UpdateApplied?.Invoke(version);

            progress?.Invoke($"更新 v{version} 已应用完成。");
            StagedUpdate.ClearPending();
            CleanupStagingCache();

            // [Evidence-4] 清理确认
            var pendingAfter = StagedUpdate.ReadPending().Version;
            Logger.Info($"[Apply] Cleanup: pending after apply = {(pendingAfter ?? "null")} (expected null)");

            // 【ADR-024 FP1 终极防线】npm exit 0 ≠ 更新成功：重发现 Identity 并留痕比对结论。
            LogPostApplyIdentity(version);
            Logger.Info($"[Apply] Done: dsh update {version} applied successfully");
        }
        else
        {
            npmSw.Stop();
            // [Evidence-3] npm 失败
            Logger.Warn($"[Apply] Result: npm FAILED, Duration={npmSw.ElapsedMilliseconds}ms, ExitCode={errorTail}");

            if (ct.IsCancellationRequested)
            {
                Logger.Info("[Apply] Canceled by user; will retry next launch");
                return;
            }
            StagedUpdate.MarkApplyFailed();
            Logger.Warn($"[Apply] Failed: dsh update {version} apply failed; continuing with current version",
                ErrorCodes.E4002, new { version, tail = errorTail });
            NotifyApplyFailedInternal(version, errorTail);
        }
    }

    /// <summary>[FP1 防线] 应用动作后的物理身份取证：重发现 Identity 与目标版本比对并留痕。
    /// 不一致只记 Warn（启动链的就绪验证与 Outcome 测试会响亮拦截），绝不静默假装成功。</summary>
    private void LogPostApplyIdentity(string targetVersion)
    {
        try
        {
            var after = DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime();
            var changed = string.Equals(after.Version, targetVersion, StringComparison.OrdinalIgnoreCase);
            Logger.Info(changed
                ? $"[Apply] Identity verified: running version == target ({targetVersion})"
                : $"[Apply] WARNING: identity NOT changed after apply (running={after.Version ?? "<null>"}, target={targetVersion}) — possible FP1",
                ctx: new { source = after.Source.ToString(), entry = after.DshEntryJsPath });
        }
        catch (Exception ex)
        {
            Logger.Warn($"[Apply] post-apply identity probe failed: {ex.Message}");
        }
    }

    /// <summary>
    /// 更新失败的 UI 反馈 + pending 保留/清理策略（自 Program 迁出；弹窗经 NotifyApplyFailed 回调）。
    /// - 网络/超时类失败 → 保留 pending，下次启动自动重试（不打扰）——仅记录日志；
    /// - 其他失败（权限/包损坏等）→ 清 pending（防死循环）+ 回调上层模态弹窗明确告知。
    /// </summary>
    private void NotifyApplyFailedInternal(string version, string errorTail)
    {
        var retryable = ShellLogic.NpmHelpers.IsRetryableNpmError(errorTail);
        // 网络/超时类：保留 pending，下次启动重试；日志记录（避免每次启动都打扰）
        if (retryable)
        {
            Logger.Info($"update {version} apply failed with retryable error; pending kept for next launch",
                ctx: new { version, tail = errorTail });
            return;
        }
        // 非重试类（权限/包损坏）：清 pending 防死循环 + 上层模态弹窗
        StagedUpdate.ClearPending();
        Logger.Error($"update {version} apply failed with non-retryable error; pending cleared",
            ErrorCodes.E4002, new { version, tail = errorTail });
        NotifyApplyFailed?.Invoke(version, errorTail);
    }

    /// <summary>下载缓存管理：清理 DataDir\staging 中修改时间超过 7 天的文件。
    /// 下载中的当前包（刚写入）不受影响；应用成功后调用方再整体清空。</summary>
    public void CleanupStagingCache()
    {
        try
        {
            var staging = Path.Combine(_dataDir, "staging");
            if (!Directory.Exists(staging)) return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
            foreach (var file in Directory.GetFiles(staging))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        Logger.Info("staging cache cleaned (expired)", ctx: new { file = Path.GetFileName(file) });
                    }
                }
                catch { /* 单文件清理失败跳过 */ }
            }
        }
        catch { /* 清理失败不影响启动 */ }
    }

    // ==================== 构建内核（2026-09 RealOS 可测性抽离，签名保持不变） ====================

    /// <summary>
    /// 从已下载 tarball 构建完整运行时（原"步骤 2"全量逻辑）：
    /// pnpm 可用则 ndjson 真实百分比构建（粘住 pack 成功的镜像源），失败或不可用降级 npm
    /// （npm 无真实进度，脉冲动画由调用方维持）。构建失败（npm 亦败）时清理 buildDir。
    /// </summary>
    internal static (bool Ok, string Tool) BuildRuntimeFromTarball(
        string tarballPath, string tarballName, string buildDir,
        string[] regSources, int packSourceIdx,
        Action<int>? percentProgress, Action? beforeNpmFallback)
    {
        var buildOk = false;
        var buildTool = "npm";

        // 检测 pnpm 可用性（绝不安装）
        var nodeEnv = RuntimeResolver.ResolveExisting();
        var nodeExe = nodeEnv?.NodeExe;
        var pnpmEntryJs = nodeExe is not null ? DshWeb.Domain.JsEntryResolver.ResolvePnpmEntry() : null;
        Logger.Info($"pnpm detection: nodeExe={nodeExe ?? "null"}, pnpmEntry={pnpmEntryJs ?? "not found"}");
        var isPnpm = pnpmEntryJs is not null && nodeExe is not null;

        if (isPnpm)
        {
            try
            {
                Logger.Info($"building dsh runtime with pnpm (ndjson progress)");
                // 粘住 pack 成功的源（依赖解析/缓存与下载同源），失败再沿其余源降级
                var pnpmSources = packSourceIdx > 0
                    ? regSources.Skip(packSourceIdx).Concat(regSources.Take(packSourceIdx)).ToArray()
                    : regSources;
                buildOk = ProcessRunner.TryNpmOverRegistries(pnpmSources, srcIdx => ProcessRunner.RunPnpmInstall(
                    nodeExe!, pnpmEntryJs!, tarballPath, buildDir, percentProgress,
                    pnpmSources[srcIdx]), "pnpm-build", out _);
                buildTool = "pnpm";
                Logger.Info($"pnpm build result: {buildOk}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"pnpm build failed, falling back to npm: {ex.Message}");
            }
        }

        if (!buildOk)
        {
            Logger.Info("building dsh runtime with npm");
            beforeNpmFallback?.Invoke();
            string buildTail = "";
            var npmSources = packSourceIdx > 0
                ? regSources.Skip(packSourceIdx).Concat(regSources.Take(packSourceIdx)).ToArray()
                : regSources;
            buildOk = ProcessRunner.TryNpmOverRegistries(npmSources, srcIdx => ProcessRunner.RunNpmCommand(
                $"install \"./{tarballName}\" --prefix . --prefer-offline --no-audit --no-fund"
                    + npmSources[srcIdx],
                out buildTail, timeoutMs: 1200000, workingDirectory: buildDir), "npm-build", out _);
            if (!buildOk)
            {
                Logger.Warn($"npm build failed: {buildTail}");
                ProcessRunner.TryDeleteDir(buildDir);
            }
        }

        return (buildOk, buildTool);
    }

    /// <summary>
    /// 构建产物 bin 入口解析（原"步骤 3"的纯读取段）：读 node_modules/@deepseek-ai/dsh/package.json
    /// 并解析 bin 入口。文件缺失由调用方先行区分报错；解析失败/入口缺失返回 null。
    /// </summary>
    internal static string? ResolveBuiltBinEntry(string buildDir)
    {
        var dshPkg = Path.Combine(buildDir, "node_modules", "@deepseek-ai", "dsh", "package.json");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(dshPkg));
            return DshWeb.Domain.DshDiscovery.ResolveBinEntry(buildDir, doc.RootElement);
        }
        catch { return null; }
    }
}
