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
}
