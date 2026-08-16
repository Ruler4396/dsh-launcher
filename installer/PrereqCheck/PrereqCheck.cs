// PrereqCheck.exe —— MSI 安装前置检查（Type-38 外部 exe，客户端进程内弹窗）。
// 安装向导在"下一步"进入安装前执行（product.wxs 的 CheckPrereq CA），检测：
//   1) .NET Desktop Runtime 10（壳为框架依赖单文件，必需）
//   2) Node.js 18+（dsh 服务运行必需，dsh 可 npx 拉取但 node 本体必须装）
// 任一缺失 → MessageBox 弹窗说明。当 .NET 缺失时可选择「自动安装」：
//   通过 winget 静默安装 .NET Desktop Runtime 10；无 winget 时退化为打开下载页。
//   Node 缺失保持原「去下载」行为（不提供自动安装）。
// 退出码：0 = 全部满足（继续安装）；2 = 缺失（中止安装）；3 = 用户取消/去下载。
// 说明：不检测 npx 全局 dsh——启动器自动 npx 拉取，只有 node 本体是硬前置。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;

internal static class Program
{
    private const string DotNetDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";
    private const string NodeDownloadUrl = "https://nodejs.org/";

    [STAThread]
    private static int Main()
    {
        var missing = new StringBuilder();
        // 测试开关：PREREQ_SIMULATE_MISSING=1 模拟两者缺失（验收弹窗/退出码，不弹真实检测）
        bool simulate = Environment.GetEnvironmentVariable("PREREQ_SIMULATE_MISSING") == "1";
        bool hasDotNet = !simulate && DetectDotNet10Desktop();
        bool hasNode = !simulate && DetectNode();

        if (!hasDotNet)
            missing.AppendLine("• .NET Desktop Runtime 10（壳程序运行必需）");
        if (!hasNode)
            missing.AppendLine("• Node.js 18 或更高版本（dsh 服务运行必需）");

        if (hasDotNet && hasNode)
            return 0; // 全部满足，继续安装

        // 静默安装（/qn 等无交互上下文）：弹窗无人可点会挂起安装——直接返回 2 中止，
        // 由安装日志说明原因（用户以 UI 向导安装时弹窗可见、可正常交互）。
        if (!Environment.UserInteractive)
            return 2;

        // 只有 .NET 缺失时才提供「自动安装」；仅 Node 缺失保持「去下载 / 取消」原行为。
        string message =
            "检测到缺少以下运行环境，安装后 dsh-launcher 无法正常启动：\n\n"
            + missing.ToString().TrimEnd('\n', '\r')
            + "\n\n请选择处理方式：自动安装缺失项、打开下载页手动安装，或取消本次安装。";
        var result = ShowPrereqDialog(message, showAutoInstall: !hasDotNet);

        if (result == MessageBoxResult.OK)
            return AutoInstallDotNetDesktopRuntime(); // 自动安装（仅 .NET）

        if (result == MessageBoxResult.Yes)
        {
            // 去下载：打开第一个缺失项的下载页；多个缺失时先引导最关键的
            OpenBrowser(!hasDotNet ? DotNetDownloadUrl : NodeDownloadUrl);
            return 3; // 用户去下载了，视为取消本次安装（重新运行向导即可）
        }
        return result == MessageBoxResult.Cancel ? 3 : 2; // 取消=3，否/超时=2
    }

    #region 自动安装 .NET Desktop Runtime 10（winget）

    /// <summary>「自动安装」动作：winget 静默安装 .NET Desktop Runtime 10。
    /// 返回 0=已满足继续安装；2=失败中止；3=用户取消失败/去下载。</summary>
    private static int AutoInstallDotNetDesktopRuntime()
    {
        // 1) 检测 winget；不存在 → 等价于「去下载」（打开 .NET 下载页，返回 3）
        if (!WingetAvailable())
        {
            OpenBrowser(DotNetDownloadUrl);
            return 3;
        }

        // 2) 先弹提示窗告知用户将要进行静默安装（此后同步等待，不设 60 秒超时）
        var notice = new MessageBoxWindow(
            "正在通过 winget 静默安装 .NET Desktop Runtime 10。\n\n"
            + "可能需要几分钟，期间可能弹出 UAC 管理员确认，请留意并同意。\n"
            + "安装完成后将自动返回安装向导继续。",
            new MessageBoxButtonDef("开始安装", MessageBoxResult.OK, true));
        notice.ShowDialogAndGetResult();

        // 3) 静默安装，10 分钟超时
        try
        {
            var psi = new ProcessStartInfo("winget",
                "install Microsoft.DotNet.DesktopRuntime.10 --silent --accept-package-agreements --accept-source-agreements")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null)
                return FailInstallPrompt("无法启动 winget 进程。");

            if (!proc.WaitForExit(600000)) // 10 分钟超时
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                proc.WaitForExit();
                return FailInstallPrompt("安装超时（超过 10 分钟）。");
            }

