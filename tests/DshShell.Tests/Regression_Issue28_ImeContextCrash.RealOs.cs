using System.Runtime.InteropServices;
using System.Windows.Forms;
using DshWeb.Win32;
using DshWeb.Windows;
using Xunit;
using Xunit.Abstractions;

namespace DshShell.Tests;

/// <summary>
/// 【Regression_Issue28_ImeContextCrash】点标题栏版本徽标 → 启动器卡死/闪退 的零 Mock 回归测试。
///
/// 事故（issue #28-2，用户报告"点击左上角版本号会卡死无法关闭然后闪退"）：
/// 事件日志 Application Error 1000（DshWeb.exe，异常码 0xc0000005，故障模块 coreclr.dll）
/// + .NET Runtime 1026，托管栈两条路径均以 <c>ImmSetOpenStatus</c> 结尾：
///   A. 弹窗打开：<c>Label.WndProc WM_SETFOCUS → Control.WmSetFocus → UpdateImeContextMode →
///      ImeContext.SetImeStatus(Disable) → ImeContext.Disable → SetOpenStatus → ImmSetOpenStatus</c>；
///   B. 弹窗关闭：<c>Label.WndProc WM_KILLFOCUS → Control.WmImeKillFocus → SetImeStatus →
///      SetOpenStatus → ImmSetOpenStatus</c>。
/// 两次崩溃栈都经过 <c>Program.ShowVersionInfoDialog ← CustomTitleBar.OnMouseDown</c>，
/// 且 14:17:20 的"版本弹窗已成功拉取最新版本"日志与 14:17:24 的崩溃时间戳严格吻合。
/// 机理：WinForms 按 ImeMode 调 <c>ImeContext</c> 落地 IME 状态，而第三方 IME（本机实测
/// 手心输入法 PalmInput 3.2.9）会给壳自有 WinForms 窗口返回不可用 HIMC，
/// <c>ImmSetOpenStatus</c> 随即 native AV —— 托管层不可 catch，进程直接消失。
///
/// 修复：<see cref="ImeContextGuard"/> 在句柄创建时 <c>ImmAssociateContext(hwnd, NULL)</c>，
/// 使 WinForms 侧 <c>ImeContext.GetImeMode</c> 恒为 Disable、<c>IsOpen</c> 恒 false，
/// 上面两条路径在调用 <c>ImmSetOpenStatus</c> 之前就短路。
///
/// 本测试三条用例（全部真实 OS 调用，零 Mock，Category=RealOS）：
///   1. <see cref="RealOs_ImeGuard_DisassociatesContext_AndWinFormsSeesDisable"/>：
///      显式给窗口关联一个真实 IME 上下文（模拟第三方 IME 挂上下文），断言护栏真实解绑，
///      且 WinForms 观察到的 ImeMode 变为 Disable（= SetImeStatus 短路的充分条件）；
///   2. <see cref="RealOs_VersionDialog_AllShellWindowsHaveNoImeContext"/>：
///      真实版本信息窗的窗体与全部子控件句柄上 <c>ImmGetContext == NULL</c>；
///   3. <see cref="RealOs_VersionDialog_ShowCloseCycles_Survive"/>：
///      真实 ShowDialog 打开→关闭多轮（崩溃现场 A/B 两条路径），进程必须存活。
/// </summary>
[Collection("RealOS")]
[Trait("Category", "RealOS")]
public class Regression_Issue28_ImeContextCrash_RealOs
{
    private readonly ITestOutputHelper _out;
    public Regression_Issue28_ImeContextCrash_RealOs(ITestOutputHelper o) => _out = o;

    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern IntPtr ImmGetContext(IntPtr hWnd);

    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern bool ImmReleaseContext(IntPtr hWnd, IntPtr hIMC);

    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern IntPtr ImmCreateContext();

    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern bool ImmDestroyContext(IntPtr hIMC);

    [DllImport("imm32.dll", ExactSpelling = true)]
    private static extern IntPtr ImmAssociateContext(IntPtr hWnd, IntPtr hIMC);

    private static bool HasImeContext(IntPtr hwnd)
    {
        var himc = ImmGetContext(hwnd);
        if (himc == IntPtr.Zero) return false;
        ImmReleaseContext(hwnd, himc);
        return true;
    }

