using System.Diagnostics;
using DshWeb;
using DshWeb.Lifecycle;
using DshWeb.Managers;
using Xunit;
using System.Globalization;

namespace DshShell.Tests.Lifecycle;

/// <summary>
/// 【Regression_Issue28_RestartPidLedgerRefresh】DSH 内置重启后插件消失 —— 零 Mock 真实 OS 复现。
///
/// 事故链（issue #28 报告人复测第 5 条"重大异常"）：
///   装完新插件 → 点 DSH 页面自带的"重启" → 重启后插件凭空消失。
///
/// 三个物理环节全部用真实 node 子进程 + 真实端口 + 真实账本文件复现（测试铁律：
/// 进程/文件相关绝不 Mock；P0 环境 Bug 修复必须配零 Mock 复现测试）：
///   ① 账本必须指向**当前真正在监听的那个进程**（修复前 RestartDshServiceCoreAsync 从不
///      RecordServicePid → 账本与 _servicePid 停留在已死的旧 pid）；
///   ② 被停进程自我重新拉起（DSH 内置重启的实现方式）时，端口上的新进程必须被**接管**而不是
///      被 taskkill /T /F 整树强杀（强杀会打断它正在跑的 npm/pnpm 插件安装）；
///   ③ attach 错 pid = 运行期自愈静默失效：下一次服务退出不再触发
///      <see cref="BootHealthMonitor.ServiceExitedWhileRunning"/>，而会被判成启动自检失败
///      → 连续失败计数推进 → 询问进安全模式 → .dsh-safe 剥掉第三方插件。
/// </summary>
[Collection("RealOS")]
[Trait("Category", "RealOS")]
public class Regression_Issue28_RestartPidLedgerRefresh_RealOs : IDisposable
{
    private readonly string _work = Path.Combine(
        Path.GetTempPath(), "dsh-issue28-ledger-" + Guid.NewGuid().ToString("N"));
    private readonly List<Process> _spawned = new();

    public Regression_Issue28_RestartPidLedgerRefresh_RealOs() => Directory.CreateDirectory(_work);

    private static string NodeExeOrSkip()
    {
        var node = RuntimeResolver.ResolveExisting().NodeExe;
        Assert.True(node is not null, "Real-OS 用例需要本机 Node.js（CI Real-OS Stage 会真实安装）");
        return node!;
    }

    private Process StartNode(string nodeExe, string args)
    {
        var psi = new ProcessStartInfo(nodeExe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var p = Process.Start(psi) ?? throw new InvalidOperationException("failed to spawn real node process");
        _spawned.Add(p);
        return p;
    }

    private string WriteScript(string name, string body)
    {
        var path = Path.Combine(_work, name);
        File.WriteAllText(path, body);
        return path;
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>真 node HTTP 服务 + 它自己拉起并仍在运行的"装插件"子进程（孙进程）。</summary>
    private const string RespondingServiceWithInstallChild = """
        const http = require('http');
        const { spawn } = require('child_process');
        const fs = require('fs');
        // 模拟 dsh plugin add：服务进程自己拉起的安装子进程（taskkill /T 会连它一起杀）
        const child = spawn(process.execPath, ['-e', 'setTimeout(()=>process.exit(0),120000)'],
            { stdio: 'ignore', detached: false });
        fs.writeFileSync(process.argv[2], String(child.pid));
        http.createServer((req, res) => { res.writeHead(200); res.end('ok'); })
            .listen(Number(process.argv[3]), '127.0.0.1');
        setTimeout(() => process.exit(0), 120000);
        """;

    /// <summary>只开 TCP 端口、永不应答 HTTP 的占用者（真僵尸形态，必须被杀）。</summary>
    private const string ListeningButNeverResponding = """
        const net = require('net');
        const { spawn } = require('child_process');
        const fs = require('fs');
        // 模拟 dsh plugin add：服务进程自己拉起的安装子进程
        const child = spawn(process.execPath, ['-e', 'setTimeout(()=>process.exit(0),120000)'],
            { stdio: 'ignore', detached: false });
        fs.writeFileSync(process.argv[2], String(child.pid));
        net.createServer(() => { /* accept 但永不响应：HTTP 探针超时 → 不就绪 */ }).listen(
            Number(process.argv[3]), '127.0.0.1');
        setTimeout(() => process.exit(0), 120000);
        """;

    private (int ServicePid, int ChildPid, int Port) StartServiceScript(string scriptName, string body)
    {
        var node = NodeExeOrSkip();
        var script = WriteScript(scriptName, body);
        var childPidFile = Path.Combine(_work, scriptName + ".childpid");
        var port = FreePort();
        var proc = StartNode(node, $"\"{script}\" \"{childPidFile}\" {port}");

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline
               && (!File.Exists(childPidFile) || !ShellLogic.ServiceReadiness.PortOpen("127.0.0.1", port)))
            System.Threading.Thread.Sleep(50);
        Assert.True(File.Exists(childPidFile), $"{scriptName}: 子进程 pid 未落盘（node 未起来？）");
        Assert.True(ShellLogic.ServiceReadiness.PortOpen("127.0.0.1", port), $"{scriptName}: 端口未监听");
        var childPid = int.Parse(File.ReadAllText(childPidFile).Trim(), CultureInfo.InvariantCulture);
        return (proc.Id, childPid, port);
    }

    private static void WaitForPortFree(int port, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && ShellLogic.ServiceReadiness.PortOpen("127.0.0.1", port))
            System.Threading.Thread.Sleep(100);
    }

