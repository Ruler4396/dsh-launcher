using System.Net;
using System.Net.Sockets;
using DshWeb;
using DshWeb.Domain;
using DshWeb.Managers;
using Xunit;
using Xunit.Abstractions;

namespace DshShell.Tests;

/// <summary>
/// 【Regression_20260829_WebTokenAuthFence】dsh ≥0.1.2 根路径 token 信任栅栏回归测试
/// （零 Mock，RealOS）。
///
/// 事故根因：dsh 0.1.2-alpha.1 的 web-startup 给根路径加了 token 鉴权（实测对照：
/// 0.1.1-rc.2 `GET /` = 200，0.1.2-alpha.1 `GET /` = 401，启动横幅从
/// `dsh web: http://127.0.0.1:P` 变为 `dsh web: http://127.0.0.1:P/?token=...`）。
/// 壳此前不解析该横幅 → WebView 导航裸 URL 停在 401 页（E2004），页面探针永久挂死，
/// 一切自愈路径失效。
///
/// 本测试真实拉起 node 子进程（经 ServiceManager.Start 全链路：psi 装配 → 管道挂接 →
/// 解析 → 静态事件），断言：
/// 1. 0.1.2 形态横幅 → <see cref="ServiceManager.ServiceTokenUrlObserved"/> 携带精确 URL 触发；
/// 2. 该行照常以 "[dsh] " 前缀落统一日志（管道语义不回退）；
/// 3. 0.1.1 形态（无 token）与端口不匹配行绝不触发（防误导航/劫持）。
/// 修复前该测试必红（事件不存在/不触发）；修复后秒绿。
/// </summary>
[Collection("RealOS")]
[Trait("Category", "RealOS")]
public class Regression_WebTokenAuthFence_RealOs
{
    private readonly ITestOutputHelper _out;
    public Regression_WebTokenAuthFence_RealOs(ITestOutputHelper o) => _out = o;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    private static (DshRuntimeIdentity Identity, string Work, string LogPath) MakeIdentity()
    {
        var nodeExe = RuntimeResolver.ResolveExisting().NodeExe;
        if (nodeExe is null) return (null!, "", ""); // 无 node 环境跳过（CI Real-OS Stage 会安装 node）
        var work = Path.Combine(Path.GetTempPath(), "dsh-token-reg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var entryJs = Path.Combine(work, "fake-dsh-entry.js");
        var logPath = Path.Combine(work, "dsh.log");
        File.WriteAllText(entryJs, string.Empty); // 横幅行由各用例自写
        return (new DshRuntimeIdentity(
            DshSource.GlobalNpm, nodeExe, entryJs, Version: "0.0.0-token-test", ProfilePath: null),
            work, logPath);
    }

    [Fact]
    public async Task RealOs_V012TokenBanner_FiresStaticEventWithExactUrl()
    {
        var (identity, work, logPath) = MakeIdentity();
        if (identity is null) return;
        var port = FreePort();
        File.WriteAllText(identity.DshEntryJsPath!,
            $"console.log('dsh web: http://127.0.0.1:{port}/?token=T0kEn123');\n" +
            "setTimeout(() => process.exit(0), 1500);\n");

        var observed = new List<string>();
        void Handler(string url) => observed.Add(url);
        ServiceManager.ServiceTokenUrlObserved += Handler;
        try
        {
            Assert.True(new ServiceManager().Start(identity, port, logPath),
                "ServiceManager.Start must report success for a valid identity");

            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline && observed.Count == 0)
                await Task.Delay(100);

            _out.WriteLine("observed urls: " + string.Join(" | ", observed));
            _out.WriteLine("---- unified log ----");
            _out.WriteLine(File.Exists(logPath) ? File.ReadAllText(logPath) : "(missing)");

            Assert.True(observed.Count > 0, "token banner must raise ServiceTokenUrlObserved");
            Assert.Contains($"http://127.0.0.1:{port}/?token=T0kEn123", observed);
            var log = File.Exists(logPath) ? File.ReadAllText(logPath) : "";
            Assert.True(log.Contains("[dsh] dsh web: http://127.0.0.1:" + port + "/?token=T0kEn123"),
                "banner line must still reach unified log with [dsh] prefix");
        }
        finally
        {
            ServiceManager.ServiceTokenUrlObserved -= Handler;
            try { Directory.Delete(work, recursive: true); } catch { /* temp 清理失败可忽略 */ }
        }
    }

    [Fact]
    public async Task RealOs_V011BannerAndPortMismatch_NeverFire()
    {
        var (identity, work, logPath) = MakeIdentity();
        if (identity is null) return;
        var port = FreePort();
        // 第一行：0.1.1 形态（无 token）；第二行：token 形态但端口不匹配（伪造/陈旧行）
        File.WriteAllText(identity.DshEntryJsPath!,
            "console.log('dsh web: http://127.0.0.1:" + port + "');\n" +
            "console.log('dsh web: http://127.0.0.1:39999/?token=forged');\n" +
            "setTimeout(() => process.exit(0), 1500);\n");

        var observed = new List<string>();
        void Handler(string url) => observed.Add(url);
        ServiceManager.ServiceTokenUrlObserved += Handler;
        try
        {
            Assert.True(new ServiceManager().Start(identity, port, logPath));
            await Task.Delay(4000); // 给足两行输出与管道排空时间

            _out.WriteLine("observed urls: " + string.Join(" | ", observed));
            Assert.Empty(observed);
        }
        finally
        {
            ServiceManager.ServiceTokenUrlObserved -= Handler;
            try { Directory.Delete(work, recursive: true); } catch { /* temp 清理失败可忽略 */ }
        }
    }
}
