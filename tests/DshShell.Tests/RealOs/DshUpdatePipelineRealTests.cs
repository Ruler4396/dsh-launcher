using DshWeb;
using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests.RealOs;

/// <summary>
/// dsh 更新引擎 RealOS 全链路（零 Mock，真实 npm 与镜像网络）：
/// 真实拉取最新版本 → npm pack 下载 tarball → 双路径构建完整运行时 → bin 入口校验
/// → MarkPending → 生产 <see cref="Program.ApplyPendingDshUpdate"/>（SelfContained 原子切换路径 A）
/// → 应用后运行时以 node 真实执行并校验版本号。
/// 门禁约定与 RealWorldNpmExecutionTests 一致：DSH_FORCE_NPM_SMOKE=1 时无 Node 即硬失败
/// （本地 scripts/test.ps1 强制实跑）；CI 无该标志且无 Node 时静默跳过。
/// 隔离铁律：全程 %TEMP% 隔离 DSH_HOME；绝不触碰用户真实 .dsh、全局 npm 或固定端口。
/// </summary>
public class DshUpdatePipelineRealTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dsh-update-real-" + Guid.NewGuid().ToString("N"));
        public TempDir() => System.IO.Directory.CreateDirectory(Path);
        public void Dispose() { try { System.IO.Directory.Delete(Path, recursive: true); } catch { } }
    }

    private static bool ForceSmoke =>
        Environment.GetEnvironmentVariable("DSH_FORCE_NPM_SMOKE") == "1";

    private static void RequireNodeOrSkip()
    {
        if (!ForceSmoke && RuntimeResolver.ResolveExisting().NodeExe is null)
            return; // 无 Node 且未强制：静默跳过（CI 默认路径）
        Assert.True(RuntimeResolver.ResolveExisting().NodeExe is not null,
            "本测试由 DSH_FORCE_NPM_SMOKE=1 强制运行，请先安装 Node.js 18+。");
    }

    [Fact]
    [Trait("Category", "RealNet")]
    public async Task FullPipeline_FetchDownloadBuildApply_RuntimeExecutableAndVersionMatches()
    {
        var nodeExe = RuntimeResolver.ResolveExisting().NodeExe;
        if (nodeExe is null && !ForceSmoke) return; // 跳过

        var savedHome = Environment.GetEnvironmentVariable("DSH_HOME");
        var savedNoUi = Environment.GetEnvironmentVariable("DSH_NO_UI");
        var savedFake = Environment.GetEnvironmentVariable("DSH_TEST_FAKE_APPLY");
        using var tmp = new TempDir();
        try
        {
            var home = System.IO.Path.Combine(tmp.Path, "home");
            var dataDir = System.IO.Path.Combine(home, "dsh-launcher");
            Directory.CreateDirectory(dataDir);
            Environment.SetEnvironmentVariable("DSH_HOME", home);
            Environment.SetEnvironmentVariable("DSH_NO_UI", "1"); // 应用失败通知等 UI 分支全部守卫关闭
            Environment.SetEnvironmentVariable("DSH_TEST_FAKE_APPLY", null);
            StagedUpdate.Init(dataDir);

            // ---- ① 真实拉取最新版本号（重试 + 官方源兜底：境内镜像限流时切 registry.npmjs.org） ----
            string? version = null;
            var savedReg = Environment.GetEnvironmentVariable("DSH_NPM_REGISTRY");
            try
            {
                for (var attempt = 1; attempt <= 4 && version is null; attempt++)
                {
                    // 第 3 次起切官方源（境外 runner / 镜像限流场景的兜底链）
                    Environment.SetEnvironmentVariable("DSH_NPM_REGISTRY",
                        attempt >= 3 ? "https://registry.npmjs.org/" : savedReg);
                    if (attempt > 1) await Task.Delay(TimeSpan.FromSeconds(2 * attempt));
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    version = UpdateChecker.FetchLatestDshVersionAsync(http).GetAwaiter().GetResult();
                }
            }
            finally { Environment.SetEnvironmentVariable("DSH_NPM_REGISTRY", savedReg); }
            Assert.True(!string.IsNullOrWhiteSpace(version), "registry 版本拉取失败（4 次重试含官方源兜底仍为空）");
            Logger.Info($"[RealOS] latest dsh version = {version}");

            // ---- ② npm pack 真实下载 tarball（镜像源序列与生产一致） ----
            var staging = System.IO.Path.Combine(dataDir, "staging");
            var buildDir = System.IO.Path.Combine(staging, $"runtime-build-{version}");
            Directory.CreateDirectory(buildDir);
            var tarballName = $"deepseek-ai-dsh-{version}.tgz";
            var tarballPath = System.IO.Path.Combine(buildDir, tarballName);
            var sources = Program.GetNpmRegistrySources();
            string packTail = "";
            var packed = Program.TryNpmOverRegistries(sources, i => Program.RunNpmCommand(
                $"pack @deepseek-ai/dsh@{version} --pack-destination \"" + buildDir + "\"" + sources[i],
                out packTail), "realos-pack", out var packIdx);
            Assert.True(packed && File.Exists(tarballPath),
                $"npm pack 失败: {packTail}");

            // ---- ③ 双路径构建完整运行时（pnpm 优先 / npm 降级，生产内核） ----
            var (buildOk, tool) = DshUpdateManager.BuildRuntimeFromTarball(
                tarballPath, tarballName, buildDir, sources, packIdx,
                percentProgress: null, beforeNpmFallback: null);
            Assert.True(buildOk, "运行时构建失败（pnpm/npm 双路径均未成功）");

            // ---- ④ bin 入口校验（产物完整性） ----
            var binEntry = DshUpdateManager.ResolveBuiltBinEntry(buildDir);
            Assert.True(binEntry is not null, "构建产物缺少可解析的 @deepseek-ai/dsh bin 入口");
            Logger.Info($"[RealOS] built with {tool}, bin={binEntry}");

            // ---- ⑤ pending 往返 + 生产应用路径（原子切换） ----
            StagedUpdate.MarkPending(version!, tarballName, prefetched: true, runtimeDir: buildDir);
            var pend = StagedUpdate.ReadPending();
            Assert.Equal(version, pend.Version);
            Assert.Equal(tarballName, pend.Tarball);

            Program.ApplyPendingDshUpdate();

            var appliedDir = System.IO.Path.Combine(dataDir, "runtimes", version!);
            Assert.True(Directory.Exists(appliedDir), $"应用后运行时目录缺失: {appliedDir}");
            Assert.True(File.Exists(System.IO.Path.Combine(
                appliedDir, "node_modules", "@deepseek-ai", "dsh", "package.json")),
                "应用后运行时不完整（package.json 缺失）");
            Assert.Null(StagedUpdate.ReadPendingVersion()); // 成功应用必须清 pending

            // ---- ⑥ 应用后的 dsh 必须真的能执行且版本一致 ----
            var binJs = System.IO.Path.Combine(
                appliedDir, "node_modules", "@deepseek-ai", "dsh",
                binEntry!.TrimStart('.', '/', '\\'));
            Assert.True(File.Exists(binJs), $"bin 入口文件缺失: {binJs}");
            var probed = DshDiscovery.ProbeVersionOutput(nodeExe!, $"\"{binJs}\" --version", 60_000);
            Assert.True(probed is not null && probed.Contains(version!),
                $"应用后 bin.js --version 输出异常: got '{probed}', want contains '{version}'");
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_HOME", savedHome);
            Environment.SetEnvironmentVariable("DSH_NO_UI", savedNoUi);
            Environment.SetEnvironmentVariable("DSH_TEST_FAKE_APPLY", savedFake);
            DshDiscovery.InvalidateCache();
        }
    }
}
