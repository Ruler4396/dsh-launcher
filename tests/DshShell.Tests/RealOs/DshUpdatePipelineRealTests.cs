using DshWeb;
using DshWeb.Domain;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.RealOs;

/// <summary>
/// dsh 更新引擎 RealOS 全链路（零 Mock，真实 npm 与镜像网络）：
/// 真实拉取最新版本 → npm pack 下载 tarball → 双路径构建完整运行时 → bin 入口校验
/// → MarkPending → 生产 <see cref="DshUpdateManager.ApplyPending"/>（SelfContained 原子切换路径 A）
/// → 应用后运行时以 node 真实执行并校验版本号。
/// 门禁约定：DSH_FORCE_REALNET=1（scripts/test.ps1 -RealNet）时无 Node 即硬失败；
/// 未设置则跳过——CI（build 与 realos 工作流）默认均不触发，防发布流水线被镜像网络劫持。
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

    /// <summary>真实拉取最新版本号：4 次退避重试，第 3 次起切 registry.npmjs.org 官方源兜底。</summary>
    private static string? FetchLatestVersionWithFallback()
    {
        string? version = null;
        var savedReg = Environment.GetEnvironmentVariable("DSH_NPM_REGISTRY");
        try
        {
            for (var attempt = 1; attempt <= 4 && version is null; attempt++)
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

    /// <summary>拉取并解析 @deepseek-ai/dsh 的 packument（双源 × 2 轮重试，抗镜像瞬时抖动）。</summary>
    private static System.Text.Json.JsonDocument FetchPackument()
    {
        var bases = new[] { UpdateChecker.NpmRegistryBase.TrimEnd('/'), "https://registry.npmjs.org" }
            .Where(b => !string.IsNullOrWhiteSpace(b)).Distinct().ToArray();
        Exception? last = null;
        for (var round = 0; round < 2; round++)
        {
            foreach (var b in bases)
            {
                try
                {
                    using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                    return System.Text.Json.JsonDocument.Parse(
                        http.GetStringAsync(b + "/@deepseek-ai%2Fdsh").GetAwaiter().GetResult());
                }
                catch (Exception ex) { last = ex; Logger.Warn($"packument via {b} 失败(轮{round + 1}): {ex.Message}"); }
            }
        }
        Assert.True(false, $"packument 拉取失败（双源两轮）: {last?.Message}");
        return null!;
    }

    /// <summary>从 registry packument 拉取全部已发布版本号（HTTP 直读，规避 npm 子进程输出截断）。</summary>
    private static string[] FetchPublishedVersions()
    {
        using var doc = FetchPackument();
        return doc.RootElement.GetProperty("versions").EnumerateObject()
            .Select(p => p.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToArray();
    }

    /// <summary>
    /// 跨版本升级真实场景（旧版在位 → 升级到最新 → 身份链切换 → 服务可用）：
    /// ① 真实安装一个旧版本运行时进 runtimes\（发现器此时应选中它）；
    /// ② 真实安装最新版并 MarkPending → 生产 ApplyPendingDshUpdate 原子切换；
    /// ③ 断言升级后 DiscoverCurrentRuntime 指向新版 runtimes\&lt;new&gt;（SelfContained 优先级压过全局安装）、
    ///    旧版目录保留可回滚、新 bin 真实可执行且版本一致；
    /// ④ 以 start-dsh.vbs 同款方式（node bin web --port --no-open）拉起服务并等待 HTTP 200——
    ///    即用户侧"更新后能否顺利使用"的零 Mock 回答。
    /// </summary>
    [Fact]
    [Trait("Category", "RealNet")]
    public void CrossVersionUpgrade_IdentitySwitchesToNew_ServiceServesHttp200()
    {
        if (Environment.GetEnvironmentVariable("DSH_FORCE_REALNET") != "1") return;
        var nodeExe = RuntimeResolver.ResolveExisting().NodeExe;
        Assert.True(nodeExe is not null, "本测试由 DSH_FORCE_REALNET=1 强制运行，请先安装 Node.js 18+。");

        var savedHome = Environment.GetEnvironmentVariable("DSH_HOME");
        var savedNoUi = Environment.GetEnvironmentVariable("DSH_NO_UI");
        var savedFake = Environment.GetEnvironmentVariable("DSH_TEST_FAKE_APPLY");
        using var tmp = new TempDir();
        System.Diagnostics.Process? service = null;
        try
        {
            var home = System.IO.Path.Combine(tmp.Path, "home");
            var dataDir = System.IO.Path.Combine(home, "dsh-launcher");
            var runtimes = System.IO.Path.Combine(dataDir, "runtimes");
            Directory.CreateDirectory(runtimes);
            Environment.SetEnvironmentVariable("DSH_HOME", home);
            Environment.SetEnvironmentVariable("DSH_NO_UI", "1");
            Environment.SetEnvironmentVariable("DSH_TEST_FAKE_APPLY", null);
            StagedUpdate.Init(dataDir);

            var newVer = FetchLatestVersionWithFallback();
            Assert.True(!string.IsNullOrWhiteSpace(newVer), "registry 版本拉取失败（含官方源兜底）");

            // ---- ① 选一个真实的"上一代"版本并铺设进 runtimes\<old> ----
            var all = FetchPublishedVersions();
            // 注意：System.Version 不支持预发布号（TryParse("0.1.0-rc.6")==false），
            // 必须用生产同款 DshDiscovery.CompareVersions（core 比较 + prerelease 序数比较）
            var older = all.Where(v => !string.IsNullOrWhiteSpace(v)
                    && v != newVer
                    && DshDiscovery.CompareVersions(v, newVer) < 0)
                .Distinct().ToList();
            older.Sort((a, b) => DshDiscovery.CompareVersions(b!, a!)); // 降序：取最接近的最新旧版
            Assert.True(older.Count > 0, $"registry 无低于 {newVer} 的历史版本，无法构造跨版本场景（共 {all.Length} 版）");
            var oldVer = older[0];
            Logger.Info($"[RealOS] cross-version: {oldVer} -> {newVer}");

            var oldDir = System.IO.Path.Combine(runtimes, oldVer!);
            LayRuntimeFromTarball(oldVer!, oldDir); // 官方 tarball 直铺（秒级、零 npm 子进程）
            Assert.True(DshUpdateManager.ResolveBuiltBinEntry(oldDir) is not null, "旧版运行时缺 bin 入口");

            // 升级前：发现器必须选中旧版（证明"旧版本在位且可用"这一前提成立）
            var before = DshDiscovery.DiscoverCurrentRuntime();
            Assert.Equal(DshSource.SelfContained, before.Source);
            Assert.Equal(oldVer, before.Version);

            // ---- ② 构建最新版运行时 → pending → 生产应用 ----
            // 同样走 tarball 直铺（上游真实产物、秒级）：本用例专测"跨版本切换与升级后可用性"；
            // 下载→构建内核的零 Mock 覆盖由 FullPipeline 用例承担（网络允许时实跑 pnpm/npm）。
            var staging = System.IO.Path.Combine(dataDir, "staging");
            var newDir = System.IO.Path.Combine(staging, $"runtime-build-{newVer}");
            LayRuntimeFromTarball(newVer!, newDir);
            var binEntryNew = DshUpdateManager.ResolveBuiltBinEntry(newDir);
            Assert.True(binEntryNew is not null, "新版运行时缺 bin 入口");

            StagedUpdate.MarkPending(newVer!, $"deepseek-ai-dsh-{newVer}.tgz",
                prefetched: true, runtimeDir: newDir);
            new DshUpdateManager(dataDir).ApplyPending();

            // ---- ③ 升级后断言：身份切到新版 / 旧版保留 / bin 可执行 ----
            var appliedDir = System.IO.Path.Combine(runtimes, newVer!);
            Assert.True(Directory.Exists(appliedDir), "应用后 runtimes\\<new> 缺失");
            Assert.True(Directory.Exists(oldDir), "旧版运行时被误删（应保留作回滚）");
            Assert.Null(StagedUpdate.ReadPendingVersion());

            var after = DshDiscovery.DiscoverCurrentRuntime();
            Assert.Equal(DshSource.SelfContained, after.Source);
            Assert.Equal(newVer, after.Version);
            Assert.Equal(appliedDir, after.RuntimeDir);

            var binJs = System.IO.Path.Combine(
                appliedDir, "node_modules", "@deepseek-ai", "dsh", binEntryNew!.TrimStart('.', '/', '\\'));
            Assert.True(File.Exists(binJs), $"bin 入口文件缺失: {binJs}");
            var probed = DshDiscovery.ProbeVersionOutput(nodeExe!, $"\"{binJs}\" --version", 60_000);
            Assert.True(probed is not null && probed.Contains(newVer!),
                $"升级后 --version 输出异常: got '{probed}', want contains '{newVer}'");

            // ---- ④ 服务可用性：start-dsh.vbs 同款启动方式，HTTP 就绪即"顺利使用" ----
            var port = GetFreePort();
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = nodeExe!,
                Arguments = $"\"{binJs}\" web --port {port} --no-open",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = appliedDir,
            };
            psi.EnvironmentVariables["DSH_HOME"] = home;
            service = System.Diagnostics.Process.Start(psi);
            Assert.True(service is not null, "服务进程启动失败");
            // 必须持续排水：管道缓冲写满会阻塞 dsh 输出线程导致服务假死（生产 vbs 重定向到文件无此问题）
            var outLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            var errLines = new System.Collections.Concurrent.ConcurrentQueue<string>();
            service.OutputDataReceived += (_, e) => { if (e.Data is not null) outLines.Enqueue(e.Data); };
            service.ErrorDataReceived += (_, e) => { if (e.Data is not null) errLines.Enqueue(e.Data); };
            service.BeginOutputReadLine();
            service.BeginErrorReadLine();

            var ready = false;
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                if (service.HasExited) break;
                try
                {
                    using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    var resp = client.GetAsync($"http://127.0.0.1:{port}/").GetAwaiter().GetResult();
                    if (resp.IsSuccessStatusCode) { ready = true; break; }
                }
                catch { /* 未就绪继续等 */ }
                Thread.Sleep(700);
            }
            Assert.True(ready,
                $"升级后服务未就绪 (exited={service.HasExited}" +
                (service.HasExited ? $", code={service.ExitCode}" : "") + ") port=" + port +
                " stderr尾部=[" + string.Join(" | ", errLines.TakeLast(8)) + "]" +
                " stdout尾部=[" + string.Join(" | ", outLines.TakeLast(8)) + "]");
        }
        finally
        {
            if (service is not null && !service.HasExited)
            {
                try { service.Kill(entireProcessTree: true); } catch { }
            }
            Environment.SetEnvironmentVariable("DSH_HOME", savedHome);
            Environment.SetEnvironmentVariable("DSH_NO_UI", savedNoUi);
            Environment.SetEnvironmentVariable("DSH_TEST_FAKE_APPLY", savedFake);
            DshDiscovery.InvalidateCache();
        }
    }

    /// <summary>取指定版本的官方 tarball 下载地址（packument dist.tarball，双源兜底）。</summary>
    private static string FetchTarballUrl(string version)
    {
        using var doc = FetchPackument();
        return doc.RootElement.GetProperty("versions").GetProperty(version)
            .GetProperty("dist").GetProperty("tarball").GetString()!;
    }

    /// <summary>
    /// 以 registry 官方 tarball 直接铺设一个完整运行时目录（node_modules/@deepseek-ai/dsh 布局）：
    /// .NET 内置 GZip+Tar 解包，零 npm 子进程——用于构造跨版本场景的"旧版在位"前提，
    /// 与生产 npm 装机产物布局逐字节同构（同一上游包内容）。
    /// </summary>
    private static void LayRuntimeFromTarball(string version, string runtimeDir)
    {
        var url = FetchTarballUrl(version);
        var pkgDir = System.IO.Path.Combine(
            runtimeDir, "node_modules", "@deepseek-ai", "dsh");
        Directory.CreateDirectory(pkgDir);
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(180) };
        var bytes = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
        using var ms = new MemoryStream(bytes);
        using var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress);
        // tar 包内顶层为 package/ —— 解到临时目录后把内容搬进目标布局
        var tmpEx = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dsh-extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmpEx);
        try
        {
            System.Formats.Tar.TarFile.ExtractToDirectory(gz, tmpEx, overwriteFiles: true);
            var src = System.IO.Path.Combine(tmpEx, "package");
            Assert.True(Directory.Exists(src), "tarball 缺少顶层 package/ 目录");
            foreach (var item in Directory.GetFileSystemEntries(src))
            {
                var dest = System.IO.Path.Combine(pkgDir, System.IO.Path.GetFileName(item));
                if (Directory.Exists(item))
                {
                    if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
                    Directory.Move(item, dest);
                }
                else
                {
                    System.IO.File.Move(item, dest, overwrite: true);
                }
            }

            // 嫁接依赖树：dsh 有 60+ 运行时依赖（裸包无法执行）。从本机全局安装只读复制
            // 嵌套 node_modules（与包体同版本系）——零 npm 子进程，规避镜像网络波动。
            var globalDsh = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "npm", "node_modules", "@deepseek-ai", "dsh");
            var globalDeps = System.IO.Path.Combine(globalDsh, "node_modules");
            Assert.True(Directory.Exists(globalDeps),
                $"本机全局 dsh 缺少嵌套依赖树（{globalDeps}）——请先 npm install -g @deepseek-ai/dsh 一次");
            CopyDirectory(globalDeps, System.IO.Path.Combine(pkgDir, "node_modules"));
        }
        finally { try { Directory.Delete(tmpEx, recursive: true); } catch { } }
    }

    /// <summary>递归复制目录（已存在文件覆盖）。</summary>
    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var f in Directory.GetFiles(src)) System.IO.File.Copy(f, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(f)), overwrite: true);
        foreach (var d in Directory.GetDirectories(src))
            CopyDirectory(d, System.IO.Path.Combine(dest, System.IO.Path.GetFileName(d)));
    }

    /// <summary>取一个当前空闲的高位 TCP 端口。</summary>
    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }

    [Fact]
    [Trait("Category", "RealNet")]
    public void FullPipeline_FetchDownloadBuildApply_RuntimeExecutableAndVersionMatches()
    {
        var nodeExe = RuntimeResolver.ResolveExisting().NodeExe;
        if (Environment.GetEnvironmentVariable("DSH_FORCE_REALNET") != "1") return; // 未显式开启：跳过

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

            // ---- ① 真实拉取最新版本号（4 次退避重试，第 3 次起官方源兜底） ----
            var version = FetchLatestVersionWithFallback();
            Assert.True(!string.IsNullOrWhiteSpace(version), "registry 版本拉取失败（含官方源兜底仍为空）");
            Logger.Info($"[RealOS] latest dsh version = {version}");

            // ---- ② npm pack 真实下载 tarball（镜像源序列与生产一致） ----
            var staging = System.IO.Path.Combine(dataDir, "staging");
            var buildDir = System.IO.Path.Combine(staging, $"runtime-build-{version}");
            Directory.CreateDirectory(buildDir);
            var tarballName = $"deepseek-ai-dsh-{version}.tgz";
            var tarballPath = System.IO.Path.Combine(buildDir, tarballName);
            var sources = ProcessRunner.GetNpmRegistrySources();
            string packTail = "";
            var packed = ProcessRunner.TryNpmOverRegistries(sources, i => ProcessRunner.RunNpmCommand(
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

            new DshUpdateManager(dataDir).ApplyPending();

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