    /// <summary>
    /// WinForms 侧"惰性"IME 模式——**不会**落到 <c>ImmSetOpenStatus</c> 的两种取值：
    /// · <see cref="ImeMode.Disable"/>：窗口上下文已解绑（<c>ImmGetContext == NULL</c>）⇒
    ///   <c>ImeContext.Disable()</c> 的 <c>IsOpen()</c> 恒 false，只做一次 <c>ImmAssociateContext(NULL)</c>；
    /// · <see cref="ImeMode.Inherit"/>：当前输入语言表不含 IME（无中日韩输入法的环境，如 GitHub
    ///   runner / 纯英文机器）时 <c>ImeContext.GetImeMode</c> 的首个分支直接返回它，而
    ///   <c>SetImeStatus</c> 对 Inherit 立即 return。
    /// 本机实测（zh-CN 输入语言）：同一窗口 <c>GetImeMode == Close</c>；把线程输入语言切到 en-US 后
    /// 变 <c>Inherit</c> —— 断言必须接受这两种"惰性"取值，真正的判别力在
    /// <c>ImmGetContext(hwnd) == NULL</c>（护栏是否生效）。
    /// </summary>
    private static bool IsInertImeMode(ImeMode mode) => mode is ImeMode.Disable or ImeMode.Inherit;

    /// <summary>
    /// 测试专用开关：<c>DSH_TEST_IME_LANG</c>（如 <c>en-US</c>）强制本 STA 线程的输入语言，
    /// 用于在开发机（中文输入法）上**复现 CI runner 的"无中日韩输入法"分支**
    /// （该分支下 <c>GetImeMode</c> 返回 Inherit 而非 Disable——2026-09-14 CI 首次红即此因）。
    /// 不设该变量时零副作用。
    /// </summary>
    private static void ApplyForcedInputLanguage()
    {
        var lang = Environment.GetEnvironmentVariable("DSH_TEST_IME_LANG");
        if (string.IsNullOrWhiteSpace(lang)) return;
        try
        {
            InputLanguage.CurrentInputLanguage =
                InputLanguage.FromCulture(new System.Globalization.CultureInfo(lang));
            Console.WriteLine($"[ime-test] forced input language = {InputLanguage.CurrentInputLanguage.Culture.Name}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ime-test] force input language failed ({lang}): {ex.Message}");
        }
    }

