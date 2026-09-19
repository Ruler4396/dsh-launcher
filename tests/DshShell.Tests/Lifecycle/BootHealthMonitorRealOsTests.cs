using System.Diagnostics;
using DshWeb;
using DshWeb.Lifecycle;
using Xunit;

namespace DshShell.Tests.Lifecycle;

/// <summary>
/// BootHealthMonitor 真实 OS 交互测试（Category=RealOS，铁律：进程相关必须真机验证）。
/// 用真实 PowerShell 子进程验证：非零退出被捕获且带 exit code；存活进程保持 Pending。
/// </summary>
public class BootHealthMonitorRealOsTests
{
    private static string? ResolveShellExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "PowerShell", "7", "pwsh.exe"),
        };
        foreach (var c in candidates)
            if (File.Exists(c)) return c;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var pwsh = Path.Combine(dir.Trim(), "pwsh.exe");
            if (File.Exists(pwsh)) return pwsh;
        }
        return null;
    }

    /// <summary>
    /// 场景：真起一个 PowerShell 子进程，但它**不再按固定毫秒数自己消失**——它等一个标志文件
    /// 才退出，于是"attach 先于退出落地"由测试自己保证，而不是赌一次线程池调度。
    /// 返回 (进程层是否接上, 监控给出的裁决)。裁决预算只给 5 秒，用于"接上了却不出裁决"的分离诊断。
    /// </summary>
    private static async Task<(bool Attached, BootVerdict? Verdict)> RunRealExitScenarioAsync(
        bool needVerdict = true, int attachBudgetMs = 30000, int verdictBudgetMs = 60000)
    {
        var shell = ResolveShellExe();
        Assert.True(shell != null, "no PowerShell host available for real-process test");
        var flag = Path.Combine(Path.GetTempPath(), "dsh-exit7-" + Guid.NewGuid().ToString("N") + ".flag");
        var psi = new ProcessStartInfo(shell!,
            "-NoProfile -Command \"while (-not (Test-Path -LiteralPath '" + flag + "')) { "
            + "Start-Sleep -Milliseconds 100 }; exit 7\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        var profile = new ShellLogic.BootGuard.BootProfile { GraceMs = 60000, AbsentThreshold = 1000 };
        var attachedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var m = new BootHealthMonitor(profile, null, "http://127.0.0.1:1", null,
            pid => new RealProcessHandle(pid),
            trace: s => { if (s.Contains("process layer attached")) attachedTcs.TrySetResult(true); });
        var verdictTcs = new TaskCompletionSource<BootVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Failed += v => verdictTcs.TrySetResult(v);
        Process? proc = null;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to spawn real process");
            m.AttachProcess(proc.Id);
            if (await Task.WhenAny(attachedTcs.Task, Task.Delay(attachBudgetMs)) != attachedTcs.Task)
                return (false, null);
            File.WriteAllText(flag, "go"); // 此刻才放行：子进程下一步就是 exit 7
            if (!needVerdict) return (true, null);
            if (await Task.WhenAny(verdictTcs.Task, Task.Delay(verdictBudgetMs)) != verdictTcs.Task)
                return (true, null);
            return (true, await verdictTcs.Task);
        }
        finally
        {
            try { if (proc is not null && !proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch { /* 已退出：无子进程可回收 */ }
            proc?.Dispose();
            try { File.Delete(flag); } catch { /* 交给临时目录回收 */ }
        }
    }

    // 下面三条**故意拆开**：realos-test.yml 用 `dotnet test -v q`，xUnit 的 Error Message 整段被吞，
    // CI 红只留一行 "[FAIL] 测试名"。所以把这条链的三个环节做成三个独立断言——哪一个断，
    // 那个环节的名字就直接出现在红灯里，不需要日志 verbosity 就能归因。

    [Fact]
    [Trait("Category", "RealOS")]
    public async Task RealOs_BootMonitor_RealProcessNonZeroExit_AttachLayerLands()
    {
        var (attached, _) = await RunRealExitScenarioAsync(needVerdict: false);
        Assert.True(attached,
            "进程层从未接上：AttachProcess 在后台任务里跑，若 pid 已消失则 GetProcessById 抛异常、"
            + "监控按设计只 Warn 不判死（接线问题，不是退出码问题）");
    }

    [Fact]
    [Trait("Category", "RealOS")]
    public async Task RealOs_BootMonitor_RealProcessNonZeroExit_FailsWithE2007()
    {
        var (attached, verdict) = await RunRealExitScenarioAsync(verdictBudgetMs: 15000);
        Assert.True(attached, "前置不成立：进程层未接上（归 AttachLayerLands 那条管）");
        Assert.NotNull(verdict); // 已接上 + 已放行退出，却拿不到裁决 = 进程层失明
        Assert.Equal("E2007", verdict!.ErrorCode);
        Assert.Single(verdict.Evidence, e => e.Layer == BootLayer.Process);
    }

    [Fact]
    [Trait("Category", "RealOS")]
    public async Task RealOs_BootMonitor_RealProcessNonZeroExit_EvidenceCarriesExitCode7()
    {
        var (attached, verdict) = await RunRealExitScenarioAsync();
        Assert.True(attached, "前置不成立：进程层未接上（归 AttachLayerLands 那条管）");
        Assert.NotNull(verdict);
        var evidence = Assert.Single(verdict!.Evidence, e => e.Layer == BootLayer.Process);
        Assert.Contains("7", evidence.Detail);
    }

    [Fact]
    [Trait("Category", "RealOS")]
    public async Task RealOs_BootMonitor_AttachToAliveProcess_StaysPendingUntilExit()
    {
        var shell = ResolveShellExe();
        Assert.True(shell != null, "no PowerShell host available for real-process test");
        var psi = new ProcessStartInfo(shell, "-NoProfile -Command \"Start-Sleep -Seconds 30\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to spawn real process");
        try
        {
            var profile = new ShellLogic.BootGuard.BootProfile { GraceMs = 60000, AbsentThreshold = 1000 };
            using var m = new BootHealthMonitor(profile, null, "http://127.0.0.1:1", null, pid => new RealProcessHandle(pid));
            m.AttachProcess(proc.Id);
            await Task.Delay(1500);
            Assert.Equal(BootHealthState.Pending, m.State);
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
        }
    }
}
