using System.Diagnostics;
using System.Drawing;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace DshWeb;

internal static class Program
{
    private const string DefaultUrl = "http://127.0.0.1:3080";
    private const int SW_RESTORE = 9;

    /// 目标服务地址/端口：默认 3080。优先级：DSH_WEB_URL（视为外部托管，壳不拉起服务）→
    /// DSH_WEB_PORT（壳按此端口托管拉起服务，3080 被占用时可用）→ 默认 3080。
    private static readonly (string Url, int Port) Target = ResolveTarget();

    /// <summary>解析目标地址/端口；空值/非法值回退默认 3080。</summary>
    private static (string Url, int Port) ResolveTarget()
    {
        var envUrl = Environment.GetEnvironmentVariable("DSH_WEB_URL");
        if (!string.IsNullOrWhiteSpace(envUrl))
        {
            try
            {
                var uri = new Uri(envUrl, UriKind.Absolute);
                if (uri.Scheme is "http" or "https")
                    return (uri.GetLeftPart(UriPartial.Path).TrimEnd('/'), uri.Port);
            }
            catch
            {
                // 非法输入回退默认
            }
        }
        var envPort = Environment.GetEnvironmentVariable("DSH_WEB_PORT");
        if (int.TryParse(envPort, out var port) && port is > 0 and < 65536)
            return ($"http://127.0.0.1:{port}", port);
        return ("http://127.0.0.1:3080", 3080);
    }

    /// 设置 DSH_WEB_URL 时视为“外部托管服务”，壳不再自动拉起 dsh（DSH_WEB_PORT 则相反：壳托管拉起）。
    private static readonly bool ServerManagedExternally =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSH_WEB_URL"));

    /// 渲染进程崩溃自动重载的节流时间戳（避免崩溃死循环）。
    /// 崩溃事件可能在浏览器进程线程上触发，跨线程读写一律经 Interlocked（v0.3.0）。
    private static long _lastReloadTick;

    /// 主窗口的 WebView2 控件（托盘恢复窗口时需要检查/恢复渲染）。
    private static WebView2? _mainWeb;

    /// WebView2 渲染/GPU 进程崩溃标志：窗口隐藏期间进程崩溃后，恢复窗口时必须重载页面
    /// 兜底，否则显示出来是白屏（隐藏状态下 Reload 无效，只能在窗口可见后执行）。
    private static bool _webviewRecoveryNeeded;

    /// 上次隐藏窗口的时间戳：长隐藏（>5 分钟）期间渲染进程可能被系统回收（无崩溃事件），
    /// 恢复窗口时强制重载页面兜底，避免白屏。
    private static DateTime _hiddenSince = DateTime.MinValue;

    /// 本次会话是否由壳拉起了 dsh 服务（决定"跟随窗口/托盘退出"时是否停它；外部托管/用户手动起的服务不动）。
    private static bool _serviceStartedByShell;

    /// 本次会话壳托管服务的监听 PID（内存缓存，关窗时直接使用，避免再跑 netstat 造成卡顿）。
    private static int _servicePid;

    /// 托盘图标（v0.3.0 起按需显示：仅在装了 lifetime 插件或存在待通知更新时创建，见 EnsureTrayIcon）。
    private static NotifyIcon? _trayIcon;

    /// 托盘"退出"请求（允许 FormClosing 真正关闭，而不是再次隐藏到托盘）。
    private static bool _trayExitRequested;

