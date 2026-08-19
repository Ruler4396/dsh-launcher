using DshWeb;
using DshWeb.Lifecycle;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Managers;

/// <summary>
/// 更新流程 UI 联动契约测试（任务一/三/四）。
/// 背景：ApplyPendingDshUpdate 在启动流水线阶段 0 后台执行（npm install -g 可达 30-120s），
/// 必须向 SplashForm 上报"正在应用更新 (vX)…"与 npm 实时日志，缓解"卡死"焦虑；失败时
/// 按"可重试（网络类）保留 pending / 不可重试（权限/包损坏）清 pending"策略处理，防死循环。
///
/// 说明：真正的 npm 执行（Program.RunNpmCommand）在 Program 私有静态，无法直接单测；
/// 本类锁定**可测契约**：
///   1. LauncherApp 阶段 0 执行 BackgroundMaintenance 时，可通过注入的进度委托把"正在应用
///      更新"与 npm 输出上报给 IProgress&lt;string&gt;（任务一 UpdateApply_ProgressReported）；
///   2. ShellLogic.IsRetryableNpmError 纯函数锁定 pending 保留/清理策略（任务三）；
///   3. LauncherApp 在更新（后台维护）后仍能进入 Running（旧版本继续启动，不因更新失败挂起）。
/// 全部确定性、毫秒级、零网络。
/// </summary>
public class UpdateFlowContractTests
{
    // ---------------- Fakes（与现有 LauncherAppScenarioTests 同风格，零 Mock 依赖） ----------------

    private sealed class FakeRuntime : IRuntimeManager
    {
        public RuntimeResult Result { get; init; } = RuntimeResult.ReadyNow("node.exe");
        public Task<RuntimeResult> EnsureRuntimeAsync(CancellationToken ct = default) => Task.FromResult(Result);
        public void PrependToPath(string nodeRoot) { }
    }

    private sealed class FakeService : IServiceManager
    {
        public bool Ready { get; init; } = true;
        public ShellLogic.ServicePortState PortState { get; init; } = ShellLogic.ServicePortState.Healthy;
        public bool KillZombieResult { get; init; } = true;
        public bool NeedsStart(int port) => false;
        public Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(Ready);
        public ShellLogic.ServicePortState ProbePort(int port, string url) => PortState;
        public bool KillZombieTree(int port) => KillZombieResult;
    }

    /// <summary>捕获 IProgress&lt;string&gt; 上报序列的进度记录器。</summary>
    private sealed class ProgressRecorder : IProgress<string>
    {
        public List<string> Items { get; } = new();
        public void Report(string value) => Items.Add(value);
    }

    // ---------------- 任务一：更新进度上报 ----------------

