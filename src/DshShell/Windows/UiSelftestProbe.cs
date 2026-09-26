using System.Drawing;
using System.Windows.Forms;
using DshWeb.Chrome;
using DshWeb.Managers;
using Microsoft.Web.WebView2.WinForms;

namespace DshWeb.Windows;

/// <summary>
/// CI/UI 探针与几何自测的宿主（从组合根 Program.cs 迁出，臃肿审计 Phase 4 · T7）。
///
/// 为什么单独成文件而不是留在 Program.cs：这三段是**测试脚手架**，既不是组合根装配也不是消息泵，
/// 且各自内联重抄了一遍生产窗体的装配规则（无边框 + 自绘标题栏 32×DPI + LayoutChrome +
/// DWM frame 补偿）。同一份知识两处抄写 = 迟早一处改一处漏——注释里就记着探针曾漏掉
/// ApplyWindowShadow，导致 CI 上最大化四周留 8px 缝隙（e2e-geo G1/G10 根因）。
///
/// 生产依赖一律经 <see cref="Context"/> 注入，本文件不回调 Program 静态
/// （由 scripts/test.ps1 棘轮 G3 机器把关）。
/// </summary>
internal static class UiSelftestProbe
{
    /// <summary>探针窗与生产窗共用的装配能力（组合根注入）。</summary>
    internal sealed record Context(
        string LogPath,
        Func<bool> IsDarkMode,
        Action<IntPtr> ApplyShadow,
        Func<WebView2, string, Task> InitWebView,
        Action<Form> ShowVersionDialog);
    /// <summary>
    /// 无头 UI 几何自测（GitHub CI 用）：建主窗 → 最大化 → 断言"窗口矩形 == 工作区"（0px 铺满，ADR-001）。
    /// 不依赖 dsh 服务 / Node / WebView2 内容，只验证自绘边框的 Win32 消息（WS_CAPTION 移除 + WM_GETMINMAXINFO）。
    /// 退出码：0=通过，1=几何不符，2=内部异常。结果同时写统一日志与 stdout（CI 抓取）。
    /// </summary>
    internal static int RunUiSelftest(Context ctx)
    {
        Logger.Init(ctx.LogPath);
        try
        {
            var form = new DshShellForm
            {
                Text = "dsh selftest",
                ClientSize = new Size(1280, 840),
                MinimumSize = new Size(800, 600),
                FormBorderStyle = FormBorderStyle.None,
                ShowInTaskbar = false,
            };
            form.TitleBar = new CustomTitleBar(form, ctx.IsDarkMode())
            {
                Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2,
                    DshWeb.ShellLogic.DpiScale.Px(32, DshWeb.ShellLogic.DpiScale.Of(form.DeviceDpi))),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            form.Controls.Add(form.TitleBar);
            form.Show();
            form.Refresh(); // 强制重绘（替代 DoEvents，避免重入风险）
            form.WindowState = FormWindowState.Maximized;
            form.Refresh();

            var wa = Screen.FromHandle(form.Handle).WorkingArea;
            var b = form.Bounds;
            var ok = b.X == wa.X && b.Y == wa.Y && b.Width == wa.Width && b.Height == wa.Height;

            // 第二遍（issue #28-2）：Splash 启动窗"文字必须装得进控件框"的实测自检。
            // 在 100% 屏上它必然通过（新旧实现都是）——它的价值是在**高分屏开发机/真机**上
            // 直接变红；跨 DPI 的缩放正确性由 SplashLayoutContractTests 在 CI 钉死。
            var splashOk = RunSplashLayoutSelftest();

            var msg = $"UI-SELFTEST pass={ok && splashOk} geometry={ok} splashLayout={splashOk} "
                + $"bound=({b.X},{b.Y},{b.Width}x{b.Height}) workarea=({wa.X},{wa.Y},{wa.Width}x{wa.Height})";
            Logger.Info(msg);
            Console.WriteLine(msg);
            Managers.SelftestReporter.Write(ok && splashOk, msg);
            form.Close();
            return (ok && splashOk) ? 0 : 1;
        }
        catch (Exception ex)
        {
            var msg = "UI-SELFTEST threw: " + ex.Message;
            Logger.Error(msg);
            Console.Error.WriteLine(msg);
            Managers.SelftestReporter.Write(false, msg);
            return 2;
        }
    }