    // ==================== ① 账本真实性：内容 == 端口真实归属 ====================

    [Fact]
    public void RealOs_RecordServicePid_LedgerEqualsRealPortOwner()
    {
        var (servicePid, _, port) = StartServiceScript("svc.js", RespondingServiceWithInstallChild);
        var dataDir = Path.Combine(_work, "data-1");
        Directory.CreateDirectory(dataDir);

        ServiceLifecycleOps.RecordServicePid(dataDir, port);

        var ledger = int.Parse(File.ReadAllText(ServiceLifecycleOps.PidFilePath(dataDir, port)).Trim(), CultureInfo.InvariantCulture);
        Assert.Equal(ShellLogic.ProcessManagement.GetProcessIdByPort(port), ledger);
        Assert.Equal(servicePid, ledger);
    }

    // ==================== ② 自我重新拉起的新进程：接管而非强杀 ====================

    [Fact]
    public void RealOs_StopService_SelfRespawnedRespondingService_IsAdopted_AndInstallChildSurvives()
    {
        var (servicePid, installChildPid, port) =
            StartServiceScript("respawn.js", RespondingServiceWithInstallChild);
        var dataDir = Path.Combine(_work, "data-2");
        Directory.CreateDirectory(dataDir);
        var deadPid = StartDeadRememberedPid();

        var result = ServiceLifecycleOps.StopService(
            dataDir, port, $"http://127.0.0.1:{port}", deadPid, allowReplacementAdoption: true);

        Assert.Equal(servicePid, result.AdoptedReplacementPid);
        Assert.True(ServiceLifecycleOps.IsProcessAlive(servicePid), "接管路径绝不得杀掉自我重新拉起的服务");
        Assert.True(ServiceLifecycleOps.IsProcessAlive(installChildPid),
            "issue #28-4：接管后插件安装子进程必须存活（旧实现 taskkill /T /F 把它拦腰打断）");
        var ledger = int.Parse(File.ReadAllText(ServiceLifecycleOps.PidFilePath(dataDir, port)).Trim(), CultureInfo.InvariantCulture);
        Assert.Equal(servicePid, ledger); // 账本随之改指新进程

        // 收尾：真的停掉它，验证端口释放
        ShellLogic.ProcessManagement.KillServiceProcess(servicePid, port);
        WaitForPortFree(port);
    }