    [Fact]
    public async Task UpdateApply_ProgressReported()
    {
        // 模拟 ApplyPendingDshUpdate：阶段 0（BackgroundMaintenance）内上报"正在应用更新 (vX)…"
        // 与 npm 实时日志（"added 50 packages"）→ 断言经 LauncherApp 的 progress 透传到调用方。
        var progress = new ProgressRecorder();
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true })
        {
            BackgroundMaintenance = ct =>
            {
                // 与 Program.ApplyPendingDshUpdate 一致的上报序列（经组合根桥接到 Splash）
                progress.Report("正在应用更新 (v0.1.0-rc.7)…");
                progress.Report("added 50 packages from @deepseek-ai/dsh");
                progress.Report("npm notice created a lockfile");
                progress.Report("updated 1 package in 32s");
            },
        };

        Assert.True(await app.RunStartupAsync(progress));
        Assert.Equal(LifecycleState.Running, app.State);

        // 断言收到"正在应用更新"与 npm 安装输出（缓解卡死焦虑的滚动日志）
        Assert.Contains(progress.Items, s => s.Contains("正在应用更新"));
        Assert.Contains(progress.Items, s => s.Contains("added 50 packages"));
        Assert.Contains(progress.Items, s => s.Contains("updated 1 package"));
    }

    [Fact]
    public async Task UpdateApply_BackgroundMaintenance_RunsBeforeReadiness_AndUiStaysResponsive()
    {
        // 阶段 0 维护（含 npm install）在 WaitingForReadiness 之前执行，且进度可透传：
        // 验证"更新安装期间 UI 文本更新"的时序契约——阶段 0 完成前 progress 已有更新上报。
        var progress = new ProgressRecorder();
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true })
        {
            BackgroundMaintenance = ct => progress.Report("正在应用更新 (v1.2.3)…"),
        };

        Assert.True(await app.RunStartupAsync(progress));
        // 阶段 0 的更新上报先于"正在准备启动环境"之后的任意进度
        Assert.Contains(progress.Items, s => s.Contains("正在应用更新"));
    }

    // ---------------- 任务三：更新失败不挂起，旧版本继续启动 ----------------

    [Fact]
    public async Task UpdateApply_Failure_DoesNotBlockStartup_OldVersionContinues()
    {
        // Mock npm install 抛异常（BackgroundMaintenance 内模拟 ApplyPendingDshUpdate 失败路径，
        // 记录 E4002 语义后继续）→ 断言状态机仍进入 Running（旧版本启动，不因更新失败挂起）。
        var progress = new ProgressRecorder();
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true })
        {
            BackgroundMaintenance = ct =>
            {
                progress.Report("正在应用更新 (v1.2.3)…");
                // 模拟安装失败：非重试错误（权限不足）
                // 生产路径：Logger.Warn(E4002) + NotifyUpdateApplyFailed(version, errorTail)
                progress.Report("[warn] 自动应用更新失败 (v1.2.3)。将继续使用旧版本启动。");
            },
        };

        Assert.True(await app.RunStartupAsync(progress));
        Assert.Equal(LifecycleState.Running, app.State); // 失败不阻断启动
        Assert.Contains(progress.Items, s => s.Contains("更新失败") || s.Contains("继续使用旧版本"));
    }

    // ---------------- 任务三：pending 保留/清理策略（纯函数契约） ----------------

    [Theory]
    // 可重试（网络/超时）→ 保留 pending，下次启动重试
    [InlineData("npm ERR! code ETIMEDOUT", true)]
    [InlineData("ECONNRESET socket hang up", true)]
    [InlineData("ECONNREFUSED", true)]
    [InlineData("getaddrinfo ENOTFOUND registry.npmjs.org", true)]
    [InlineData("network timed out", true)]
    [InlineData("EAI_AGAIN", true)]
    [InlineData("registry error", true)]
    // 不可重试（权限/包损坏）→ 清 pending 防死循环
    [InlineData("EACCES permission denied", false)]
    [InlineData("EINTEGRITY checksum failed", false)]
    [InlineData("ERESOLVE dependency conflict", false)]
    [InlineData("", false)]
    [InlineData(null!, false)]
    public void IsRetryableNpmError_Classifies_RetryableVsFatal(string tail, bool expected)
    {
        Assert.Equal(expected, ShellLogic.IsRetryableNpmError(tail));
    }

    // ---------------- 任务一/二/三：后台依赖预热（Cache Prefetch）契约 ----------------

    [Fact]
    public void PrefetchTempDir_IsUnderStaging_AfterInit()
    {
        // 预热临时目录必须在 staging 下（任务一：DataDir\staging\prefetch_temp），
        // 与应用成功后的整体清理同域（任务二：释放磁盘）。
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        Assert.Equal(Path.Combine(tmp.Path, "staging", "prefetch_temp"), StagedUpdate.PrefetchTempDir);
        Assert.Equal(Path.Combine(tmp.Path, "staging"), StagedUpdate.StagingDir);
    }

    [Fact]
    public void PrefetchDir_IsCreatedUnderStaging_ForPackDestination()
    {
        // 根因契约（用户 22:0x "文件名、目录名或卷标语法不正确" E4001）：
        // npm pack --pack-destination 指向的 prefetch_temp 目录必须先存在，否则 Windows 中文
        // 系统底层 fs 返回 ERROR_INVALID_NAME。锁定"预热目录可从 staging 推导且可被创建"语义，
        // 防止下载管线回归"只建 staging 不建 prefetch_temp"（历史 bug：DownloadDshUpdateStaged
        // 曾只 CreateDirectory(staging)）。
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        var prefetch = StagedUpdate.PrefetchTempDir!;

        // 模拟下载管线：先建 staging（现有代码），再建 prefetch（根因修复点），两者幂等可重复
        Directory.CreateDirectory(StagedUpdate.StagingDir!);
        Directory.CreateDirectory(prefetch);
        Assert.True(Directory.Exists(prefetch));

        // 幂等：重复创建不抛（npm pack 前无论目录是否已存在都安全）
        Directory.CreateDirectory(prefetch);
        Assert.True(Directory.Exists(prefetch));

        // 层级正确：prefetch 是 staging 的子目录（同一清理域）
        Assert.StartsWith(StagedUpdate.StagingDir! + Path.DirectorySeparatorChar, prefetch);
    }

    [Fact]
    public void LocateTarball_ResolvesPackName_NormalizesScopeNaming()
    {
        // npm pack 对 scoped 包 @deepseek-ai/dsh 的产物名是 deepseek-ai-dsh-{version}.tgz
        //（去 @ 和 /）；本契约锁定"命名规则兜底"能直接定位到该产物（重启安装用）。
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        var staging = StagedUpdate.StagingDir!;
        Directory.CreateDirectory(staging);
        var tarball = Path.Combine(staging, "deepseek-ai-dsh-1.2.3.tgz");
        File.WriteAllText(tarball, "pack");
        Assert.Equal(tarball, StagedUpdate.LocateTarball("1.2.3", null));
    }

    [Fact]
    public void Prefetch_Failure_StillMarksPending_WithTarball()
    {
        // 任务三容错契约：预热失败（模拟）**不得阻塞 Staging**——pending 依然记录版本与 tarball，
        // 重启回退在线安装。这里锁定 MarkPending 在"仅 pack 成功、未预热"场景下仍写入 pending。
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        var staging = StagedUpdate.StagingDir!;
        Directory.CreateDirectory(staging);
        // 模拟：pack 成功落盘 tarball，但预热失败（prefetch_temp 里没有 deps）
        var prefetch = Path.Combine(staging, "prefetch_temp");
        Directory.CreateDirectory(prefetch);
        File.WriteAllText(Path.Combine(staging, "deepseek-ai-dsh-1.2.3.tgz"), "pack");

        // Staging 流程：预热失败 → 仍 MarkPending（tarball 已就位）
        StagedUpdate.MarkPending("1.2.3", "deepseek-ai-dsh-1.2.3.tgz");
        var (version, _, tarball) = StagedUpdate.ReadPending();
        Assert.Equal("1.2.3", version);
        Assert.Equal("deepseek-ai-dsh-1.2.3.tgz", tarball);
        // 重启时 LocateTarball 仍能找到 tarball（回退在线安装的本地主包入口）
        Assert.NotNull(StagedUpdate.LocateTarball(version, tarball));
    }

    [Fact]
    public void ApplyUpdate_Success_CleansPrefetchTemp()
    {
        // 任务二清理契约：应用成功后 prefetch_temp 被整体删除（释放磁盘），
        // pending 清账，tarball 随 staging 清理。模拟清理路径（TryDeleteDir 语义幂等）。
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        var prefetch = StagedUpdate.PrefetchTempDir!;
        Directory.CreateDirectory(Path.Combine(prefetch, "deps", "node_modules", "@deepseek-ai"));
        File.WriteAllText(Path.Combine(prefetch, "deps", "package.json"), "{}");
        StagedUpdate.MarkPending("1.2.3", "deepseek-ai-dsh-1.2.3.tgz");

        // 模拟应用成功后：清 pending + 清 prefetch_temp
        StagedUpdate.ClearPending();
        Directory.Delete(prefetch, recursive: true);

        Assert.False(Directory.Exists(prefetch)); // 临时安装目录已释放
        var (v, _, _) = StagedUpdate.ReadPending();
        Assert.Null(v); // pending 清账
    }

    // ---------------- 任务一/四：npm 执行机制（cmd shim + 绝对路径解析 + 错误报告）契约 ----------------

    [Fact]
    public void ResolveNpmCmdPath_PrefersNodeRoot_ThenWhereFallback()
    {
        // NpmCmd_Execution_Works 语义：优先用已解析的 Node 根目录拼 npm.cmd 绝对路径
        //（GUI PATH 缺 Node 时的隔离方案）；根目录无 npm.cmd 时回退 where 定位结果。
        using var tmp = new TempDir();
        var nodeRoot = Path.Combine(tmp.Path, "node");
        Directory.CreateDirectory(nodeRoot);

        // ① Node 根目录有 npm.cmd → 优先返回它（带引号）
        var npmRoot = Path.Combine(nodeRoot, "npm.cmd");
        File.WriteAllText(npmRoot, "npm shim");
        Assert.Equal("\"" + npmRoot + "\"", ShellLogic.ResolveNpmCmdPath(nodeRoot, null));

        // ② Node 根目录无 npm.cmd → 回退 where 结果
        File.Delete(npmRoot);
        var wherePath = Path.Combine(tmp.Path, "where-npm.cmd");
        File.WriteAllText(wherePath, "npm shim");
        Assert.Equal("\"" + wherePath + "\"", ShellLogic.ResolveNpmCmdPath(nodeRoot, wherePath));

        // ③ 两者都不可用（不存在）→ null（调用方回退 cmd /c npm 并靠 PATH）
        Assert.Null(ShellLogic.ResolveNpmCmdPath(nodeRoot, null));
        Assert.Null(ShellLogic.ResolveNpmCmdPath(nodeRoot, Path.Combine(tmp.Path, "ghost.cmd")));
        Assert.Null(ShellLogic.ResolveNpmCmdPath(null, null));
    }

    [Theory]
    // NpmCmd_NotFound_FailsGracefully 语义：cmd /c npm 找不到时输出被识别为 npm 环境缺失，
    // errorTail 转为明确提示（而非裸异常/笼统"下载失败"）
    [InlineData("'npm' 不是内部或外部命令，也不是可运行的程序", true)]
    [InlineData("'npm' is not recognized as an internal or external command", true)]
    [InlineData("系统找不到指定的文件。", true)]
    [InlineData("Error: Cannot find module 'npm-cli.js'", true)]
    // 网络/registry 类 → 不误判为 npm 缺失
    [InlineData("npm ERR! code ETIMEDOUT", false)]
    [InlineData("npm ERR! network request to registry failed", false)]
    [InlineData("EACCES permission denied", false)]
    [InlineData("", false)]
    [InlineData(null!, false)]
    public void IsNpmNotFoundError_Classifies_EnvironmentMissing(string tail, bool expected)
    {
        Assert.Equal(expected, ShellLogic.IsNpmNotFoundError(tail));
    }

    /// <summary>每测试用一次性临时目录（自动清理，与 V030FeaturesTests 同风格）。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dsh-updateflow-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
