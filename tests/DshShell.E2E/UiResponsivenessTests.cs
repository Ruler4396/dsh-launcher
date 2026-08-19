using System.Diagnostics;
using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Capturing;
using FlaUI.UIA3;
using Xunit;
using Xunit.Sdk;

namespace DshShell.E2E;

/// <summary>
/// 方案 B：UI 渲染完整性与响应性——后台任务模拟耗时 10s 期间，Splash 窗口不得"假死"，
/// 且所有控件渲染完整（无空白）。断言维度：
///   1. 渲染完整性：取消按钮/状态文本 BoundingRectangle 非 0、IsOffscreen=false；
///   2. 响应性：Process.Responding 在后台任务进行中持续为 true（消息泵未被锁死）；
///   3. 截图分析：取消按钮区域白色像素占比低于阈值（无大面积"渲染空白"）；
///   4. 可交互：点击"取消"后进程正常退出（取消消息被 UI 线程处理）。
/// </summary>
public class UiResponsivenessTests
{
    private const string SplashTitle = "dsh-launcher 启动中";

    [Fact]
    public async Task Splash_stays_responsive_and_fully_rendered_during_slow_startup()
    {
        var exe = E2ETestHelpers.LocateDshWebExe();
        var home = E2ETestHelpers.CreateIsolatedHome();
        // 模拟"后台启动耗时"10s：期间断言消息泵健康与控件渲染
        using var proc = Process.Start(E2ETestHelpers.NewStartInfo(exe, home,
            ("DSH_TEST_SPLASH_DELAY_MS", "10000")));
        Assert.NotNull(proc);

        using var automation = new UIA3Automation();
        try
        {
            // 1. 捕获 Splash 窗口（最多 10s）
            var splash = await WaitForWindowAsync(automation, SplashTitle, TimeSpan.FromSeconds(10));
            Assert.NotNull(splash);

            // 2. 渲染完整性：取消按钮与状态文本完全可见、尺寸正常
            var cancel = splash.FindFirstDescendant(cf => cf.ByName("取消"));
            Assert.NotNull(cancel);
            Assert.False(cancel.IsOffscreen, "取消按钮 IsOffscreen=true（不可见）");
            Assert.True(cancel.BoundingRectangle.Width >= 60 && cancel.BoundingRectangle.Height >= 20,
                $"取消按钮 BoundingRectangle 异常（渲染不完整）：{cancel.BoundingRectangle}");

            var status = splash.FindFirstDescendant(cf => cf.ByName("正在准备启动…"));
            Assert.NotNull(status);
            Assert.False(status.IsOffscreen, "状态文本 IsOffscreen=true（不可见）");
            Assert.True(status.BoundingRectangle.Width > 0 && status.BoundingRectangle.Height > 0,
                $"状态文本 BoundingRectangle 异常：{status.BoundingRectangle}");

            // 3. 响应性：后台任务进行中 UI 线程仍健康（无 .Wait()/DoEvents 锁死消息泵）
            Assert.True(proc.Responding, "启动流水线运行期间 UI 线程无响应（消息泵被锁死）");
            await Task.Delay(2000); // 后台仍在"启动"（10s 未满）
            Assert.False(proc.HasExited, "后台任务运行期间进程意外退出");
            Assert.True(proc.Responding, "2 秒后 UI 线程仍无响应");

            // 4. 截图分析：取消按钮区域无大面积空白（全白 = 未绘制）
            using (var capture = Capture.Rectangle(cancel.BoundingRectangle))
            using (var bmp = capture.Bitmap)
            {
                var whiteRatio = ComputeWhiteRatio(bmp);
                Assert.True(whiteRatio < 0.98, $"取消按钮区域 {whiteRatio:P0} 为空白（渲染异常）");
            }

            // 5. 可交互：触发"取消" → 消息泵处理 → 进程退出
            // 注意：必须用 UIA InvokePattern 而非鼠标 Click()——FlaUI 的 Click() 不把窗口带到
            // 前台，鼠标点击前台之外的窗口时第一击只激活窗口、不触发按钮事件（本地复现：
            // "点击取消后进程未退出"根因）。InvokePattern 直接调用控件 Click 处理器，与前台
            // 焦点无关，CI/本地一致可靠。
            var invoke = cancel.Patterns.Invoke.Pattern;
            Assert.NotNull(invoke);
            invoke.Invoke();
            await Task.Delay(1500);
            Assert.True(proc.HasExited, "触发取消后进程未退出（取消消息未被 UI 线程处理）");
        }
        finally
        {
            if (proc.HasExited == false) proc.Kill(entireProcessTree: true);
            try { Directory.Delete(home, recursive: true); } catch { /* 清理失败不影响结果 */ }
        }
    }

    // ---------------- FlaUI / 图像辅助 ----------------

    private static async Task<Window> WaitForWindowAsync(AutomationBase automation, string title, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // 用 FindWindow 拿句柄（UIA Name 匹配 WinForms 顶层窗口标题不可靠），再 FromHandle
            var hwnd = E2ETestHelpers.FindTopLevelWindow(title);
            if (hwnd != IntPtr.Zero)
            {
                var el = automation.FromHandle(hwnd);
                var w = el?.AsWindow();
                if (w is not null) return w;
            }
            await Task.Delay(100);
        }
        throw new XunitException($"窗口 '{title}' 在 {timeout} 内未出现");
    }

    private static double ComputeWhiteRatio(Bitmap bmp)
    {
        var white = 0;
        var total = 0;
        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var c = bmp.GetPixel(x, y);
                if (c.R > 240 && c.G > 240 && c.B > 240) white++;
                total++;
            }
        }
        return total == 0 ? 1.0 : (double)white / total;
    }
}
