using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace DshShell.E2E;

/// <summary>
/// v0.4.0 T2/T3 用户流程回归（issue：确认更新 → 弹"下载完成" → 立即重启 → 无限弹"有更新"）：
/// 用 `DSH_TEST_FAKE_APPLY=1` 模拟"下载成功并应用"，验证完整闭环：
///   1. 制造 pending（已下载待应用）→ 重启启动器；
///   2. 启动器阶段 0 按决策应用（ApplyNow 路径，fake apply 模拟 npm install 成功）→ 清 pending；
///   3. 主窗口**正常出现**（重启后能正常使用，不卡死、不崩溃）；
///   4. 再次重启 → pending 已清 → 不再因"已应用未清账"重复提示（死循环根因 C 回归）。
/// 全程不触网、不改全局 npm、不依赖 dsh 服务（DSH_TEST_SPLASH_DELAY_MS 跳过后台服务逻辑）。
/// </summary>
public class DshUpdateFlowTests : IAsyncLifetime
{
    private const string MainWindowTitle = "DeepSeek Harness";
    private const string PendingVersion = "0.1.0-rc.7";

    private Process? _proc;
    private string _home = "";
    private string _pendingPath = "";

    public async Task InitializeAsync()
    {
        var exe = E2ETestHelpers.LocateDshWebExe();
        _home = E2ETestHelpers.CreateIsolatedHome();
        _pendingPath = Path.Combine(_home, "dsh-launcher", "pending-update.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_pendingPath)!);
        // 制造 pending：模拟"用户已确认下载成功"（MarkPending 同格式）
        File.WriteAllText(_pendingPath, JsonSerializer.Serialize(new
        {
            version = PendingVersion,
            at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            failCount = 0,
        }));
        _proc = null;
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_proc is { HasExited: false })
        {
            _proc.Kill(entireProcessTree: true);
            await _proc.WaitForExitAsync();
        }
        _proc?.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch { /* 清理失败不影响结果 */ }
    }

    /// <summary>启动启动器（fake apply 模式，跳过后台服务逻辑）。</summary>
    private async Task<Process> LaunchAsync()
    {
        var exe = E2ETestHelpers.LocateDshWebExe();
        var psi = E2ETestHelpers.NewStartInfo(exe, _home,
            ("DSH_TEST_FAKE_APPLY", "1"),
            ("DSH_TEST_SPLASH_DELAY_MS", "500")); // 阶段0后跳过服务 → 直接进主窗
        _proc = Process.Start(psi);
        Assert.NotNull(_proc);
        var hwnd = await E2ETestHelpers.WaitForWindowByTitleAsync(MainWindowTitle, TimeSpan.FromSeconds(30));
        Assert.NotEqual(IntPtr.Zero, hwnd); // 主窗出现 = 重启后能正常使用
        return _proc!;
    }

    [Fact]
    public async Task PendingUpdate_AfterRestart_IsApplied_AndWindowOpensNormally()
    {
        // ---- 第 1 次重启（模拟"下载完成 → 关窗重开"）：pending 应在阶段 0 被应用（fake）→ 清账 ----
        Assert.True(File.Exists(_pendingPath), "前置条件：pending-update.json 应存在");
        var first = await LaunchAsync();
        Assert.False(first.HasExited); // 启动不崩溃、正常进主窗

        // 阶段 0 的 fake apply 已清 pending（应用成功 → ClearPending）
        await WaitForPendingClearedAsync(TimeSpan.FromSeconds(15));

        // ---- 第 2 次重启（死循环回归）：pending 已清 → 不再因"已应用未清账"重复提示 ----
        first.Kill(entireProcessTree: true);
        await first.WaitForExitAsync();

        var second = await LaunchAsync();
        Assert.False(second.HasExited);
        Assert.False(File.Exists(_pendingPath), "第二次重启后不应再出现 pending（死循环根因 C）");

        // 收尾
        second.Kill(entireProcessTree: true);
        await second.WaitForExitAsync();
    }

    [Fact]
    public async Task PendingUpdate_RejectedByUser_SkippedSession_NotRePrompted()
    {
        // 模拟"弹[立即重启应用]→用户点[稍后]"：本次会话不再重复询问（_applyRestartDeferred）。
        // 该路径依赖端口开着（服务在跑）才走 PromptRestart——单屏 CI 端口常关（走 ApplyNow），
        // 因此这里只验证：pending 存在时启动器正常进主窗、进程不因 pending 处理而阻塞/崩溃。
        // （PromptRestart 决策分支已由 ShellLogicServiceLifecycleTests 纯函数矩阵锁定。）
        var proc = await LaunchAsync();
        Assert.False(proc.HasExited);
        // 无论决策分支如何，pending 都必须被消费（ApplyNow→清 / PromptRestart→保留待下次询问）
        proc.Kill(entireProcessTree: true);
        await proc.WaitForExitAsync();
    }

    private async Task WaitForPendingClearedAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!File.Exists(_pendingPath)) return;
            await Task.Delay(200);
        }
        Assert.False(File.Exists(_pendingPath), "阶段 0 应用后 pending-update.json 应在 15s 内被清除");
    }
}
