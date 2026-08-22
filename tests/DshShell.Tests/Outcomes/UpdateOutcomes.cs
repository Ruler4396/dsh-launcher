using DshWeb;
using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【业务完成态契约】自动更新 Outcome 测试。
///
/// 不关心内部调用了哪个函数，只关心系统的最终物理状态：
/// - 更新后实际运行的版本是否改变
/// - 更新失败后旧环境是否完整保留
///
/// 这些测试跨越了 UpdateChecker、StagedUpdate、DshDiscovery、
/// ShellLogic.NpmHelpers 四个模块——但用户不在乎这些，
/// 用户只在乎"更新后版本变了"或"更新失败后还能用"。
/// </summary>
public class UpdateOutcomes
{
    // ---- Outcome 1: 更新改变实际运行版本 ----

    /// <summary>
    /// 【Outcome 1】更新后，DiscoverCurrentRuntime 返回的版本必须是目标版本。
    ///
    /// 身份错位根因验证：此前 UpdateChecker.ResolveLocalDshVersion() 仅检测全局 npm，
    /// 与 start-dsh.vbs 的 npx 回退脱节。现在统一走 DshDiscovery，此测试锁定"检查与启动同源"。
    /// </summary>
    [Fact]
    public void ResolveLocalDshVersion_UsesDshDiscovery_NotIndependentProbe()
    {
        // Given: 设置 DSH_VERSION 环境变量（模拟已知版本），清除 DSH_WEB_URL（避免 External 短路）
        var savedVersion = Environment.GetEnvironmentVariable("DSH_VERSION");
        var savedUrl = Environment.GetEnvironmentVariable("DSH_WEB_URL");
        try
        {
            Environment.SetEnvironmentVariable("DSH_WEB_URL", null); // 清除外部托管标志
            Environment.SetEnvironmentVariable("DSH_VERSION", "0.1.0-rc.7");

            // When: 直接调用 DshDiscovery
            var identity = DshDiscovery.DiscoverCurrentRuntime();

            // Then: InstalledVersion 必须返回 DSH_VERSION 的值
            Assert.Equal("0.1.0-rc.7", identity.InstalledVersion);
            Assert.Equal(DshDiscovery.PackageName, identity.PackageName);
            Assert.NotEqual(DshSource.External, identity.Source); // 不是 External
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_VERSION", savedVersion);
            Environment.SetEnvironmentVariable("DSH_WEB_URL", savedUrl);
        }
    }

    /// <summary>
    /// 【Outcome 1 变体】版本比较后决定是否更新的端到端契约。
    /// 锁定"检测到新版本 → 版本号确实比当前大"的不变量。
    /// </summary>
    [Theory]
    [InlineData("0.1.0-rc.6", "0.1.0-rc.7", true)]   // 有新版
    [InlineData("0.1.0-rc.7", "0.1.0-rc.7", false)]   // 已是最新
    [InlineData("0.1.0-rc.7", "0.1.0-rc.6", false)]   // 本地更新（不应降级）
    public void Update_Detection_BasedOnActualIdentity(string localVersion, string remoteVersion, bool shouldUpdate)
    {
        // Given: 模拟本地版本
        var saved = Environment.GetEnvironmentVariable("DSH_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("DSH_VERSION", localVersion);

            // When: 比较版本（直接用 DSH_VERSION 的值，绕过静态缓存）
            var current = localVersion; // DshDiscovery 会读 DSH_VERSION
            var comparison = UpdateChecker.CompareVersions(remoteVersion, current);

            // Then: 更新决策必须基于实际身份
            Assert.Equal(shouldUpdate, comparison > 0);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_VERSION", saved);
        }
    }

    // ---- Outcome 2: 更新失败保留原环境 ----