            // 4) 退出码 0 → 重新检测 .NET；已满足返回 0（继续安装）
            if (proc.ExitCode == 0 && DetectDotNet10Desktop())
                return 0;

            return FailInstallPrompt($"winget 退出码 {proc.ExitCode}。");
        }
        catch (Exception ex)
        {
            return FailInstallPrompt($"执行出错：{ex.Message}");
        }
    }

    /// <summary>winget 是否存在（where winget，5 秒超时）。</summary>
    private static bool WingetAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo("where", "winget")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return false;
            proc.WaitForExit(5000);
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    /// <summary>安装失败提示：说明失败原因并给出「去下载」路径。返回 2/3 语义：
    /// 去下载=3、取消=3、用户未响应（60 秒超时/否）=2 中止。</summary>
    private static int FailInstallPrompt(string detail)
    {
        var result = ShowTimeoutableDialog(
            "自动安装 .NET Desktop Runtime 10 失败——" + detail + "\n\n"
            + "仍可自行下载安装；安装完成后重新运行本安装向导即可。",
            System.Windows.MessageBoxResult.No, // 60 秒超时 → 中止（返回 2）
            new MessageBoxButtonDef("去下载(Y)", MessageBoxResult.Yes, true),
            new MessageBoxButtonDef("取消", MessageBoxResult.Cancel, false));

        if (result == MessageBoxResult.Yes)
        {
            OpenBrowser(DotNetDownloadUrl);
            return 3;
        }
        return result == MessageBoxResult.Cancel ? 3 : 2; // 取消=3，否/超时=2
    }

    #endregion

    #region 对话框

    /// <summary>弹出前置检查对话框。showAutoInstall 为 true 时才出现「自动安装」按钮；
    /// 仅 Node 缺失时保持「去下载 / 取消」两个按钮。60 秒无响应自动按「否」（中止安装）——
    /// 兜底静默/无人值守场景，避免安装进程无限挂起。</summary>
    private static MessageBoxResult ShowPrereqDialog(string message, bool showAutoInstall)
    {
        var buttons = new List<MessageBoxButtonDef>();
        if (showAutoInstall)
            buttons.Add(new MessageBoxButtonDef("自动安装(A)", MessageBoxResult.OK, true)); // 默认按钮
        buttons.Add(new MessageBoxButtonDef("去下载(Y)", MessageBoxResult.Yes, !showAutoInstall));
        buttons.Add(new MessageBoxButtonDef("取消", MessageBoxResult.Cancel, false));
        return ShowTimeoutableDialog(message, MessageBoxResult.No, buttons.ToArray());
    }

    /// <summary>带 60 秒超时自动关闭的对话框容器：超时按 timeoutDefault 关闭返回。</summary>
    private static MessageBoxResult ShowTimeoutableDialog(
        string message,
        System.Windows.MessageBoxResult timeoutDefault,
        params MessageBoxButtonDef[] buttons)
    {
        var autoClose = new System.Threading.Timer(_ => { }, null, Timeout.Infinite, Timeout.Infinite);
        var result = timeoutDefault;
        var dialog = new MessageBoxWindow(message, buttons);
        var closed = false;
        // 60 秒后自动关闭（按 timeoutDefault）
        autoClose = new System.Threading.Timer(_ =>
        {
            if (!closed) { closed = true; dialog.CloseWith(timeoutDefault); }
        }, null, TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
        try
        {
            result = dialog.ShowDialogAndGetResult();
        }
        finally
        {
            closed = true;
            autoClose.Dispose();
        }
        return result;
    }

    private static void OpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* 打开失败忽略 */ }
    }

    #endregion

    #region 检测

    /// <summary>.NET Desktop Runtime 10：检测 shared 目录下 Microsoft.WindowsDesktop.App 10.x
    /// （SDK 自带与独立安装器都会装到这里；SDK 机器无 InstalledVersions 注册表键）。</summary>
    private static bool DetectDotNet10Desktop()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(dir)) return false;
            foreach (var d in Directory.GetDirectories(dir))
            {
                var v = Path.GetFileName(d);
                if (v.StartsWith("10.", StringComparison.Ordinal)) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// <summary>Node.js：PATH 上能跑 node 且主版本 ≥ 18 即算有（覆盖任何安装方式）。
    /// 注册表 HKLM\SOFTWARE\Node.js\InstallPath 作为兜底（PATH 未刷新时）。</summary>
    private static bool DetectNode()
    {
        try
        {
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                try
                {
                    var exe = Path.Combine(p.Trim('"'), "node.exe");
                    if (File.Exists(exe))
                    {
                        var psi = new ProcessStartInfo(exe, "--version")
                        {
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true,
                        };
                        using var proc = Process.Start(psi);
                        if (proc is null) continue;
                        string outText = proc.StandardOutput.ReadToEnd().Trim();
                        proc.WaitForExit(3000);
                        if (TryParseMajor(outText, out int major) && major >= 18)
                            return true;
                    }
                }
                catch { /* 该 PATH 项不可用则跳过 */ }
            }
            // 注册表兜底（安装器写了 InstallPath 但当前会话 PATH 未刷新）
            foreach (var hive in new[] { "HKLM\\SOFTWARE\\Node.js", "HKLM\\SOFTWARE\\WOW6432Node\\Node.js" })
            {
                try
                {
                    var ip = Microsoft.Win32.Registry.GetValue(hive, "InstallPath", null) as string;
                    if (!string.IsNullOrWhiteSpace(ip) && File.Exists(Path.Combine(ip, "node.exe")))
                        return true;
                }
                catch { }
            }
            return false;
        }
        catch { return false; }
    }

    private static bool TryParseMajor(string version, out int major)
    {
        major = 0;
        if (string.IsNullOrWhiteSpace(version)) return false;
        var v = version.Trim();
        if (v.StartsWith("v", StringComparison.OrdinalIgnoreCase)) v = v.Substring(1);
        var dot = v.IndexOf('.');
        if (dot > 0) v = v.Substring(0, dot);
        return int.TryParse(v, out major);
    }

    #endregion
}