    /// <summary>
    /// [issue #28-2] Splash 启动窗布局自检：按**当前宿主 DPI** 实测每个控件的文字墨迹，
    /// 断言它装得进自己的框。旧实现把 380×180 / 60×22 写成硬编码物理像素而字体是 point
    /// （随 DPI 变大），200% 屏上"取消"按钮被自己的文字撑破——用户截图里的"按钮基本看不到"。
    /// 流水线注入"永不完成"的 Task：自检不拉起任何真实服务、不碰网络与进程。
    /// </summary>
    internal static bool RunSplashLayoutSelftest()
    {
        using var splash = new SplashForm(
            (_, _, _) => new TaskCompletionSource<SplashForm.Outcome>().Task, visible: false);
        splash.Show();
        splash.Refresh();

        var g = splash.CurrentLayout;
        using var gfx = splash.CreateGraphics();
        bool Fits(string text, Font font, Rectangle box)
        {
            var ink = TextRenderer.MeasureText(gfx, text, font, Size.Empty, TextFormatFlags.NoPadding);
            var fits = ink.Width <= box.Width && ink.Height <= box.Height;
            Console.WriteLine($"UI-SELFTEST splash {(fits ? "ok " : "FAIL")} \"{text}\" ink={ink.Width}x{ink.Height}"
                + $" box={box.Width}x{box.Height} dpi={splash.DeviceDpi}");
            return fits;
        }

        var ok = Fits("取消", splash.Font, g.Cancel)
            && Fits("正在准备启动…", splash.Font, g.Status)
            && Fits("是", splash.Font, g.ConfirmYes)
            && Fits("否", splash.Font, g.ConfirmNo);
        splash.Close();
        return ok;
    }

