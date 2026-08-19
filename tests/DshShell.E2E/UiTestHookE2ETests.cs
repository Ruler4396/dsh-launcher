using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Xunit;
using Xunit.Sdk;

namespace DshShell.E2E;

/// <summary>
/// 维度三：TestHook（NamedPipe 内部通信通道）E2E。
/// 通过 TestHook 发送 <c>ToggleMaximize</c> 指令，再用 <c>GetWindowRect</c>/<c>GetWorkArea</c>
/// 读取窗口**真实物理边界**与目标工作区，断言"最大化 0px 间隙"（窗口矩形 ⊆ 工作区，≤2px）。
///
/// 与 MaximizeAcrossVirtualDisplayTests（裸 Win32 PostMessage/SetWindowPos）互补：
/// TestHook 让客户端不再触碰 Win32 细节，通过进程内可控入口驱动真实窗口几何——
/// 这正是解决"WinForms UI 几何状态难以自动化"的机制（DSH_TEST_MODE=1 时才激活，生产零接触）。
/// </summary>
public class UiTestHookE2ETests : IAsyncLifetime
{
    private const string WindowTitle = "DeepSeek Harness";
    private const int TolerancePx = 2;

    private Process? _proc;
    private string _home = "";

    public async Task InitializeAsync()
    {
        var exe = E2ETestHelpers.LocateDshWebExe();
        _home = E2ETestHelpers.CreateIsolatedHome();
        // --ui-probe（无服务探针窗）+ DSH_TEST_MODE=1（激活 TestHook NamedPipe）
        var psi = new ProcessStartInfo(exe, "--ui-probe")
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.Environment["DSH_HOME"] = _home;
        psi.Environment["DSH_TEST_MODE"] = "1";
        _proc = Process.Start(psi);
        Assert.NotNull(_proc);

        // 等待探针主窗出现（TestHook 随窗口句柄建立）
        var hwnd = await E2ETestHelpers.WaitForWindowByTitleAsync(WindowTitle, TimeSpan.FromSeconds(30));
        Assert.NotEqual(IntPtr.Zero, hwnd);
    }

    public async Task DisposeAsync()
    {
        if (_proc is { HasExited: false })
        {
            _proc.Kill(entireProcessTree: true);
            await _proc.WaitForExitAsync();
        }
        _proc?.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch { /* 清理失败不影响结果 */ }
    }

    [Fact]
    public async Task ToggleMaximize_FillsWorkingArea_Within_Tolerance()
    {
        Assert.NotNull(_proc);
        var pipeName = DshWeb.Win32.UiTestHook.PipeName(_proc.Id);

        // 1. 发送 ToggleMaximize（等价点击最大化按钮）
        var maxResp = await SendAsync(pipeName, """{"cmd":"ToggleMaximize"}""", TimeSpan.FromSeconds(15));
        using (var doc = JsonDocument.Parse(maxResp))
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(),
                $"ToggleMaximize 返回失败：{maxResp}");

        await Task.Delay(800); // 等系统完成最大化重排

        // 2. 读取窗口真实物理边界与目标工作区
        var rect = ParseRect(await SendAsync(pipeName, """{"cmd":"GetWindowRect"}""", TimeSpan.FromSeconds(5)));
        var work = ParseRect(await SendAsync(pipeName, """{"cmd":"GetWorkArea"}""", TimeSpan.FromSeconds(5)));

        // 3. 断言 0px 间隙：窗口物理矩形完全包含在工作区内（与 e2e geo 探针同容差 ≤2px）
        Assert.True(rect.Left >= work.Left - TolerancePx,
            $"左越界：left={rect.Left} 要求 ≥ {work.Left - TolerancePx}");
        Assert.True(rect.Top >= work.Top - TolerancePx,
            $"上越界：top={rect.Top} 要求 ≥ {work.Top - TolerancePx}");
        Assert.True(rect.Right <= work.Right + TolerancePx,
            $"右越界：right={rect.Right} 要求 ≤ {work.Right + TolerancePx}");
        Assert.True(rect.Bottom <= work.Bottom + TolerancePx,
            $"下越界：bottom={rect.Bottom} 要求 ≤ {work.Bottom + TolerancePx}");
    }

    [Fact]
    public async Task Shutdown_Command_ExitsProcessGracefully()
    {
        Assert.NotNull(_proc);
        var pipeName = DshWeb.Win32.UiTestHook.PipeName(_proc.Id);

        var resp = await SendAsync(pipeName, """{"cmd":"Shutdown"}""", TimeSpan.FromSeconds(15));
        using (var doc = JsonDocument.Parse(resp))
            Assert.True(doc.RootElement.GetProperty("shutdown").GetBoolean());

        // 优雅退出路径：Shutdown → onShutdown → form.Close → Application.Run 返回 → 进程退出
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!_proc.HasExited && DateTime.UtcNow < deadline)
            await Task.Delay(100);
        Assert.True(_proc.HasExited, "Shutdown 后进程未在 10s 内退出");
    }

    // ---------------- NamedPipe 客户端 + JSON 辅助 ----------------

    /// <summary>发送一条命令并读取单行 JSON 回复；服务端未就绪时按 deadline 重试连接。</summary>
    private static async Task<string> SendAsync(string pipeName, string request, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut);
                client.Connect(Math.Max(50, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                // leaveOpen: true —— 否则 reader/writer 逆序 Dispose 会先关管道再 flush
                using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true);
                using var reader = new StreamReader(client, Encoding.UTF8, true, 1024, leaveOpen: true);
                writer.WriteLine(request);
                var reply = await reader.ReadLineAsync();
                if (reply is null) throw new IOException("empty reply");
                return reply;
            }
            catch (TimeoutException)
            {
                throw new XunitException($"pipe '{pipeName}' 在 {timeout.TotalSeconds:0}s 内未就绪/未回复");
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(100); // 服务端尚未建 pipe → 重试
            }
        }
        throw new XunitException($"pipe '{pipeName}' 连接超时（{timeout.TotalSeconds:0}s）");
    }

    /// <summary>解析 {"left","top","right","bottom"} 回复为 Rectangle。</summary>
    private static Rectangle ParseRect(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (!r.GetProperty("ok").GetBoolean())
            throw new XunitException("GetWindowRect/GetWorkArea 返回失败：" + json);
        return Rectangle.FromLTRB(
            r.GetProperty("left").GetInt32(),
            r.GetProperty("top").GetInt32(),
            r.GetProperty("right").GetInt32(),
            r.GetProperty("bottom").GetInt32());
    }
}
