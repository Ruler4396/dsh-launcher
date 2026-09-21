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

internal static class Program
{
    private const string DotNetDownloadUrl = "https://dotnet.microsoft.com/download/dotnet/10.0";
    private const string NodeDownloadUrl = "https://nodejs.org/";

    [STAThread]
    private static int Main(string[] args)
    {
        // 自检模式：`PrereqCheck.exe --selftest-node <结果文件>` —— 只跑 Node 判定并把结论写文件。
        // 本程序是 WinExe（GUI 子系统，无控制台），所以结果落文件而不是 stdout；
        // 真实机器上"fnm 装的 node 到底认不认"必须可测，不能只靠安装向导现场试。
        if (args.Length >= 2 && string.Equals(args[0], "--selftest-node", StringComparison.OrdinalIgnoreCase))
            return SelfTestNode(args[1]);

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
            return Continue; // 全部满足，继续安装

        // 静默安装（/qn 等无交互上下文）：没人能点弹窗。缺 .NET 时装完也起不来 → 返回 1602 干净退出
        // （MSI 报"用户已取消安装"，而不是旧实现返回 2 之后那句"Windows Installer 程序包有问题"）；
        // 只缺 Node 则放行——启动器首启会引导装便携版 Node，不该因此拒绝安装。
        if (!Environment.UserInteractive) return hasDotNet ? Continue : UserCancelled;

        string message =
            "检测到缺少以下运行环境：\n\n"
            + missing.ToString().TrimEnd('\n', '\r')
            + "\n\n【是】自动安装缺失项（winget 静默安装，可能需要几分钟，期间请留意 UAC 确认）"
              + "\n【否】仍然继续安装（缺 Node 时，启动器首次运行会引导下载便携版 Node）"
              + "\n【取消】退出安装向导，稍后自行处理";
        switch (Ask(message, MB_YESNOCANCEL | MB_WARNING))
        {
            case IDNO: return Continue;      // 用户明确选择"仍然继续"——这是决定，不是失败
            case IDCANCEL: return UserCancelled;
        }
        return AutoInstallMissing(!hasDotNet, !hasNode);
    }

    #region 退出码与对话框（不依赖任何托管 UI 栈）

    /// <summary>继续安装。</summary>
    private const int Continue = 0;
    /// <summary>ERROR_INSTALL_USEREXIT：MSI 据此干净地报"用户已取消安装"，而不是"程序包有问题"。</summary>
    private const int UserCancelled = 1602;

    private const uint MB_OK = 0x00000000;
    private const uint MB_YESNOCANCEL = 0x00000003;
    private const uint MB_YESNO = 0x00000004;
    private const uint MB_RETRYCANCEL = 0x00000005;
    private const uint MB_WARNING = 0x00000030;
    private const uint MB_INFORMATION = 0x00000040;
    private const uint MB_SETFOREGROUND = 0x00000100;
    private const uint MB_TASKMODAL = 0x00002000;
    private const uint MB_DEFBUTTON2 = 0x00000100;
    private const int IDOK = 1, IDCANCEL = 2, IDRETRY = 4, IDYES = 6, IDNO = 7;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    /// <summary>唯一的对话框出口：user32 MessageBoxW。
    /// 【为什么不用 WPF】本程序要判断的第一件事就是"这台机器有没有 .NET 运行时"，而旧实现自己却是
    /// 框架依赖的 WPF 程序：没装 .NET 10 的机器上，apphost 抢先弹"你必须安装 .NET Desktop Runtime"，
    /// 检查逻辑一行都没执行，自动安装分支永远走不到。MessageBoxW 是系统 DLL，不需要托管 UI 栈。</summary>
    private static int Ask(string message, uint type)
        => MessageBoxW(IntPtr.Zero, message, "dsh-launcher 安装 - 运行环境检查",
            type | MB_SETFOREGROUND | MB_TASKMODAL);

    #endregion