    /// <summary>
    /// --ui-probe 无服务窗口探针（Task 0，CI geo 探针用）：不拉 dsh 服务、不导航真实内容，
    /// 只开 DshShellForm（自绘标题栏 + WebView2 + F11 钩子），WebView2 导航 about:blank。
    /// 供 e2e 探针从外部做几何（最大化==工作区）/F11（SendInput 注入翻转）/标题栏（子控件存在、
    /// Visible、高≈32×DPI）/白屏（DSH_WEBVIEW2_READYSTATE 的 document.readyState）断言。
    /// 动机：e2e 隔离 dsh 服务在全新 DSH_HOME 起不来（dsh 生态 profile 初始化缺
    /// dsh-client-ui-plan），而 geo 探针验证的窗口行为本身不依赖服务内容——解耦后 CI 可稳定跑。
    /// 返回 0=正常关闭，2=异常。
    /// </summary>
    internal static int RunUiProbe(Context ctx)
    {
        Logger.Init(ctx.LogPath);
        try
        {
            var form = new DshShellForm
            {
                Text = "DeepSeek Harness", // 与真实主窗同名，供探针 FindWindow 定位
                ClientSize = new Size(1280, 840),
                MinimumSize = new Size(800, 600),
                FormBorderStyle = FormBorderStyle.None,
            };
            form.TitleBar = new CustomTitleBar(form, ctx.IsDarkMode())
            {
                Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2,
                    DshWeb.ShellLogic.DpiScale.Px(32, DshWeb.ShellLogic.DpiScale.Of(form.DeviceDpi))),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            form.Controls.Add(form.TitleBar);
            // [issue #28-2 真机回归] 探针窗与真实主窗一样装配 dsh 版本徽标 + 点击回调：
            // 使 E2E/真机验证可以用**真实鼠标点击**走完
            // CustomTitleBar 命中测试 → VersionClick → ShowVersionInfoDialog → 弹窗开关 全链路
            // （此前探针窗无徽标，"点版本号闪退"只能测到弹窗入口，测不到点击命中段）。
            form.TitleBar._dshVersion = UpdateChecker.ResolveLocalDshVersion() ?? "";
            form.TitleBar.VersionClick = () => ctx.ShowVersionDialog(form);
            // 与真实主窗对齐（见本文件建窗处的 HandleCreated 订阅）：启用 DWM NC 渲染后，
            // 最大化窗口才会向四周外扩 frame——WM_GETMINMAXINFO 的 frame 补偿（pos=work+frame,
            // size=work-2*frame）才成立。探针此前缺此行 → CI（Server runner）上 DWM 不外扩、
            // 补偿落空 → 最大化后四周留 8px 缝隙（e2e-geo G1/G10 回归根因）。
            form.HandleCreated += (_, _) => ctx.ApplyShadow(form.Handle);

            var web = new WebView2
            {
                Bounds = new Rectangle(1, 1 + form.TitleBar.Height,
                    form.ClientSize.Width - 2, form.ClientSize.Height - form.TitleBar.Height - 2),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                // 与主窗一致：禁用 WinForms IME 状态管理（防 ImmSetOpenStatus 崩溃，见主窗注释）
                ImeMode = ImeMode.Disable,
            };
            form.Controls.Add(web);
            form.MainWebView2 = web;
            WebViewManager.MainWeb = web; // readyState 测试钩子按 ReferenceEquals(web, MainWeb) 门控，必须先设
            // 探针必须量到**生产同一条**布局规则：上面两处内联的 32*dpi/96 与 LayoutChrome 是
            // 同一份知识的两份抄写——geo 探针断言"0px 间隙"时，如果探针自己算、生产另算，
            // 探针绿了生产照样能错（#28 那轮"一处修一处漏"的同族）。
            form.LayoutChrome();
            form.DpiChanged += (_, _) =>
            {
                form.TitleBar.Rescale(DshWeb.ShellLogic.DpiScale.Of(form.DeviceDpi));
                form.LayoutChrome();
            };

            // F11 钩子（与真实路径一致）：仅主窗前台时切换并吞键。
            // 跨线程修复（Step2b）：缓存 hwnd 再进 lambda，避免销毁期 ObjectDisposedException。
            var probeHwnd = form.Handle;
            using var f11Hook = new F11LowLevelHook(() => form.BeginInvoke(new Action(form.ToggleFullscreen)),
                () => F11LowLevelHook.GetForegroundWindow() == probeHwnd);
            Logger.Info($"ui-probe: f11 hook installed hwnd=0x{probeHwnd.ToInt64():X}"); // 诊断：确认走 --ui-probe 分支

            form.Shown += async (_, _) =>
            {
                var userDataFolder = Environment.GetEnvironmentVariable("DSH_WEBVIEW2_DATA");
                if (string.IsNullOrWhiteSpace(userDataFolder))
                    userDataFolder = Path.Combine(Path.GetTempPath(), "dsh-ui-probe-wv2");
                try
                {
                    await ctx.InitWebView(web, userDataFolder);
                    web.CoreWebView2.Navigate("about:blank"); // 无需网络，readyState 钩子照常触发
                }
                catch (Exception ex)
                {
                    Logger.Error("ui-probe webview init failed: " + ex.Message);
                }
            };

            // TestHook（Task 2 维度三）：DSH_TEST_MODE=1 时启动 NamedPipe 几何控制服务。
            // 生产路径零接触（Enabled 恒 false 即不建 pipe 不开线程）；供 E2E 发 ToggleMaximize/
            // GetWindowRect/GetWorkArea 精确断言"最大化 0px 间隙"。
            using var hookCts = new CancellationTokenSource();
            Task? hookTask = null;
            if (DshWeb.Win32.UiTestHook.Enabled)
            {
                hookTask = Task.Run(() => DshWeb.Win32.UiTestHook.RunAsync(
                    form.Handle, hookCts.Token,
                    onShutdown: () => form.BeginInvoke(() => form.Close()),
                    // [issue #28-2 回归] E2E 可经 TestHook 触发"版本徽标点击"的真实入口，
                    // 断言该弹窗打开/关闭不再打死进程（0xc0000005 现场）。
                    onShowVersionDialog: () => form.BeginInvoke(new Action(() => ctx.ShowVersionDialog(form))),
                    // 徽标命中矩形（生产 OnPaint 计算值 → 屏幕物理像素）：E2E 据此做真实鼠标点击。
                    // 经 UI 线程 Invoke 读取，避免跨线程读 Rectangle 结构撕裂。
                    versionBadgeRect: () =>
                    {
                        var bar = form.TitleBar;
                        if (bar is null || !form.IsHandleCreated) return null;
                        return (DshWeb.Win32.UiTestHook.VersionBadgeRect?)form.Invoke(
                            new Func<DshWeb.Win32.UiTestHook.VersionBadgeRect?>(() =>
                            {
                                var local = bar.GetVersionBadgeRect();
                                if (local.IsEmpty) return null;
                                var screen = bar.RectangleToScreen(local);
                                return new DshWeb.Win32.UiTestHook.VersionBadgeRect(
                                    screen.Left, screen.Top, screen.Right, screen.Bottom);
                            }));
                    }));
                Logger.Info($"ui-probe: test hook listening ({DshWeb.Win32.UiTestHook.PipeName(Environment.ProcessId)})");
            }

            Application.Run(form);
            hookCts.Cancel();
            if (hookTask is not null)
            {
                try { hookTask.Wait(TimeSpan.FromSeconds(1)); } catch { /* 退出清理不阻断 */ }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("ui-probe threw: " + ex.Message);
            return 2;
        }
    }
}