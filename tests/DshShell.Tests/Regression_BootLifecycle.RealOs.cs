using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DshWeb;
using Xunit;
using Xunit.Abstractions;

namespace DshShell.Tests;

/// <summary>
/// 启动生命周期回归测试（零 Mock，真实 OS 进程；[Category=RealOS]，CI 无 node 时自动跳过）。
/// 覆盖 2026-08 三处 P0/P1 修复的真实构件：
///  - 修复点1：KillServiceProcess 可靠终止真实监听进程（等待 taskkill 退出 + 强杀确认窗口）。
///  - 修复点2：启动清扫闭环的构件——KillServiceProcess 终结"活着且监听目标端口"的真僵尸并释放端口。
///  - 修复点1 防误杀：端口归属校验失败时拒绝杀进程，绝不误杀无辜 node。
/// 说明：SweepStaleServicePid 的 pid 文件认领闭环私有，但其核心动作即本文件中
///       KillServiceProcess(真实监听 pid, 端口)→终结+释放，已在此零 Mock 复现。
/// </summary>
[Collection("RealOS")]
[Trait("Category", "RealOS")]
public class Regression_BootLifecycle_RealOs
{
    private readonly ITestOutputHelper _out;
    public Regression_BootLifecycle_RealOs(ITestOutputHelper o) => _out = o;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static Process? StartNodeServer(int port, out int pid)
    {
        var node = RuntimeResolver.ResolveExisting().NodeExe;
        if (node is null) { pid = 0; return null; }
        var script = Path.Combine(Path.GetTempPath(), "dsh-realos-" + Guid.NewGuid().ToString("N") + ".js");
        File.WriteAllText(script,
            "const http=require('http');const port=parseInt(process.argv[2],10);"
            + "const s=http.createServer((q,r)=>{r.end('ok');});"
            + "s.listen(port,'127.0.0.1',()=>{process.stdout.write('READY\\n');});"
            + "setInterval(()=>{},1<<30);");
        var psi = new ProcessStartInfo(node, "\"" + script + "\" " + port)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var proc = Process.Start(psi)!;
        var got = false;
        proc.OutputDataReceived += (_, e) => { if (e.Data != null && e.Data.Contains("READY")) got = true; };
        proc.BeginOutputReadLine();
        for (int i = 0; i < 100 && !got && !proc.HasExited; i++) Thread.Sleep(50);
        if (!got && !proc.HasExited)
        {
            try { proc.Kill(true); } catch { }
            pid = 0; return null;
        }
        pid = proc.Id;
        return proc;
    }

    [Fact]
    public void RealOs_KillServiceProcess_TerminatesLiveListener_AndFreesPort()
    {
        if (RuntimeResolver.ResolveExisting().NodeExe is null) return; // 无 node 环境跳过
        int port = FreePort();
        using var proc = StartNodeServer(port, out var pid);
        if (proc is null) return;
        try
        {
            // 端口归属校验：GetProcessIdByPort 必须指向我们拉起的 node
            var owner = ShellLogic.ProcessManagement.GetProcessIdByPort(port);
            Assert.Equal(pid, owner);

            // 修复点1+2：KillServiceProcess 可靠终止真实监听进程并释放端口（启动清扫闭环的构件）
            bool killed = ShellLogic.ProcessManagement.KillServiceProcess(pid, port);
            _out.WriteLine($"KillServiceProcess(pid={pid}, port={port}) => {killed}");
            Assert.True(killed, "real node listener should be terminated");
            Assert.False(ShellLogic.ProcessManagement.IsLikelyDshService(pid), "process must be gone after kill");
            Assert.Equal(0, ShellLogic.ProcessManagement.GetProcessIdByPort(port));
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
        }
    }

    [Fact]
    public void RealOs_KillServiceProcess_RefusesWrongPort_NoMisKill()
    {
        if (RuntimeResolver.ResolveExisting().NodeExe is null) return;
        int port = FreePort();
        using var proc = StartNodeServer(port, out var pid);
        if (proc is null) return;
        try
        {
            // 修复点1 防误杀：真实 pid 但传入"未归属该进程的端口" → 拒绝，绝不误杀无辜进程
            bool killed = ShellLogic.ProcessManagement.KillServiceProcess(pid, port + 1);
            _out.WriteLine($"KillServiceProcess(pid={pid}, wrongPort={port + 1}) => {killed}");
            Assert.False(killed, "must refuse kill when port is not owned by pid");
            Assert.True(ShellLogic.ProcessManagement.IsLikelyDshService(pid), "target must remain alive (not mis-killed)");
            Assert.Equal(pid, ShellLogic.ProcessManagement.GetProcessIdByPort(port));
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
        }
    }

    // ==================== 2026-08-25 事故回归：死 PID 与活非 node 的区分 ====================

    [Fact]
    public void RealOs_KillServiceProcess_DeadPid_ReportsSuccess_NothingToKill()
    {
        // 事故日志形态：服务已 exit=1，壳停止链再次停止同一 pid → GetProcessById 抛异常被
        // 归因为 "not a dsh service process"（Warn+false），pid 文件滞留、日志噪音误导排障。
        // 修复语义：pid 已不存在 = 目标已消失 → Info + true。
        var psi = new ProcessStartInfo("cmd.exe", "/c exit 0")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        Assert.True(p.WaitForExit(5000), "trivial cmd must exit immediately");
        int deadPid = p.Id;
        Assert.False(ShellLogic.ProcessManagement.IsProcessAlive(deadPid),
            "setup: pid must be gone (guards against PID reuse flakiness)");

        bool result = ShellLogic.ProcessManagement.KillServiceProcess(deadPid, FreePort());
        _out.WriteLine($"KillServiceProcess(deadPid={deadPid}) => {result}");
        Assert.True(result, "dead pid means nothing to kill; must not be reported as refusal");
    }

    [Fact]
    public void RealOs_KillServiceProcess_LiveNonNodeProcess_StillRefused()
    {
        // 修复不得削弱防误杀：活着但不是 node 的进程必须照旧拒绝
        var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 30 127.0.0.1 -w 500")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)!;
        try
        {
            Assert.True(ShellLogic.ProcessManagement.IsLikelyDshService(p.Id) == false,
                "setup: cmd.exe is not a node service");
            bool result = ShellLogic.ProcessManagement.KillServiceProcess(p.Id, 0);
            Assert.False(result, "live non-node process must be refused (no mis-kill)");
            Assert.False(p.HasExited, "refused target must remain alive");
        }
        finally
        {
            try { if (!p.HasExited) p.Kill(true); } catch { }
        }
    }
}
