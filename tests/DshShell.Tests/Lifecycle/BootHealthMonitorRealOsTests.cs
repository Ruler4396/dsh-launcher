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

    [Fact]
    [Trait("Category", "RealOS")]
    public async Task RealOs_BootMonitor_RealProcessNonZeroExit_CapturedWithCode()
    {
        var shell = ResolveShellExe();
        Assert.True(shell != null, "no PowerShell host available for real-process test");
        var psi = new ProcessStartInfo(shell, "-NoProfile -Command \"Start-Sleep -Milliseconds 300; exit 7\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("failed to spawn real process");
        var profile = new ShellLogic.BootGuard.BootProfile { GraceMs = 60000, AbsentThreshold = 1000 };
        using var m = new BootHealthMonitor(profile, null, "http://127.0.0.1:1", null, pid => new RealProcessHandle(pid));
        var tcs = new TaskCompletionSource<BootVerdict>(TaskCreationOptions.RunContinuationsAsynchronously);
        m.Failed += v => tcs.TrySetResult(v);
        m.AttachProcess(proc.Id);
        Assert.True(await Task.WhenAny(tcs.Task, Task.Delay(20000)) == tcs.Task,
            "monitor did not observe real process exit within 20s");
        var verdict = await tcs.Task;
        Assert.Equal("E2007", verdict.ErrorCode);
        var evidence = Assert.Single(verdict.Evidence, e => e.Layer == BootLayer.Process);
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
