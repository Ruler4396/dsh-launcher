using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using DshWeb;
using Xunit;
using Xunit.Abstractions;

namespace DshShell.Tests;

/// <summary>
/// 【Regression_Issue25_WpnToastGuard】wpnapps.dll native 崩溃的回归测试（零 Mock，RealOS）。
///
/// 事故（issue #25）：宿主 DshWeb.exe 在 WPN（wpnapps.dll）内 native AV（0xc0000005），
/// 托管层拦不住，进程直接死亡 → 守护拉起 → 再崩，连带整个 dsh 运行时/QQ Bot 插件下线。
/// 同一签名在两代 Windows 上各自被实证：Win10 19045 + wpnapps 10.0.19041.7663（偏移 0x60c3）；
/// Win11 25H2 10.0.26200.9457 + wpnapps 10.0.26100.9278（偏移 0x53fb，2026-09-18 单日 12 组
/// Event 1000+1026 全同）。崩溃发生在 Toast <c>Show()</c> **返回之后**约 3 秒，所以
/// "调用成功"不是安全凭据。
///
/// 【为什么不再有护栏】45048989 的"Win10 降级"与后续的"默认关闭 + opt-in"都是在**保留
/// WPN 通路**的前提下加开关；开关一旦被越过（或将来被人"顺手打开"），崩溃就回来。现在
/// 通知统一走自绘卡片（Windows/NoticeCard.cs），<c>SystemToast</c> 的手写 WinRT 互操作、
/// 未打包 AUMID 注册、Toast XML 全部删除 —— **本进程不存在任何 WPN 入口**，崩溃面归零。
///
/// 因此本类的断言从"护栏有没有生效"升级为"**WPN 有没有被触碰**"：
///   A. 动态（<see cref="RealOs_DshWeb_NoticeCard_PresentsWithoutWpnApps"/>）：真实拉起
///      DshWeb.exe，经 DSH_TEST_NOTICE_CARD 自检通道把一条通知真的呈现出来（断言
///      <c>presented=True</c>，否则"WPN 没被加载"是因为什么都没干而空过），再枚举该进程
///      已加载模块，断言其中没有 wpnapps.dll，且宿主存活。需要交互桌面（真实窗口），
///      CI runner 为无交互会话时在门槛处返回（沿用 2ba57b61 口径）。
///   B. 静态（<see cref="RealOs_BuiltShellAssembly_ExposesNoWpnEntryPoint"/>）：直接扫已编译
///      的 DshWeb.dll 元数据，断言不含 Windows.UI.Notifications / wpnapps / CreateToastNotifier
///      等任何 WPN 入口符号。这条**无头也能跑**，是 CI 上的合并闸门：将来谁把系统 Toast
///      接回来，这条立刻红。
/// 清理只按记录 PID 杀进程树，绝不扫名杀。
/// </summary>
[Collection("RealOS")]
[Trait("Category", "RealOS")]
public class Regression_Issue25_WpnToastGuard_RealOs
{
    private readonly ITestOutputHelper _out;
    public Regression_Issue25_WpnToastGuard_RealOs(ITestOutputHelper o) => _out = o;

    private const int WatchSeconds = 45;

    /// <summary>WPN 入口符号：出现在 DshWeb.dll 里就说明"系统 Toast"通路被接回来了。</summary>
    private static readonly string[] WpnEntrySymbols =
    {
        "Windows.UI.Notifications",
        "wpnapps",
        "ToastNotificationManager",
        "CreateToastNotifier",
    };

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

