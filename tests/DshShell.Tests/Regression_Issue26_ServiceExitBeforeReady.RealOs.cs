using System.Threading;
using DshWeb;
using DshWeb.Domain;
using DshWeb.Managers;
using Xunit;
using Xunit.Abstractions;

namespace DshShell.Tests;

/// <summary>
/// 【Regression_Issue26_ServiceExitedBeforeReady】服务就绪前进程已退出的快速失败回归测试（零 Mock，RealOS）。
///
/// 事故形态（issue #26 用户桉例）：壳拉起 dsh 服务后一直卡在"正在等待 dsh 服务就绪…"，
/// 最终弹出误导性的 E2002"启动超时：可能是首次下载 dsh 组件较慢/网络问题"。根因：
/// PollReadiness 只观测 TCP/HTTP 与启动错误标志——拉起的服务进程**就绪前退出**且退出输出
/// 不含启动错误标志（如 EADDRINUSE / 引擎内部 TypeError 等非词表崩溃）时，日志判定盲，
/// 只能盲等完整轮询预算（180s），完全无视进程对象早已退出的事实（统一日志里
/// "service process exited (code=N)" 就躺在那里）。
///
/// 本测试真实拉起 node 子进程（经 ServiceManager.Start 全链路：psi 装配 → 管道挂接 →
/// 进程对象追踪），子进程打印不含启动错误标志的报错并退出非零；随后以**生产默认退出探针**
/// （静态追踪器）调用 PollReadiness（真实 TCP/HTTP 探针 + 虚拟延迟），断言：
/// 1. 返回 "service-exited"（新增第五态）而非 "timeout"——修复前该测试必红（返回 timeout）；
/// 2. 跟踪到的退出码与子进程一致（7）；
/// 3. 快速失败：进程退出观测命中后首个轮询即收敛（无需完整预算）。
/// </summary>
[Collection("RealOS")]
[Trait("Category", "RealOS")]
public class Regression_Issue26_ServiceExitBeforeReady_RealOs
{
    private readonly ITestOutputHelper _out;
    public Regression_Issue26_ServiceExitBeforeReady_RealOs(ITestOutputHelper o) => _out = o;

    [Fact]
    public void RealOs_ServiceExitedBeforeReady_PollFailsFastWithServiceExited()
    {
        var nodeExe = RuntimeResolver.ResolveExisting().NodeExe;
        if (nodeExe is null) return; // 无 node 环境跳过（CI Real-OS Stage 会安装 node）

        var work = Path.Combine(Path.GetTempPath(), "dsh-exit-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var entryJs = Path.Combine(work, "fake-dsh-crashed-entry.js");
        var logPath = Path.Combine(work, "dsh.log");
        // 与线上崩溃同构：输出**不含任何 StartupErrorMarker**（无 Cannot find module / npm ERR /
        // ECONN* / EACCES …），日志判定对它是盲的——修复前只能盲等预算直至 timeout。
        File.WriteAllText(entryJs,
            "console.error('Error: something went boom in the harness boot chain');\n" +
            "console.error('    at boot (file:///C:/fake/dsh-app-boot/lib/index.js:1511:9)');\n" +
            "process.exit(7);\n");

        var identity = new DshRuntimeIdentity(
            DshSource.GlobalNpm, nodeExe, entryJs, Version: "0.0.0-exit-test", ProfilePath: null);
        var port = FreePort();

        try
        {
            var started = new ServiceManager().Start(identity, port, logPath);
            Assert.True(started, "ServiceManager.Start must report success for a valid identity");

            // 进程秒退：等静态追踪器观测到退出（Exited 事件异步触发；生产较慢时 PollReadiness
            // 自身会在下一轮迭代收敛，这里轮询等待后调用以保证判定确定性）。
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline
                   && ServiceManager.TrackedServiceExitCodeOrMinusOne() < 0)
                Thread.Sleep(20);
            var observedCode = ServiceManager.TrackedServiceExitCodeOrMinusOne();
            Assert.Equal(7, observedCode);

            // 生产路径：默认退出探针（静态追踪器） + 真实 TCP/HTTP 探针 + 虚拟延迟（测试提速）。
            var result = new ServiceManager().PollReadiness(
                CancellationToken.None, port, $"http://127.0.0.1:{port}", logPath,
                e2eMode: true, delay: _ => { });

            _out.WriteLine($"---- poll verdict: {result} ----");
            _out.WriteLine(File.Exists(logPath) ? File.ReadAllText(logPath) : "(no unified log)");

            Assert.Equal(ShellLogic.ServiceReadiness.ServiceExitedVerdict, result);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp 清理失败可忽略 */ }
        }
    }

    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}