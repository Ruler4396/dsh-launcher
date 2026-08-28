using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using DshWeb;
using DshWeb.Domain;
using DshWeb.Managers;
using Xunit;
using Xunit.Abstractions;

namespace DshShell.Tests;

/// <summary>
/// 【Regression_20260825_ServiceOutputBlindSpot】服务输出管道失明回归测试（零 Mock，RealOS）。
///
/// 事故根因（P0）：ServiceManager.Start 曾用 `using var p` 持有进程对象——Start 返回即
/// Dispose，stdout/stderr 异步排空随句柄释放失效，服务输出从此从未落统一日志：
/// 日志层签名表全程收不到原料，插件崩溃堆栈丢失，安全模式归因链断裂。
///
/// 本测试真实拉起 node 子进程（经 ServiceManager.Start 全链路：psi 装配 → 管道挂接 →
/// 进程对象追踪），断言子进程 stdout/stderr 都以 "[dsh] " 前缀落到统一日志文件。
/// 修复前该测试必红（日志文件永不出现 [dsh] 行）；修复后秒绿。
/// </summary>
[Collection("RealOS")]
[Trait("Category", "RealOS")]
public class Regression_ServiceOutputPipe_RealOs
{
    private readonly ITestOutputHelper _out;
    public Regression_ServiceOutputPipe_RealOs(ITestOutputHelper o) => _out = o;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public void RealOs_ServiceStdoutAndStderr_ArePipedToUnifiedLog()
    {
        var nodeExe = RuntimeResolver.ResolveExisting().NodeExe;
        if (nodeExe is null) return; // 无 node 环境跳过（CI Real-OS Stage 会安装 node）

        var work = Path.Combine(Path.GetTempPath(), "dsh-pipe-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var entryJs = Path.Combine(work, "fake-dsh-entry.js");
        var logPath = Path.Combine(work, "dsh.log");
        File.WriteAllText(entryJs,
            "console.log('DSH-PIPE-MARKER-STDOUT');\n" +
            "console.error('DSH-PIPE-MARKER-STDERR');\n" +
            "process.exit(1);\n");

        // 与生产同构的身份要件：node.exe × JS 入口直启（BuildArgs 拼参，ADR-024 唯一入口）。
        // 入口脚本忽略 web/--port 等多余参数，仅验证管道语义本身。
        var identity = new DshRuntimeIdentity(
            DshSource.GlobalNpm, nodeExe, entryJs, Version: "0.0.0-pipe-test", ProfilePath: null);
        var port = FreePort();

        bool started;
        try
        {
            started = new ServiceManager().Start(identity, port, logPath);
            Assert.True(started, "ServiceManager.Start must report success for a valid identity");

            // 排空是异步事件流：轮询等待两路标记落盘（修复前永不出现）
            var deadline = DateTime.UtcNow.AddSeconds(15);
            string content = string.Empty;
            while (DateTime.UtcNow < deadline)
            {
                if (File.Exists(logPath))
                    content = File.ReadAllText(logPath); // 共享读语义由 ReadAllText 短开短关近似
                if (content.Contains("DSH-PIPE-MARKER-STDOUT") && content.Contains("DSH-PIPE-MARKER-STDERR"))
                    break;
                Thread.Sleep(100);
            }

            _out.WriteLine("---- unified log ----");
            _out.WriteLine(content);

            Assert.Multiple(
                () => Assert.True(content.Contains("[dsh] DSH-PIPE-MARKER-STDOUT"),
                    "service stdout must reach unified log with [dsh] prefix (pipe must survive Start returning)"),
                () => Assert.True(content.Contains("[dsh] DSH-PIPE-MARKER-STDERR"),
                    "service stderr (crash stack source) must reach unified log with [dsh] prefix"));
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch { /* temp 清理失败可忽略 */ }
        }
    }
}
