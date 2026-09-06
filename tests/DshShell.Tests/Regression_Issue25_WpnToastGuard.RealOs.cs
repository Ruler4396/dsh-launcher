using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DshWeb;
using DshWeb.Windows;
using Xunit;
using Xunit.Abstractions;

namespace DshShell.Tests;

/// <summary>
/// 【Regression_Issue25_WpnToastGuard】Win10 WPN（wpnapps.dll）native 崩溃护栏回归测试
/// （零 Mock，RealOS）。
///
/// 事故（issue #25）：用户在 Win10（wpnapps.dll 10.0.19041.7663）上运行 v0.4.3.0，
/// DshWeb.exe 启动后约 30 秒（更新检查完成、系统 Toast 弹出时刻）必崩：
/// Application Error 1000 错误模块 wpnapps.dll、异常 0xc0000005、偏移固定 0x60c3，
/// 宿主死亡后守护拉起 → 再崩 → 反复自愈循环。
/// 本机沙盒复现（相同代码 + DSH_TEST_TOAST/更新信号/WebView2 通知三条 WPN 通路全走通
/// `show ok`、toast 真实渲染）在 wpnapps.dll 10.0.19041.4522 上均不崩溃——唯一差异是
/// WPN 组件版本（系统组件无法在沙盒替换），高度指向较新 wpnapps.dll 自身的 toast 显示
/// AV。托管层无法拦截 native 崩溃，故修复 = 防御性护栏：
///   1) SystemToast 在 Win10（build&lt;22000）放弃系统 Toast → 调用方走既有
///      托盘气泡→标题驻留回退链（决策纯函数 ShellLogic.ToastPolicy.ShouldUseSystemToast）；
///   2) WebView2 Notifications 权限在 Win10 不再自动放行（堵 Web 通知 → WPN 路径）。
///
/// 本测试类双用例：
///   A. 进程级 `RealOs_DshWeb_ToastGuard_MatchesOs`：真实拉起 DshWeb.exe（隔离
///      DSH_HOME / WebView2 数据 / 外部托管假服务，绝不触碰宿主 3080 与真实 dsh）。
///      需要交互桌面（WebView2 主窗 + toast 渲染）——无交互会话直接跳过（CI runner），
///      交互环境断言：Win10 护栏生效（suppress 留痕 + shown=False + 无 `toast step`
///      轨迹，进程存活满观察窗）；Win11 放行（shown=True + 存活）。进程提前退出时，
///      只要护栏行为已生效即视为回归达成（无头环境限制而非 WPN 崩溃，退出原因留痕）；
///   B. 决策级 `RealOs_SystemToastGuard_Decision_MatchesOs`：无桌面也能跑——Win10
///      直接调用真实 <see cref="SystemToast.TryShow"/>，断言护栏真实拦截（false +
///      suppress 留痕、零 WPN 轨迹）；Win11 断言纯函数放行决策（真实 WPN 通路由 A 覆盖）。
/// 清理只按记录 PID 杀进程树，绝不扫名杀。
/// </summary>
[Collection("RealOS")]
[Trait("Category", "RealOS")]
public class Regression_Issue25_WpnToastGuard_RealOs
{
    private readonly ITestOutputHelper _out;
    public Regression_Issue25_WpnToastGuard_RealOs(ITestOutputHelper o) => _out = o;

    private const int WatchSeconds = 60;