    [Fact]
    public void RealOs_StopService_WithoutAdoptionPermission_StillKillsWholeTree()
    {
        // 负对照（关窗/退出语义）：不允许接管时，整棵进程树必须照旧被清干净——
        // 本修复不得让 node 残留成为新默认。
        var (servicePid, installChildPid, port) =
            StartServiceScript("shutdown.js", RespondingServiceWithInstallChild);
        var dataDir = Path.Combine(_work, "data-3");
        Directory.CreateDirectory(dataDir);
        var deadPid = StartDeadRememberedPid();

        var result = ServiceLifecycleOps.StopService(
            dataDir, port, $"http://127.0.0.1:{port}", deadPid, allowReplacementAdoption: false);

        Assert.Equal(0, result.AdoptedReplacementPid);
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && ServiceLifecycleOps.IsProcessAlive(installChildPid))
            System.Threading.Thread.Sleep(100);
        Assert.False(ServiceLifecycleOps.IsProcessAlive(servicePid), "服务进程必须被停掉");
        Assert.False(ServiceLifecycleOps.IsProcessAlive(installChildPid), "整树回收能力不得退化");
        WaitForPortFree(port);
    }

    [Fact]
    public void RealOs_StopService_OccupantNeverAnswersHttp_IsKilledAfterGrace()
    {
        // 监听但永不应答 = 真僵尸：即便允许接管也必须被杀（不能把界面交给半死服务）。
        var (servicePid, installChildPid, port) =
            StartServiceScript("zombie.js", ListeningButNeverResponding);
        var dataDir = Path.Combine(_work, "data-4");
        Directory.CreateDirectory(dataDir);
        var deadPid = StartDeadRememberedPid();

        var result = ServiceLifecycleOps.StopService(
            dataDir, port, $"http://127.0.0.1:{port}", deadPid, allowReplacementAdoption: true);

        Assert.Equal(0, result.AdoptedReplacementPid);
        var deadline = DateTime.UtcNow.AddSeconds(30); // 含 8s 宽限轮询 + taskkill 收敛
        while (DateTime.UtcNow < deadline && ServiceLifecycleOps.IsProcessAlive(servicePid))
            System.Threading.Thread.Sleep(100);
        Assert.False(ServiceLifecycleOps.IsProcessAlive(servicePid));
        Assert.False(ServiceLifecycleOps.IsProcessAlive(installChildPid));
        WaitForPortFree(port);
    }

    // ==================== ③ attach 错 pid = 自愈静默失效（真实进程句柄，零 Fake） ====================

    [Fact]
    public async Task RealOs_AttachToLivePid_RaisesRuntimeExitEventExactlyOnce()
    {
        var node = NodeExeOrSkip();
        var sleeper = StartNode(node, "-e \"setTimeout(()=>process.exit(0),120000)\"");
        using var m = NewMonitor();

        var exits = new List<int?>();
        m.ServiceExitedWhileRunning += code => { lock (exits) exits.Add(code); };

        m.AttachProcess(sleeper.Id);           // 生产修复后：账本刷新 → attach 到活的新 pid
        await WaitHealthyAsync(m);

        sleeper.Kill();
        Assert.True(await WaitForAsync(() => { lock (exits) return exits.Count > 0; }, 15000),
            "attach 到真实存活 pid 后，Healthy 状态下的退出必须触发运行期自愈事件");
        await System.Threading.Tasks.Task.Delay(400);
        lock (exits) Assert.Single(exits);
    }

    [Fact]
    public async Task RealOs_AttachToDeadPid_ServiceExitIsNeverObserved_PreFixBlindness()
    {
        // 修复前的真实形态：ResumeAfterRestart(旧 pid) attach 到已死进程 → 服务再退出时
        // 进程层完全失明（本用例即"为什么必须 RecordServicePid"的物理证据）。
        var node = NodeExeOrSkip();
        var deadPid = StartDeadRememberedPid();
        var service = StartNode(node, "-e \"setTimeout(()=>process.exit(0),120000)\"");
        using var m = NewMonitor();

        var fired = false;
        m.ServiceExitedWhileRunning += _ => fired = true;
        var failed = false;
        m.Failed += _ => failed = true;

        m.AttachProcess(deadPid);               // attach 错进程
        await WaitHealthyAsync(m);

        service.Kill();
        service.WaitForExit(10000);
        await System.Threading.Tasks.Task.Delay(1500);

        Assert.False(fired,
            "attach 到死 pid 时运行期退出无法被进程层观测——这正是修复前账本不刷新的后果（回归护栏）");
        Assert.False(failed, "attach 失败只是监控接线失败，不得被误判成启动自检失败（既有 E2007 误报根治语义）");
    }

    // ==================== 辅助 ====================

    /// <summary>启动一个立刻退出的 node，返回其**已死** pid = 账本里残留的旧服务 PID（线上真实形态）。</summary>
    private int StartDeadRememberedPid()
    {
        var node = NodeExeOrSkip();
        var p = StartNode(node, "-e \"process.exit(0)\"");
        p.WaitForExit(10000);
        Assert.True(p.HasExited);
        return p.Id;
    }

    private static BootHealthMonitor NewMonitor() => new(
        new ShellLogic.BootGuard.BootProfile
        {
            GraceMs = 60,            // 与 Headless 同款快节奏：探针窗口不拖慢真机用例
            ProbeIntervalMs = 30,
            AbsentThreshold = 3,
            BadSignatures = new[] { "fake-bad-marker" },
        },
        logPath: null,
        httpUrl: "http://127.0.0.1:1",
        pageProbe: _ => System.Threading.Tasks.Task.FromResult<string?>(
            "{\"good\":true,\"text\":\"__DSH_BOOT__ 0.1.5-rc.1\",\"err\":\"\"}"),
        processHandleFactory: null,     // null = 生产默认 RealProcessHandle（零 Mock 关键）
        httpProbe: _ => true,
        logPollInterval: TimeSpan.FromMilliseconds(50),
        httpPollInterval: TimeSpan.FromMilliseconds(50));

    private static async Task WaitHealthyAsync(BootHealthMonitor m, int timeoutMs = 20000)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        m.HealthyDetected += () => tcs.TrySetResult(true);
        m.OnNavigationCompleted();
        Assert.True(await System.Threading.Tasks.Task.WhenAny(tcs.Task,
            System.Threading.Tasks.Task.Delay(timeoutMs)) == tcs.Task,
            $"monitor did not become healthy within {timeoutMs}ms");
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await System.Threading.Tasks.Task.Delay(100);
        }
        return false;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this); // CA1816: Dispose 模式要求，勿跳过派生类终结器
        foreach (var p in _spawned)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            p.Dispose();
        }
        try { Directory.Delete(_work, recursive: true); } catch { /* 临时目录回收失败不影响结论 */ }
    }
}