    /// <summary>
    /// dsh 主目录（与 dsh 生态一致，向其他插件学习：配置不散落在 %LOCALAPPDATA%，
    /// 跟着 dsh 走，卸载/迁移时一并处理）：DSH_HOME 环境变量，未设置时 ~/.dsh。
    /// </summary>
    private static string DshHomeDir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("DSH_HOME");
            if (!string.IsNullOrWhiteSpace(env)) return env;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        }
    }

    /// <summary>壳的数据目录（settings.json / 统一日志 / service-pid 等）：DSH_HOME\dsh-launcher。</summary>
    private static string DataDir => Path.Combine(DshHomeDir, "dsh-launcher");

    /// <summary>统一日志路径（v0.3.0 单一日志文件）：壳的 JSON Lines 与 dsh 服务输出同文件。
    /// 通过 DSH_LOG 环境变量传给 start-dsh.vbs（追加写入）。</summary>
    private static string UnifiedLogPath => Path.Combine(DataDir, "dsh.log");

    /// <summary>
    /// 启动时迁移旧版数据（%LOCALAPPDATA%\dsh-launcher → DSH_HOME\dsh-launcher）：
    /// settings.json 保留用户的选择；旧文件迁移后删除，避免卸载后残留。
    /// 旧版曾把 WebView2 用户数据放 %LOCALAPPDATA%\DshWeb（标准位置，保持不动）。
    /// </summary>
    private static void MigrateLegacyData()
    {
        try
        {
            var legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-launcher");
            var newDir = DataDir;
            if (!Directory.Exists(legacyDir) || string.Equals(legacyDir, newDir, StringComparison.OrdinalIgnoreCase))
                return;
            Directory.CreateDirectory(newDir);

            var legacySettings = Path.Combine(legacyDir, "settings.json");
            var newSettings = Path.Combine(newDir, "settings.json");
            if (File.Exists(legacySettings) && !File.Exists(newSettings))
            {
                try { File.Copy(legacySettings, newSettings); } catch { /* 复制失败保留旧文件 */ }
            }

            // 清理旧目录（shell.log / service-pid 等历史文件一并删除，无残留）
            foreach (var file in Directory.GetFiles(legacyDir))
            {
                try { File.Delete(file); } catch { /* 被占用则跳过 */ }
            }
            try { if (Directory.GetFiles(legacyDir).Length == 0) Directory.Delete(legacyDir); } catch { }
        }
        catch
        {
            // 迁移失败不影响启动
        }
    }

    /// <summary>清理卸载后 ProgramData 范围外的空目录残留（清理项 1）：安装用 FolderPicker
    /// 会在 C:\ProgramData\dsh-launcher 创建中转文件（picked.txt），卸载不删该目录；目录为空
    /// （无其他用户残留文件）时顺手清掉。非空（如被其他软件占用）则不动。</summary>
    private static void CleanupProgramDataResidue()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "dsh-launcher");
            if (!Directory.Exists(dir)) return;
            // 只清理中转文件与空目录；不删除任何非本产品文件
            var picked = Path.Combine(dir, "picked.txt");
            if (File.Exists(picked))
            {
                try { File.Delete(picked); } catch { /* 占用则跳过 */ }
            }
            try
            {
                if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                    Directory.Delete(dir);
            }
            catch { /* 删除失败（可能有其他用户文件/占用）不动 */ }
        }
        catch
        {
            // 清理失败不影响启动
        }
    }

    /// <summary>自启落地（修复 0.2.3 issue：per-machine 提权安装写 HKCU Run 不可靠，
    /// 勾选了也不落到任何真实用户 hive）。MSI 勾选自启时只在 HKLM 写机器级意图标志
    /// （AutoStartWanted=1，随卸载自动清除），本方法在壳启动时读标志、以当前用户
    /// 身份补写 HKCU Run——用户上下文写 HKCU 100% 可靠，交互/静默安装均覆盖；也顺带
    /// 解决"其他管理员过 UAC 时自启写错 hive"的问题（谁先用壳，自启就落在谁头上）。
    /// 升级/自定义目录导致的路径变化自动更新；用户跑了 uninstall-autostart.cmd
    /// （会同时清 HKLM 标志）则不再自愈。</summary>
    private static void EnsureAutoStartRequested()
    {
        try
        {
            var wanted = false;
            try
            {
                using var flagKey = Registry.LocalMachine.OpenSubKey(@"Software\dsh-launcher");
                wanted = flagKey?.GetValue("AutoStartWanted") is int v && v == 1;
            }
            catch { /* 读不到按无标志处理（便携版/未勾选） */ }
            if (!wanted) return;

            // 自启直接拉起壳（登录即见窗口）：壳自行探测/拉起 dsh 服务（无服务时
            // 自己跑 start-dsh.vbs），不再走 wscript 静默自启服务。值格式与安装器
            // SetAutoStartFlag CA 一致；旧版 wscript+vbs 格式的存量值会被下面重写
            // 为新格式（自动迁移）。
            var exe = Path.Combine(AppContext.BaseDirectory, "DshWeb.exe");
            if (!File.Exists(exe)) return; // 自身路径异常时不写
            var expected = "\"" + exe + "\"";

            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            var cur = run.GetValue("dsh-launcher") as string;
            if (string.Equals(cur, expected, StringComparison.OrdinalIgnoreCase)) return;
            run.SetValue("dsh-launcher", expected, RegistryValueKind.String);
            Trace("autostart: " + (cur is null ? "created" : "updated") + " HKCU Run entry (HKLM AutoStartWanted=1)");
        }
        catch (Exception ex)
        {
            Trace("autostart ensure failed: " + ex);
        }
    }

    /// <summary>
    /// 启动轨迹日志：v0.3.0 起统一走 <see cref="Logger"/>（DSH_HOME\dsh-launcher\dsh.log，JSON Lines）。
    /// 保留 Trace 名称以最小化调用点改动；写失败静默（日志不影响启动）。
    /// </summary>
    private static void Trace(string message) => Logger.Info(message);

    /// 共享 WebView2 环境：主窗口与插件弹窗共用同一用户数据目录与浏览器参数。
    private static CoreWebView2Environment? _sharedEnvironment;

    /// <summary>
    /// 创建（或复用）共享 WebView2 环境。
    /// AdditionalBrowserArguments 放行无手势自动播放：WebView2 在当前 SDK 中不会为
    /// Autoplay 触发 PermissionRequested 事件（直接静默拒绝），
    /// --autoplay-policy=no-user-gesture-required 是唯一可用的开关（声音类插件依赖）。
    /// </summary>
    private static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync(string userDataFolder)
    {
        if (_sharedEnvironment is null)
        {
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
            };
            // 内部续延只做字段赋值/返回，无 UI 依赖：ConfigureAwait(false) 避免无谓的
            // UI 线程回跳（v0.3.0 后台代码纪律）；调用方 InitWebViewAsync 仍保留 UI 续延。
            _sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options)
                .ConfigureAwait(false);
        }
        return _sharedEnvironment;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? cls, string? title);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    /// <summary>设置进程 DPI 感知上下文（Per-Monitor V2）。</summary>
    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [STAThread]
    private static async Task Main()
    {
        // 进程级 Per-Monitor V2 DPI 感知：必须在任何窗口/控件创建之前调用，
        // 否则 150% 等缩放下 Windows 对 WebView2 内容做位图拉伸（字体/图标模糊，issue #2）。
        // 用 user32 直接调用（WinForms 的 Application.SetHighDpiMode 在部分环境下
        // 可能因先前的 MessageBox 等窗口创建而失效）。
        SetProcessDpiAwarenessContext((IntPtr)(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

        // v0.3.0：一键诊断导出（--diagnose [--min-level warn|error]）。不初始化 UI，
        // 打包脱敏日志/环境/版本/错误码汇总到"下载"文件夹后直接退出。
        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => string.Equals(a, "--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            Logger.Init(Path.Combine(DshHomeDir, "dsh-launcher", "dsh.log"));
            var zip = DiagnoseExport.Run(args, DshHomeDir, Logger.Path);
            if (zip is not null)
            {
                MessageBox.Show(
                    "诊断包已导出（已脱敏，不含任何密钥/会话/插件数据）：\n\n" + zip
                    + "\n\n可随 Issue 一起上传，或在命令行以 --diagnose --min-level warn 单独导出告警/错误。",
                    "dsh-launcher 诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                ShowError(ErrorCodes.E5001, "无法生成诊断包（详见统一日志）。可手动打包 " + Logger.Path + "。");
            }
            return;
        }

        // WinForms 全局初始化必须在任何窗口/控件创建之前完成：冷启动流程会先创建
        // 启动状态窗（IWin32Window），若此时才调用 SetCompatibleTextRenderingDefault
        // 会抛 InvalidOperationException 导致进程静默崩溃——主窗口不出现，用户只能
        // 二次点击（服务已在跑、跳过状态流后才轮到正常调用）才开窗。这是"要二次点击"
        // 的根因，必须放在 Main 最前面。
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // v0.3.0：统一日志初始化（单一日志文件 + 启动早段轮转）
        Logger.Init(UnifiedLogPath);
        Logger.RotateIfNeeded();
        WindowStateStore.Init(DataDir);
        StagedUpdate.Init(DataDir);

        Trace($"start target={Target.Url} external={ServerManagedExternally}");
        MigrateLegacyData(); // 旧版 %LOCALAPPDATA% 数据迁移到 DSH_HOME（settings.json 保留、旧目录清理）
        CleanupProgramDataResidue(); // 清理卸载后 ProgramData 空目录残留
        EnsureAutoStartRequested(); // 自启落地：MSI 机器级意图标志 → 当前用户 HKCU Run

        // 单实例：重复启动只把已开窗口带到前台，避免多开 WebView2 进程白白占用内存。
        // 锁按目标端口隔离，不同服务可各开一个壳窗口。
        using var mutex = new Mutex(true, $@"Local\DshWeb.SingleInstance.{Target.Port}", out var firstInstance);
        if (!firstInstance)
        {
            // 首次实例可能仍在启动（状态窗/服务拉起中，主窗口还没创建）。
            // 等待其主窗口出现再聚焦，避免"点了没反应"（用户以为要再点一次）。
            var existing = FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, "DeepSeek Harness");
            for (var i = 0; existing == IntPtr.Zero && i < 40; i++)
            {
                Thread.Sleep(500);
                existing = FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, "DeepSeek Harness");
            }
            if (existing != IntPtr.Zero)
            {
                Trace($"second instance: found main window 0x{existing.ToInt64():X}, restore+foreground");
                ShowWindow(existing, SW_RESTORE);
                SetForegroundWindow(existing);
            }
            else
            {
                Trace("second instance: main window not found within 20s");
            }
            return;
        }
        Trace("first instance");

        // 升级场景：检测并提示清理旧版本（per-user 0.1.0-0.1.5 等）。
        // MSI 的跨作用域 MajorUpgrade 在标准机器上找不到 HKCU 里的 per-user 旧版，
        // 这里由壳提示用户提权卸载（提权卸载不触发 Config.Msi 1926）。
        TryPromptOldVersionCleanup();

        // 自愈孤儿快捷方式：per-user 旧版被（提权）卸载后，其用户级快捷方式可能残留
        //（指向已删除的 exe），这里每次启动扫描并清理，避免开始菜单/桌面出现幽灵图标。
        CleanupOrphanShortcuts();

        // 服务未就绪时自动拉起/等待（仅壳托管模式；DSH_WEB_URL 视为外部托管，直接开窗）。
        // 就绪 = 端口可连 + HTTP 有响应：dsh 前端在端口监听后可能还需数十秒才提供 HTTP，
        // 若只等 TCP 就提前"成功"，主窗口会加载失败（白屏，用户以为没反应而多点一次）；
        // 若探测太早判失败，用户要二次点击才能开窗。这里统一在状态窗里等 HTTP 就绪。
        if (!ServerManagedExternally && !HttpReady())
        {
            if (!PortOpen(Target.Port))
            {
                // v0.3.0 ① 僵尸清扫：上次崩溃记录过、但已不在监听的进程 → 清理（只动我们记录的 PID）
                SweepStaleServicePid();

                // v0.3.0 ② 延迟更新应用：下次启动拉起服务前应用已下载的 dsh 新版（失败不阻塞启动）
                ApplyPendingDshUpdate();

                // v0.3.0 ③ Node/npm 本位解析：系统 Node ≥18 优先（尊重用户环境），否则便携
                //（一次性确认后自动下载到 %LOCALAPPDATA%\dsh-launcher\env\node，绝不打包进安装包）
                var nodeEnv = RuntimeResolver.ResolveExisting();
                if (nodeEnv.NodeExe is null)
                {
                    if (!await TryEnsureNodeAsync()) return;
                    nodeEnv = RuntimeResolver.ResolveExisting();
                    if (nodeEnv.NodeExe is null) return;
                }
                if (nodeEnv.IsPortable)
                {
                    RuntimeResolver.PrependToPath(nodeEnv.RootDir!);
                    Trace("using portable node: " + nodeEnv.RootDir);
                }

                var vbs = Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs");
                if (!File.Exists(vbs))
                {
                    ShowError(ErrorCodes.E2001, $"未找到 {vbs}，无法自动拉起 dsh 服务（{Target.Url}）。");
                    return;
                }

                // 端口与统一日志路径透传给 start-dsh.vbs（进程级环境变量，wscript → cmd → dsh 依次继承）；
                // DSH_PORT 不设时 vbs 默认 3080。DSH_HOME 等环境变量同理自动继承。
                Environment.SetEnvironmentVariable("DSH_PORT", Target.Port.ToString());
                Environment.SetEnvironmentVariable("DSH_LOG", UnifiedLogPath);
                Process.Start(new ProcessStartInfo("wscript.exe", "\"" + vbs + "\"") { UseShellExecute = true });
                _serviceStartedByShell = true;
                Trace("service start requested via start-dsh.vbs");
            }
            else
            {
                // 端口已开但 HTTP 前端尚未就绪（服务可能刚被拉起、正在初始化）：也显示
                // 状态窗等待，避免直接开窗看到白屏（用户以为没反应而多点一次）。
                Trace("port open but HTTP not ready; waiting with status window");
            }

            // 启动状态窗：等待服务就绪。首次运行 npx 需要下载 dsh 组件（可能几分钟），
            // 此期间明确提示而不是静默干等；可随时取消。
            // 轮询期间持续检查启动日志：一旦出现明确错误（下载失败/权限/无 npx 等）
            // 结束等待（有 15 秒宽限期，避免启动过程中的良性告警误判）。
            var logPath = UnifiedLogPath;
            using var status = CreateStartupStatusForm();
            var cts = new CancellationTokenSource();
            var pollTask = Task.Run(() =>
            {
                var lastLogCheck = DateTime.MinValue;
                var logErrorSeen = false;
                var logErrorSince = DateTime.MinValue;
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                for (var i = 0; i < 180; i++)
                {
                    if (cts.IsCancellationRequested) return "canceled";
                    if ((DateTime.Now - lastLogCheck).TotalSeconds >= 5)
                    {
                        lastLogCheck = DateTime.Now;
                        var content = SafeReadText(logPath);
                        if (ShellLogic.LogShowsStartupError(content))
                        {
                            if (!logErrorSeen)
                            {
                                logErrorSeen = true;
                                logErrorSince = DateTime.Now;
                                // 日志出现错误标志：不立即判死——启动过程中的良性告警（如网络探测）
                                // 也会命中，误判会导致用户"要多点一次"。给 15 秒宽限期，期间
                                // HTTP 就绪仍算成功；只有持续失败才判定启动出错。
                                Trace("poll: log shows error markers, grace 15s");
                            }
                        }
                        else
                        {
                            logErrorSeen = false; // 日志恢复干净，重置记时
                        }
                    }
                    if (PortOpen(Target.Port))
                    {
                        try
                        {
                            using var resp = http.GetAsync(Target.Url).GetAwaiter().GetResult();
                            Trace("poll: ready (tcp + http)");
                            return "ready"; // TCP + HTTP 都已就绪
                        }
                        catch
                        {
                            // HTTP 尚未就绪（前端还在启动），继续等
                        }
                    }
                    if (logErrorSeen && DateTime.Now - logErrorSince >= TimeSpan.FromSeconds(15))
                    {
                        Trace("poll: log error markers persisted 15s, giving up");
                        return "logerror";
                    }
                    Thread.Sleep(1000);
                }
                Trace("poll: timeout after 180s");
                return "timeout";
            });
            _ = pollTask.ContinueWith(_ =>
            {
                try { status.Invoke(status.Close); } catch { /* 窗口已关闭 */ }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

            status.ShowDialog();
            var waitResult = pollTask.GetAwaiter().GetResult();
            Trace($"status window closed, waitResult={waitResult}");

            if (waitResult != "ready")
            {
                // v0.3.0：启动失败时清理"本次拉起但未就绪"的半启动服务（避免残留占端口）
                if (waitResult is "logerror" or "timeout" && _serviceStartedByShell && PortOpen(Target.Port))
                {
                    var pid = FindPidListeningOn(Target.Port);
                    if (pid > 0)
                    {
                        Logger.Warn("service failed to become ready; cleaning up", ErrorCodes.E2005, new { pid });
                        KillProcess(pid);
                    }
                    ClearServicePidFile();
                }
                var tail = ShellLogic.ReadLogTail(logPath, 12);
                var tailText = tail.Count == 0 ? "（日志为空或不可读）" : string.Join("\n", tail.Select(l => "  " + l));
                var body = waitResult switch
                {
                    "canceled" => "已取消启动。若服务仍在后台下载/启动，可稍后重新打开 dsh-launcher。",
                    "logerror" => "启动过程报错（可能是下载失败、权限或环境问题）。\n\n日志尾部：\n" + tailText,
                    _ => "启动超时：可能是首次下载 dsh 组件较慢（可稍后重试），也可能是网络/代理问题。\n\n日志尾部：\n" + tailText
                        + "\n\n完整日志：" + logPath,
                };
                var code = waitResult switch
                {
                    "logerror" => ErrorCodes.E2003,
                    "timeout" => ErrorCodes.E2002,
                    _ => ErrorCodes.E9001,
                };
                ShowError(code, "dsh 服务未能就绪。\n\n" + body);
                return;
            }

            // 就绪判定（TCP + HTTP）已在轮询内完成；此处无需再探测。
            RecordServicePid(); // 记录本次拉起的服务 PID（供下次启动接管残留服务）
        }

        // 端口已开且本次没拉起服务：接管上次崩溃/退出残留的壳托管服务
        if (!ServerManagedExternally && !_serviceStartedByShell)
            TryAdoptOrphanService();

        if (!PortOpen(Target.Port))
        {
            ShowError(ErrorCodes.E2004, $"dsh 服务不可用（{Target.Url}），请确认服务已启动并查看统一日志：{UnifiedLogPath}");
            return;
        }

        var form = new DshShellForm
        {
            Text = "DeepSeek Harness",
            ClientSize = new Size(1280, 840),
            StartPosition = FormStartPosition.CenterScreen,
            MinimumSize = new Size(800, 600),
            // 无边框 + 自绘标题栏：主题切换即时生效，不依赖 DWM 标题栏重绘
            // （实测本机 DWM 属性切换后标题栏画面不刷新，只有焦点变化才重绘）
            FormBorderStyle = FormBorderStyle.None,
            Icon = TrayWhaleIcon ?? SystemIcons.Application // 系统任务栏图标固定白色鲸鱼
        };
        var titleHeight = (int)Math.Round(32 * form.DeviceDpi / 96f);
        form.TitleBar = new CustomTitleBar(form, ResolveDarkMode())
        {
            // 手动布局 + Anchor（不依赖 Dock 布局顺序：Dock 下 WebView2 内容会盖住标题栏区域，
            // 表现为"正常内容被标题栏挡住一部分"）。四周留 1px 作为窗口边框（Form.BackColor=边框色）
            Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2, titleHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        form.Controls.Add(form.TitleBar);

        var web = new WebView2
        {
            Bounds = new Rectangle(1, 1 + titleHeight, form.ClientSize.Width - 2, form.ClientSize.Height - titleHeight - 2),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
        };
        form.Controls.Add(web);
        _mainWeb = web;

        // 无边框窗口阴影（DWM NCRENDERING_POLICY；带 WebView2 时系统阴影实际不呈现，边框替代质感）
        form.HandleCreated += (_, _) => ApplyWindowShadow(form.Handle);

        // DPI 变化（跨缩放显示器移动窗口）：重算标题栏尺寸并重新布局内容区
        form.DpiChanged += (_, _) =>
        {
            var scale = form.DeviceDpi / 96f;
            var h = (int)Math.Round(32 * scale);
            form.TitleBar.Rescale(scale);
            form.TitleBar.Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2, h);
            web.Bounds = new Rectangle(1, 1 + h, form.ClientSize.Width - 2, form.ClientSize.Height - h - 2);
        };

        // 托盘图标始终显示（任何服务模式）：提供"服务模式"切换与退出的常驻入口。
        // 之前只在"托盘驻留"模式创建，导致默认"常驻"模式下用户找不到切换入口。
        EnsureTrayIcon(form);
        // 窗口图标跟随主题（深色 → 白色鲸鱼 + 深色标题栏），主题切换时实时更新。
        ApplyThemeIcon(form);
        form.HandleCreated += (_, _) => ApplyThemeIcon(form); // 句柄创建后应用标题栏配色
        RegisterThemeWatcher(form);
        form.Shown += (_, _) => Trace("main form shown");

        form.FormClosing += (_, e) =>
        {
            // 生命周期模式（由 dsh-launcher-lifetime 插件写入 settings.json，壳执行）：
            // 常驻(0) / 托盘驻留(1) / 跟随窗口(2)。
            var mode = ReadLifetimeMode();

            // 托盘驻留：拦截关闭，隐藏到托盘（服务继续）。
            // 必须先于 WebView2 销毁判断：WebView2 一旦 Dispose，从托盘唤起时
            // 控件已销毁，窗口只剩空白（历史上 WebView2 销毁在拦截之前 → 必然白屏）。
            if (!_trayExitRequested && mode == ShellLogic.ServiceLifetime.Tray)
            {
                e.Cancel = true;
                form.Hide();
                _hiddenSince = DateTime.Now;
                return;
            }

            // 真正退出路径不显式 Dispose WebView2：Dispose 会等待浏览器进程关闭，
            // 造成关窗卡顿 1-2 秒；进程退出后 WebView2 子进程会自动检测父进程退出并清理。
            // 图标为进程级缓存（GDI 对象随进程退出释放），此处不销毁，
            // 避免托盘驻留/主题切换时复用已销毁的句柄。

            // v0.3.0：主窗口位置/大小持久化（多显示器记忆，真实退出时写回）
            SaveWindowState(form);

            if (mode == ShellLogic.ServiceLifetime.FollowWindow && _serviceStartedByShell)
            {
                // 跟随窗口：关窗即停服务（只停壳本次拉起的）
                StopShellService();
            }
            _trayIcon?.Dispose();
            _trayIcon = null;
        };

        form.Load += async (_, _) =>
        {
            // v0.3.0：窗口记忆恢复（多显示器容灾）——已保存的状态优先，
            // 否则保持默认 1280×840 逻辑尺寸。
            var savedWindow = WindowStateStore.Load();
            // PerMonitorV2：ClientSize 是物理像素。按窗口初始 DPI 放大，保持
            // 150% 等缩放下窗口的逻辑大小与 100% 一致（否则窗口会显得很小）。
            var scale = (double)form.DeviceDpi / 96.0;
            if (savedWindow is not null)
            {
                var w = Math.Max(savedWindow.WidthLogical, 800);
                var h = Math.Max(savedWindow.HeightLogical, 600);
                form.ClientSize = new Size((int)Math.Round(w * scale), (int)Math.Round(h * scale));
            }
            else if (Math.Abs(scale - 1.0) > 0.01)
            {
                form.ClientSize = new Size((int)Math.Round(1280 * scale), (int)Math.Round(840 * scale));
            }

            if (savedWindow is not null)
            {
                // 越界（副屏拔掉等）→ 主屏居中；可见 → 工作区内钳制（ShellLogic 纯函数）
                var (x, y) = ShellLogic.RestoreWindowPosition(
                    savedWindow.X, savedWindow.Y, form.Width, form.Height,
                    Screen.AllScreens.Select(s => s.WorkingArea).ToList(),
                    Screen.PrimaryScreen?.WorkingArea ?? Rectangle.Empty);
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(x, y);
                Trace($"window restored to ({x},{y}) size={form.Width}x{form.Height}");
            }

            // WebView2 user data goes to %LOCALAPPDATA%\DshWeb to keep the app dir clean
            // (固定目录：避免系统临时目录被清理导致会话/插件登录态丢失)
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DshWeb", "WebView2");
            try
            {
                await InitWebViewAsync(web, userDataFolder);
                web.CoreWebView2.Navigate(Target.Url);
                // 页面加载失败（如端口被其他程序占用、服务异常退出）：明确提示而非白屏静默
                var navWarned = false;
                web.CoreWebView2.NavigationCompleted += (_, e) =>
                {
                    if (!e.IsSuccess && !navWarned)
                    {
                        navWarned = true;
                        ShowError(ErrorCodes.E2004,
                            $"页面加载失败。\n\n请确认 {Target.Url} 上运行的是 dsh 服务（端口可能被其他程序占用，或服务已异常退出）。\n\n统一日志：{UnifiedLogPath}");
                    }
                };
            }
            catch (Exception ex)
            {
                // WebView2 Runtime 缺失等初始化失败：明确提示而不是静默无窗口
                ShowError(ErrorCodes.E1006,
                    "无法初始化 WebView2：\n" + ex.Message
                    + "\n\n请确认系统已安装 Microsoft Edge WebView2 Runtime（Windows 10/11 通常已自带）。");
                form.Close();
            }
        };

        ScheduleUpdateCheck(form); // 启动后异步检查更新（dsh 新版 / launcher 安全更新才推送）

        Application.Run(form);
        Trace("main loop exited");
    }

    private enum PendingUpdate { None, Dsh, LauncherSecurity }
    private static PendingUpdate _pendingUpdate;
    private static string _pendingLatest = "", _pendingLocal = "";
    private static Form? _pendingForm;

    /// <summary>统一出错弹窗（v0.3.0 显式差错控制）：正文含 [错误码]，错误一并写入结构化日志；
    /// 消息文本可 Ctrl+C 复制，便于粘贴到 Issue。</summary>
    private static DialogResult ShowError(string code, string detail,
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.Warning)
    {
        Logger.Error(detail, code);
        return MessageBox.Show($"[{code}] {ErrorCodes.Describe(code)}\n\n{detail}", "DeepSeek Harness", buttons, icon);
    }

    /// <summary>v0.3.0 Node 缺失处理：一次性确认 → 状态窗期间自动下载便携 Node（可取消）。
    /// 返回是否已具备可用 Node。</summary>
    private static async Task<bool> TryEnsureNodeAsync()
    {
        var ask = MessageBox.Show(
            "未检测到 Node.js（dsh 服务运行必需）。\n\n是否自动下载便携版 Node.js 到用户目录？\n" +
            "（约 30MB，仅用于本启动器，不改动系统环境；版本采用 LTS 固定版）",
            "dsh-launcher - 需要 Node.js", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes)
        {
            ShowError(ErrorCodes.E1002, "未安装 Node.js，dsh 服务无法启动。可安装 Node.js 18+ 后重新打开。");
            return false;
        }
        using var status = CreateStartupStatusForm("正在下载并安装便携 Node.js…（约 30MB，请稍候）");
        var cts = new CancellationTokenSource();
        var task = RuntimeResolver.EnsurePortableNodeAsync(cts.Token);
        _ = task.ContinueWith(_ =>
        {
            try { status.Invoke(status.Close); } catch { /* 窗口已关闭 */ }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        status.ShowDialog();
        var (ok, code, detail) = await task;
        if (!ok)
        {
            ShowError(code ?? ErrorCodes.E1003, detail ?? "便携 Node 安装失败。可稍后重试，或手动安装 Node.js 18+。");
            return false;
        }
        return true;
    }

    /// <summary>v0.3.0 主窗口位置/大小持久化（多显示器记忆）：RestoreBounds 位置为物理像素，
    /// 尺寸存 96dpi 逻辑值（跨 DPI 恢复时按当前 DPI 缩放）。</summary>
    private static void SaveWindowState(Form form)
    {
        try
        {
            if (form.WindowState == FormWindowState.Minimized) return;
            var rb = form.RestoreBounds;
            if (rb.Width <= 0 || rb.Height <= 0) return;
            var scale = form.DeviceDpi / 96f;
            WindowStateStore.Save(new WindowStateStore.WindowState(
                rb.X, rb.Y,
                (int)Math.Round(rb.Width / scale),
                (int)Math.Round(rb.Height / scale)));
        }
        catch { /* 保存失败不影响退出 */ }
    }

    /// <summary>
    /// 启动后异步检查更新（仅启动时一次，避免频繁请求 GitHub/npm）：
    /// - dsh-launcher 自身：**普通更新不推送**，只有标记为**安全/重要更新**（Release
    ///   body 含 "SECURITY" 或 tag 含 "-sec"）才托盘气泡提示（点击打开 Releases 下载页）
    /// - dsh（@deepseek-ai/dsh）：有新版即提示（点击一键 npm 更新）
    /// 网络失败/无更新静默，不打扰用户；匿名限流影响可控。
    /// </summary>
    private static void ScheduleUpdateCheck(Form form)
    {
        _pendingForm = form;
        _ = Task.Run(async () =>
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-launcher");

                // 1) launcher 安全更新优先（安全修复比功能更新重要）
                var lr = await UpdateChecker.FetchLatestLauncherReleaseAsync(http);
                if (lr is not null && lr.IsSecurity
                    && UpdateChecker.CompareVersions(lr.Version, UpdateChecker.CurrentLauncherVersion) > 0)
                {
                    form.BeginInvoke(() => NotifyPending(PendingUpdate.LauncherSecurity, lr.Version,
                        UpdateChecker.CurrentLauncherVersion ?? "?"));
                    return;
                }

                // 2) dsh 新版
                var latest = await UpdateChecker.FetchLatestDshVersionAsync(http);
                var local = UpdateChecker.ResolveLocalDshVersion();
                if (!string.IsNullOrWhiteSpace(latest) && !string.IsNullOrWhiteSpace(local)
                    && UpdateChecker.CompareVersions(latest, local) > 0)
                {
                    form.BeginInvoke(() => NotifyPending(PendingUpdate.Dsh, latest, local));
                }
            }
            catch { /* 检测失败静默 */ }
        });
    }

    /// <summary>下载完成但非"无害扩展名"（可能含可执行代码）时的提示：托盘气泡告知落盘位置，
    /// 不自动打开——防恶意页面触发下载后自动执行本地代码（S2 修复）。</summary>
    private static void NotifyDownloadComplete(string filePath)
    {
        try
        {
            if (_trayIcon is null) return;
            _trayIcon.ShowBalloonTip(8000, "下载完成",
                "文件已保存：\n" + filePath + "\n（点击" + _trayIcon.Text + "托盘图标查看）",
                ToolTipIcon.Info);
        }
        catch { /* 气泡失败忽略 */ }
    }

    private static void NotifyPending(PendingUpdate type, string latest, string local)
    {
        try
        {
            _pendingUpdate = type;
            _pendingLatest = latest;
            _pendingLocal = local;
            // v0.3.0：托盘按需显示——有待通知的更新时临时创建托盘（无插件也提示更新）
            if (_pendingForm is null) return;
            EnsureTrayIcon(_pendingForm);
            if (_trayIcon is null) return;
            _trayIcon.BalloonTipClicked -= OnPendingBalloonClicked;
            _trayIcon.BalloonTipClicked += OnPendingBalloonClicked;
            var (title, body) = type == PendingUpdate.LauncherSecurity
                ? ("dsh-launcher 安全更新", $"检测到重要安全更新 {latest}（当前 {local}）。点击查看下载。\n如有严重漏洞请尽快更新。")
                : ("dsh 有新版本", $"检测到 dsh {latest}（当前 {local}）。点击此处在后台下载更新。");
            _trayIcon.ShowBalloonTip(25000, title, body, ToolTipIcon.Info); // 驻留 25s，安全更新要让人看到
        }
        catch { /* 气泡提示失败忽略 */ }
    }

    private static void OnPendingBalloonClicked(object? s, EventArgs e)
    {
        var f = _pendingForm;
        if (_pendingUpdate == PendingUpdate.Dsh && f is not null)
        {
            PromptDshUpdate(f, _pendingLatest, _pendingLocal);
        }
        else if (_pendingUpdate == PendingUpdate.LauncherSecurity)
        {
            try { Process.Start(new ProcessStartInfo("https://github.com/Ruler4396/dsh-launcher/releases/latest") { UseShellExecute = true }); }
            catch { /* 打开失败忽略 */ }
        }
        _pendingUpdate = PendingUpdate.None;
    }

    /// <summary>点击气泡后：确认 → 后台下载 dsh 新版（npm pack，不碰运行中的环境）→
    /// 写 pending-update.json，下次启动时自动应用（延迟应用，v0.3.0，绝不打断当前会话）。</summary>
    private static void PromptDshUpdate(Form form, string latest, string local)
    {
        var r = MessageBox.Show(
            $"检测到 dsh 新版本 {latest}（当前 {local}）。\n\n是否在后台下载并安排更新？\n" +
            "（下载完成不打扰当前会话；下次启动 dsh-launcher 时自动应用新版本）",
            "dsh 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes) return;
        _ = Task.Run(() => DownloadDshUpdateStaged(form, latest));
    }

    /// <summary>后台执行：npm pack 下载指定版本到 staging；成功 → MarkPending（下次启动应用）。</summary>
    private static void DownloadDshUpdateStaged(Form form, string latest)
    {
        try
        {
            var staging = Path.Combine(DataDir, "staging");
            Directory.CreateDirectory(staging);
            var ok = RunNpmCommand($"pack @deepseek-ai/dsh@{latest} --pack-destination \"" + staging + "\"", out var errorTail);
            if (ok)
            {
                StagedUpdate.MarkPending(latest);
                try
                {
                    form.BeginInvoke(() => MessageBox.Show(
                        $"dsh {latest} 已下载完成，下次启动 dsh-launcher 时自动应用（不会打断当前会话）。",
                        "dsh 更新", MessageBoxButtons.OK, MessageBoxIcon.Information));
                }
                catch { /* 窗体已关闭则下次启动再说 */ }
                Logger.Info($"staged dsh update downloaded: {latest}");
            }
            else
            {
                Logger.Error("staged dsh update download failed: " + errorTail, ErrorCodes.E4001, new { latest });
                try
                {
                    form.BeginInvoke(() => ShowError(ErrorCodes.E4001,
                        $"dsh {latest} 下载失败。\n\n可稍后重试，或在命令行手动执行：\nnpm install -g @deepseek-ai/dsh@{latest}"));
                }
                catch { /* 窗体已关闭 */ }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("staged dsh update download error: " + ex.Message, ErrorCodes.E4001);
            try { form.BeginInvoke(() => ShowError(ErrorCodes.E4001, ex.Message)); } catch { /* 窗体已关闭 */ }
        }
    }

    /// <summary>v0.3.0 延迟应用：下次启动拉起服务前，应用已下载的 dsh 新版。
    /// 失败不阻塞启动（继续用旧版，错误码 E4002，下次启动重试，幂等）。</summary>
    private static void ApplyPendingDshUpdate()
    {
        var version = StagedUpdate.ReadPendingVersion();
        if (string.IsNullOrWhiteSpace(version)) return;
        Logger.Info($"applying staged dsh update to {version}");
        if (RunNpmCommand($"install -g @deepseek-ai/dsh@{version}", out var errorTail))
        {
            StagedUpdate.ClearPending();
            Logger.Info($"staged dsh update applied: {version}");
        }
        else
        {
            Logger.Warn("staged dsh update apply failed; continuing with current version", ErrorCodes.E4002,
                new { version, tail = errorTail });
        }
    }

    /// <summary>运行 npm 命令（v0.3.0 起唯一 npm 执行点）：最多等 120s；输出重定向避免死锁。</summary>
    private static bool RunNpmCommand(string args, out string errorTail)
    {
        errorTail = "";
        try
        {
            var psi = new ProcessStartInfo("npm.cmd", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(120000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 尽力 */ }
                return false;
            }
            var combined = outTask.GetAwaiter().GetResult() + "\n" + errTask.GetAwaiter().GetResult();
            var lines = combined.Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            if (lines.Count > 0)
                errorTail = string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 6)));
            return p.ExitCode == 0;
        }
        catch (Exception ex)
        {
            errorTail = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 升级场景：检测已安装的其他版本 dsh-launcher（per-user 旧版 0.1.0-0.1.5 等），
    /// 提示用户提权卸载。用户选择"否"时记录 HKCU 标记，之后不再打扰（直到旧版被移除）。
    /// 卸载失败（被取消/旧版仍在运行）不阻断启动，提示用户稍后到"设置 → 应用"手动卸载。
    /// </summary>
    private static void TryPromptOldVersionCleanup()
    {
        try
        {
            // 当前产品代码（安装时写入 HKLM\Software\dsh-launcher\CurrentProductCode）：永远不清理自己
            string? currentCode = null;
            try
            {
                using var selfKey = Registry.LocalMachine.OpenSubKey(@"Software\dsh-launcher");
                currentCode = selfKey?.GetValue("CurrentProductCode") as string;
            }
            catch
            {
                // 读不到按无当前产品处理（便携版等）
            }

            var olds = ShellLogic.FilterByUpgradeCode(
                ShellLogic.ReadCandidateProducts(), ReadUpgradeCodeOfProduct);
            olds = ShellLogic.PickOldInstalls(olds, currentCode);
            if (olds.Count == 0) return;

            const string skipKeyName = @"Software\dsh-launcher";
            try
            {
                using var skipKey = Registry.CurrentUser.OpenSubKey(skipKeyName);
                if (skipKey?.GetValue("SkipOldUninstall") is int skipFlag && skipFlag == 1)
                    return;
            }
            catch
            {
                // 读不到标记按未标记处理
            }

            var list = string.Join("\n", olds.Select(o => $"  • {o.ProductCode}  (v{o.Version})"));
            var answer = MessageBox.Show(
                "检测到旧版本的 dsh-launcher，建议先卸载旧版本，避免两个版本共存。\n\n" + list +
                "\n\n是否现在卸载？\n（卸载需要管理员确认；请先关闭其他 dsh-launcher 窗口）",
                "DeepSeek Harness", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (answer != DialogResult.Yes)
            {
                try { Registry.CurrentUser.CreateSubKey(skipKeyName)?.SetValue("SkipOldUninstall", 1); }
                catch { /* ignore */ }
                return;
            }

            var failed = 0;
            foreach (var old in olds)
            {
                try
                {
                    var psi = new ProcessStartInfo("msiexec.exe", $"/x {old.ProductCode} /qn /norestart")
                    {
                        UseShellExecute = true,
                        Verb = "runas",
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit();
                    if (p is null || p.ExitCode != 0) failed++;
                }
                catch
                {
                    failed++;
                }
            }

            MessageBox.Show(failed == 0
                ? "旧版本已全部卸载。"
                : "部分旧版本未能卸载（可能被取消，或旧版本窗口仍在运行）。\n可稍后在 设置 → 应用 中手动卸载。",
                "DeepSeek Harness", MessageBoxButtons.OK,
                failed == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        catch
        {
            // 检测/清理失败不打扰用户
        }
    }

    /// <summary>
    /// 读取产品的 UpgradeCode（经其缓存 MSI 的 Property 表）。用于精确识别"我们的产品"，
    /// 避免误清理其他恰好同名的软件。任何一步失败返回 null（该产品将被过滤掉）。
    /// </summary>
    private static string? ReadUpgradeCodeOfProduct(string productCode)
    {
        try
        {
            dynamic installer = Activator.CreateInstance(
                Type.GetTypeFromProgID("WindowsInstaller.Installer") ?? throw new InvalidOperationException());
            var localPackage = (string)installer.ProductInfo(productCode, "LocalPackage");
            if (string.IsNullOrWhiteSpace(localPackage) || !File.Exists(localPackage))
                return null;
            dynamic db = installer.OpenDatabase(localPackage, 0);
            dynamic view = db.OpenView("SELECT `Value` FROM `Property` WHERE `Property`='UpgradeCode'");
            view.Execute();
            dynamic rec = view.Fetch();
            return rec is null ? null : (string)rec.StringData(1);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 清理 per-user 旧版本（0.1.0-0.1.5）残留的用户级快捷方式。
    /// 旧版被（提权）卸载后，其用户开始菜单/桌面快捷方式可能不被删除（MSI 提权卸载
    /// 跳过 per-user 上下文组件）。只删除"目标确实是 DshWeb.exe"的快捷方式，
    /// 用户自行创建的同名 .lnk（指向其他程序）不受影响；无法读取目标时保守不删。
    /// </summary>
    private static void CleanupOrphanShortcuts()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var userMenuDir = Path.Combine(appData, @"Microsoft\Windows\Start Menu\Programs\dsh-launcher");
        try
        {
            // 目录是 MSI 专用名；只有确认里面有我们的快捷方式（指向 DshWeb.exe）才整体删除
            if (Directory.Exists(userMenuDir))
            {
                var hasOurs = Directory.GetFiles(userMenuDir, "*.lnk")
                    .Any(lnk => ShellLogic.IsOurShortcutTarget(GetShortcutTarget(lnk)));
                if (hasOurs) Directory.Delete(userMenuDir, true);
            }
        }
        catch
        {
            // 忽略无法访问的目录
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var userDesktopLnk = Path.Combine(desktop, "dsh-launcher.lnk");
        try
        {
            if (File.Exists(userDesktopLnk)
                && ShellLogic.IsOurShortcutTarget(GetShortcutTarget(userDesktopLnk)))
            {
                File.Delete(userDesktopLnk);
            }
        }
        catch
        {
            // 忽略
        }

        // 清理孤儿自启：HKCU Run 的 dsh-launcher 指向的 DshWeb.exe / start-dsh.vbs 已不存在
        //（per-machine 提权卸载跳过 per-user 组件时残留），避免下次登录白启一个死项。
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (runKey?.GetValue("dsh-launcher") is string runValue)
            {
                var m = Regex.Match(runValue, "\"([^\"]+(?:start-dsh\\.vbs|DshWeb\\.exe))\"",
                    RegexOptions.IgnoreCase);
                var targetPath = m.Success ? m.Groups[1].Value : null;
                if (targetPath is null || !File.Exists(targetPath))
                    runKey.DeleteValue("dsh-launcher", false);
            }
        }
        catch
        {
            // 忽略
        }
    }

    /// <summary>读取 .lnk 的目标路径；失败返回 null（保守不删）。</summary>
    private static string? GetShortcutTarget(string lnkPath)
    {
        try
        {
            dynamic shell = Activator.CreateInstance(
                Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException());
            dynamic lnk = shell.CreateShortcut(lnkPath);
            return (string)lnk.TargetPath;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读取文件文本（容错，失败返回 null）。</summary>
    private static string? SafeReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 服务就绪判定：端口可连 + HTTP 有响应。dsh 前端在端口监听后可能还需数十秒
    /// 才提供 HTTP，只探测 TCP 会提前"成功"（主窗口白屏）。
    /// </summary>
    private static bool HttpReady()
    {
        if (!PortOpen(Target.Port)) return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var resp = http.GetAsync(Target.Url).GetAwaiter().GetResult();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>settings.json 路径（dsh-launcher-lifetime 插件写入，壳读取）：DSH_HOME\dsh-launcher\settings.json。</summary>
    private static string SettingsPath => Path.Combine(DataDir, "settings.json");

    /// <summary>
    /// 读取服务停留模式；缺失/非法回退跟随窗口。兼容旧版路径（%LOCALAPPDATA%，
    /// 迁移前旧插件写入的位置）：新位置读不到时回退旧位置，读到后迁移并清理，
    /// 避免"用户选了常驻/托盘驻留，壳却按默认跟随窗口执行"的路径错位。
    /// v0.3.0 配置降级：校验 dsh-launcher-lifetime 插件物理存在否——插件已卸载时
    /// 忽略残留在 settings.json 里的 serviceLifetime 并回退跟随窗口，同时抹除失效字段
    /// （幂等，免除用户手动删 JSON）。
    /// </summary>
    private static ShellLogic.ServiceLifetime ReadLifetimeMode()
    {
        var json = SafeReadText(SettingsPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "dsh-launcher", "settings.json");
                if (File.Exists(legacy))
                {
                    json = SafeReadText(legacy);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        try
                        {
                            Directory.CreateDirectory(DataDir);
                            File.WriteAllText(SettingsPath, json);
                            File.Delete(legacy);
                        }
                        catch { /* 迁移失败按旧值执行 */ }
                    }
                }
            }
            catch { /* 旧路径不可读按默认执行 */ }
        }
        var pluginPresent = ShellLogic.IsLifetimePluginInstalled(DshHomeDir);
        var (mode, shouldPurge) = ShellLogic.ResolveEffectiveLifetime(json, pluginPresent);
        if (shouldPurge)
        {
            Logger.Warn("settings.json serviceLifetime ignored (lifetime plugin missing); purging stale value",
                ErrorCodes.E2011, new { path = SettingsPath, pluginPresent });
            PurgeServiceLifetime(SettingsPath);
        }
        return mode;
    }

    /// <summary>抹除 settings.json 中的 serviceLifetime 字段（只改字段，不动插件其他内容）；失败幂等。</summary>
    private static void PurgeServiceLifetime(string path)
    {
        try
        {
            var text = SafeReadText(path);
            if (string.IsNullOrWhiteSpace(text)) return;
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (!prop.NameEquals("serviceLifetime")) prop.WriteTo(writer);
                writer.WriteEndObject();
            }
            File.WriteAllText(path, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch { /* 抹除失败幂等：下次启动再判 */ }
    }

    /// <summary>壳托管服务的 PID 记录文件（按端口隔离）：崩溃/异常退出后残留的服务可被下次启动接管管理。</summary>
    private static string ServicePidFile => Path.Combine(DataDir, $"service-pid-{Target.Port}.txt");

    /// <summary>记录本次壳拉起的服务 PID（服务就绪后调用），供下次启动接管残留服务。</summary>
    private static void RecordServicePid()
    {
        try
        {
            var pid = FindPidListeningOn(Target.Port);
            if (pid > 0)
            {
                _servicePid = pid;
                File.WriteAllText(ServicePidFile, pid.ToString());
            }
        }
        catch
        {
            // 记录失败不影响启动
        }
    }

    /// <summary>
    /// 端口已开但本实例没拉起服务时调用：若监听进程正是壳上次拉起的残留服务
    /// （PID 记录在 ServicePidFile），则校验健康后接管管理（跟随窗口关窗时停掉），
    /// 避免崩溃/异常退出后服务永久残留。
    /// v0.3.0 健康校验：HTTP 就绪才算可接管（状态恢复，不打断用户）；坏状态/旧版本
    /// 进程不得带病运行——监听但 HTTP 不通 → 清理（只动我们记录的 PID）。
    /// </summary>
    private static void TryAdoptOrphanService()
    {
        try
        {
            if (!File.Exists(ServicePidFile)) return;
            if (!int.TryParse(File.ReadAllText(ServicePidFile).Trim(), out var pid) || pid <= 0) return;
            if (FindPidListeningOn(Target.Port) == pid)
            {
                if (HttpReady())
                {
                    _serviceStartedByShell = true;
                    _servicePid = pid;
                    Trace($"adopted orphan service pid={pid}");
                }
                else
                {
                    Logger.Warn($"orphan service pid={pid} unhealthy (no HTTP); killing", ErrorCodes.E2005,
                        new { port = Target.Port });
                    KillProcess(pid);
                    ClearServicePidFile();
                }
            }
        }
        catch
        {
            // 接管失败不影响启动
        }
    }

    /// <summary>端口未开时的遗留清扫（拉起服务前调用）：上次崩溃记录过、但已不在
    /// 监听的进程 → 清理（只动我们记录的 PID），确保端口不被占用、不留僵尸进程。</summary>
    private static void SweepStaleServicePid()
    {
        try
        {
            if (!File.Exists(ServicePidFile)) return;
            if (!int.TryParse(File.ReadAllText(ServicePidFile).Trim(), out var pid) || pid <= 0)
            {
                ClearServicePidFile();
                return;
            }
            if (!IsProcessAlive(pid))
            {
                ClearServicePidFile();
                return;
            }
            if (FindPidListeningOn(Target.Port) != pid)
            {
                Logger.Warn($"stale service pid={pid} alive but not listening; killing", ErrorCodes.E2005,
                    new { port = Target.Port });
                KillProcess(pid);
                ClearServicePidFile();
            }
        }
        catch { /* 清扫失败不影响启动 */ }
    }

    private static void ClearServicePidFile()
    {
        try { if (File.Exists(ServicePidFile)) File.Delete(ServicePidFile); } catch { }
    }

    private static bool IsProcessAlive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    /// <summary>停止指定 PID：先温和 taskkill，短等待未退则强制 /f（全程限时 &lt;1s，不卡调用方）。</summary>
    private static void KillProcess(int pid)
    {
        try
        {
            Process.Start(new ProcessStartInfo("taskkill", "/pid " + pid)
            { UseShellExecute = false, CreateNoWindow = true });
            var deadline = DateTime.UtcNow.AddMilliseconds(900);
            while (DateTime.UtcNow < deadline && IsProcessAlive(pid))
                Thread.Sleep(100);
            if (IsProcessAlive(pid))
            {
                Process.Start(new ProcessStartInfo("taskkill", "/f /pid " + pid)
                { UseShellExecute = false, CreateNoWindow = true });
            }
        }
        catch { /* 停服务失败不影响流程 */ }
    }

    /// <summary>
    /// 停止"壳本次会话拉起的"dsh 服务：优先用内存缓存的 PID（就绪时已记录，
    /// 关窗路径不再跑 netstat）。温和 taskkill 对无窗口进程（wscript 隐藏启动的
    /// node）发 WM_CLOSE 无效，必须**在壳退出前同步确认**：短等待未停则立即
    /// 强制 /f——此前强制杀在后台 Task 里延迟 1.5s，壳退出后 Task 未及执行，
    /// 导致"跟随窗口"关窗后服务残留（issue #…）。全程限时（&lt;1s），不卡关窗。
    /// </summary>
    private static void StopShellService()
    {
        try
        {
            var pid = _servicePid;
            if (pid <= 0) pid = FindPidListeningOn(Target.Port); // 兜底：内存没有时再查
            if (pid <= 0)
            {
                ClearServicePidFile();
                return;
            }
            KillProcess(pid);
            ClearServicePidFile();
        }
        catch
        {
            // 停服务失败不影响退出
        }
    }

    /// <summary>按端口找出监听进程 PID（netstat 解析）；找不到返回 0。</summary>
    private static int FindPidListeningOn(int port)
    {
        try
        {
            var psi = new ProcessStartInfo("netstat", "-ano -p tcp")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            using var p = Process.Start(psi);
            if (p is null) return 0;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            var token = ":" + port + " ";
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase)) continue;
                if (!line.Contains(token)) continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0 && int.TryParse(parts[^1], out var pid)) return pid;
            }
        }
        catch
        {
            // 忽略
        }
        return 0;
    }

    /// <summary>v0.3.0 托盘按需策略：默认隐藏；仅当装了 dsh-launcher-lifetime 插件
    ///（常驻/托盘驻留模式需要唤窗入口），或本会话存在待通知的更新时才创建托盘。
    /// 未装插件时默认"跟随窗口"，关闭即全退，托盘无存在意义。</summary>
    private static bool IsTrayWanted()
    {
        if (_pendingUpdate != PendingUpdate.None) return true;
        return ShellLogic.IsLifetimePluginInstalled(DshHomeDir);
    }

    /// <summary>创建托盘图标（按策略懒加载，幂等）；左键切换窗口，右键菜单为退出。
    /// 服务停留模式改由 dsh-launcher-lifetime 插件在 Harness 设置页里配置（不再放托盘菜单）。</summary>
    private static void EnsureTrayIcon(Form form)
    {
        if (_trayIcon is not null) return;
        if (!IsTrayWanted()) return;
        try
        {
            var tray = new NotifyIcon
            {
                // 托盘背景多为深色，固定用白色鲸鱼（深色鲸鱼看不清）
                Icon = TrayWhaleIcon ?? SystemIcons.Application,
                Text = "dsh-launcher",
                Visible = true,
            };
            // 左键单击：窗口置顶显示；右键：弹出自绘托盘菜单（浅色毛玻璃层，仅"退出"）。
            tray.MouseClick += (_, e) =>
            {
                if (e.Button == MouseButtons.Left) ShowMainWindow(form);
                else if (e.Button == MouseButtons.Right) ShowTrayMenu();
            };
            _trayIcon = tray;
        }
        catch
        {
            // 托盘创建失败不影响壳主流程
        }
    }

        /// <summary>在鼠标位置弹出托盘菜单（自绘浅色毛玻璃层）。</summary>
    private static void ShowTrayMenu()
    {
        try
        {
            var menu = new TrayMenuForm(() =>
            {
                _trayExitRequested = true;
                // 常驻模式：只退出壳（服务保留）；托盘驻留/跟随窗口：停掉壳拉起的服务
                if (ReadLifetimeMode() != ShellLogic.ServiceLifetime.AlwaysOn && _serviceStartedByShell)
                    StopShellService();
                Application.Exit();
            });
            var pt = Cursor.Position;
            // 默认：菜单位于鼠标左上方（右键弹菜单位置习惯），略微内偏移；
            // 屏幕边界自适应：左/上越界则翻转到鼠标另一侧，仍越界则贴工作区边缘
            // （左侧竖排任务栏时托盘图标贴近左边缘，不加保护菜单会被推到屏幕外）。
            var wa = Screen.FromPoint(pt).WorkingArea;
            var loc = new Point(pt.X - menu.Width + 12, pt.Y - menu.Height - 6);
            if (loc.X < wa.Left) loc.X = pt.X + 12;
            if (loc.Y < wa.Top) loc.Y = pt.Y + 6;
            if (loc.X + menu.Width > wa.Right) loc.X = wa.Right - menu.Width;
            if (loc.Y + menu.Height > wa.Bottom) loc.Y = wa.Bottom - menu.Height;
            menu.Location = loc;
            menu.Show();
        }
        catch { /* 菜单显示失败不影响壳 */ }
    }

    /// <summary>
    /// 托盘右键菜单：LayeredWindow 自绘（alpha 平滑圆角无锯齿 + 商务质感）。
    /// 内容仅"红色电源图标 + 退出"（黑色、居中、加粗、字距）；实心浅色底 + 轻阴影。
    /// 全部尺寸按当前 DPI 缩放（物理 = 逻辑 × scale），150% 屏上与 HTML 预览观感一致。
    /// </summary>
    private sealed class TrayMenuForm : Form
    {
        // 紧凑版（单功能按钮）：约缩小 20%；图标加粗、文字去加粗，视觉平衡。
        // 所有尺寸仍按 tray-preview.html 的比例体系等比缩减，DPI 缩放不变。
        private const int MenuWidth = 116;  // 原 142 等比缩减
        private const int MenuHeight = 40;  // 原 58
        private const int CornerRadius = 12; // 原 16
        private const int ItemInset = 5;     // .menu 的 padding:4 + 1px 边框 → .exit 条目内缩
        private const int ItemRadius = 6;    // 原 8
        private const int Shadow = 10;       // 阴影边距（逻辑，容纳 0 6px 16px 的扩散）

        private static readonly Color TextDanger = Color.FromArgb(216, 30, 6);    // #D81E06 电源.svg 的亮红
        private static readonly Color TextBlack = Color.FromArgb(31, 41, 55);     // #1F2937 退出文字黑
        private static readonly Color BorderColor = Color.FromArgb(229, 231, 235);
        private static readonly Color HoverFill = Color.FromArgb(20, 220, 38, 38); // .exit:hover rgba(220,38,38,.08)

        private readonly Action _onExit;
        private readonly float _s; // DPI 缩放（96 为 1）
        private readonly Font _exitFont;
        private System.Windows.Forms.Timer? _fadeTimer; // 淡入动画，完成后 Dispose（B3）
        private bool _hoverExit;
        private byte _alpha = 255;

        public TrayMenuForm(Action onExit)
        {
            _onExit = onExit;
            using (var g = CreateGraphics()) _s = Math.Max(1f, g.DpiX / 96f);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size((int)((MenuWidth + Shadow * 2) * _s), (int)((MenuHeight + Shadow * 2) * _s));
            BackColor = Color.White;
            _exitFont = CreateExitFont();
        }

        /// <summary>菜单字体回退链：Noto Sans SC（思源黑体）→ DengXian（等线，Win10/11 自带）
        /// → Microsoft YaHei UI → 系统默认，统一 Regular（400）单画——v0.2.3 再降一档：
        /// 前版 Medium(500)/伪粗体双画实测仍偏粗，与图标描边（1.8px）视觉不再平衡。
        /// 其他电脑缺字体时静默降级，不会回退成默认丑字体，也不会抛异常。
        /// 思源/等线为 TrueType 各字重独立 family，按 family 名检测存在性。</summary>
        private Font CreateExitFont()
        {
            try
            {
                var families = FontFamily.Families;
                // 1) 思源黑体：商务现代，Regular 字重清爽
                var noto = Array.Find(families, f => string.Equals(f.Name, "Noto Sans SC", StringComparison.OrdinalIgnoreCase));
                if (noto is not null) return new Font(noto, 10f * _s, FontStyle.Regular, GraphicsUnit.Point);
                // 2) 等线：Win10/11 自带
                var deng = Array.Find(families, f => string.Equals(f.Name, "DengXian", StringComparison.OrdinalIgnoreCase));
                if (deng is not null) return new Font(deng, 10f * _s, FontStyle.Regular, GraphicsUnit.Point);
                // 3) 微软雅黑：最通用兜底
                var yahei = Array.Find(families, f => string.Equals(f.Name, "Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(f.Name, "Microsoft YaHei", StringComparison.OrdinalIgnoreCase));
                if (yahei is not null) return new Font(yahei, 10f * _s, FontStyle.Regular, GraphicsUnit.Point);
            }
            catch { /* 字体枚举失败走默认 */ }
            return new Font(FontFamily.GenericSansSerif, 10f * _s, FontStyle.Regular, GraphicsUnit.Point);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Render();
            // 抢占激活：菜单窗收到焦点后，用户点击其他任意窗口/桌面时才会触发
            // OnDeactivate → 关闭（与系统右键菜单"点外即消"行为一致）。
            Activate();
            // 淡入动画 Timer：字段持有防 GC，完成后 Dispose（每次弹菜单一个，不泄漏，B3）。
            _fadeTimer = new System.Windows.Forms.Timer { Interval = 12 };
            var start = DateTime.UtcNow;
            _fadeTimer.Tick += (_, _) =>
            {
                var p = Math.Min(1.0, (DateTime.UtcNow - start).TotalMilliseconds / 120.0);
                _alpha = (byte)(255 * p);
                Render();
                if (p >= 1.0)
                {
                    _fadeTimer.Stop();
                    _fadeTimer.Dispose();
                    _fadeTimer = null;
                }
            };
            _fadeTimer.Start();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            // 关闭时清理：淡入中途关闭的 Timer + 菜单字体（GDI 句柄）
            _fadeTimer?.Stop();
            _fadeTimer?.Dispose();
            _fadeTimer = null;
            _exitFont.Dispose();
            base.OnFormClosed(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        private void Render()
        {
            try
            {
                using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                    Draw(g);
                }
                UpdateLayered(bmp, _alpha);
            }
            catch (Exception ex)
            {
                Trace("tray render failed: " + ex);
            }
        }

        private void Draw(Graphics g)
        {
            float s = _s;
            var content = new Rectangle((int)(Shadow * s), (int)(Shadow * s), (int)(MenuWidth * s), (int)(MenuHeight * s));
            int cr = (int)(CornerRadius * s);

            var item = new Rectangle(content.X + (int)(ItemInset * s), content.Y + (int)(ItemInset * s),
                content.Width - (int)(ItemInset * 2 * s), content.Height - (int)(ItemInset * 2 * s));
        
            // 柔和两级阴影（box-shadow 两级等比缩减；GDI+ 无原生高斯模糊，
            // 用多层扩张圆角矩形模拟衰减）
            DrawShadowLayer(g, content, cr, 5, 8, s);
            DrawShadowLayer(g, content, cr, 2, 3, s);
        
            // 白底 + 1px 边框（.menu: #fff + #E5E7EB）
            using (var bgPath = RoundedRect(content, cr))
            {
                using var bg = new SolidBrush(Color.White);
                g.FillPath(bg, bgPath);
                using var pen = new Pen(BorderColor);
                g.DrawPath(pen, bgPath);
            }
        
            // hover：只铺 .exit 条目区域（内缩 5、圆角 8，与 CSS 一致）
            if (_hoverExit)
            {
                using var hb = new SolidBrush(HoverFill);
                using var hoverPath = RoundedRect(item, (int)(ItemRadius * s));
                g.FillPath(hb, hoverPath);
            }
        
            // 内容：红色电源图标 + 黑色“退出”（13px 常规、字距 2px，紧凑版式）
            int iconSize = (int)(18 * s);
            int gap = (int)(12 * s);
            int letterSpacing = (int)(2 * s);
            var m1 = TextRenderer.MeasureText(g, "退", _exitFont);
            var m2 = TextRenderer.MeasureText(g, "出", _exitFont);
            int totalW = iconSize + gap + m1.Width + letterSpacing + m2.Width;
            int x = item.X + (item.Width - totalW) / 2;
        
            DrawPowerIcon(g, x + iconSize / 2f, item.Y + item.Height / 2f, 5.2f * s, 1.8f * s);
        
            int tx = x + iconSize + gap;
            var r1 = new Rectangle(tx, item.Y, m1.Width + (int)(4 * s), item.Height);
            TextRenderer.DrawText(g, "退", _exitFont, r1, TextBlack, TextFormatFlags.VerticalCenter);
            var r2 = new Rectangle(tx + m1.Width + letterSpacing, item.Y, m2.Width + (int)(4 * s), item.Height);
            TextRenderer.DrawText(g, "出", _exitFont, r2, TextBlack, TextFormatFlags.VerticalCenter);
        }

        /// <summary>电源图标，复刻「电源.svg」（#D81E06，顶部开口圆环 + 圆头竖线）。
        /// 几何按 SVG viewBox(1024) 换算到 18px 图标框并加粗：环中线半径 5.2px、线宽 1.8px；
        /// 开口 234°–305°（约 71°，居中正上方）；竖线从环顶上方伸到中心上方（r×1.22 → r×0.23）。
        /// 用 Pen 描边而不是 FillPath 双圆弧拼环体——拼环的起弧角度/填充模式易错
        /// （曾把开口画到正右方渲染成“C”），描边对任意 DPI/缩放都稳定。</summary>
        private static void DrawPowerIcon(Graphics g, float cx, float cy, float r, float stroke)
        {
            using var pen = new Pen(TextDanger, stroke)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };
            // 开口在正上方：从 305° 顺时针扫 289° 到 234°（GDI+ 角度 0°=3 点钟方向、
            // 顺时针为正，270° 即正上方）
            g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 305f, 289f);
            // 圆头竖线：上端超出环外顶 0.6px（r×1.22），下端到中心上方 1.3px（r×0.23）
            g.DrawLine(pen, cx, cy - r * 1.22f, cx, cy - r * 0.23f);
        }

        /// <summary>多层扩张圆角矩形模拟柔和投影（dy 垂直偏移、spread 最大扩散，均为逻辑 px）。</summary>
        private static void DrawShadowLayer(Graphics g, Rectangle content, int cr, int dy, int spread, float s)
        {
            const int steps = 6;
            for (int i = steps; i >= 1; i--)
            {
                int e = (int)(spread * s * i / steps);
                var r = Rectangle.Inflate(content, e, e);
                r.Offset(0, (int)(dy * s));
                using var b = new SolidBrush(Color.FromArgb(6, 0, 0, 0));
                using var p = RoundedRect(r, cr + e);
                g.FillPath(b, p);
            }
        }

        private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            var d = radius * 2;
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private bool HitExit(Point p) => new Rectangle((int)((Shadow + ItemInset) * _s), (int)((Shadow + ItemInset) * _s),
            (int)((MenuWidth - ItemInset * 2) * _s), (int)((MenuHeight - ItemInset * 2) * _s)).Contains(p);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var h = HitExit(e.Location);
            if (h != _hoverExit) { _hoverExit = h; Render(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverExit) { _hoverExit = false; Render(); }
            base.OnMouseLeave(e);
        }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HitExit(e.Location)) { Close(); _onExit(); return; }
            base.OnMouseClick(e);
        }
        protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Close(); }
        // 失效关闭能生效的前提：OnShown 里 Activate() 抢占激活（菜单窗从未被激活过
        // 则永远不会收到 Deactivate——0.2.3 前"点外不消失"的根因）。
        protected override bool ProcessDialogKey(Keys keyData)
        {
            if (keyData == Keys.Escape) { Close(); return true; }
            return base.ProcessDialogKey(keyData);
        }

        // ---- LayeredWindow ----
        private void UpdateLayered(Bitmap bmp, byte alpha)
        {
            IntPtr screenDc = GetDC(IntPtr.Zero);
            IntPtr memDc = CreateCompatibleDC(screenDc);
            IntPtr hBitmap = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
                old = SelectObject(memDc, hBitmap);
                var ptDst = new POINT { X = Left, Y = Top };
                var size = new SIZE { Width = Width, Height = Height };
                var ptSrc = new POINT { X = 0, Y = 0 };
                var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, 2);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(memDc, old);
                if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
                DeleteDC(memDc);
                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE { public int Width, Height; }
        [StructLayout(LayoutKind.Sequential)]
        private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);
        [System.Runtime.InteropServices.DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
    }
    /// <summary>
    /// 显示并置顶主窗口（托盘左键单击 / 菜单唤起）：开着就提到最上层并聚焦，
    /// 隐藏着就显示出来；**最小化时先还原**（Activate 对最小化窗口无效，
    /// 此前最小化后单击托盘"点不回来"）；含 WebView2 崩溃/长隐藏恢复。
    /// </summary>
    private static void ShowMainWindow(Form form)
    {
        if (!form.Visible) form.Show();
        if (form.WindowState == FormWindowState.Minimized)
        {
            // 最小化 → 还原（SW_RESTORE），否则 Activate 无效，窗口不会出现
            ShowWindow(form.Handle, SW_RESTORE);
            form.WindowState = FormWindowState.Normal;
        }
        form.Activate();
        // WebView2 在窗口隐藏期间可能出问题导致恢复后白屏：
        // - 渲染/GPU 进程崩溃（ProcessFailed 已置标志）→ 延迟重载页面恢复
        // - 隐藏超过 5 分钟，渲染进程可能被系统回收（无崩溃事件）→ 强制重载兜底
        // 隐藏状态下 Reload 无效；且刚显示时立即 Reload 与 WebView2 的可见性处理
        // 存在竞态（实测隐藏→恢复→立即 Reload 后进程崩溃），必须延迟执行。
        var longHidden = _hiddenSince != DateTime.MinValue
            && DateTime.Now - _hiddenSince >= TimeSpan.FromMinutes(5);
        if (_webviewRecoveryNeeded || longHidden)
        {
            _webviewRecoveryNeeded = false;
            _hiddenSince = DateTime.MinValue;
            _ = TryReloadWebViewDeferred(form); // fire-and-forget：不等待结果
        }
    }

    /// <summary>
    /// 延迟重载主窗口 WebView2 页面（隐藏/显示后的崩溃恢复）。延迟 500ms 等窗口
    /// 可见性处理完成；期间窗口若再次隐藏/关闭则放弃本次重载并留待下次恢复（标志复位）。
    /// </summary>
    private static async Task TryReloadWebViewDeferred(Form form)
    {
        try
        {
            await Task.Delay(500);
            if (form.IsDisposed || !form.Visible || _mainWeb is { IsDisposed: true })
            {
                // 窗口又隐藏/关闭了：下次恢复窗口时再处理
                _webviewRecoveryNeeded = true;
                return;
            }
            if (_mainWeb?.CoreWebView2 is not null)
            {
                Trace("tray restore: reloading webview after process failure (deferred)");
                _mainWeb.CoreWebView2.Reload();
            }
        }
        catch
        {
            // 重载失败静默（页面可能已自行恢复）
        }
    }

    /// <summary>
    /// 服务启动状态窗：显示"正在启动 dsh 服务"（含首次下载提示；v0.3.0 亦可显示
    /// 便携 Node 下载进度文案），可取消。由外部任务完成后调用 Close() 自动关闭；
    /// 取消按钮设 DialogResult.Cancel 并关闭。
    /// </summary>
    private static Form CreateStartupStatusForm(string? caption = null)
    {
        var f = new Form
        {
            // 标题不用主窗口的"DeepSeek Harness"：单实例逻辑按标题找主窗口，
            // 避免第二次点击把状态窗误当成主窗口聚焦（表现为"点了两次没反应"）。
            Text = "dsh-launcher 启动中",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(440, 150),
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            ControlBox = false,
        };
        var label = new Label
        {
            Text = caption ?? "正在启动 dsh 服务…\n首次运行需要下载 dsh 组件，可能需要几分钟。\n完成后会自动打开窗口，请稍候。",
            Location = new Point(20, 18),
            AutoSize = true,
        };
        var bar = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 30,
            Location = new Point(20, 78),
            Size = new Size(400, 16),
        };
        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(350, 110),
            Size = new Size(70, 26),
        };
        cancel.Click += (_, _) =>
        {
            f.DialogResult = DialogResult.Cancel;
            f.Close();
        };
        f.Controls.Add(label);
        f.Controls.Add(bar);
        f.Controls.Add(cancel);
        return f;
    }

    /// <summary>
    /// 统一的 WebView2 初始化：设置 + 权限 + 下载 + 弹窗 + 崩溃自愈。
    /// 主窗口与插件弹出的内部窗口共用，保证行为一致。
    /// </summary>
    private static async Task InitWebViewAsync(WebView2 web, string userDataFolder)
    {
        var env = await GetSharedEnvironmentAsync(userDataFolder);
        await web.EnsureCoreWebView2Async(env);

        var settings = web.CoreWebView2.Settings;
        settings.AreDefaultContextMenusEnabled = true;   // 保留右键菜单（复制/粘贴等）
        settings.AreDevToolsEnabled = true;              // 保留 F12（仅实际打开时才占用内存）
        settings.IsGeneralAutofillEnabled = false;       // 关闭表单自动填充，减少后台开销
        settings.IsPasswordAutosaveEnabled = false;      // 不保存密码

        // 权限：自动放行插件/DSH 依赖的能力（见 ShellLogic.IsAutoGrantedPermission），
        // 其余保持默认拒绝。麦克风/摄像头默认拒绝（隐私），将来有语音类插件再改为弹窗询问。
        web.CoreWebView2.PermissionRequested += (_, e) =>
        {
            if (ShellLogic.IsAutoGrantedPermission(e.PermissionKind))
                e.State = CoreWebView2PermissionState.Allow;
        };

        // 导航白名单（S3）：主窗口/内部弹窗只允许本地（127.0.0.1/localhost）导航；
        // 外部 http(s) 导航一律取消并转系统默认浏览器——壳无地址栏，防止被重定向到
        // 伪站点，且外部页会拿到已自动放行的剪贴板/存储等权限（白名单之外不生效）。
        web.CoreWebView2.NavigationStarting += (_, e) =>
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri)) return;
            if (uri.Scheme is not ("http" or "https")) return;   // about:/blob:/data: 等内部资源放行
            if (uri.Host is "127.0.0.1" or "localhost") return;  // 本地 dsh 服务
            e.Cancel = true;
            try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
        };

        // 下载：固定保存到系统“下载”文件夹（自动避开同名文件），完成后用默认程序打开
        web.CoreWebView2.DownloadStarting += (_, e) =>
        {
            try
            {
                var downloads = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                Directory.CreateDirectory(downloads);
                var name = ShellLogic.SanitizeFileName(ShellLogic.SuggestDownloadName(
                    e.DownloadOperation.ContentDisposition, e.DownloadOperation.Uri, e.DownloadOperation.MimeType));
                var path = Path.Combine(downloads, name);
                for (var i = 1; File.Exists(path); i++)
                    path = Path.Combine(downloads,
                        $"{Path.GetFileNameWithoutExtension(name)} ({i}){Path.GetExtension(name)}");
                e.Handled = true;   // 禁用 WebView2 默认下载对话框
                e.ResultFilePath = path;
                e.DownloadOperation.StateChanged += (_, _) =>
                {
                    if (e.DownloadOperation.State == CoreWebView2DownloadState.Completed)
                    {
                        try
                        {
                            // 仅无害扩展名（图片/文本/pdf 等）自动打开；其余（.html/.svg/.hta/.exe 等
                            // 可执行代码面）只落盘 + 气泡提示，不自动执行，防恶意下载自动运行（S2 修复）。
                            if (ShellLogic.IsSafeToOpen(e.DownloadOperation.ResultFilePath))
                            {
                                Process.Start(new ProcessStartInfo(e.DownloadOperation.ResultFilePath) { UseShellExecute = true });
                            }
                            else
                            {
                                NotifyDownloadComplete(e.DownloadOperation.ResultFilePath);
                            }
                        }
                        catch { /* 无默认程序打开时忽略 */ }
                    }
                };
            }
            catch { /* 处理失败时回退 WebView2 默认下载行为 */ }
        };

        // 弹窗策略（分类逻辑见 ShellLogic.ClassifyPopup）：
        // - 外部 http(s) 链接 → 系统默认浏览器
        // - 同源 http(s) 弹窗 → 新建轻量壳窗口（保留会话，避免主窗口被导航走）
        // - blob: / data: / about: 等 → WebView2 默认行为（插件生成的预览等）
        web.CoreWebView2.NewWindowRequested += async (_, e) =>
        {
            switch (ShellLogic.ClassifyPopup(e.Uri))
            {
                case ShellLogic.PopupTarget.External:
                    e.Handled = true;
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); } catch { }
                    return;
                case ShellLogic.PopupTarget.Internal:
                {
                    var deferral = e.GetDeferral();
                    try
                    {
                        var popup = CreatePopupForm();
                        await InitWebViewAsync(popup.Web, userDataFolder);
                        popup.Web.CoreWebView2.DocumentTitleChanged += (_, _) =>
                        {
                            var title = popup.Web.CoreWebView2.DocumentTitle;
                            if (!string.IsNullOrWhiteSpace(title)) popup.Form.Text = title;
                        };
                        e.NewWindow = popup.Web.CoreWebView2;
                        popup.Form.Show();
                    }
                    finally { deferral.Complete(); }
                    return;
                }
                default:
                    return;
            }
        };

        // 渲染进程/GPU 进程崩溃或无响应：记下崩溃痕迹，窗口可见时自动重载避免白屏
        //（每 10 秒最多一次，防止崩溃死循环）。窗口隐藏期间的崩溃不立即 Reload——
        // 隐藏状态下 Reload 无效，等托盘恢复窗口时由 ShowMainWindow 兜底重载。
        web.CoreWebView2.ProcessFailed += (_, e) =>
        {
            Trace($"webview process failed: {e.ProcessFailedKind}");
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.GpuProcessExited)
            {
                _webviewRecoveryNeeded = true;
                if (_mainWeb is { Visible: true, IsDisposed: false })
                {
                    var now = Environment.TickCount64;
                    var last = Interlocked.Read(ref _lastReloadTick);
                    if (now - last > 10_000
                        && Interlocked.CompareExchange(ref _lastReloadTick, now, last) == last)
                    {
                        try { web.CoreWebView2.Reload(); } catch { }
                    }
                }
            }
        };
    }

    /// 插件内部弹窗用的轻量窗口（与主窗口共享 WebView2 用户数据，保持登录态/会话）。
    private static (Form Form, WebView2 Web) CreatePopupForm()
    {
        var popupWeb = new WebView2();
        var form = new DshShellForm
        {
            // 初始标题区别于主窗口（"DeepSeek Harness"）：单实例逻辑按标题找主窗口，
            // 弹窗开着时第二实例不会被误聚焦到 popup（B2）。页面加载后 DocumentTitle
            // 会覆盖成实际页面标题。
            Text = "dsh-launcher 弹窗",
            ClientSize = new Size(900, 640),
            StartPosition = FormStartPosition.CenterParent,
            FormBorderStyle = FormBorderStyle.None, // 自绘标题栏（与主窗口一致，主题即时切换）
            Icon = SystemIcons.Application
        };
        var titleHeight = (int)Math.Round(32 * form.DeviceDpi / 96f);
        form.TitleBar = new CustomTitleBar(form, ResolveDarkMode())
        {
            // 四周 1px 窗口边框（Form.BackColor=边框色）
            Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2, titleHeight),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        form.Controls.Add(form.TitleBar);
        form.HandleCreated += (_, _) => ApplyWindowShadow(form.Handle);
        popupWeb.Bounds = new Rectangle(1, 1 + titleHeight, form.ClientSize.Width - 2, form.ClientSize.Height - titleHeight - 2);
        popupWeb.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        form.DpiChanged += (_, _) =>
        {
            var scale = form.DeviceDpi / 96f;
            var h = (int)Math.Round(32 * scale);
            form.TitleBar.Rescale(scale);
            form.TitleBar.Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2, h);
            popupWeb.Bounds = new Rectangle(1, 1 + h, form.ClientSize.Width - 2, form.ClientSize.Height - h - 2);
        };
        form.Controls.Add(popupWeb);
        form.FormClosing += (_, _) =>
        {
            try { popupWeb.Dispose(); } catch { /* ignore */ }
        };
        return (form, popupWeb);
    }

    /// <summary>从嵌入资源按资源名后缀加载图标（favicon.png 深色鲸鱼 / favicon-white.png 白色鲸鱼）。</summary>
    private static Icon? LoadIconResource(string resourceSuffix)
    {
        try
        {
            var name = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (stream is null) return null;
            using var bmp = new Bitmap(stream);
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch
        {
            return null;
        }
    }

    /// <summary>深色鲸鱼图标（窗口浅色主题/任务栏浅色时用）。</summary>
    private static Icon? _darkWhaleIcon;

    /// <summary>白色鲸鱼图标（窗口深色主题/托盘深色背景时用）。</summary>
    private static Icon? _lightWhaleIcon;

    /// <summary>蓝色鲸鱼图标（托盘/任务栏按钮固定用：DeepSeek 蓝 #4D6BFE，深浅背景都清晰）。</summary>
    private static Icon? _blueWhaleIcon;

    /// <summary>检测系统应用深色模式（注册表 AppsUseLightTheme=0）。</summary>
    private static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取 dsh 前端的主题选择（DSH_HOME/settings.yaml 的 ui-theme.preference）。
    /// 严格限定在 ui-theme 段内查找，避免误读其他段（如 agent-default-model 等）的 preference 键。</summary>
    private static string? ReadDshThemePreference()
    {
        try
        {
            var yaml = Path.Combine(DshHomeDir, "settings.yaml");
            if (!File.Exists(yaml)) return null;
            var inUiTheme = false;
            foreach (var raw in File.ReadAllLines(yaml))
            {
                var t = raw.Trim();
                if (t.Length == 0 || t.StartsWith('#')) continue;
                if (t.StartsWith("ui-theme:", StringComparison.Ordinal))
                {
                    inUiTheme = true;
                    continue;
                }
                if (inUiTheme)
                {
                    if (t.StartsWith("preference:", StringComparison.Ordinal))
                        return t["preference:".Length..].Trim().Trim('"', '\'').ToLowerInvariant();
                    // 遇到下一段（无缩进的顶层键）则离开 ui-theme 段
                    if (!raw.StartsWith(' ') && !raw.StartsWith('\t'))
                        inUiTheme = false;
                }
            }
        }
        catch
        {
            // 读取失败回退系统主题
        }
        return null;
    }

    /// <summary>
    /// 解析壳的主题：以用户的选择为主——dsh 前端设置页里的主题选择
    /// （ui-theme.preference: dark / light / system）优先；system 或未设置时跟随系统深色模式。
    /// </summary>
    private static bool ResolveDarkMode()
    {
        var pref = ReadDshThemePreference();
        if (pref == "dark") return true;
        if (pref == "light") return false;
        return IsSystemDarkMode();
    }

    /// <summary>白色鲸鱼（托盘/任务栏固定用，深色鲸鱼在深色背景上看不清）。</summary>
    /// <summary>蓝色鲸鱼（托盘/任务栏按钮固定用：DeepSeek 蓝 #4D6BFE，深浅背景都清晰；
    /// 不用白色——白色在浅色背景/浅色任务栏上看不清，蓝色则始终可见）。</summary>
    private static Icon? TrayWhaleIcon => _blueWhaleIcon ??= LoadIconResource("favicon-blue.png");

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr rectUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const uint RDW_INVALIDATE = 0x0001;
    private const uint RDW_UPDATENOW = 0x0100;
    private const uint RDW_FRAME = 0x0400;
    private const uint RDW_ALLCHILDREN = 0x0080;

    private const int WM_NCPAINT = 0x0085;
    private const int WM_NCACTIVATE = 0x0086;
    private const int WM_SETTINGCHANGE = 0x001A;
    private const int SPI_SETNONCLIENTMETRICS = 0x002A;
    private const int GWL_STYLE = -16;

    /// <summary>
    /// 强制标题栏深色/浅色（Win10 1809+ 的沉浸式深色标题栏）：让标题栏与图标/前端主题
    /// 保持一致。DWM 属性设置后标题栏**不会自动重绘**（表现为"切换没反应，点走再点回来
    /// 才变"；实测本机 SWP_FRAMECHANGED/RedrawWindow/DwmFlush/WM_NCPAINT 均不触发）。
    /// 追加两记重手段：①同值重设窗口样式（SetWindowLongPtr 强制系统重算窗口帧）；
    /// ②广播 WM_SETTINGCHANGE(SPI_SETNONCLIENTMETRICS)（系统级非客户区设置变更通知）。
    /// </summary>
    private static void SetTitleBarDark(Form form, bool dark)
    {
        try
        {
            if (form.Handle == IntPtr.Zero) return;
            var value = dark ? 1 : 0;
            var hr = DwmSetWindowAttribute(form.Handle, 20, ref value, sizeof(int));
            if (hr != 0)
            {
                hr = DwmSetWindowAttribute(form.Handle, 19, ref value, sizeof(int)); // Win10 1809 用 19
            }
            if (hr != 0)
                Trace($"title bar dark set failed hr=0x{hr:X8} dark={dark}");
            DwmGetWindowAttribute(form.Handle, 20, out var actual, sizeof(int));
            Trace($"title bar dark: set dark={dark} hr=0x{hr:X8} actual={actual}");
            // 组合拳：窗口帧重算 + 非客户区重绘 + 系统设置变更广播
            SetWindowPos(form.Handle, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            RedrawWindow(form.Handle, IntPtr.Zero, IntPtr.Zero,
                RDW_FRAME | RDW_INVALIDATE | RDW_UPDATENOW | RDW_ALLCHILDREN);
            try { DwmFlush(); } catch { }
            SendMessage(form.Handle, WM_NCPAINT, (IntPtr)1, IntPtr.Zero);
            // 同值重设窗口样式：强制系统重算窗口帧（SWP_FRAMECHANGED 在本机无效）
            try
            {
                var style = GetWindowLongPtr(form.Handle, GWL_STYLE);
                if (style != IntPtr.Zero)
                    SetWindowLongPtr(form.Handle, GWL_STYLE, style);
            }
            catch { }
            SendMessage(form.Handle, WM_SETTINGCHANGE, (IntPtr)SPI_SETNONCLIENTMETRICS, IntPtr.Zero);
            // 模拟"点走再点回来"：焦点变化时系统正是发 WM_NCACTIVATE 触发非客户区重绘，
            // 手动发一条（wParam=1 激活态）让标题栏按当前 DWM 属性立即重绘，不改变真实焦点。
            SendMessage(form.Handle, WM_NCACTIVATE, (IntPtr)1, IntPtr.Zero);
        }
        catch
        {
            // 标题栏配色失败不影响功能
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    private static extern IntPtr TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 0x0002;

    /// <summary>给无边框窗口加 DWM 阴影（DWMWA_NCRENDERING_POLICY=ENABLED）。</summary>
    private static void ApplyWindowShadow(IntPtr hwnd)
    {
        try
        {
            const int DWMWA_NCRENDERING_POLICY = 2;
            const int DWMNCRP_ENABLED = 2;
            var policy = DWMNCRP_ENABLED;
            DwmSetWindowAttribute(hwnd, DWMWA_NCRENDERING_POLICY, ref policy, sizeof(int));
        }
        catch { /* 阴影失败不影响功能 */ }
    }

    /// <summary>
    /// 自绘标题栏（无边框窗口用）：背景/文字/按钮颜色完全自绘，主题切换即时生效，
    /// 不依赖 DWM 标题栏重绘（实测本机 DWM 属性切换后标题栏画面不刷新，只有焦点变化才重绘）。
    /// 提供：标题 + 主题鲸鱼图标 + 最小化/最大化/关闭按钮 + 拖拽移动 + 双击最大化 + 右键系统菜单。
    /// </summary>
    private sealed class CustomTitleBar : Panel
    {
        private readonly Form _owner;
        private float _scale;
        private int _btnWidth;
        private bool _dark;
        private bool _hoverMin, _hoverMax, _hoverClose;

        private static readonly Font TitleFont = new("Microsoft YaHei UI", 9F);
        private static readonly Color DarkBg = Color.FromArgb(32, 32, 32);
        private static readonly Color LightBg = Color.FromArgb(240, 240, 240);
        private static readonly Color DarkText = Color.White;
        private static readonly Color LightText = Color.FromArgb(30, 30, 30);
        private static readonly Color DarkHover = Color.FromArgb(58, 58, 58);
        private static readonly Color LightHover = Color.FromArgb(229, 229, 229);
        private static readonly Color CloseHover = Color.FromArgb(232, 17, 35);

        public CustomTitleBar(Form owner, bool dark)
        {
            _owner = owner;
            _dark = dark;
            // DPI 缩放：150% 缩放下 32px 物理高度会显得又矮又挤（按钮/图标/间距全按逻辑缩放）
            _scale = owner.DeviceDpi / 96f;
            _btnWidth = (int)Math.Round(46 * _scale);
            BackColor = _dark ? DarkBg : LightBg;
            MouseDown += OnMouseDown;
            MouseUp += OnMouseUp;
            MouseDoubleClick += OnDoubleClick;
            MouseMove += OnMouseMove;
            MouseLeave += (_, _) =>
            {
                if (_hoverMin || _hoverMax || _hoverClose)
                {
                    _hoverMin = _hoverMax = _hoverClose = false;
                    Invalidate();
                }
            };
        }

        /// <summary>主题切换：自绘颜色立即更新（无 DWM 重绘问题）。</summary>
        public void ApplyTheme(bool dark)
        {
            _dark = dark;
            BackColor = _dark ? DarkBg : LightBg;
            Invalidate();
        }

        /// <summary>DPI 变化时重算缩放比例与按钮宽度。</summary>
        public void Rescale(float scale)
        {
            _scale = scale;
            _btnWidth = (int)Math.Round(46 * _scale);
            Invalidate();
        }

        private Rectangle BtnRect(int i) => new(Width - _btnWidth * (3 - i), 0, _btnWidth, Height);

        private int HitButton(int x)
        {
            for (var i = 0; i < 3; i++)
                if (BtnRect(i).Contains(x, Height / 2)) return i;
            return -1;
        }

        private void OnMouseDown(object? s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                ShowSystemMenu(e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (HitButton(e.X) >= 0) return; // 按钮点击交给 MouseUp
            // 拖拽移动窗口（系统级 HTCAPTION 拖拽）
            ReleaseCapture();
            SendMessage(_owner.Handle, (uint)WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }

        private void OnMouseUp(object? s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            switch (HitButton(e.X))
            {
                case 0: _owner.WindowState = FormWindowState.Minimized; break;
                case 1:
                    _owner.WindowState = _owner.WindowState == FormWindowState.Maximized
                        ? FormWindowState.Normal : FormWindowState.Maximized;
                    break;
                case 2: _owner.Close(); break;
            }
        }

        private void OnDoubleClick(object? s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left && HitButton(e.X) < 0)
                _owner.WindowState = _owner.WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal : FormWindowState.Maximized;
        }

        private void OnMouseMove(object? s, MouseEventArgs e)
        {
            var btn = HitButton(e.X);
            var h1 = btn == 0;
            var h2 = btn == 1;
            var h3 = btn == 2;
            if (h1 != _hoverMin || h2 != _hoverMax || h3 != _hoverClose)
            {
                _hoverMin = h1;
                _hoverMax = h2;
                _hoverClose = h3;
                Invalidate();
            }
        }

        private void ShowSystemMenu(Point p)
        {
            try
            {
                var hMenu = GetSystemMenu(_owner.Handle, false);
                if (hMenu == IntPtr.Zero) return;
                TrackPopupMenu(hMenu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                    _owner.Left + p.X, _owner.Top + p.Y, 0, _owner.Handle, IntPtr.Zero);
            }
            catch { /* 系统菜单失败忽略 */ }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(_dark ? DarkBg : LightBg);
            var textColor = _dark ? DarkText : LightText;

            // 标题栏图标（主题对应鲸鱼，按 DPI 缩放）
            var icon = _dark
                ? (_lightWhaleIcon ??= LoadIconResource("favicon-white.png"))
                : (_darkWhaleIcon ??= LoadIconResource("favicon.png"));
            var iconSize = (int)Math.Round(16 * _scale);
            if (icon is not null)
            {
                g.DrawIcon(icon, new Rectangle((int)Math.Round(10 * _scale), (Height - iconSize) / 2, iconSize, iconSize));
            }

            // 标题
            var titleLeft = (int)Math.Round(34 * _scale);
            TextRenderer.DrawText(g, "DeepSeek Harness", TitleFont,
                new Rectangle(titleLeft, 0, Math.Max(0, Width - _btnWidth * 3 - titleLeft - 8), Height),
                textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

            // 窗口按钮：用 Segoe MDL2 字形（最小化/最大化/还原/关闭），清晰且与系统图标一致
            using (var btnFont = new Font("Segoe MDL2 Assets", (float)Math.Round(11 * _scale), FontStyle.Regular, GraphicsUnit.Pixel))
            {
                for (var i = 0; i < 3; i++)
                {
                    var r = BtnRect(i);
                    var hover = (i == 0 && _hoverMin) || (i == 1 && _hoverMax) || (i == 2 && _hoverClose);
                    if (hover)
                    {
                        using var hb = new SolidBrush(i == 2 ? CloseHover : (_dark ? DarkHover : LightHover));
                        g.FillRectangle(hb, r);
                    }
                    var glyph = i switch
                    {
                        0 => '\uE921', // Minimize
                        1 => _owner.WindowState == FormWindowState.Maximized ? '\uE923' : '\uE922', // Restore / Maximize
                        _ => '\uE8BB', // ChromeClose
                    };
                    var glyphColor = hover && i == 2 && _dark ? Color.White : textColor;
                    TextRenderer.DrawText(g, glyph.ToString(), btnFont, r, glyphColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
            }

            // 底部细分隔线
            using var line = new Pen(_dark ? Color.FromArgb(48, 48, 48) : Color.FromArgb(225, 225, 225));
            g.DrawLine(line, 0, Height - 1, Width, Height - 1);
        }
    }

    /// <summary>
    /// 无边框主窗口：处理最大化限制在工作区（WM_GETMINMAXINFO）与边缘缩放（WM_NCHITTEST）。
    /// 标题栏由 <see cref="CustomTitleBar"/> 自绘。
    /// </summary>
    private sealed class DshShellForm : Form
    {
        internal CustomTitleBar? TitleBar;

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_NCHITTEST = 0x0084;
        // Aero Snap（拖到屏幕边缘的半屏/最大化、Win+方向键）依赖 WS_CAPTION|WS_THICKFRAME
        // 样式位；FormBorderStyle.None 会把它们剥掉（0.1.10 自绘标题栏后贴边失效的根因）。
        // 方案：样式位加回来，再用 WM_NCCALCSIZE 吃掉原生框架预留区，观感仍是全自绘无边框
        //（Chromium / Windows Terminal 同款做法）。
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WS_CAPTION = 0x00C00000;
        private const int WS_THICKFRAME = 0x00040000;
        private const int HTCLIENT = 0x0001;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;
        private const int ResizeEdge = 8;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                // 加回 WS_CAPTION|WS_THICKFRAME：恢复 Aero Snap / Win+方向键 / 系统窗口动画与
                // 任务栏正常交互（FormBorderStyle.None 默认全部剥掉）。原生标题栏/边框区域
                // 由 WM_NCCALCSIZE 移除，自绘标题栏与 1px 边框视觉不变。
                var cp = base.CreateParams;
                cp.Style |= WS_CAPTION | WS_THICKFRAME;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_NCCALCSIZE:
                    // wParam=TRUE：把客户区设为整个窗口矩形（吃掉系统标题栏/边框预留，
                    // 自绘标题栏照常占据客户区顶部）。不加此处理，窗口顶部会被原生
                    // 标题栏顶下来、四周出现原生边框。wParam=FALSE（初次计算）走默认。
                    if (m.WParam != IntPtr.Zero)
                    {
                        m.Result = IntPtr.Zero;
                        return;
                    }
                    base.WndProc(ref m);
                    return;
                case WM_GETMINMAXINFO:
                {
                    var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
                    var wa = Screen.FromHandle(Handle).WorkingArea;
                    mmi.ptMaxSize.X = wa.Width;
                    mmi.ptMaxSize.Y = wa.Height;
                    mmi.ptMaxPosition.X = wa.Left;
                    mmi.ptMaxPosition.Y = wa.Top;
                    Marshal.StructureToPtr(mmi, m.LParam, false);
                    m.Result = IntPtr.Zero;
                    return;
                }
                case WM_NCHITTEST:
                    base.WndProc(ref m);
                    if (m.Result == (IntPtr)HTCLIENT && WindowState != FormWindowState.Maximized)
                    {
                        // 64 位屏幕坐标：左侧/上方副屏为负坐标，LParam.ToInt32() 会抛 OverflowException
                        //（B1）。正确拆位：低 16 位有符号 = X，高 16 位有符号 = Y。
                        var (x, y) = ShellLogic.SplitLParam(m.LParam.ToInt64());
                        var pt = new Point(x, y);
                        var r = RectangleToScreen(ClientRectangle);
                        var left = pt.X < r.Left + ResizeEdge;
                        var right = pt.X > r.Right - ResizeEdge;
                        var top = pt.Y < r.Top + ResizeEdge;
                        var bottom = pt.Y > r.Bottom - ResizeEdge;
                        if (left && top) m.Result = (IntPtr)HTTOPLEFT;
                        else if (right && top) m.Result = (IntPtr)HTTOPRIGHT;
                        else if (left && bottom) m.Result = (IntPtr)HTBOTTOMLEFT;
                        else if (right && bottom) m.Result = (IntPtr)HTBOTTOMRIGHT;
                        else if (left) m.Result = (IntPtr)HTLEFT;
                        else if (right) m.Result = (IntPtr)HTRIGHT;
                        else if (top) m.Result = (IntPtr)HTTOP;
                        else if (bottom) m.Result = (IntPtr)HTBOTTOM;
                    }
                    return;
                default:
                    base.WndProc(ref m);
                    return;
            }
        }
    }

    /// <summary>标题栏小图标消息（WM_SETICON + ICON_SMALL）。
    /// <para><b>固定白色鲸鱼</b>：Windows 11 任务栏按钮读取的是小图标（ICON_SMALL），
    /// 若跟随主题（浅色 → 深色鲸鱼）任务栏 logo 会变黑——因此小图标恒为白色，
    /// 与托盘一致；标题栏内的鲸鱼由自绘 OnPaint 跟随主题，不受此影响。</para></summary>
    private static void SetTitleBarIcon(Form form)
    {
        try
        {
            if (form.Handle == IntPtr.Zero) return;
            var icon = _blueWhaleIcon ??= LoadIconResource("favicon-blue.png");
            if (icon is not null)
                SendMessage(form.Handle, 0x0080 /* WM_SETICON */, (IntPtr)0 /* ICON_SMALL */, icon.Handle);
        }
        catch
        {
            // 标题栏图标设置失败不影响功能
        }
    }

    /// <summary>
    /// 应用主题（以用户的选择为主——dsh 前端主题设置，其次跟随系统）：
    /// - **系统任务栏图标 + 托盘图标：固定白色鲸鱼**（Windows 11 任务栏按钮读 ICON_SMALL，
    ///   因此小图标也固定白色，任何主题下任务栏 logo 始终为白；任务栏/托盘多为深色背景，
    ///   深色鲸鱼看不清）
    /// - **窗口标题栏**：自绘鲸鱼图标跟随主题（深色 → 白色鲸鱼，浅色 → 深色鲸鱼），
    ///   标题栏背景用 DWM 沉浸式深色/浅色（DwmSetWindowAttribute）
    /// - 主题状态写入 <c>theme.json</c>（插件设置页可读取显示当前情况）
    /// </summary>
    private static void ApplyThemeIcon(Form form)
    {
        var dark = ResolveDarkMode();
        try { form.Icon = TrayWhaleIcon ?? SystemIcons.Application; } catch { /* ignore */ }
        SetTitleBarIcon(form);
        // 自绘标题栏主题（主窗口/弹窗）：自绘颜色即时生效，无 DWM 重绘问题
        if (form is DshShellForm sf && sf.TitleBar is not null)
        {
            sf.TitleBar.ApplyTheme(dark);
            // 窗口 1px 边框色（替代阴影的质感）：深色比标题栏亮一档、浅色比标题栏深一档
            try { form.BackColor = dark ? Color.FromArgb(58, 58, 58) : Color.FromArgb(208, 208, 208); } catch { }
        }
        else
        {
            SetTitleBarDark(form, dark); // 兜底：未自绘标题栏的窗口
        }
        if (_trayIcon is not null)
        {
            try { _trayIcon.Icon = TrayWhaleIcon ?? SystemIcons.Application; } catch { /* ignore */ }
        }
        WriteThemeState(dark);
    }

    /// <summary>把壳当前的主题判定写入 theme.json，供插件设置页显示诊断（"现在是什么情况"）。</summary>
    private static void WriteThemeState(bool dark)
    {
        try
        {
            var state = "{\"preference\":" + System.Text.Json.JsonSerializer.Serialize(ReadDshThemePreference())
                + ",\"resolved\":\"" + (dark ? "dark" : "light")
                + "\",\"at\":\"" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "\"}";
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(Path.Combine(DataDir, "theme.json"), state);
        }
        catch
        {
            // 状态写入失败不影响功能
        }
    }

    /// <summary>
    /// 主题实时刷新（无感切换）：dsh 前端写 settings.yaml 时 FileSystemWatcher 立即触发
    /// （Changed + Renamed 都监听——dsh 可能原子替换写文件），500ms 轮询兜底
    /// （watcher 漏事件也能 2 个周期内赶上）。两路共用同一去抖状态：
    /// 主题结果变化才应用（换图标/标题栏会让任务栏重绘，频繁触发会造成"一卡一卡"）。
    /// </summary>
    private static void RegisterThemeWatcher(Form form)
    {
        var lastDark = ResolveDarkMode();
        void ApplyIfThemeChanged()
        {
            try
            {
                var nowDark = ResolveDarkMode();
                if (nowDark != lastDark)
                {
                    lastDark = nowDark;
                    ApplyThemeIcon(form);
                    Trace($"theme changed: {(nowDark ? "dark" : "light")}");
                }
            }
            catch
            {
                // 轮询失败下次再试
            }
        }

        try
        {
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
                    ApplyIfThemeChanged();
            };
        }
        catch
        {
            // 系统主题监听失败不影响启动
        }

        try
        {
            var dir = DshHomeDir;
            if (Directory.Exists(dir))
            {
                var watcher = new FileSystemWatcher(dir, "settings.yaml")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true,
                };
                watcher.Changed += (_, _) => { try { form.BeginInvoke(ApplyIfThemeChanged); } catch { } };
                watcher.Renamed += (_, _) => { try { form.BeginInvoke(ApplyIfThemeChanged); } catch { } };
            }
        }
        catch
        {
            // 文件监听失败不影响启动（轮询兜底）
        }

        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        timer.Tick += (_, _) => ApplyIfThemeChanged();
        timer.Start();
    }

    private static bool PortOpen(int port)
    {
        try
        {
            using var c = new TcpClient();
            c.Connect("127.0.0.1", port);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