    #region 自动安装缺失项（winget）

    /// <summary>用户同意后把缺的都装上：.NET Desktop Runtime 10 与 Node.js LTS。
    /// 每项单独复检、单独报告；仍装不上的交给用户决定"继续/取消"，绝不返回随手非零码。</summary>
    private static int AutoInstallMissing(bool needDotNet, bool needNode)
    {
        var what = new List<string>();
        if (needDotNet) what.Add(".NET Desktop Runtime 10");
        if (needNode) what.Add("Node.js LTS");

        if (!WingetAvailable())
        {
            Ask("本机没有 winget（App Installer），无法自动安装。\n\n【重试】装好 App Installer 后再试一次"
                + "\n【取消】返回后请手动安装：\n  .NET: " + DotNetDownloadUrl + "\n  Node: " + NodeDownloadUrl,
                MB_RETRYCANCEL | MB_WARNING | MB_DEFBUTTON2);
            if (!WingetAvailable())
            {
                OpenBrowser(needDotNet ? DotNetDownloadUrl : NodeDownloadUrl);
                return UserCancelled;
            }
        }
        Ask("正在通过 winget 静默安装：" + string.Join(" 与 ", what)
            + "\n\n可能需要几分钟，期间可能弹出 UAC 管理员确认，请留意并同意。",
            MB_OK | MB_INFORMATION);

        var stillMissing = new StringBuilder();
        if (needDotNet)
        {
            if (!InstallViaWinget("Microsoft.DotNet.DesktopRuntime.10", out var dotNetDetail))
                stillMissing.AppendLine("• .NET Desktop Runtime 10 — " + dotNetDetail);
            else if (!DetectDotNet10Desktop())
                stillMissing.AppendLine("• .NET Desktop Runtime 10 — winget 报成功，但复检没在 "
                    + @"%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App 下找到 10.x 目录");
        }
        if (needNode)
        {
            if (!InstallViaWinget("OpenJS.NodeJS.LTS", out var nodeDetail))
                stillMissing.AppendLine("• Node.js LTS — " + nodeDetail);
            else if (!DetectNode())
                stillMissing.AppendLine("• Node.js LTS — winget 报成功，但复检跑不到主版本 ≥ 18 的 node"
                    + "（新写入的 PATH 可能要重开资源管理器/重新登录才生效；启动器首启也会引导装便携版）");
        }
        if (stillMissing.Length == 0) return Continue;

        var r = Ask("以下项自动安装未成功：\n\n" + stillMissing.ToString().TrimEnd('\n', '\r')
            + "\n\n【是】仍然继续安装（缺 Node 时启动器首启会引导下载便携版 Node；"
              + "缺 .NET 时壳起不来，装好后重开本向导即可）\n【否】取消本次安装",
            MB_YESNO | MB_WARNING | MB_DEFBUTTON2);
        return r == IDYES ? Continue : UserCancelled;
    }