    /// <summary>在独立 STA 线程上跑一段真实 WinForms 代码（句柄/消息泵需要 STA）。</summary>
    private static void RunSta(Action body, string what, int timeoutSeconds = 60)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(timeoutSeconds)), $"{what}：STA 场景超时（{timeoutSeconds}s）");
        if (failure is not null) throw new Xunit.Sdk.XunitException($"{what} 抛异常：{failure}");
    }

    [Fact]
    public void RealOs_ImeGuard_DisassociatesContext_AndWinFormsSeesDisable()
    {
        RunSta(() =>
        {
            ApplyForcedInputLanguage();
            using var form = new Form { Text = "ime-guard-probe", ShowInTaskbar = false };
            var hwnd = form.Handle;

            // 前置：给窗口显式关联一个真实 IME 上下文（第三方 IME 给壳窗口挂上下文的等价状态）。
            // 无 IME 的环境（CI runner 无中日韩输入法）ImmCreateContext 可能返回 0：此时无法构造
            // "危险态"，但护栏不变量仍须成立——显式留痕走弱化断言，绝不静默通过。
            var himc = ImmCreateContext();
            if (himc == IntPtr.Zero)
            {
                _out.WriteLine("SKIP 危险态构造：本机 ImmCreateContext 返回 0（无可用 IME 环境）——仍断言护栏后上下文为空");
                ImeContextGuard.Harden(form);
                Assert.False(HasImeContext(hwnd));
                Assert.True(IsInertImeMode(ImeContext.GetImeMode(hwnd)));
                return;
            }
            try
            {
                ImmAssociateContext(hwnd, himc);
                Assert.True(HasImeContext(hwnd),
                    "前置条件失败：ImmAssociateContext 之后窗口应持有 IME 上下文");
                var modeBefore = ImeContext.GetImeMode(hwnd);
                _out.WriteLine($"probe hwnd=0x{hwnd.ToInt64():X} himc=0x{himc.ToInt64():X} mode={modeBefore}");

                // 危险态：本机（中文输入法活跃）实测护栏前 WinForms 观察到 ImeMode.Close
                // ——这正是 WmImeKillFocus → SetImeStatus(Close) → SetOpenStatus → ImmSetOpenStatus
                // 的触发条件（崩溃栈 B）。无中日韩输入法的环境返回 Disable/Inherit，跳过该断言。
                var dangerState = modeBefore is not (ImeMode.Disable or ImeMode.Inherit);

                // 护栏：解绑该窗口的 IME 上下文 → WinForms 侧再也拿到不出一条会落到
                // ImmSetOpenStatus 的模式（本机语义为 Disable；无 IME 环境为 Inherit）。
                ImeContextGuard.Harden(form);
                Assert.False(HasImeContext(hwnd),
                    "ImeContextGuard.Harden 之后窗口不得再持有 IME 上下文（否则 WinForms 仍会走 ImmSetOpenStatus）");
                var modeAfter = ImeContext.GetImeMode(hwnd);
                Assert.True(IsInertImeMode(modeAfter),
                    $"护栏后 WinForms 观察到 ImeMode.{modeAfter}——仍属会驱动 SetImeStatus 的活跃模式");
                if (dangerState)
                    _out.WriteLine($"危险态已复现（护栏前 {modeBefore}）→ 护栏后 {modeAfter}：WinForms 的 SetImeStatus 路径不可达");
            }
            finally
            {
                ImmAssociateContext(hwnd, IntPtr.Zero);
                ImmDestroyContext(himc);
            }
        }, "IME 护栏机制验证");
    }

    [Fact]
    public void RealOs_ImeGuard_SurvivesFocusAndActivation()
    {
        // 关键风险点：第三方 IME 可能在窗口获得焦点时重新关联上下文（那样护栏就形同虚设）。
        // 本用例真实 Show + 真实 Focus（崩溃发生的时刻），断言护栏之后 IME 上下文依然为空，
        // 即 WinForms 侧的 ImeContext 路径在整个焦点变化周期内都保持短路。
        RunSta(() =>
        {
            ApplyForcedInputLanguage();
            using var form = new Form { Text = "ime-guard-focus", ShowInTaskbar = false };
            var button = new Button { Text = "focus me", Location = new System.Drawing.Point(10, 10) };
            form.Controls.Add(button);
            _ = form.Handle;

            ImeContextGuard.Harden(form);

            form.Shown += (_, _) =>
            {
                button.Focus();
                Application.DoEvents();
            };
            form.Show();
            Application.DoEvents();
            button.Focus();
            Application.DoEvents();

            var handles = new List<(string Tag, IntPtr Handle)> { ("窗体", form.Handle), ("按钮", button.Handle) };
            Collect(form, handles);
            foreach (var (tag, handle) in handles)
            {
                _out.WriteLine($"{tag} hwnd=0x{handle.ToInt64():X} himc={(HasImeContext(handle) ? "NON-NULL" : "null")} "
                    + $"mode={ImeContext.GetImeMode(handle)}");
                Assert.False(HasImeContext(handle), $"{tag} 在获得焦点后又被 IME 关联了上下文（护栏未覆盖焦点路径）");
                var mode = ImeContext.GetImeMode(handle);
                Assert.True(IsInertImeMode(mode), $"{tag} 焦点变化后 WinForms 观察到 ImeMode.{mode}（会驱动 SetImeStatus）");
            }
            form.Close();
        }, "IME 护栏焦点路径验证");
    }

    [Fact]
    public void RealOs_VersionDialog_AllShellWindowsHaveNoImeContext()
    {
        RunSta(() =>
        {
            ApplyForcedInputLanguage();
            using var dialog = new VersionInfoDialog("0.1.5-rc.1", "0.4.5", dark: true);
            var handles = new List<(string Tag, IntPtr Handle)> { ("版本信息窗", dialog.Handle) };
            Collect(dialog, handles);

            _out.WriteLine($"版本信息窗句柄 {handles.Count} 个：{string.Join(", ", handles.Select(h => $"{h.Tag}=0x{h.Handle.ToInt64():X}"))}");
            Assert.True(handles.Count > 1, "版本信息窗应至少含标题栏/文本/按钮子控件（句柄枚举失败？）");

            foreach (var (tag, handle) in handles)
            {
                Assert.False(HasImeContext(handle),
                    $"{tag} 仍持有 IME 上下文 → WinForms 焦点变化时会再次调用 ImmSetOpenStatus（崩溃复发）");
                var mode = ImeContext.GetImeMode(handle);
                Assert.True(IsInertImeMode(mode), $"{tag} WinForms 观察到 ImeMode.{mode}（会驱动 SetImeStatus）");
            }
        }, "版本信息窗 IME 上下文检查");
    }

    [Fact]
    public void RealOs_VersionDialog_ShowCloseCycles_Survive()
    {
        // 崩溃现场：ShowDialog 激活（路径 A）+ 模态循环结束失焦（路径 B）。
        // 三轮开关，任一轮若触发 native AV，本测试进程会直接死亡（0xc0000005），测试必然报错。
        RunSta(() =>
        {
            for (var round = 1; round <= 3; round++)
            {
                using var dialog = new VersionInfoDialog("0.1.5-rc.1", "0.4.5", dark: round % 2 == 0);
                dialog.Shown += (_, _) => dialog.BeginInvoke(new Action(dialog.Close));
                dialog.ShowDialog();
                _out.WriteLine($"round {round}: 版本信息窗已打开并关闭，进程存活");
            }
        }, "版本信息窗 打开/关闭 三轮", timeoutSeconds: 120);
    }

    private static void Collect(Control parent, List<(string Tag, IntPtr Handle)> sink)
    {
        foreach (Control child in parent.Controls)
        {
            // 故意读 .Handle（惰性创建）：真实显示流程同样在句柄创建时才走 WinForms 的挂载路径，
            // 护栏必须覆盖"构造后才创建句柄"的控件。
            sink.Add(($"{parent.GetType().Name}/{child.GetType().Name}", child.Handle));
            Collect(child, sink);
        }
    }
}


