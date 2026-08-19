using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DshWeb.Win32;

/// <summary>
/// UI 测试钩子（TestHook，Task 2 维度三）：仅在 <c>DSH_TEST_MODE=1</c> 且 <c>--ui-probe</c>
/// 探针模式下由 <c>Program.RunUiProbe</c> 启动的 NamedPipe 通信服务，供 E2E 精确验证窗口几何
///（最大化 0px 间隙 / DPI 缩放）。
///
/// 协议（每连接一条命令，行分隔 JSON，回复单行 JSON）：
///   {"cmd":"ToggleMaximize"} → 对窗口发送 WM_SYSCOMMAND SC_MAXIMIZE（等价点击最大化按钮）
///   {"cmd":"GetWindowRect"}  → 窗口物理像素矩形 {"left","top","right","bottom"}
///   {"cmd":"GetWorkArea"}    → 窗口所在监视器的物理 rcWork（MonitorFromWindow + GetMonitorInfo）
///   {"cmd":"Shutdown"}       → 请求退出探针
///
/// 设计约束：
/// - 生产路径零接触：只读 DSH_TEST_MODE 环境变量，缺省即完全 inert（不建 pipe、不开线程）；
/// - 只对 --ui-probe 探针窗口接线，真实主窗/托盘路径不启动；
/// - 与现有 e2e 探针（MaximizeAcrossVirtualDisplayTests 的 PostMessage+GetWindowRect）互补：
///   探针直接操作 Win32，而 TestHook 提供进程内可控入口，避免 E2E 与 Win32 细节强耦合。
/// </summary>
public static class UiTestHook
{
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MAXIMIZE = 0xF030;

    /// <summary>仅测试环境激活（DSH_TEST_MODE=1）；生产路径永远 false。</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable("DSH_TEST_MODE") == "1";

    /// <summary>pipe 名（按进程 PID 隔离，避免并行 E2E 互踩）。</summary>
    public static string PipeName(int pid) => $"dsh-launcher-uites-{pid}";

    /// <summary>
    /// 启动 NamedPipe 服务循环：每连接处理一条命令后关闭连接。ct 取消即退出。
    /// 由 RunUiProbe 在 Application.Run 前后台任务驱动。
    /// 收到 Shutdown 命令时调用 <paramref name="onShutdown"/>（组合根注入关窗动作，实现优雅退出）。
    /// </summary>
    public static async Task RunAsync(IntPtr hwnd, CancellationToken ct, Action? onShutdown = null)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    PipeName(Environment.ProcessId), PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                // leaveOpen: true —— StreamReader/Writer 默认持有并关闭底层流，逆序 Dispose 时会
                // 先关管道再 flush，导致 "Cannot access a closed pipe"；统一由 server 关闭。
                // AutoFlush: true —— 必须显式开启：回复 WriteLine 后立即落管道，否则客户端
                // ReadLineAsync 永远读不到（v0.4.2 卡死根因：改 leaveOpen 时丢了 AutoFlush）。
                using var reader = new StreamReader(server, Encoding.UTF8, true, 1024, leaveOpen: true);
                using var writer = new StreamWriter(server, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
                // 一条连接可处理多条命令；保持连接直到客户端 EOF（ReadLineAsync 返回 null），
                // 避免"服务端回复后立即关闭"导致客户端 StreamWriter.Dispose 时踩关闭的管道。
                while (true)
                {
                    var line = await reader.ReadLineAsync(ct);
                    if (line is null) break; // 客户端关闭连接 → 本连接结束
                    var reply = HandleCommand(line, hwnd);
                    writer.WriteLine(reply);
                    if (IsShutdownReply(reply)) onShutdown?.Invoke();
                }
            }
            catch (OperationCanceledException) { break; }
            catch { /* 单次连接失败不影响服务存活；下次连接重试 */ }
        }
    }

    private static bool IsShutdownReply(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("shutdown", out var v) && v.GetBoolean();
        }
        catch { return false; }
    }

    private static string HandleCommand(string line, IntPtr hwnd)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var cmd = doc.RootElement.TryGetProperty("cmd", out var p) ? p.GetString() : null;
            return cmd switch
            {
                "ToggleMaximize" => ToggleMaximize(hwnd),
                "GetWindowRect" => RectJson(GetWindowRect(hwnd)),
                "GetWorkArea" => RectJson(GetMonitorWorkArea(hwnd)),
                "Shutdown" => """{"ok":true,"shutdown":true}""",
                _ => """{"ok":false,"error":"unknown command"}""",
            };
        }
        catch (Exception ex)
        {
            return "{\"ok\":false,\"error\":\"" + JsonEscape(ex.Message) + "\"}";
        }
    }

    private static string ToggleMaximize(IntPtr hwnd)
    {
        PostMessage(hwnd, WM_SYSCOMMAND, SC_MAXIMIZE, IntPtr.Zero);
        return """{"ok":true,"maximized":true}""";
    }

    private static string RectJson(RECT r) =>
        $"{{\"ok\":true,\"left\":{r.Left},\"top\":{r.Top},\"right\":{r.Right},\"bottom\":{r.Bottom}}}";

    private static string JsonEscape(string s) =>
        JsonEncodedText.Encode(s).ToString();

    // ---------------- Win32 ----------------

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, int wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags); // 1 = MONITOR_DEFAULTTONEAREST

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    private static RECT GetWindowRect(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return r;
    }

    /// <summary>窗口所在监视器的物理像素工作区（rcWork，即"最大化目标"，不含任务栏）。</summary>
    private static RECT GetMonitorWorkArea(IntPtr hwnd)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, 1), ref mi);
        return mi.rcWork;
    }
}