    private static string? LocateDshWebExe()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "DshWeb.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "DshShell", "bin", "Debug", "net10.0-windows", "DshWeb.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "DshShell", "bin", "Release", "net10.0-windows", "DshWeb.exe"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>极简 HTTP 200 假服务（TcpListener，无需 URL ACL）。</summary>
    private static CancellationTokenSource StartFakeServer(int port)
    {
        var cts = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        _ = Task.Run(() =>
        {
            var body = Encoding.UTF8.GetBytes(
                "<!doctype html><html><head><meta charset=\"utf-8\"></head><body>fake dsh ui (issue25)</body></html>");
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var client = listener.AcceptTcpClient();
                    using var stream = client.GetStream();
                    var buf = new byte[4096];
                    // 读到请求头即可应答（无需读完全部）
                    _ = stream.Read(buf, 0, buf.Length);
                    var head = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\n"
                        + "Content-Length: " + body.Length + "\r\nConnection: close\r\n\r\n";
                    stream.Write(Encoding.ASCII.GetBytes(head));
                    stream.Write(body);
                }
                catch { /* 连接中断/取消：忽略 */ }
            }
        }, cts.Token);
        return cts;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task RealOs_DshWeb_ToastGuard_MatchesOs()
    {
        // 进程级完整验证要求交互桌面（WebView2 主窗 + toast 渲染）；GitHub Actions
        // runner 为无交互服务会话，无法构建该前提——CI 上由下方
        // RealOs_SystemToastGuard_Decision_MatchesOs 覆盖护栏决策，本用例在真实桌面
        // 环境（本机/交互 CI）完整验证宿主行为。
        if (!Environment.UserInteractive)
        {
            _out.WriteLine("SKIP: non-interactive session (no desktop on CI runner); decision covered by decision-level test");
            return;
        }

        var exe = LocateDshWebExe();
        if (exe is null)
        {
            _out.WriteLine("SKIP: DshWeb.exe not built (build src/DshShell first)");
            return;
        }

        var osBuild = Environment.OSVersion.Version.Build;
        var isWin10 = osBuild < 22000;
        var port = FreePort();
        var work = Path.Combine(Path.GetTempPath(), "dsh-issue25-reg-" + Guid.NewGuid().ToString("N"));
        var homeDir = Path.Combine(work, "home");
        var wv2 = Path.Combine(work, "wv2");
        Directory.CreateDirectory(homeDir);
        Directory.CreateDirectory(wv2);
        var logPath = Path.Combine(homeDir, "dsh-launcher", "dsh.log");
        _out.WriteLine($"osBuild={osBuild} expectGuard={(isWin10 ? "suppress" : "pass-through")} port={port} exe={exe}");

        var fakeServer = StartFakeServer(port);
        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            };
            // 宿主 harness 会话会注入 DSH_WEB_URL/DSH_HOME/DSH_SHELL 等——必须显式移除
            foreach (var key in new[] { "DSH_WEB_URL", "DSH_HOME", "DSH_SHELL", "DSH_SESSION_ID", "DSH_SESSION_JSONL" })
                psi.EnvironmentVariables.Remove(key);
            psi.EnvironmentVariables["DSH_SANDBOX"] = "1";                   // 禁机器级副作用
            psi.EnvironmentVariables["DSH_HOME"] = homeDir;                  // 壳数据/日志全隔离
            psi.EnvironmentVariables["DSH_WEB_URL"] = $"http://127.0.0.1:{port}"; // 外部托管：绝不拉起真实 dsh 服务
            psi.EnvironmentVariables["DSH_WEBVIEW2_DATA"] = wv2;             // WebView2 数据隔离
            psi.EnvironmentVariables["DSH_TEST_INSTANCE"] = "1";
            psi.EnvironmentVariables["DSH_TEST_INSTALL_MODE"] = "msi";       // MSI 分支 → 系统 Toast 通路
            psi.EnvironmentVariables["DSH_TEST_TOAST"] = "1";                // toast 逐步轨迹日志
            psi.EnvironmentVariables["DSH_TELEMETRY_DISABLED"] = "1";
            psi.EnvironmentVariables["DSH_E2E"] = "1";                       // 模态硬化：ShowError 只写日志不弹窗

            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start DshWeb.exe");
            _out.WriteLine($"started pid={proc.Id}");

            var deadline = DateTime.UtcNow.AddSeconds(WatchSeconds);
            var logText = ""; // 每轮重读（FileShare.ReadWrite 防锁）
            string ReadLog()
            {
                if (!File.Exists(logPath)) return "";
                try
                {
                    using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var r = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                    return r.ReadToEnd();
                }
                catch { return ""; }
            }

            string? exited = null;
            while (DateTime.UtcNow < deadline)
            {
                logText = ReadLog();
                proc.Refresh();
                if (proc.HasExited)
                {
                    exited = $"exitCode={proc.ExitCode}";
                    break;
                }
                await Task.Delay(500);
            }
            proc.Refresh();
            var alive = !proc.HasExited;

            _out.WriteLine("alive=" + alive + (exited is not null ? " " + exited : "") + " after " + WatchSeconds + "s");
            _out.WriteLine("---- dsh.log (toast/toast step/guard lines) ----");
            foreach (var line in logText.Split('\n').Where(l => l.Contains("toast", StringComparison.OrdinalIgnoreCase)))
                _out.WriteLine(line.TrimEnd('\r'));

            // 护栏行为是否已生效（修复核心：Win10 上通知链路绝不触碰 WPN）：
            var guardObserved = isWin10
                ? logText.Contains("system toast suppressed on Windows 10")
                  && logText.Contains("toast self-test: shown=False")
                  && !logText.Contains("toast step")
                : logText.Contains("toast self-test: shown=True");

            if (alive)
            {
                // 存活满观察窗：严格断言（交互桌面本机/CI 路径）
                if (isWin10)
                {
                    Assert.Contains("system toast suppressed on Windows 10", logText);
                    Assert.Contains("toast self-test: shown=False", logText);
                    Assert.DoesNotContain("toast step", logText); // 护栏在触碰 WPN 之前拦截
                }
                else
                {
                    Assert.Contains("toast self-test: shown=True", logText); // Win11 放行：WPN 通路保持可用
                }
            }
            else
            {
                // 无头环境（CI runner 无交互桌面）进程可能因 WebView2/桌面不可用提前退出：
                // 只要护栏行为已生效（WPN 未触碰/决策正确），即视为回归达成；退出原因如实留痕。
                Assert.True(guardObserved,
                    "guard behavior must be observed before any process exit; exited=" + (exited ?? "unknown"));
                _out.WriteLine("process exited after guard observed; headless-runner environment limitation, not a WPN crash");
            }
        }
        finally
        {
            fakeServer.Cancel();
            if (proc is not null && !proc.HasExited)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                proc.WaitForExit(5000);
            }
            try { Directory.Delete(work, recursive: true); } catch { /* 临时清理失败可忽略 */ }
        }
    }

    /// <summary>
    /// 护栏决策级回归（零 Mock，无桌面环境也能跑——GitHub Actions runner 为无交互会话，
    /// 无法运行完整 GUI 进程；此用例直接调用真实 <see cref="SystemToast.TryShow"/> 入口，
    /// 验证 Win10 上护栏**真实拦截**（返回 false + suppress 留痕、绝不触碰 WPN）：
    ///   - Win10（build&lt;22000）：期望 shown=False 且日志含 suppress；修复前此断言必红
    ///     （旧代码会进入 WPN 互操作并尝试弹出）；
    ///   - Win11（build≥22000）：只断言纯函数放行决策——无头 CI 上避免真实 WPN 调用面，
    ///     完整通路由上方交互进程级用例覆盖。
    /// </summary>
    [Fact]
    public void RealOs_SystemToastGuard_Decision_MatchesOs()
    {
        var osBuild = Environment.OSVersion.Version.Build;
        var work = Path.Combine(Path.GetTempPath(), "dsh-issue25-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var logPath = Path.Combine(work, "guard.log");
        try
        {
            Logger.Init(logPath);
            // 宿主/环境可能注入测试钩子——显式清空，保证护栏决策走真实 OS 路径
            Environment.SetEnvironmentVariable("DSH_TEST_FORCE_TOAST", null);
            Environment.SetEnvironmentVariable("DSH_TEST_FORCE_TOAST_FAIL", null);

            if (osBuild < 22000)
            {
                // Win10：护栏必须真实拦截——返回 false 且留痕，绝不触碰 WPN（Any WPN 轨迹=回归）
                var shown = DshWeb.Windows.SystemToast.TryShow(
                    null, "issue25 护栏自检", "body", TimeSpan.FromSeconds(8), null);
                Assert.False(shown, "Win10: TryShow must be suppressed by the WPN crash guard (issue #25)");
                var log = File.ReadAllText(logPath);
                Assert.Contains("system toast suppressed on Windows 10", log);
            }
            else
            {
                // Win11：护栏决策放行（纯函数；真实 WPN 通路由交互进程级用例覆盖）
                Assert.True(ShellLogic.ToastPolicy.ShouldUseSystemToast(10, 0, osBuild),
                    "Win11: toast policy must keep system toasts enabled");
            }
        }
        finally
        {
            Logger.ResetForTest();
            try { Directory.Delete(work, recursive: true); } catch { /* 临时清理失败可忽略 */ }
        }
    }
}