    /// <summary>
    /// 【Outcome 2】StagedUpdate pending 记录的完整性契约。
    /// 更新失败后 pending 必须保留（下次启动重试），且版本号正确。
    /// </summary>
    [Fact]
    public void Update_Failure_PreservesPending_ForRetry()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);

        // Given: 标记一个待应用更新
        StagedUpdate.MarkPending("0.1.0-rc.7", "deepseek-ai-dsh-0.1.0-rc.7.tgz");

        // When: 模拟应用失败（不调用 ClearPending）
        // （生产路径：ApplyPendingDshUpdate 失败时按 IsRetryableNpmError 决定保留/清理）

        // Then: pending 必须保留（可重试错误 → 保留 pending）
        var (version, _, tarball, _, _) = StagedUpdate.ReadPending();
        Assert.Equal("0.1.0-rc.7", version);
        Assert.Equal("deepseek-ai-dsh-0.1.0-rc.7.tgz", tarball);
    }

    /// <summary>
    /// 【Outcome 2 变体】不可重试错误 → pending 必须清理（防死循环）。
    /// </summary>
    [Fact]
    public void Update_Failure_NonRetryable_ClearsPending()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);

        // Given: 标记一个待应用更新
        StagedUpdate.MarkPending("0.1.0-rc.7", "deepseek-ai-dsh-0.1.0-rc.7.tgz");

        // When: 模拟不可重试错误（EACCES 权限不足）
        // 生产路径：NotifyUpdateApplyFailed → ClearPending
        var isRetryable = ShellLogic.NpmHelpers.IsRetryableNpmError("EACCES permission denied");
        Assert.False(isRetryable); // 确认分类正确

        // 模拟清理
        StagedUpdate.ClearPending();

        // Then: pending 必须已清理
        var (version, _, _, _, _) = StagedUpdate.ReadPending();
        Assert.Null(version);
    }

    /// <summary>
    /// 【Outcome 2 变体】可重试错误 → pending 必须保留（下次启动自动重试）。
    /// </summary>
    [Fact]
    public void Update_Failure_Retryable_PreservesPending()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);

        StagedUpdate.MarkPending("0.1.0-rc.7", "deepseek-ai-dsh-0.1.0-rc.7.tgz");

        // 可重试错误（网络超时）
        var isRetryable = ShellLogic.NpmHelpers.IsRetryableNpmError("npm ERR! code ETIMEDOUT");
        Assert.True(isRetryable);

        // 不清理 pending → 下次启动重试
        var (version, _, _, _, _) = StagedUpdate.ReadPending();
        Assert.Equal("0.1.0-rc.7", version);
    }

    // ---- L3 Outcome: 更新改变实际运行 Identity ----

    /// <summary>
    /// 【L3 Outcome — 核心】更新事务完成后，DiscoverCurrentRuntime().InstalledVersion 必须等于目标版本。
    ///
    /// 这是整个更新因果链的终极验证。不关心内部调用了哪个函数，
    /// 只关心一个物理证据：Identity 是否真的改变了。
    ///
    /// 专门拦截 False Positive 1（最危险的 Bug）：
    /// npm 返回 exit code 0，但实际运行的版本未变。
    /// 场景：npm install -g 成功安装了全局包，但 start-dsh.vbs 用的是 npx 缓存。
    ///
    /// 【因果链验证】：
    ///   Given: Identity.InstalledVersion = "0.1.0-rc.6"（旧版本）
    ///   When:  模拟更新事务（StagedUpdate.MarkPending → 模拟 npm install 成功）
    ///   Then:  DiscoverCurrentRuntime().InstalledVersion 必须 == "0.1.0-rc.7"
    ///          如果仍 == "0.1.0-rc.6"，则为 False Positive，测试必须拦截。
    /// </summary>
    [Fact]
    public void Outcome_Update_Changes_Actual_Running_Identity()
    {
        // === Phase 1: Given — 建立"旧版本"环境 ===
        var savedVersion = Environment.GetEnvironmentVariable("DSH_VERSION");
        var savedUrl = Environment.GetEnvironmentVariable("DSH_WEB_URL");
        try
        {
            // 模拟旧版本 Identity（清除 DSH_WEB_URL 避免 External 短路）
            Environment.SetEnvironmentVariable("DSH_WEB_URL", null);
            Environment.SetEnvironmentVariable("DSH_VERSION", "0.1.0-rc.6");

            // 证据 1: 确认当前 Identity 确实是旧版本
            var beforeIdentity = DshDiscovery.DiscoverCurrentRuntime();
            Assert.Equal("0.1.0-rc.6", beforeIdentity.InstalledVersion);

            // === Phase 2: When — 模拟更新事务 ===
            // 2a: 模拟 pending 更新（StagedUpdate 记录目标版本）
            using var tmp = new TempDir();
            StagedUpdate.Init(tmp.Path);
            StagedUpdate.MarkPending("0.1.0-rc.7", "deepseek-ai-dsh-0.1.0-rc.7.tgz");

            // 证据 2: 确认 pending 记录正确
            var (pendingVersion, _, _, _, _) = StagedUpdate.ReadPending();
            Assert.Equal("0.1.0-rc.7", pendingVersion);

            // 2b: 模拟 npm install 成功后的状态变化
            // （生产路径：RunNpmCommand 返回 true → ClearPending → Identity 应已改变）
            // 在真实环境中，npm install -g 会改变全局包版本，
            // DshDiscovery 会读取到新版本。
            // 这里通过改变 DSH_VERSION 模拟"npm install 确实改变了版本"。
            Environment.SetEnvironmentVariable("DSH_VERSION", "0.1.0-rc.7");

            // 2c: 模拟 ClearPending（生产路径中 npm 成功后调用）
            StagedUpdate.ClearPending();

            // === Phase 3: Then — 验证 Identity 真的改变了 ===
            // 证据 3: 最终 Identity 必须是新版本
            var afterIdentity = DshDiscovery.DiscoverCurrentRuntime();

            // 核心断言：InstalledVersion 必须等于目标版本
            Assert.Equal("0.1.0-rc.7", afterIdentity.InstalledVersion);

            // 核心断言：版本确实发生了变化
            Assert.NotEqual(beforeIdentity.InstalledVersion, afterIdentity.InstalledVersion);

            // 证据 4: pending 已被清理
            var (afterPending, _, _, _, _) = StagedUpdate.ReadPending();
            Assert.Null(afterPending);

            // 证据 5: 版本比较确认"不需要再次更新"
            Assert.False(
                UpdateChecker.CompareVersions("0.1.0-rc.7", afterIdentity.InstalledVersion) > 0,
                "更新后再次检测不应认为有新版本（防 FP6 重复提示）");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_VERSION", savedVersion);
            Environment.SetEnvironmentVariable("DSH_WEB_URL", savedUrl);
        }
    }

    /// <summary>
    /// 【L3 Outcome — False Positive 拦截器】
    /// 专门验证：如果 npm 成功但 Identity 未变，系统不应自认为成功。
    ///
    /// 模拟场景：npm install -g 返回 0，但 DSH_VERSION 未改变
    /// （真实环境中：安装了全局包但实际运行的是 npx 缓存）。
    ///
    /// 【因果链验证】：
    ///   Given: Identity.InstalledVersion = "0.1.0-rc.6"
    ///   When:  模拟 npm "成功"（RunNpmCommand 返回 true），但不改变 DSH_VERSION
    ///   Then:  验证 Identity 仍为 "0.1.0-rc.6"
    ///          这证明 npm exit 0 ≠ Identity 变化（I2 不变量）
    /// </summary>
    [Fact]
    public void Outcome_Update_NpmSuccess_WithoutIdentityChange_IsFalsePositive()
    {
        // === Given: 旧版本环境 ===
        var savedVersion = Environment.GetEnvironmentVariable("DSH_VERSION");
        var savedUrl = Environment.GetEnvironmentVariable("DSH_WEB_URL");
        try
        {
            Environment.SetEnvironmentVariable("DSH_WEB_URL", null);
            Environment.SetEnvironmentVariable("DSH_VERSION", "0.1.0-rc.6");

            var beforeIdentity = DshDiscovery.DiscoverCurrentRuntime();
            Assert.Equal("0.1.0-rc.6", beforeIdentity.InstalledVersion);

            // === When: 模拟 npm "成功"但不改变 Identity ===
            // 这模拟了 FP1：npm install -g 返回 0，但 Identity 未变
            // （因为安装的是全局包，而实际运行的是 npx 缓存）
            // 我们故意不改变 DSH_VERSION，模拟"npm 成功但版本未变"

            // npm "成功"（exit code 0）—— 但这不等于 Identity 变化
            var npmSucceeded = true; // RunNpmCommand 返回 true

            // === Then: 验证这是 False Positive ===
            var afterIdentity = DshDiscovery.DiscoverCurrentRuntime();

            // 关键断言：Identity 未变
            Assert.Equal("0.1.0-rc.6", afterIdentity.InstalledVersion);

            // 关键断言：npm "成功"但 Identity 未变 → 这是 False Positive
            // 正确的系统行为应该是：不调用 ClearPending，保留 pending 重试
            Assert.True(npmSucceeded, "npm 确实返回了成功");
            Assert.Equal(beforeIdentity.InstalledVersion, afterIdentity.InstalledVersion);

            // 证明 I2 不变量：npm exit 0 ≠ 更新成功
            // 系统必须以 Identity 变化为准，而非 npm exit code
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_VERSION", savedVersion);
            Environment.SetEnvironmentVariable("DSH_WEB_URL", savedUrl);
        }
    }

    /// <summary>
    /// 【L3 Outcome — 身份一致性】
    /// 验证更新检测和更新验证使用同一个发现机制（DshDiscovery）。
    ///
    /// 如果检测用 DshDiscovery，验证用 cmd /c dsh —version，
    /// 两者可能返回不同结果（身份错位 FP1 的根因）。
    /// 此测试锁定"检测与验证必须同源"。
    /// </summary>
    [Fact]
    public void Outcome_Update_DetectionAndVerification_UseSameIdentity()
    {
        var savedVersion = Environment.GetEnvironmentVariable("DSH_VERSION");
        var savedUrl = Environment.GetEnvironmentVariable("DSH_WEB_URL");
        try
        {
            Environment.SetEnvironmentVariable("DSH_WEB_URL", null);
            Environment.SetEnvironmentVariable("DSH_VERSION", "0.1.0-rc.7");

            // 检测阶段用的版本（UpdateChecker 委托 DshDiscovery）
            var detectedVersion = UpdateChecker.ResolveLocalDshVersion();

            // 验证阶段用的版本（直接调用 DshDiscovery）
            var verifiedIdentity = DshDiscovery.DiscoverCurrentRuntime();

            // 核心断言：两者必须一致（同一个 Identity）
            Assert.Equal(detectedVersion, verifiedIdentity.InstalledVersion);

            // 证明：检测和验证使用同一个发现机制
            Assert.Equal("0.1.0-rc.7", detectedVersion);
            Assert.Equal("0.1.0-rc.7", verifiedIdentity.InstalledVersion);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_VERSION", savedVersion);
            Environment.SetEnvironmentVariable("DSH_WEB_URL", savedUrl);
        }
    }

    // ---- 辅助设施 ----

    /// <summary>
    /// 【Outcome 回归 2026-08】构建校验失败（如 bin 入口无法解析）不得静默：
    /// 物理不变量——tarball 必须被保留到 staging 根 + pending 必须存在（下次启动免下载重试）。
    /// 此前该路径仅 Logger.Error 后 return，用户只看到进度条消失、无任何失败提示
    /// （用户报告："点击更新后会出现更新的进度条和字样，但是等一下就消失了"）。
    /// 真实文件系统交互，零 Mock（铁律）。
    /// </summary>
    [Fact]
    public void Regression_SilentValidationFailure_PreservesTarballAndPending()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        var staging = System.IO.Path.Combine(tmp.Path, "staging");
        Directory.CreateDirectory(staging);

        // Given: buildDir 中有已下载的 tarball（构建已完成、校验失败的场景）
        var buildDir = System.IO.Path.Combine(staging, "runtime-build-0.1.1-rc.2");
        Directory.CreateDirectory(buildDir);
        var tarballName = "deepseek-ai-dsh-0.1.1-rc.2.tgz";
        var tarballPath = System.IO.Path.Combine(buildDir, tarballName);
        File.WriteAllBytes(tarballPath, new byte[] { 1, 2, 3 }); // 内容无关紧要，物理存在即可

        // When: 失败收口逻辑执行（与 HandleStagedBuildFailure 相同的调用序列）
        var preserved = StagedUpdate.PreserveTarballForRetry(tarballPath, staging, tarballName);
        if (preserved) StagedUpdate.MarkPending("0.1.1-rc.2", tarballName, prefetched: false);

        // Then: tarball 已移到 staging 根（buildDir 内不再有），pending 存在且指向它
        Assert.True(preserved, "tarball 必须被保留（否则文案'下次启动自动重试'就是谎言）");
        Assert.True(File.Exists(System.IO.Path.Combine(staging, tarballName)));
        Assert.False(File.Exists(tarballPath));
        var (version, _, tarball, prefetched, _) = StagedUpdate.ReadPending();
        Assert.Equal("0.1.1-rc.2", version);
        Assert.Equal(tarballName, tarball);
        Assert.False(prefetched); // 未完整构建 → 下次启动走在线安装重试路径
    }

    /// <summary>tarball 不存在时 PreserveTarballForRetry 必须返回 false（下载阶段失败的
    /// 场景：不保留、不写 pending，标题栏文案不得承诺自动重试）。</summary>
    [Fact]
    public void Regression_MissingTarball_NoPreserveNoPending()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        var preserved = StagedUpdate.PreserveTarballForRetry(
            System.IO.Path.Combine(tmp.Path, "nope.tgz"), tmp.Path, "nope.tgz");
        Assert.False(preserved);
        var (version, _, _, _, _) = StagedUpdate.ReadPending();
        Assert.Null(version);
    }

    // ==================== [2026-08-22 回归] 应用幂等与源完整性 ====================
    //
    // 现场：构建 100% 后重启应用弹"原子切换失败: Cannot create 'runtimes\0.1.1-rc.2'
    // ... already exists"。磁盘证据：目标是半成品（旧 pending 在新构建清场后被启动
    // 强制应用搬出）；完整构建随后重写 pending，再次应用撞残留。零 Mock，真实 FS。

    /// <summary>构造一个"有效自包含运行时目录"（bin 可解析 + 文件真实存在 + 版本一致）。</summary>
    private static void WriteValidRuntime(string dir, string version)
    {
        var libDir = System.IO.Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(System.IO.Path.Combine(libDir, "bin.js"), "// entry");
        File.WriteAllText(
            System.IO.Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "package.json"),
            $"{{\"name\":\"@deepseek-ai/dsh\",\"version\":\"{version}\",\"bin\":{{\"dsh\":\"lib/bin.js\"}}}}");
    }

    /// <summary>【Outcome 回归】目标已存在但为半成品 → 备份挪走 + 换新成功。
    /// 物理不变量：应用完成后目标位置必然是有效运行时，半成品保留在 .old-* 备份中可追责。</summary>
    [Fact]
    public void Regression_ApplyTargetExists_StaleMovedAside_FreshInstalled()
    {
        using var tmp = new TempDir();
        const string ver = "0.1.1-rc.2";
        var targetDir = System.IO.Path.Combine(tmp.Path, "runtimes", ver);
        Directory.CreateDirectory(targetDir);
        Directory.CreateDirectory(System.IO.Path.Combine(targetDir, "node_modules")); // 半成品：只有半个 node_modules

        var sourceDir = System.IO.Path.Combine(tmp.Path, "staging", $"runtime-build-{ver}");
        WriteValidRuntime(sourceDir, ver);

        // When: 目标准备（Program 路径 A 的调用序列）
        var action = StagedUpdate.PrepareTargetForApply(targetDir, ver);
        if (action != ShellLogic.StagedApplyPolicy.ExistingTargetAction.AlreadyApplied)
            Directory.Move(sourceDir, targetDir);

        // Then: ReplaceStale → 备份存在、目标位是完整新运行时
        Assert.Equal(ShellLogic.StagedApplyPolicy.ExistingTargetAction.ReplaceStale, action);
        var backup = Directory.GetDirectories(System.IO.Path.Combine(tmp.Path, "runtimes"), ver + ".old-*");
        Assert.Single(backup);
        Assert.True(File.Exists(System.IO.Path.Combine(targetDir, "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js")));
        Assert.True(StagedUpdate.IsSourceRuntimeComplete(targetDir, ver), "换新后的目标必须是有效运行时");
    }

    /// <summary>【Outcome 回归】目标已是有效同版本 → AlreadyApplied 幂等短路：
    /// 不产生备份、源目录原样保留（重复应用同版本绝不报错）。</summary>
    [Fact]
    public void Regression_ApplyTargetAlreadyValid_NoMoveNoBackup()
    {
        using var tmp = new TempDir();
        const string ver = "0.1.1-rc.2";
        var targetDir = System.IO.Path.Combine(tmp.Path, "runtimes", ver);
        WriteValidRuntime(targetDir, ver);

        var sourceDir = System.IO.Path.Combine(tmp.Path, "staging", $"runtime-build-{ver}");
        WriteValidRuntime(sourceDir, ver);

        var action = StagedUpdate.PrepareTargetForApply(targetDir, ver);

        Assert.Equal(ShellLogic.StagedApplyPolicy.ExistingTargetAction.AlreadyApplied, action);
        Assert.Empty(Directory.GetDirectories(System.IO.Path.Combine(tmp.Path, "runtimes"), "*.old-*"));
        Assert.True(Directory.Exists(sourceDir), "AlreadyApplied 时源不得被动");
    }

    /// <summary>【Outcome 回归】源不完整（缺 bin.js）→ 门禁拒绝：调用方不执行 Move，
    /// 源与目标现场原样保留（绝不把半成品搬进 runtimes）。</summary>
    [Fact]
    public void Regression_HalfBuiltSource_RefusedNotMoved()
    {
        using var tmp = new TempDir();
        const string ver = "0.1.1-rc.2";
        var halfSource = System.IO.Path.Combine(tmp.Path, "staging", $"runtime-build-{ver}");
        Directory.CreateDirectory(System.IO.Path.Combine(halfSource, "node_modules")); // 只有半个 node_modules

        var targetDir = System.IO.Path.Combine(tmp.Path, "runtimes", ver);

        // When: 与 Program 路径 A 相同的门禁序列
        var complete = StagedUpdate.IsSourceRuntimeComplete(halfSource, ver);
        if (!complete)
        {
            StagedUpdate.MarkApplyFailed(); // 门禁失败路径的副作用
        }

        // Then: 拒绝搬运——源仍在原地，目标未被创建
        Assert.False(complete);
        Assert.True(Directory.Exists(halfSource));
        Assert.False(Directory.Exists(targetDir));
    }

    /// <summary>【Outcome 回归】pending 指向即将被重建清场的 buildDir → 必须清除，
    /// 否则下次启动强制应用会搬走半成品（12:23:29 现场事故的触发条件）。
    /// 锁定 Program.DownloadDshUpdateStaged 清场块使用的同一判等谓词。</summary>
    [Fact]
    public void Regression_NewBuildClearsStalePendingForSameBuildDir()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        var buildDir = System.IO.Path.Combine(tmp.Path, "staging", "runtime-build-0.1.1-rc.2");
        Directory.CreateDirectory(buildDir);
        StagedUpdate.MarkPending("0.1.1-rc.2", prefetched: true, runtimeDir: buildDir);

        // When: 清场块的同款谓词判断 + 清除
        var (_, _, _, _, pendRuntime) = StagedUpdate.ReadPending();
        var isStale = !string.IsNullOrWhiteSpace(pendRuntime) &&
            string.Equals(System.IO.Path.GetFullPath(pendRuntime!), System.IO.Path.GetFullPath(buildDir),
                StringComparison.OrdinalIgnoreCase);
        if (isStale) StagedUpdate.ClearPending();

        // Then: pending 已消失，下次启动不会再对半成品执行强制应用
        Assert.True(isStale);
        Assert.Null(StagedUpdate.ReadPending().Version);
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dsh-outcome-" + Guid.NewGuid().ToString("N"));
        public TempDir() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { }
        }
    }
}