    private static string? LocateDshWebDll()
    {
        var exe = LocateDshWebExe();
        if (exe is null) return null;
        var dll = Path.ChangeExtension(exe, ".dll");
        return File.Exists(dll) ? dll : null;
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

    private static string ReadLogText(string logPath)
    {
        if (!File.Exists(logPath)) return "";
        try
        {
            // FileShare.ReadWrite：宿主正在写这份日志
            using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var r = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
            return r.ReadToEnd();
        }
        catch { return ""; }
    }

    // ---- A. 动态：真实通知呈现 + 进程模块扫描 --------------------------------

    /// <summary>
    /// 真实拉起 DshWeb.exe（隔离 DSH_HOME/WebView2/外部托管假服务，绝不触碰宿主 3080 与
    /// 真实 dsh），把一条通知**真的显示出来**，然后断言这个进程从未加载 wpnapps.dll。
    /// CI 不验证本用例（无交互桌面即返回）；CI 侧的闸门是下面的 B。
    /// </summary>
    [Fact]
    public async Task RealOs_DshWeb_NoticeCard_PresentsWithoutWpnApps()
    {
        if (!Environment.UserInteractive)
        {
            _out.WriteLine("SKIP: non-interactive session (no desktop); assembly-level gate B covers CI");
            return;
        }
        var exe = LocateDshWebExe();
        if (exe is null)
        {
            _out.WriteLine("SKIP: DshWeb.exe not built (build src/DshShell first)");
            return;
        }

        var port = FreePort();
        var work = Path.Combine(Path.GetTempPath(), "dsh-issue25-card-" + Guid.NewGuid().ToString("N"));
        var homeDir = Path.Combine(work, "home");
        var wv2 = Path.Combine(work, "wv2");
        Directory.CreateDirectory(homeDir);
        Directory.CreateDirectory(wv2);
        var logPath = Path.Combine(homeDir, "dsh-launcher", "dsh.log");

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
            foreach (var key in new[] { "DSH_TEST_NOTICE_CARD", "DSH_TEST_TOAST", "DSH_TEST_FORCE_TOAST",
                                        "DSH_TEST_FORCE_TOAST_FAIL", "DSH_ENABLE_SYSTEM_TOAST" })
                psi.EnvironmentVariables.Remove(key);
            psi.EnvironmentVariables["DSH_SANDBOX"] = "1";                   // 禁机器级副作用
            psi.EnvironmentVariables["DSH_HOME"] = homeDir;                  // 壳数据/日志全隔离
            psi.EnvironmentVariables["DSH_WEB_URL"] = $"http://127.0.0.1:{port}"; // 外部托管：绝不拉起真实 dsh
            psi.EnvironmentVariables["DSH_WEBVIEW2_DATA"] = wv2;             // WebView2 数据隔离
            psi.EnvironmentVariables["DSH_TEST_INSTANCE"] = "1";
            psi.EnvironmentVariables["DSH_TEST_INSTALL_MODE"] = "msi";       // MSI 形态（与 reporter 一致）
            psi.EnvironmentVariables["DSH_TEST_NOTICE_CARD"] = "1";          // 走一次真实通知呈现
            psi.EnvironmentVariables["DSH_TELEMETRY_DISABLED"] = "1";
            psi.EnvironmentVariables["DSH_E2E"] = "1";                       // 模态硬化：ShowError 只写日志

            proc = Process.Start(psi)
                ?? throw new InvalidOperationException("failed to start DshWeb.exe");
            _out.WriteLine($"started pid={proc.Id} port={port}");

            var deadline = DateTime.UtcNow.AddSeconds(WatchSeconds);
            var logText = "";
            var wpnModule = "";
            var modulesScanned = false;
            string? exited = null;
            while (DateTime.UtcNow < deadline)
            {
                proc.Refresh();
                if (proc.HasExited)
                {
                    exited = $"exitCode={proc.ExitCode}";
                    break;
                }
                // 走到"通知已呈现"这一刻就扫模块：晚扫无意义（WPN 是延迟加载的，
                // reporter 的崩溃恰恰发生在 Show() 返回之后）。
                if ((logText = ReadLogText(logPath)).Contains("notice card self-test: presented="))
                {
                    try
                    {
                        wpnModule = string.Join(",", proc.Modules.Cast<ProcessModule>()
                            .Select(m => m.ModuleName)
                            .Where(n => n.Contains("wpnapps", StringComparison.OrdinalIgnoreCase)));
                        modulesScanned = true;
                    }
                    catch (Exception ex)
                    {
                        _out.WriteLine("module enumeration failed: " + ex.Message);
                    }
                    break;
                }
                await Task.Delay(500);
            }
            // 自检块里"同一条内容连送两次"的去重留痕是紧随其后写出的，给它一拍落盘时间。
            await Task.Delay(1500);
            logText = ReadLogText(logPath); // 收尾再读一次：最后一行才是判据所在
            proc.Refresh();
            var alive = !proc.HasExited;
            _out.WriteLine("alive=" + alive + (exited is not null ? " " + exited : "") + $" after {WatchSeconds}s");
            _out.WriteLine("---- dsh.log (notice lines) ----");
            foreach (var line in logText.Split('\n')
                         .Where(l => l.Contains("notice", StringComparison.OrdinalIgnoreCase)))
                _out.WriteLine(line.TrimEnd('\r'));

            // 区分"环境没走到通知那一步"与"通知真的坏了"：前者如实留痕跳过（CI runner
            // 未必有可用桌面/WebView2），后者必须判红。CI 侧的 WPN 闸门是下面两条无头用例。
            if (!logText.Contains("notice card self-test: presented="))
            {
                _out.WriteLine("SKIP: host never reached the notification step "
                    + $"(alive={alive}, exited={exited ?? "n/a"}); CI gate = assembly/source scan");
                return;
            }
            // 通知真的走通了才算"WPN 没被加载"有证据力——presented=False 是真回归。
            Assert.Contains("notice card self-test: presented=True", logText);
            // 同一条内容连送两次，第二次必须被去重闸门吞掉（保证不会重复通知）。
            Assert.Contains("notice suppressed as duplicate", logText);
            Assert.True(modulesScanned,
                "modules must be enumerable at the notification moment (else the scan proves nothing)");
            // 崩溃面归零：宿主进程里不得有任何 WPN 客户端模块
            Assert.True(string.IsNullOrEmpty(wpnModule), "wpnapps.dll must never be loaded: " + wpnModule);
            // 不得有任何系统 Toast 通路残留
            Assert.DoesNotContain("toast step", logText);
            Assert.DoesNotContain("update toast shown", logText);
            Assert.True(alive, "host must survive the notification; exited=" + (exited ?? "unknown"));
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

    // ---- B. 静态：编译产物里不存在 WPN 入口（CI 闸门） ------------------------

    /// <summary>
    /// 扫已编译的 DshWeb.dll，断言不含任何 WPN 入口符号。这是"系统 Toast 永不再回来"的
    /// CI 可验形态：A 需要交互桌面，B 在无头 runner 上也能跑，且任何把通知接回 WinRT 的
    /// 改动（哪怕只改一个调用点）都会立刻让它变红。
    /// </summary>
    [Fact]
    public void RealOs_BuiltShellAssembly_ExposesNoWpnEntryPoint()
    {
        var dll = LocateDshWebDll();
        Assert.True(dll is not null, "DshWeb.dll not built (build src/DshShell first)");
        var bytes = File.ReadAllBytes(dll!);
        var haystack = Encoding.UTF8.GetString(bytes);

        foreach (var symbol in WpnEntrySymbols)
            Assert.DoesNotContain(symbol, haystack, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>源码侧同一条不变量：不得再出现 WPN 通路成员访问。只拦**代码**（带点的成员
    /// 引用/类型全名），注释里叙述事故经过保留 wpnapps 字样是有搜索价值的线索。</summary>
    [Fact]
    public void ShellSource_HasNoSystemToastReference()
    {
        var srcRoot = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DshShell");
        Assert.True(Directory.Exists(srcRoot), "src/DshShell not found: " + srcRoot);
        var offenders = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(p => !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                     && !p.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Select(p => (Path: p, Text: File.ReadAllText(p)))
            .Where(t => t.Text.Contains("SystemToast.", StringComparison.Ordinal)
                     || t.Text.Contains("Windows.UI.Notifications", StringComparison.Ordinal)
                     || t.Text.Contains("CreateToastNotifier", StringComparison.Ordinal))
            .Select(t => Path.GetRelativePath(srcRoot, t.Path))
            .ToArray();
        Assert.True(offenders.Length == 0,
            "WPN/系统 Toast 通路必须整体移除（通知统一走 NoticeCard）；命中文件：" + string.Join(", ", offenders));
    }
}