    /// <summary>winget 静默安装一个包；返回是否成功，失败原因经 detail 带回。</summary>
    private static bool InstallViaWinget(string packageId, out string detail)
    {
        detail = "";
        try
        {
            var psi = new ProcessStartInfo("winget",
                "install " + packageId + " --silent --accept-package-agreements --accept-source-agreements")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) { detail = "无法启动 winget 进程"; return false; }
            string stdout = proc.StandardOutput.ReadToEnd();
            if (!proc.WaitForExit(600000)) // 10 分钟超时
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* 已退出则忽略 */ }
                proc.WaitForExit();
                detail = "安装超时（超过 10 分钟）";
                return false;
            }
            if (proc.ExitCode != 0)
            {
                detail = "winget 退出码 " + proc.ExitCode
                    + (string.IsNullOrWhiteSpace(stdout) ? "" : "：" + stdout.Trim().Split('\n')[0]);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
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

    /// <summary>Node.js 检测（带证据版）：返回"找到了哪个 node、什么版本"。</summary>
    private static (bool Found, string Path, string Version) DetectNodeDetailed()
    {
        foreach (var cand in NodeCandidatePaths())
        {
            try
            {
                if (!File.Exists(cand)) continue;
                var psi = new ProcessStartInfo(cand, "--version")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var proc = Process.Start(psi);
                if (proc is null) continue;
                var outText = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(3000);
                if (TryParseMajor(outText, out int major) && major >= 18)
                    return (true, cand, outText);
            }
            catch { /* 该候选不可执行/无权限：下一个 */ }
        }
        return (false, "", "");
    }

    private static bool DetectNode() => DetectNodeDetailed().Found;

    /// <summary>把"只有 IO 才能知道的事实"（fnm 装了哪些版本）取出来，交给纯函数枚举候选。</summary>
    private static List<string> NodeCandidatePaths()
    {
        var fnmRoot = Path.Combine(
            Environment.GetEnvironmentVariable("FNM_DIR")
            ?? Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "fnm"),
            "node-versions");
        var versionDirs = Array.Empty<string>();
        try { if (Directory.Exists(fnmRoot)) versionDirs = Directory.GetDirectories(fnmRoot); }
        catch { /* 无权限/竞态删除：别名那几条仍然会试 */ }
        return NodeLocator.EnumerateCandidates(
            Environment.GetEnvironmentVariable("PATH"),
            ReadRegistryPath("Machine"),
            ReadRegistryPath("User"),
            Environment.GetEnvironmentVariable,
            ReadRegistryNodeInstallPath(),
            versionDirs);
    }

    /// <summary>自检模式实现：把判定结论（含命中的 node 路径与版本）写进指定文件。</summary>
    private static int SelfTestNode(string outFile)
    {
        var r = DetectNodeDetailed();
        var text = $"found={(r.Found ? 1 : 0)}\r\nnode={r.Path}\r\nversion={r.Version}\r\n"
                   + $"candidates={NodeCandidatePaths().Count}";
        try { File.WriteAllText(outFile, text, Encoding.UTF8); } catch { return 2; }
        return r.Found ? 0 : 2;
    }

    /// <summary>读注册表里的持久 PATH（Machine / User）。msiexec 的进程 PATH 就是这两份拼出来的，
    /// 但"刚装完还没刷新环境变量"的场景下进程 PATH 可能滞后，所以两份都单独扫一遍。</summary>
    private static string ReadRegistryPath(string scope)
    {
        try
        {
            var key = scope == "Machine"
                ? @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment"
                : @"Environment";
            return Microsoft.Win32.Registry.GetValue(
                (scope == "Machine" ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER") + "\\" + key,
                "Path", "") as string ?? "";
        }
        catch { return ""; }
    }

    /// <summary>官方安装器写的 HKLM\SOFTWARE\Node.js\InstallPath（仅作候选之一，**必须过版本**）。</summary>
    private static string ReadRegistryNodeInstallPath()
    {
        foreach (var hive in new[] { "HKLM\\SOFTWARE\\Node.js", "HKLM\\SOFTWARE\\WOW6432Node\\Node.js" })
        {
            try
            {
                var ip = Microsoft.Win32.Registry.GetValue(hive, "InstallPath", null) as string;
                if (!string.IsNullOrWhiteSpace(ip)) return ip;
            }
            catch { }
        }
        return "";
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

    #region Node 候选路径枚举（纯函数，无 IO）

    /// <summary>
    /// 枚举"机器上可能是 node.exe 的地方"。
    ///
    /// 【为什么要认识版本管理器】旧实现只看两件事：进程 PATH 里有没有 node.exe、
    /// HKLM\SOFTWARE\Node.js\InstallPath 在不在。用 fnm / nvm / volta / scoop 装 node 的人，
    /// node 既不在持久 PATH 里（那些工具是**每个 shell 会话**用 `fnm env` 注入一个软链目录），
    /// 也不写官方安装器的注册表键 —— 于是安装向导对着机器上真实可用的 node 说"你没有 Node.js"，
    /// 直接把人拦在 MSI 门外（本机实测：node v24.21.0 装在 %APPDATA%\fnm 下，注册表 PATH 里
    /// 一个 node.exe 都没有）。反过来，旧实现那条注册表兜底只判 <c>File.Exists</c> 不判版本，
    /// 残留的 <c>D:\node</c> 哪怕是个 node 12 也会放行 —— 一个假阴性配一个假阳性。
    ///
    /// 本函数只产候选、不碰文件系统（IO 与版本判定留给调用方），因此可被逐条枚举核对。
    /// </summary>
    internal static class NodeLocator
    {
        public static List<string> EnumerateCandidates(
            string? processPath, string? machinePath, string? userPath,
            Func<string, string?> env, string registryInstallPath,
            IReadOnlyList<string>? fnmVersionDirs = null)
        {
            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path)) return;
                var trimmed = path.Trim('"');
                if (trimmed.Length == 0) return;
                if (seen.Add(trimmed)) candidates.Add(trimmed);
            }

            // 1) 三份 PATH：进程（可能已被版本管理器注入）、机器、用户（持久真相）
            foreach (var scope in new[] { processPath, machinePath, userPath })
                foreach (var dir in (scope ?? "").Split(';'))
                    if (!string.IsNullOrWhiteSpace(dir)) Add(Path.Combine(dir.Trim(), "node.exe"));

            // 2) fnm：FNM_DIR 优先，退到 %APPDATA%\fnm；别名目录在前，实体版本目录按名倒序
            var fnm = FirstNonEmpty(env("FNM_DIR"), Path.Combine(AppData(env), "fnm"));
            foreach (var alias in new[] { "default", "lts-latest", "lts" })
                Add(Path.Combine(fnm, "aliases", alias, "node.exe"));
            Add(Path.Combine(fnm, "node-versions", "node.exe")); // 少数布局直接放根上
            // 没设过别名（`fnm install` 后没 `fnm default`）时，别名目录是空的——
            // 所以调用方还要把 node-versions\<ver>\installation 这份实账递进来。
            foreach (var dir in fnmVersionDirs ?? Array.Empty<string>())
                Add(Path.Combine(dir, "installation", "node.exe"));

            // 3) nvm-windows：NVM_SYMLINK 是当前生效版本的软链
            Add(Path.Combine(FirstNonEmpty(env("NVM_SYMLINK"),
                Path.Combine(AppData(env), "nvm")), "node.exe"));

            // 4) volta / scoop / chocolatey：各自固定的 current 目录
            Add(Path.Combine(FirstNonEmpty(env("VOLTA_HOME"),
                Path.Combine(LocalAppData(env), "Volta")), "bin", "node.exe"));
            Add(Path.Combine(env("USERPROFILE") ?? "", "scoop", "apps", "nodejs", "current", "node.exe"));
            Add(Path.Combine(env("ProgramData") ?? @"C:\ProgramData",
                "chocolatey", "lib", "nodejs", "tools", "node.exe"));

            // 5) 官方安装器的注册表键（交给调用方判版本，不再"存在即通过"）
            if (!string.IsNullOrWhiteSpace(registryInstallPath))
                Add(Path.Combine(registryInstallPath, "node.exe"));

            return candidates;
        }

        private static string FirstNonEmpty(params string?[] values)
        {
            foreach (var v in values) if (!string.IsNullOrWhiteSpace(v)) return v;
            return "";
        }

        private static string AppData(Func<string, string?> env)
            => env("APPDATA") ?? Path.Combine(env("USERPROFILE") ?? "", "AppData", "Roaming");

        private static string LocalAppData(Func<string, string?> env)
            => env("LOCALAPPDATA") ?? Path.Combine(env("USERPROFILE") ?? "", "AppData", "Local");
    }

    #endregion
}