using DshWeb;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.RealOs;

/// <summary>
/// 暂存构建事务（臃肿审计 Phase 4 · T2 自组合根迁出）的 RealNet 端到端执行证据。
///
/// 【为什么单独一条】T2 搬完之后，本仓库对它的覆盖是：裁决层有契约测试、失败状态收口有单测，
/// 而**整条事务一次都没有真跑过**——真 `npm pack` 下载、真构建、真 pending 落盘。
/// "搬了却没跑过"正是本轮审计对更新链路的原话，所以这条用例是执行证据而非装饰。
///
/// 断言按 Outcome Contract 铁律只关心**最终物理状态**：pending 里指向的那份运行时是不是真的
/// 完整可执行（用生产判据 <see cref="StagedUpdate.IsSourceRuntimeComplete"/>，不另造一套），
/// 以及"构建中"这个事实有没有随返回清零（那是 T6b 的全部意义）。
///
/// 门禁：仅 DSH_FORCE_REALNET=1（即 `scripts/test.ps1 -RealNet`）时执行，否则直接返回——
/// 与 <c>DshUpdatePipelineRealTests</c> 同规，防发布流水线被镜像网络劫持。
/// 隔离：%TEMP% 下的 DSH_HOME，绝不触碰用户真实 ~/.dsh、全局 npm 或固定端口。
/// </summary>
public sealed class StagedBuildRealNetTests
{
    /// <summary>
    /// 版本前提的取数与 <c>DshUpdatePipelineRealTests.FetchLatestVersionWithFallback</c> 同规：
    /// 4 次退避、第 3 次起切官方 registry 兜底。实测镜像**会瞬时抽风**（第一次跑取到了版本号，
    /// 第二次同一条用例直接拿不到 → 前提红而非事务红），单次 HTTP 探测不配当网络门控用例的前提。
    /// </summary>
    private static string? FetchLatestWithFallback()
    {
        string? version = null;
        var savedReg = Environment.GetEnvironmentVariable("DSH_NPM_REGISTRY");
        try
        {
            for (var attempt = 1; attempt <= 4 && string.IsNullOrWhiteSpace(version); attempt++)
            {
                Environment.SetEnvironmentVariable("DSH_NPM_REGISTRY",
                    attempt >= 3 ? "https://registry.npmjs.org/" : savedReg);
                if (attempt > 1) Task.Delay(TimeSpan.FromSeconds(2 * attempt)).GetAwaiter().GetResult();
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                version = UpdateChecker.FetchLatestDshVersionAsync(http).GetAwaiter().GetResult();
            }
        }
        finally { Environment.SetEnvironmentVariable("DSH_NPM_REGISTRY", savedReg); }
        return version;
    }

    [Fact]
    [Trait("Category", "RealNet")]
    public void BuildStagedUpdate_PacksBuildsAndStagesPending_ForReal()
    {
        if (Environment.GetEnvironmentVariable("DSH_FORCE_REALNET") != "1") return;
        Assert.True(RuntimeResolver.ResolveExisting().NodeExe is not null,
            "DSH_FORCE_REALNET=1 要求本机有 Node.js 18+");

        var savedHome = Environment.GetEnvironmentVariable("DSH_HOME");
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "dsh-staged-real-" + Guid.NewGuid().ToString("N"));
        var traces = new List<string>();
        var progressTicks = 0;
        var phaseStarts = 0;
        try
        {
            var home = System.IO.Path.Combine(tmp, "home");
            var dataDir = System.IO.Path.Combine(home, "dsh-launcher");
            Directory.CreateDirectory(dataDir);
            Environment.SetEnvironmentVariable("DSH_HOME", home);
            StagedUpdate.Init(dataDir);

            var latest = FetchLatestWithFallback();
            Assert.False(string.IsNullOrWhiteSpace(latest),
                "registry 最新版本在 4 次退避 + 官方源兜底后仍拉不到（前提不成立，不是事务红）");

            var mgr = new DshUpdateManager(dataDir, 3999);
            var outcome = mgr.BuildStagedUpdate(latest!, s => traces.Add(s),
                _ => Interlocked.Increment(ref progressTicks), () => Interlocked.Increment(ref phaseStarts));

            // —— 事务结束后的状态收口（T6b：BuildInProgress 由本事务自己的 finally 清零）——
            Assert.False(mgr.BuildInProgress, "返回后 BuildInProgress 必须为 false，否则关窗会永久等一个不存在的构建");
            Assert.Equal(DshUpdateManager.StagedBuildResult.Success, outcome.Result);

            // —— 最终物理状态：pending 指向的运行时必须真完整 ——
            var pending = StagedUpdate.ReadPending();
            Assert.Equal(latest, pending.Version);
            Assert.False(string.IsNullOrWhiteSpace(pending.RuntimeDir),
                "MarkPending 必须带上构建产物目录（apply 阶段据此原子切换）");
            Assert.True(StagedUpdate.IsSourceRuntimeComplete(pending.RuntimeDir!, latest!),
                $"构建产物不完整：{pending.RuntimeDir}");
            Assert.True(pending.Prefetched, "暂存构建成功的语义就是 prefetched=true（下次启动直接应用，不再重下）");
            Assert.Equal(outcome.TarballName, pending.Tarball);
            // tarball 在成功路径上留在 buildDir 内（失败时 PreserveRetryState 才把它挪到 staging 根
            // 供下次免重下）——所以这里断言的是"outcome 带回的路径确实存在"，而不是 StagedUpdate
            // .LocateTarball（它只扫 staging 根，成功路径上必然为 null：我第一版就是这么断错的）。
            Assert.True(outcome.TarballPath is not null && File.Exists(outcome.TarballPath),
                "成功结论必须带回真实存在的 tarball 路径（apply 前的唯一产物凭证）");
            Assert.False(outcome.PreservedForRetry, "PreservedForRetry 只属于失败路径，成功时置位即为谎报");
            Assert.NotNull(outcome.BinEntry);
            Assert.NotEmpty(traces);
            Assert.True(phaseStarts > 0, "构建阶段起始回调必须被调用过（Splash 阶段切换的数据源）");
            // 不断言 progressTicks > 0：onBuildProgress 是**按百分比里程碑**回调的，实测一次成功
            // 构建里它一次都没触发（npm 非详细输出时确实为 0）。把"没进度"写成断言红，等于用一条
            // 虚构的不变量冒充证据；而写成 Assert.True(progressTicks >= 0) 则是我自己删过的那种空断言。
            // 真正要防的"构建期间完全无输出"由上面的 traces 非空把关。
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_HOME", savedHome);
            try { Directory.Delete(tmp, recursive: true); } catch (Exception ex)
            {
                // 构建产物上万文件，杀软句柄可能拖住删除：留痕即可，不能让清理失败冒充测试失败
                Logger.Warn("staged build temp dir cleanup failed: " + ex.Message);
            }
        }
    }
}
