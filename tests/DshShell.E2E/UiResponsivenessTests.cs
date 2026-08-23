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
            // 1. 捕获 Splash 窗口（最多 20s；CI runner 冷启动 .NET+WinForms 可达 10s+）
            var splash = await WaitForWindowAsync(automation, SplashTitle, TimeSpan.FromSeconds(20));
            Assert.NotNull(splash);

            // 2. 渲染完整性：取消按钮与状态文本完全可见、尺寸正常。
            // 注意：窗口句柄出现（FindWindowW 命中标题）≠ UIA 控件树就绪——控件树随 WM_PAINT
            // 异步同步，立即 FindFirstDescendant 偶发 null（CI runner 更明显，见 32238645282）。
            // 必须轮询等待控件出现（≤3s），窗口已就绪而控件尚未渲染才算真失败。
            var cancel = await WaitForDescendantAsync(splash,
                cf => cf.ByName("取消"), TimeSpan.FromSeconds(3));
            Assert.NotNull(cancel);
            Assert.False(cancel.IsOffscreen, "取消按钮 IsOffscreen=true（不可见）");
            Assert.True(cancel.BoundingRectangle.Width >= 60 && cancel.BoundingRectangle.Height >= 20,
                $"取消按钮 BoundingRectangle 异常（渲染不完整）：{cancel.BoundingRectangle}");

            // 阶段 0 会覆盖状态文本为"正在准备启动环境…"（v0.4.0 文案）
            var status = await WaitForDescendantAsync(splash,
                cf => cf.ByName("正在准备启动环境…"), TimeSpan.FromSeconds(3));
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
            var cancelRect = cancel.BoundingRectangle; // 诊断用（DPI 虚拟化时可能是逻辑坐标）
            using (var capture = Capture.Rectangle(cancelRect))
            using (var bmp = capture.Bitmap)
            {
                var whiteRatio = ComputeWhiteRatio(bmp);
                Assert.True(whiteRatio < 0.98,
                    $"取消按钮区域 {whiteRatio:P0} 为空白：rect={cancelRect} " +
                    $"work={System.Windows.Forms.Screen.PrimaryScreen!.Bounds}");
            }

            // 5. 可交互：触发"取消" → 消息泵处理 → 进程退出
            // 注意：必须用 UIA InvokePattern 而非鼠标 Click()——FlaUI 的 Click() 不把窗口带到
            // 前台，鼠标点击前台之外的窗口时第一击只激活窗口、不触发按钮事件（本地复现：
            // "点击取消后进程未退出"根因）。InvokePattern 直接调用控件 Click 处理器，与前台
            // 焦点无关，CI/本地一致可靠。
            var invoke = cancel.Patterns.Invoke.Pattern;
            Assert.NotNull(invoke);
            // CI runner（Windows Server 2025）上 UIA Invoke 偶发竞态：窗口刚渲染完成时 UIA 元素
            // 与控件树同步存在极短窗口，Invoke 命中抛 Catastrophic failure (0x8000FFFF) 或
            // ElementNotAvailable (0x80040201，元素已失效)。防御：短重试（≤5 次，每次 100ms），
            // 元素失效时重新查找控件（UI 树刷新后引用重建），仍失败才判定为真失败。
            var invokeDeadline = DateTime.UtcNow.AddSeconds(2);
            Exception? lastInvokeError = null;
            while (DateTime.UtcNow < invokeDeadline)
            {
                try { invoke.Invoke(); lastInvokeError = null; break; }
                catch (System.ComponentModel.Win32Exception ex) { lastInvokeError = ex; /* 0x8000FFFF：重试 */ }
                catch (FlaUI.Core.Exceptions.ElementNotAvailableException ex)
                {
                    // 0x80040201：元素已不可用——重新查找 Splash 内的"取消"按钮并重建 Invoke 模式
                    lastInvokeError = ex;
                    cancel = await WaitForDescendantAsync(splash,
                        cf => cf.ByName("取消"), TimeSpan.FromSeconds(2));
                    Assert.NotNull(cancel);
                    invoke = cancel.Patterns.Invoke.Pattern;
                    Assert.NotNull(invoke);
                }
                catch (System.Runtime.InteropServices.COMException ex) { lastInvokeError = ex; /* 其他 UIA COM 竞态：重试 */ }
                await Task.Delay(100);
            }
            if (lastInvokeError is not null) throw lastInvokeError;
            // 轮询等待进程退出（最长 10s）：取消 → cts.Cancel → 流水线收到 OCE → Close →
            // Application.Run 返回 → Main 退出。CI 冷启动下退出收尾可能 >1.5s（此前固定等待
            // 偶发误报"进程未退出"）；同时阶段 0 的 npm 安装取消路径（Kill 进程树）也在此验证。
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!proc.HasExited && DateTime.UtcNow < deadline)
                await Task.Delay(150);
            Assert.True(proc.HasExited, "触发取消后 10s 内进程未退出（取消消息未被 UI 线程处理）");
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

    /// <summary>轮询等待 UIA 后代控件出现（控件树异步就绪，窗口就绪≠控件就绪）。</summary>
    private static async Task<FlaUI.Core.AutomationElements.AutomationElement?> WaitForDescendantAsync(
        FlaUI.Core.AutomationElements.Window window,
        Func<FlaUI.Core.Conditions.ConditionFactory, FlaUI.Core.Conditions.ConditionBase> condition,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var el = window.FindFirstDescendant(condition);
            if (el is not null) return el;
            await Task.Delay(100);
        }
        return null;
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