/// <summary>对话框按钮描述。</summary>
internal readonly struct MessageBoxButtonDef
{
    public readonly string Text;
    public readonly System.Windows.MessageBoxResult Result;
    public readonly bool IsDefault;

    public MessageBoxButtonDef(string text, System.Windows.MessageBoxResult result, bool isDefault)
    {
        Text = text;
        Result = result;
        IsDefault = isDefault;
    }
}

/// <summary>带超时自动关闭的 MessageBox 替代（WPF 窗口）：支持程序化按指定结果关闭，
/// 兜底静默/无人值守场景防挂起。按钮集合由调用方传入（顺序即显示顺序）。</summary>
internal sealed class MessageBoxWindow : System.Windows.Window
{
    private System.Windows.MessageBoxResult _result;
    private readonly System.Windows.Controls.WrapPanel _panel = new();

    public MessageBoxWindow(string message, params MessageBoxButtonDef[] buttons)
    {
        Title = "dsh-launcher 安装 - 缺少运行环境";
        Width = 460;
        SizeToContent = System.Windows.SizeToContent.Height;
        WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen;
        ResizeMode = System.Windows.ResizeMode.NoResize;
        ShowInTaskbar = true;

        var grid = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(16) };
        var text = new System.Windows.Controls.TextBlock
        {
            Text = message,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 16),
        };
        grid.Children.Add(text);

        _panel.HorizontalAlignment = System.Windows.HorizontalAlignment.Right;
        foreach (var b in buttons)
            AddButton(b.Text, b.Result, b.IsDefault);
        grid.Children.Add(_panel);
        Content = grid;
    }

    private void AddButton(string text, System.Windows.MessageBoxResult result, bool isDefault)
    {
        var btn = new System.Windows.Controls.Button
        {
            Content = text,
            MinWidth = 76,
            Margin = new System.Windows.Thickness(6, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = result == System.Windows.MessageBoxResult.Cancel,
        };
        btn.Click += (_, _) => CloseWith(result);
        _panel.Children.Add(btn);
    }

    /// <summary>以指定结果关闭（线程安全：任意线程可调用）。</summary>
    public void CloseWith(System.Windows.MessageBoxResult result)
    {
        _result = result;
        Dispatcher.Invoke(() => Close());
    }

    /// <summary>显示窗口并返回用户选择（或超时默认结果）。</summary>
    public System.Windows.MessageBoxResult ShowDialogAndGetResult()
    {
        ShowDialog();
        return _result;
    }
}
