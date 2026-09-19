using System.Diagnostics;
using System.Drawing;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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
        // 隔离 home 下发现链版本未知 → 显式注入版本，保证标题栏 dsh 版本徽标真实渲染
        // （真机回归 RealMouseClick_OnVersionBadge_* 需要可点击的徽标）
        psi.Environment["DSH_VERSION"] = "0.1.5-rc.1";
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
    public async Task VersionDialog_OpenAndClose_KeepsProcessAlive_Issue28()
    {
        // 【issue #28-2 回归】用户报告"点击标题栏版本号卡死无法关闭然后闪退"：
        // 事件日志为 0xc0000005（coreclr 内 ImmSetOpenStatus），托管栈经过
        // ShowVersionInfoDialog ← CustomTitleBar.OnMouseDown。本用例经 TestHook 真实触发
        // 版本弹窗的打开/关闭（等价点版本徽标），断言进程存活且弹窗可正常关闭。
        Assert.NotNull(_proc);
        var pipeName = DshWeb.Win32.UiTestHook.PipeName(_proc.Id);

        var resp = await SendAsync(pipeName, """{"cmd":"ShowVersionDialog"}""", TimeSpan.FromSeconds(15));
        using (var doc = JsonDocument.Parse(resp))
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean(), $"ShowVersionDialog 返回失败：{resp}");

        var dialog = await E2ETestHelpers.WaitForWindowByTitleAsync("版本信息", TimeSpan.FromSeconds(15));
        Assert.NotEqual(IntPtr.Zero, dialog);

        // 【DPI 纪律】无边框窗的窗口矩形 == 客户端尺寸，必须等于纯函数按**该窗口所在屏的 DPI**
        // 算出的结果。旧实现把 520×188 写死在 96dpi 像素上（且没有 OnDpiChanged），
        // 缩放屏上"窗口不跟着长、字体跟着长"→ 叠列；这条断言在真实 GUI 上钉死它，
        // 不靠肉眼（AGENTS.md：改窗口布局/DPI 必须用 --ui-selftest 或 E2E 量）。
        var dpi = (int)GetDpiForWindow(dialog);
        Assert.True(GetWindowRect(dialog, out var rect), "GetWindowRect 失败");
        var expected = DshWeb.ShellLogic.VersionDialogLayout.Compute(dpi);
        Assert.InRange(rect.Right - rect.Left, expected.ClientWidth - 1, expected.ClientWidth + 1);
        Assert.InRange(rect.Bottom - rect.Top, expected.ClientHeight - 1, expected.ClientHeight + 1);

        // 关闭（等价点标题栏 X / 按 ESC）：模态循环结束正是崩溃路径 B 的时刻
        PostMessage(dialog, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

        var closedDeadline = DateTime.UtcNow.AddSeconds(10);
        while (E2ETestHelpers.FindTopLevelWindow("版本信息") != IntPtr.Zero && DateTime.UtcNow < closedDeadline)
            await Task.Delay(100);

        Assert.False(_proc.HasExited, "版本弹窗打开/关闭后进程崩溃（issue #28-2 复发）");
        Assert.Equal(IntPtr.Zero, E2ETestHelpers.FindTopLevelWindow("版本信息"));
    }

    [Fact]
    public async Task RealMouseClick_OnVersionBadge_OpensDialog_AndProcessSurvives_Issue28()
    {
        // 【issue #28-2 真机回归】用**真实鼠标输入**点击标题栏 dsh 版本徽标（不是 TestHook 直调入口）：
        // 走完 CustomTitleBar 命中测试 → VersionClick → ShowVersionInfoDialog → 弹窗开关 全链路。
        // 用户现场正是"点版本号 → 卡死 → 闪退"（0xc0000005 / ImmSetOpenStatus）。
        Assert.NotNull(_proc);
        var pipeName = DshWeb.Win32.UiTestHook.PipeName(_proc.Id);

        // 1) 取徽标命中矩形（生产 OnPaint 计算 → 屏幕物理像素）。首次绘制完成前 _versionRect 为空，
        //    窗口句柄出现 ≠ 已绘制 → 轮询等待首帧。
        int cx = 0, cy = 0;
        var rectDeadline = DateTime.UtcNow.AddSeconds(15);
        while (true)
        {
            var json = await SendAsync(pipeName, """{"cmd":"GetVersionBadgeRect"}""", TimeSpan.FromSeconds(15));
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("ok").GetBoolean())
            {
                var l = doc.RootElement.GetProperty("left").GetInt32();
                var t = doc.RootElement.GetProperty("top").GetInt32();
                var r = doc.RootElement.GetProperty("right").GetInt32();
                var b = doc.RootElement.GetProperty("bottom").GetInt32();
                Assert.True(r > l && b > t, $"徽标矩形非法：{json}");
                cx = (l + r) / 2;
                cy = (t + b) / 2;
                break;
            }
            if (DateTime.UtcNow >= rectDeadline)
                throw new XunitException($"版本徽标始终未渲染（GetVersionBadgeRect：{json}）");
            await Task.Delay(200);
        }

        // 2) 真实鼠标：先移到标题栏空白处（触发 MouseMove 命中矩形），再进徽标点击
        GetCursorPos(out var saved);
        try
        {
            SetCursorPos(cx - 150, cy);
            await Task.Delay(200);
            SetCursorPos(cx, cy);
            await Task.Delay(250);
            mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, IntPtr.Zero);
            await Task.Delay(80);
            mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, IntPtr.Zero);

            var dialog = await E2ETestHelpers.WaitForWindowByTitleAsync("版本信息", TimeSpan.FromSeconds(15));
            Assert.NotEqual(IntPtr.Zero, dialog);
            Assert.False(_proc.HasExited, "真实点击版本徽标后进程崩溃（issue #28-2 复发）");

            // 3) 关闭（等价点 X / ESC）—— 崩溃路径 B 的时刻
            PostMessage(dialog, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (E2ETestHelpers.FindTopLevelWindow("版本信息") != IntPtr.Zero && DateTime.UtcNow < deadline)
                await Task.Delay(100);

            Assert.Equal(IntPtr.Zero, E2ETestHelpers.FindTopLevelWindow("版本信息"));
            Assert.False(_proc.HasExited, "版本弹窗关闭后进程崩溃（issue #28-2 复发）");
        }
        finally
        {
            SetCursorPos(saved.X, saved.Y); // 复位用户鼠标位置
        }
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

    private const int WM_CLOSE = 0x0010;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

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
                // leaveOpen: true —— 否则 reader/writer 逆序 Dispose 会先关管道再 flush。
                // AutoFlush: true —— 请求必须立即写入管道，否则服务端 ReadLineAsync 永远等不到。
                using var writer = new StreamWriter(client, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
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
