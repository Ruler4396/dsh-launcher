using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using DshWeb.Chrome; // CustomTitleBar / WindowChromeController（自绘标题栏已迁出至 Chrome 层）
using DshWeb.Managers; // F11LowLevelHook（F11 钩子已迁出至 Managers 层）
using DshWeb.Win32; // Win32Constants/NativeMethods（结构体与 P/Invoke 已迁出）
using DshWeb.Windows; // DshShellForm / TrayMenuForm（窗体类已迁出至 Windows 层）
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

    /// <summary>解析目标地址/端口；空值/非法值回退默认 3080。
    /// 统一委托 ShellLogic.ResolveTarget（契约测试覆盖生产路径，含 DSH_WEB_PORT）。</summary>
    private static (string Url, int Port) ResolveTarget() =>
        ShellLogic.ResolveTarget(
            Environment.GetEnvironmentVariable("DSH_WEB_URL"),
            Environment.GetEnvironmentVariable("DSH_WEB_PORT"));

    /// 设置 DSH_WEB_URL 时视为"外部托管服务"，壳不再自动拉起 dsh（DSH_WEB_PORT 则相反：壳托管拉起）。
    private static readonly bool ServerManagedExternally =
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DSH_WEB_URL"));

    // Step 4：WebView2 崩溃节流（进程级）/主窗引用/恢复标志/共享环境已迁入 WebViewManager
    //（static 字段语义映射见 docs/refactor-static-mapping.md A/B/F 组）。此处不再持有，
    // 统一经 WebViewManager.XXX 访问，避免双份状态漂移。

    // Step 5b：主题监听句柄（_themeTimer/_themeWatcher/_themeEventsHandler）已迁入
    // WindowManager.Instance（P2-7 统一释放）。

    /// 本次会话是否由壳拉起了 dsh 服务（决定"跟随窗口/托盘退出"时是否停它；外部托管/用户手动起的服务不动）。
    private static bool _serviceStartedByShell;

    /// 本次会话壳托管服务的监听 PID（内存缓存，关窗时直接使用，避免再跑 netstat 造成卡顿）。
    private static int _servicePid;

    // Step 5：托盘状态（_trayIcon/_trayExitRequested）已迁入 WindowManager（Instance 单例），
    // 统一经 WindowManager.Instance 访问，避免双份状态漂移。

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
    internal static void Trace(string message) => Logger.Info(message);

    /// <summary>ShowWindow 转发（WindowManager 托盘唤起 SW_RESTORE 用；internal 供 Managers 访问）。</summary>
    internal static void ShowWindowNative(IntPtr hwnd, int nCmdShow) => ShowWindow(hwnd, nCmdShow);

    /// <summary>
    /// P0-2（质量治理）：崩溃留痕钩子。未捕获异常先写日志（E9001 + 异常全文）再终止。
    /// - UI 线程异常（async void 事件处理器等）经 Application.ThreadException；
    /// - 主线程/后台线程未捕获异常经 AppDomain.UnhandledException（钩子执行完进程即终止，
    ///   Logger.Write 为同步 AppendAllText，写盘先于进程结束）。
    /// 克制：只加诊断、不加恢复逻辑（恢复 = 用户重新打开）。
    /// </summary>
    private static void RegisterCrashHooks()
    {
        Application.ThreadException += (_, e) =>
            Logger.Error("unhandled UI-thread exception: " + e.Exception, ErrorCodes.E9001,
                new { ex = e.Exception.ToString() });
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Error("unhandled exception: " + e.ExceptionObject, ErrorCodes.E9001,
                new { ex = e.ExceptionObject?.ToString() });
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
    private static void Main()
    {
        // 注意：Main 必须是【同步】签名。实测 .NET 10 对 `async Task Main` 的入口线程
        // 不应用 STAThread（GetApartmentState()==MTA），而 WebView2 环境创建
        // （CoreWebView2Environment.CreateAsync → native CreateCoreWebView2EnvironmentWithOptions）
        // 要求 STA 线程，MTA 下必抛 RPC_E_CHANGED_MODE (0x80010106)——v0.3.0 曾因改为
        // async Main 引入该回归（0.2.5 同步 Main 正常），安装后首次真实 GUI 启动即报 E1006。
        // 启动流程中唯一的 await（TryEnsureNodeAsync）用同步等待：其内部 ShowDialog 跑
        // 嵌套消息循环（窗口仍正常显示/可取消），await 已完成的 task 不漂移线程。
        // 主窗 form.Load 等事件处理器里的 await 处于 Application.Run 消息循环内，
        // 有 WindowsFormsSynchronizationContext，续延回到 STA UI 线程，不受影响。

        // 进程级 Per-Monitor V2 DPI 感知：必须在任何窗口/控件创建之前调用，
        // 否则 150% 等缩放下 Windows 对 WebView2 内容做位图拉伸（字体/图标模糊，issue #2）。
        // 用 user32 直接调用（WinForms 的 Application.SetHighDpiMode 在部分环境下
        // 可能因先前的 MessageBox 等窗口创建而失效）。
        SetProcessDpiAwarenessContext((IntPtr)(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2

        // v0.3.0：一键诊断导出（--diagnose [--min-level warn|error]）。不初始化 UI、
        // 不弹窗（CLI 工具保持无界面，可自动化/无人值守；质量治理负向测试发现模态框阻塞），
        // 成功路径把 zip 路径打印到 stdout，失败路径写日志 + stderr。
        var args = Environment.GetCommandLineArgs();
        if (args.Any(a => string.Equals(a, "--diagnose", StringComparison.OrdinalIgnoreCase)))
        {
            Logger.Init(Path.Combine(DshHomeDir, "dsh-launcher", "dsh.log"));
            var zip = DiagnoseExport.Run(args, DshHomeDir, Logger.Path);
            if (zip is not null)
            {
                // v0.3.1：zip 路径同时落日志（GUI 用户无控制台时也能事后在 dsh.log 找到产物位置）
                Logger.Info("diagnostic export written: " + zip);
                Console.WriteLine("dsh-launcher diagnose: " + zip);
                Console.WriteLine("已脱敏：不含任何密钥/会话/插件数据。可随 Issue 一起上传。");
            }
            else
            {
                Console.Error.WriteLine("dsh-launcher diagnose failed [" + ErrorCodes.E5001 + "]（详见统一日志：" + Logger.Path + "）");
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

        // 无头 UI 几何自测（GitHub CI 用，DSH_TEST_MODE 无关）：建窗→最大化→断言"窗口==工作区"（0px 铺满）。
        if (args.Any(a => a.Equals("--ui-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            Environment.Exit(RunUiSelftest());
            return;
        }

        // Task 0（CI geo 探针）：--ui-probe 无服务窗口探针模式——不拉 dsh 服务、不导航真实内容，
        // 直接开 DshShellForm（自绘标题栏 + WebView2 + F11 钩子），供 e2e 探针做几何/F11/标题栏/
        // 白屏断言。动机：e2e 隔离 dsh 服务在全新 DSH_HOME 起不来（dsh 生态 profile 初始化缺
        // dsh-client-ui-plan，非本项目代码），而 geo 探针验证的窗口行为本身不依赖服务内容。
        // 置于 mutex 之前（同 --ui-selftest）：不受单实例保护约束，本机已有实例时也能开测试窗。
        if (args.Any(a => a.Equals("--ui-probe", StringComparison.OrdinalIgnoreCase)))
        {
            // e2e 探针模式：ShowError 走日志+stdout，不弹模态（根治探针路径 E2004 挂起）。
            Environment.SetEnvironmentVariable("DSH_E2E", "1");
            Environment.Exit(RunUiProbe());
            return;
        }

        // v0.3.0：统一日志初始化（单一日志文件 + 启动早段轮转）
        Logger.Init(UnifiedLogPath);
        // 特征开关（ADR-008 迁移用）：DSH_USE_NEW_LIFECYCLE=1 走新路径（当前日志标记，实际
        // 迁入后由它切换旧/新实现，便于运行时对比）。默认 legacy。
        Trace("feature flag: DSH_USE_NEW_LIFECYCLE="
            + (Environment.GetEnvironmentVariable("DSH_USE_NEW_LIFECYCLE") == "1" ? "1 (new)" : "unset (legacy)"));
        // 轮转仅在"当前无活服务占用日志"时执行：若上次崩溃残留的孤儿服务仍用 `cmd >>` 持有
        // dsh.log，File.Move 重命名后它会继续写旧名（日志被劈裂，单一日志契约破坏）。
        // 有活服务占用端口则跳过本轮轮转；WarnIfOversized 兜底提示常驻超长日志。
        if (!PortOpen(Target.Port))
            Logger.RotateIfNeeded();
        Logger.WarnIfOversized(); // P2：常驻超长日志（>50MB 且 >24h）告警

        // P0-2（质量治理）：崩溃留痕——任何未捕获异常（UI 线程/后台线程/主线程）先写一条
        // E9001 日志再终止，杜绝"窗口突然消失但 dsh.log 无记录"的静默崩溃。只加诊断，不加恢复。
        RegisterCrashHooks();

        // 测试钩子（DSH_TEST_CRASH=1）：验证崩溃留痕钩子生效（negative N9），仅测试使用。
        if (Environment.GetEnvironmentVariable("DSH_TEST_CRASH") == "1")
            throw new InvalidOperationException("test crash hook (DSH_TEST_CRASH=1)");

        WindowStateStore.Init(DataDir);
        StagedUpdate.Init(DataDir);
        CleanupStagingCache(); // 下载缓存管理：清理 DataDir\staging 中 >7 天的过期包（防无限增长）

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
            // 并行开窗（Step5）：状态窗先建先显示（TopMost），随后的 Node 检测/服务拉起/轮询
            // 都在弹窗可见期间同步进行——双击后立即看到加载窗，不再"干等几秒无反应"。
            // cts/pollTask 在此创建；Show() 非模态 + 下方 DoEvents 消息泵驱动刷新。
            var logPath = UnifiedLogPath;
            var cts = new CancellationTokenSource();
            using var status = CreateStartupStatusForm(onCancel: () => cts.Cancel());
            status.Show(); // 非模态立即显示（TopMost，前台有窗口也能看到）
            var pollTask = Task.Run(() => WaitServiceReady(cts.Token, Target.Port, Target.Url, logPath, E2EMode));
            _ = pollTask.ContinueWith(_ =>
            {
                try { status.Invoke(status.Close); } catch { /* 窗口已关闭 */ }
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

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
                    // 同步等待（Main 为同步 STA 入口，见 Main 顶部注释）：TryEnsureNodeAsync
                    // 内部的 ShowDialog 在嵌套消息循环中运行，可正常交互；完成后继续在 STA 线程。
                    if (!TryEnsureNodeAsync().GetAwaiter().GetResult()) return;
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

            // 并行开窗（Step5）：状态窗已在进入本块时非模态 Show()（TopMost）。
            // 等待服务就绪期间用 DoEvents 消息泵驱动状态窗刷新/取消按钮——
            // 不能阻塞 Main 线程（同一线程无消息泵 → 状态窗挂起不刷新，等同卡死）。
            string waitResult;
            if (NoUiMode || E2EMode)
            {
                // 测试钩子（DSH_NO_UI=1 / DSH_E2E=1）：不显示状态窗，等待轮询自然结束（无窗口无弹窗）。
                // e2e 模式下轮询上限已缩至 20s，此处最多等 20s。
                waitResult = pollTask.GetAwaiter().GetResult();
            }
            else
            {
                // DoEvents 消息泵：状态窗可见、可取消；pollTask 完成后退出循环。
                while (!pollTask.IsCompleted)
                {
                    Application.DoEvents();
                    Thread.Sleep(50);
                }
                waitResult = pollTask.GetAwaiter().GetResult();
            }
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
                        if (KillProcess(pid)) ClearServicePidFile(); // P2-10：杀不干净则保留 pid 文件
                    }
                    else
                    {
                        ClearServicePidFile();
                    }
                }
                // P0-1（质量治理）：用户取消 ≠ 放弃服务——后台下载/启动可能仍在进行（与取消文案一致），
                // 但服务必须可被下次启动接管：已监听则记录 PID（此前无 pid 文件 → TryAdoptOrphanService
                // 永远无法认领，服务成为永久无主孤儿，占住端口无人管理）。
                else if (waitResult == "canceled" && _serviceStartedByShell && PortOpen(Target.Port))
                {
                    RecordServicePid();
                    Trace("canceled: service left running; pid recorded for next-start adoption");
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
                    "canceled" => ErrorCodes.E2006, // P0-1：取消不是内部错误（此前误归 E9001）
                    _ => ErrorCodes.E9001,
                };
                // 质量治理 P1-7：用户主动取消不是错误——按 Info 记录，避免污染错误码汇总
                ShowError(code, "dsh 服务未能就绪。\n\n" + body,
                    level: waitResult == "canceled" ? Logger.Level.Info : Logger.Level.Error);
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
            // E2004 在 NoUiMode（DSH_NO_UI=1 测试钩子）下同样写日志但不弹窗：
            // 先于 NoUiMode 退出块判定，保证"外部托管指向死端口"等场景可诊断（负向测试 N1 断言）。
            ShowError(ErrorCodes.E2004, $"dsh 服务不可用（{Target.Url}），请确认服务已启动并查看统一日志：{UnifiedLogPath}");
            return;
        }

        // 无 UI 测试钩子：服务就绪后直接退出（不建主窗，供自动化验证拉起链路；服务进程保持由测试管理）
        if (NoUiMode)
        {
            Trace("no-ui mode: service ready; exiting without window");
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
        // F11 全屏：用系统级低级键盘钩子（WH_KEYBOARD_LL）在 OS 层捕获——不依赖焦点、
        // 不依赖浏览器进程（WebView2 有时会截走物理 F11 导致 KeyDown/消息过滤器失效）。
        // 仅在主窗口位于前台时切换并吞掉 F11。
        // 跨线程修复（Step2b）：UI 线程缓存 hwnd 再进 lambda——钩子回调在线程池/系统线程触发，
        // 直接访问 form.Handle 在窗体销毁期抛 ObjectDisposedException（竞态）。
        var mainHwnd = form.Handle; // 仅触发句柄创建（此时窗体已建）
        using var f11Hook = new F11LowLevelHook(form.ToggleFullscreen,
            () => F11LowLevelHook.GetForegroundWindow() == mainHwnd);
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
        form.MainWebView2 = web;
        WebViewManager.MainWeb = web;

        // 无边框窗口阴影（DWM NCRENDERING_POLICY；带 WebView2 时系统阴影实际不呈现，边框替代质感）
        form.HandleCreated += (_, _) => ApplyWindowShadow(form.Handle);

        // DPI 变化（跨缩放显示器移动窗口）：重算标题栏尺寸并重新布局内容区
        form.DpiChanged += (_, _) =>
        {
            var scale = form.DeviceDpi / 96f;
            form.TitleBar.Rescale(scale);
            form.LayoutChrome();
        };

        // 托盘图标始终显示（任何服务模式）：提供"服务模式"切换与退出的常驻入口。
        // 之前只在"托盘驻留"模式创建，导致默认"常驻"模式下用户找不到切换入口。
        // Step 5：托盘生命周期迁入 WindowManager，依赖委托注入 + 接线自检（防漏注入静默失效）。
        WindowManager.Instance.IsTrayWantedProvider = () => IsTrayWanted();
        WindowManager.Instance.TrayWhaleIconProvider = () => TrayWhaleIcon ?? SystemIcons.Application;
        WindowManager.Instance.TrayExitAction = () =>
        {
            // 托盘"退出"：置位（FormClosing 放行真关，不再隐藏到托盘）
            WindowManager.Instance.MarkTrayExitRequested();
            // 常驻模式：只退出壳（服务保留）；托盘驻留/跟随窗口：停掉壳拉起的服务
            if (ReadLifetimeMode() != ShellLogic.ServiceLifetime.AlwaysOn && _serviceStartedByShell)
                StopShellService();
            Application.Exit();
        };
        WindowManager.Instance.TrayMenuFactory = exitAction => new TrayMenuForm(exitAction);
        WindowManager.Instance.VerifyDependencies(); // 接线自检（Debug 断言）
        WindowManager.Instance.EnsureTrayIcon(form);
        // Step 5b：主题委托注入（WindowManager 主题监听迁移用）
        WindowManager.Instance.ResolveDarkModeProvider = () => ResolveDarkMode();
        WindowManager.Instance.ApplyWindowThemeAction = (f, dark) => ApplyThemeIcon(f);
        WindowManager.Instance.DshHomeDirProvider = () => DshHomeDir;
        // Step 4：WebViewManager 下载完成提示回调注入（解耦 Program 托盘实现）
        WebViewManager.DownloadNotifyAction = NotifyDownloadComplete;
        // 窗口图标跟随主题（深色 → 白色鲸鱼 + 深色标题栏），主题切换时实时更新。
        ApplyThemeIcon(form);
        form.HandleCreated += (_, _) => ApplyThemeIcon(form); // 句柄创建后应用标题栏配色
        WindowManager.Instance.RegisterThemeWatcher(form);
        form.Shown += (_, _) => Trace("main form shown");

        form.FormClosing += (_, e) =>
        {
            // 生命周期模式（由 dsh-launcher-lifetime 插件写入 settings.json，壳执行）：
            // 常驻(0) / 托盘驻留(1) / 跟随窗口(2)。
            var mode = ReadLifetimeMode();

            // ORDER-INVARIANT（矩阵 L1，0.1.10 血泪）：托盘拦截判定必须先于 WebView2 销毁——
            // WebView2 一旦 Dispose，从托盘唤起时控件已销毁，窗口只剩空白。
            // 决策下沉纯函数 ShouldInterceptCloseToTray；下方所有"销毁/退出"路径
            // 都必须在 return 之后才执行（顺序即语义，禁止重排）。
            if (ShellLogic.ShouldInterceptCloseToTray(mode, WindowManager.Instance.TrayExitRequested))
            {
                e.Cancel = true;
                form.Hide();
                WebViewManager.HiddenSince = DateTime.Now;
                return;
            }

            // 真正退出路径不显式 Dispose WebView2：Dispose 会等待浏览器进程关闭，
            // 造成关窗卡顿 1-2 秒；进程退出后 WebView2 子进程会自动检测父进程退出并清理。
            // 图标为进程级缓存（GDI 对象随进程退出释放），此处不销毁，
            // 避免托盘驻留/主题切换时复用已销毁的句柄。

            // v0.3.0：主窗口位置/大小持久化（多显示器记忆，真实退出时写回）
            SaveWindowState(form);

            // 质量治理 P2-7：真实退出释放主题监听（SystemEvents/FSW/轮询 Timer）
            WindowManager.Instance.ReleaseThemeWatcher();

            if (mode == ShellLogic.ServiceLifetime.FollowWindow && _serviceStartedByShell)
            {
                // 跟随窗口：关窗即停服务（只停壳本次拉起的）
                StopShellService();
            }
            WindowManager.Instance.DisposeTray();
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
                // v0.3.1 修复：保存的是含边框的窗口尺寸（SaveWindowState 用 Bounds），
                // 必须赋给 Size 而非 ClientSize，否则窗口会比保存时大一圈（边框差值）。
                form.Size = new Size((int)Math.Round(w * scale), (int)Math.Round(h * scale));
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
                // v0.3.3：恢复最大化状态（IsMaximized 字段）
                if (savedWindow.IsMaximized)
                {
                    form.WindowState = FormWindowState.Maximized;
                    Trace("window restored to maximized state");
                }
            }

            // WebView2 user data goes to %LOCALAPPDATA%\DshWeb to keep the app dir clean
            // (固定目录：避免系统临时目录被清理导致会话/插件登录态丢失)
            // DSH_WEBVIEW2_DATA 测试钩子：自动化测试必须隔离 WebView2 数据目录——多个进程
            // 共用同一 user-data-dir 会导致互相锁死（实测：测试实例与真实实例并行初始化
            // WebView2 时真实实例 UI 线程卡死、整窗灰色无响应）。仅测试使用。
            var userDataFolder = Environment.GetEnvironmentVariable("DSH_WEBVIEW2_DATA");
            if (string.IsNullOrWhiteSpace(userDataFolder))
            {
                userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DshWeb", "WebView2");
            }
            try
            {
                await InitWebViewAsync(web, userDataFolder);
                web.CoreWebView2.Navigate(Target.Url);
                // 页面加载失败（如端口被其他程序占用、服务异常退出）：明确提示而非白屏静默
                var navWarned = false;
                web.CoreWebView2.NavigationCompleted += (_, e) =>
                {
                    if (e.IsSuccess) WebViewManager.ResetCrashCount(); // P1-3：页面恢复后复位崩溃计数
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
                // 质量治理（实测事故）：WebView2 数据目录被另一实例占用时 native 返回
                // 0x800700B7 (ERROR_ALREADY_EXISTS)——真实多开共用 %LOCALAPPDATA%\DshWeb\WebView2
                // 会互锁（整窗灰死）。此时给专属提示，避免用户误以为 Runtime 缺失（E1006 泛化）。
                if (ex.Message.Contains("0x800700B7", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Error("webview2 user-data-dir locked by another instance: " + ex.Message,
                        ErrorCodes.E1006, new { hint = "another-dsh-launcher-running" });
                    ShowError(ErrorCodes.E1006,
                        "无法初始化 WebView2：WebView2 数据目录正被另一个 dsh-launcher 实例占用。\n\n"
                        + "请先关闭其他 dsh-launcher 窗口（或检查任务管理器中是否有其他 DshWeb.exe），再重新打开。");
                    form.Close();
                    return;
                }

                // WebView2 Runtime 缺失等初始化失败：先尝试静默安装 Evergreen Bootstrapper
                //（P2，v0.3.1），安装成功后重试一次初始化；重试再次失败才明确提示而不是静默无窗口。
                if (await TryInstallWebView2Async())
                {
                    try
                    {
                        await InitWebViewAsync(web, userDataFolder);
                        web.CoreWebView2.Navigate(Target.Url);
                        // 页面加载失败（如端口被其他程序占用、服务异常退出）：明确提示而非白屏静默
                        var navWarned = false;
                        web.CoreWebView2.NavigationCompleted += (_, e) =>
                        {
                            if (e.IsSuccess) WebViewManager.ResetCrashCount(); // P1-3：页面恢复后复位崩溃计数
                            if (!e.IsSuccess && !navWarned)
                            {
                                navWarned = true;
                                ShowError(ErrorCodes.E2004,
                                    $"页面加载失败。\n\n请确认 {Target.Url} 上运行的是 dsh 服务（端口可能被其他程序占用，或服务已异常退出）。\n\n统一日志：{UnifiedLogPath}");
                            }
                        };
                        return; // 重试初始化成功，窗口正常驻留，不弹 E1006
                    }
                    catch
                    {
                        // 重试仍失败：吞掉异常，走下方 E1006 弹窗
                    }
                }
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

    /// <summary>
    /// 无头 UI 几何自测（GitHub CI 用）：建主窗 → 最大化 → 断言"窗口矩形 == 工作区"（0px 铺满，ADR-001）。
    /// 不依赖 dsh 服务 / Node / WebView2 内容，只验证自绘边框的 Win32 消息（WS_CAPTION 移除 + WM_GETMINMAXINFO）。
    /// 退出码：0=通过，1=几何不符，2=内部异常。结果同时写统一日志与 stdout（CI 抓取）。
    /// </summary>
    private static int RunUiSelftest()
    {
        Logger.Init(UnifiedLogPath);
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
            form.TitleBar = new CustomTitleBar(form, ResolveDarkMode())
            {
                Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2,
                    (int)Math.Round(32 * form.DeviceDpi / 96f)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            form.Controls.Add(form.TitleBar);
            form.Show();
            Application.DoEvents();
            form.WindowState = FormWindowState.Maximized;
            Application.DoEvents();
            form.Refresh();

            var wa = Screen.FromHandle(form.Handle).WorkingArea;
            var b = form.Bounds;
            var ok = b.X == wa.X && b.Y == wa.Y && b.Width == wa.Width && b.Height == wa.Height;
            var msg = $"UI-SELFTEST pass={ok} bound=({b.X},{b.Y},{b.Width}x{b.Height}) workarea=({wa.X},{wa.Y},{wa.Width}x{wa.Height})";
            Logger.Info(msg);
            Console.WriteLine(msg);
            WriteSelftestResult(ok, msg);
            form.Close();
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            var msg = "UI-SELFTEST threw: " + ex.Message;
            Logger.Error(msg);
            Console.Error.WriteLine(msg);
            WriteSelftestResult(false, msg);
            return 2;
        }
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
    private static int RunUiProbe()
    {
        Logger.Init(UnifiedLogPath);
        try
        {
            var form = new DshShellForm
            {
                Text = "DeepSeek Harness", // 与真实主窗同名，供探针 FindWindow 定位
                ClientSize = new Size(1280, 840),
                MinimumSize = new Size(800, 600),
                FormBorderStyle = FormBorderStyle.None,
            };
            form.TitleBar = new CustomTitleBar(form, ResolveDarkMode())
            {
                Bounds = new Rectangle(1, 1, form.ClientSize.Width - 2,
                    (int)Math.Round(32 * form.DeviceDpi / 96f)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            form.Controls.Add(form.TitleBar);

            var web = new WebView2
            {
                Bounds = new Rectangle(1, 1 + form.TitleBar.Height,
                    form.ClientSize.Width - 2, form.ClientSize.Height - form.TitleBar.Height - 2),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };
            form.Controls.Add(web);
            form.MainWebView2 = web;
            WebViewManager.MainWeb = web; // readyState 测试钩子按 ReferenceEquals(web, MainWeb) 门控，必须先设

            // F11 钩子（与真实路径一致）：仅主窗前台时切换并吞键。
            // 跨线程修复（Step2b）：缓存 hwnd 再进 lambda，避免销毁期 ObjectDisposedException。
            var probeHwnd = form.Handle;
            using var f11Hook = new F11LowLevelHook(form.ToggleFullscreen,
                () => F11LowLevelHook.GetForegroundWindow() == probeHwnd);
            Trace($"ui-probe: f11 hook installed hwnd=0x{probeHwnd.ToInt64():X}"); // 诊断：确认走 --ui-probe 分支

            form.Shown += async (_, _) =>
            {
                var userDataFolder = Environment.GetEnvironmentVariable("DSH_WEBVIEW2_DATA");
                if (string.IsNullOrWhiteSpace(userDataFolder))
                    userDataFolder = Path.Combine(Path.GetTempPath(), "dsh-ui-probe-wv2");
                try
                {
                    await InitWebViewAsync(web, userDataFolder);
                    web.CoreWebView2.Navigate("about:blank"); // 无需网络，readyState 钩子照常触发
                }
                catch (Exception ex)
                {
                    Logger.Error("ui-probe webview init failed: " + ex.Message);
                }
            };

            Application.Run(form);
            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error("ui-probe threw: " + ex.Message);
            return 2;
        }
    }

    /// <summary>
    /// 把自测结果落盘（CI 可靠取回通道——GUI 子系统应用的 stdout/退出码在 pwsh 里未必可靠回传）。
    /// 写入当前目录 ui-selftest-result.txt；可用 DSH_TEST_RESULT 覆盖路径。
    /// </summary>
    private static void WriteSelftestResult(bool pass, string detail)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("DSH_TEST_RESULT") ?? "ui-selftest-result.txt";
            try { System.IO.Path.GetFullPath(path); } catch { path = "ui-selftest-result.txt"; }
            File.WriteAllText(path, $"pass={pass}\n{detail}\n");
        }
        catch { /* 落盘失败不阻断 */ }
    }

    private enum PendingUpdate { None, Dsh, LauncherSecurity }
    private static PendingUpdate _pendingUpdate;
    private static string _pendingLatest = "", _pendingLocal = "";
    private static Form? _pendingForm;

    /// <summary>测试钩子：DSH_NO_UI=1 时所有用户弹窗（ShowError/状态窗/确认框）改为仅写日志，
    /// 供自动化/负向测试在无窗口环境运行（不打扰真实桌面）。仅测试使用，文档注明。</summary>
    private static bool NoUiMode =>
        string.Equals(Environment.GetEnvironmentVariable("DSH_NO_UI"), "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>e2e/探针模式（Task 0 模态硬化）：--ui-probe 参数或 DSH_E2E=1 时，ShowError 一律
    /// 只写日志 + stdout，不弹模态框——根治 E2004 模态窗在探针/e2e 路径挂起问题（探针 WaitMain
    /// 等窗口 30s + 模态弹窗不关 = 看似卡死）。仅测试路径，正常 GUI 不受影响。</summary>
    private static bool E2EMode =>
        string.Equals(Environment.GetEnvironmentVariable("DSH_E2E"), "1", StringComparison.OrdinalIgnoreCase);

    /// <summary>统一出错弹窗（v0.3.0 显式差错控制）：正文含 [错误码]，错误一并写入结构化日志；
    /// 消息文本可 Ctrl+C 复制，便于粘贴到 Issue。
    /// 质量治理 P1-7：可指定日志级别——"用户取消/拒绝"类非故障（如 E1002）传 Info，
    /// 避免污染错误码汇总；log 参数供"显式 Logger 已写过"的场景去重（如 E4001 双写）。
    /// DSH_NO_UI=1（测试钩子）时不弹窗，仅写日志并返回 OK（进程可自然退出，无残留窗口）。</summary>
    private static DialogResult ShowError(string code, string detail,
        MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.Warning,
        Logger.Level level = Logger.Level.Error, bool log = true)
    {
        if (log)
        {
            if (level == Logger.Level.Error) Logger.Error(detail, code);
            else if (level == Logger.Level.Warn) Logger.Warn(detail, code);
            else Logger.Info(detail, code);
        }
        if (NoUiMode) return DialogResult.OK; // 测试钩子：只记录不弹窗
        // e2e/探针模式（模态硬化）：--ui-probe / DSH_E2E=1 时只写日志 + stdout，不弹模态。
        // 否则探针路径（如 geo 启壳后服务误判不可用）会弹 E2004 模态窗，与探针 WaitMain 30s
        // 等待叠加造成"看似卡死"。正常 GUI 不受影响。
        if (E2EMode)
        {
            try { Console.WriteLine($"[{code}] {ErrorCodes.Describe(code)}\n{detail}"); } catch { /* stdout 不可用时忽略 */ }
            return DialogResult.OK;
        }
        return MessageBox.Show($"[{code}] {ErrorCodes.Describe(code)}\n\n{detail}", "DeepSeek Harness", buttons, icon);
    }

    /// <summary>v0.3.1 P2：WebView2 缺失兜底——下载 Evergreen Bootstrapper（官方固定链接，
    /// 约 2MB）静默安装后重测；任何一步失败返回 false（调用方回退 E1006 弹窗）。不内嵌 runtime。
    /// 质量治理 P1-5：各失败分支写入结构化日志（区分 已装/下载失败/安装失败/超时），不再静默。</summary>
    private static async Task<bool> TryInstallWebView2Async()
    {
        try
        {
            if (ShellLogic.ReadWebView2Version() is not null) return true;
            var boot = Path.Combine(Path.GetTempPath(), "dsh-wv2-bootstrapper.exe");
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-launcher");
                using var resp = await http.GetAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                if (!resp.IsSuccessStatusCode)
                {
                    Logger.Error($"webview2 bootstrapper download failed: HTTP {(int)resp.StatusCode}",
                        ErrorCodes.E1006, new { stage = "download" });
                    return false;
                }
                await using var fs = new FileStream(boot, FileMode.Create, FileAccess.Write);
                await resp.Content.CopyToAsync(fs);
            }
            catch (Exception ex)
            {
                Logger.Error("webview2 bootstrapper download error: " + ex.Message, ErrorCodes.E1006, new { stage = "download" });
                return false;
            }
            try
            {
                var psi = new ProcessStartInfo(boot, "/silent /install")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null)
                {
                    Logger.Error("webview2 bootstrapper failed to start", ErrorCodes.E1006, new { stage = "install" });
                    return false;
                }
                if (!p.WaitForExit(120000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    Logger.Error("webview2 bootstrapper install timed out", ErrorCodes.E1006, new { stage = "install", timeout = 120000 });
                    return false;
                }
                var ok = p.ExitCode == 0 && ShellLogic.ReadWebView2Version() is not null;
                if (!ok)
                    Logger.Error($"webview2 bootstrapper install failed: exit={p.ExitCode}",
                        ErrorCodes.E1006, new { stage = "install", exitCode = p.ExitCode });
                return ok;
            }
            catch (Exception ex)
            {
                Logger.Error("webview2 bootstrapper install error: " + ex.Message, ErrorCodes.E1006, new { stage = "install" });
                return false;
            }
            finally
            {
                try { if (File.Exists(boot)) File.Delete(boot); } catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("webview2 fallback install error: " + ex.Message, ErrorCodes.E1006, new { stage = "unknown" });
            return false;
        }
    }

    /// <summary>v0.3.0 Node 缺失处理：一次性确认 → 状态窗期间自动下载便携 Node（可取消）。
    /// 返回是否已具备可用 Node。</summary>
    private static async Task<bool> TryEnsureNodeAsync()
    {
        // 测试钩子：DSH_NO_UI 时不弹确认框，直接视为拒绝（自动化环境不打断）
        if (NoUiMode)
        {
            ShowError(ErrorCodes.E1002, "未安装 Node.js（DSH_NO_UI 模式：不自动下载）。", level: Logger.Level.Info);
            return false;
        }
        var ask = MessageBox.Show(
            "检测到 Node.js 问题（dsh 服务运行必需）。\n\n" +
            (RuntimeResolver.NodeMissingReason() == "too-old"
                ? "系统 Node.js 版本过低或不可用（需要 18 或更高版本）。\n"
                : "未检测到 Node.js。\n") +
            "是否自动下载便携版 Node.js 到用户目录？\n" +
            "（约 30MB，仅用于本启动器，不改动系统环境；版本采用 LTS 固定版）",
            "dsh-launcher - 需要 Node.js", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (ask != DialogResult.Yes)
        {
            ShowError(ErrorCodes.E1002, "未安装 Node.js，dsh 服务无法启动。可安装 Node.js 18+ 后重新打开。",
                level: Logger.Level.Info); // 用户主动拒绝，非错误（P1-7）
            return false;
        }
        var cts = new CancellationTokenSource();
        using var status = CreateStartupStatusForm("正在下载并安装便携 Node.js…（约 30MB，请稍候）", onCancel: () => cts.Cancel());
        var task = RuntimeResolver.EnsurePortableNodeAsync(cts.Token);
        _ = task.ContinueWith(_ =>
        {
            try { status.Invoke(status.Close); } catch { /* 窗口已关闭 */ }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        if (NoUiMode) await task; else status.ShowDialog();
        var (ok, code, detail) = await task;
        if (!ok)
        {
            ShowError(code ?? ErrorCodes.E1003, detail ?? "便携 Node 安装失败。可稍后重试，或手动安装 Node.js 18+。");
            return false;
        }
        return true;
    }

    /// <summary>v0.3.0 主窗口位置/大小持久化（多显示器记忆）：位置与尺寸存 96dpi 逻辑值（跨 DPI 恢复时按当前 DPI 缩放）。
    /// v0.3.1 修复：Normal 状态必须用 Bounds——WinForms 的 RestoreBounds 只在窗口
    /// 最小化/最大化时更新（Normal 时恒为初始字段值 (-1,-1,初始尺寸)），此前用
    /// RestoreBounds 导致位置记忆从未生效（每次重启回默认位置/大小）。
    /// v0.3.3 新增：保存 IsMaximized 标志，最大化后关闭、重启时恢复最大化状态。</summary>
    private static void SaveWindowState(Form form)
    {
        try
        {
            if (form.WindowState == FormWindowState.Minimized) return;
            // Normal → Bounds（当前真实边界）；最小化/最大化 → RestoreBounds（还原后的边界）
            var rb = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            if (rb.Width <= 0 || rb.Height <= 0) return;
            var scale = form.DeviceDpi / 96f;
            WindowStateStore.Save(new WindowStateStore.WindowState(
                rb.X, rb.Y,
                (int)Math.Round(rb.Width / scale),
                (int)Math.Round(rb.Height / scale),
                form.WindowState == FormWindowState.Maximized));
        }
        catch (Exception ex)
        {
            // 质量治理：窗口位置保存失败此前静默（用户配置的位置丢失无诊断入口）
            Logger.Warn("window state save failed; position memory unavailable this session", ctx: new { error = ex.Message });
        }
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
                // 质量治理 P1-6：存在"已下载待应用"的 dsh 更新（pending-update.json 未被清除
                // = 服务健康跳过应用或应用失败）→ 气泡提示一次（不打断会话），重启后生效。
                // 不依赖网络，先于 GitHub/npm 检查执行。
                var pendingVersion = StagedUpdate.ReadPendingVersion();
                if (!string.IsNullOrWhiteSpace(pendingVersion))
                {
                    var v = pendingVersion;
                    form.BeginInvoke(() => NotifyPendingApply(v));
                }

                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) }; // P2-9：弱网放宽 8s→15s
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
                    // v0.3.1：用户拒绝过的版本跳过（新版本 > 跳过版本时重新提示）
                    var skipped = ReadSkippedDshVersion();
                    if (skipped is not null && UpdateChecker.CompareVersions(latest, skipped) <= 0)
                    {
                        Trace($"dsh update {latest} skipped by user (skipped={skipped})");
                        return;
                    }
                    form.BeginInvoke(() => NotifyPending(PendingUpdate.Dsh, latest, local));
                }
            }
            catch { /* 检测失败静默 */ }
        });
    }

    /// <summary>用户拒绝过的 dsh 版本记录路径（DataDir\skipped-update.json）。</summary>
    private static string SkippedUpdatePath => Path.Combine(DataDir, "skipped-update.json");

    private static void MarkSkippedDshVersion(string version)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(SkippedUpdatePath, System.Text.Json.JsonSerializer.Serialize(new
            {
                version,
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            }));
            Logger.Info($"user skipped dsh update {version}; won't re-prompt until a newer version appears");
        }
        catch { /* 记录失败：下次启动可能再提示（可接受） */ }
    }

    private static string? ReadSkippedDshVersion()
    {
        try
        {
            if (!File.Exists(SkippedUpdatePath)) return null;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SkippedUpdatePath));
            return doc.RootElement.TryGetProperty("version", out var v)
                && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>下载完成但非"无害扩展名"（可能含可执行代码）时的提示：托盘气泡告知落盘位置，
    /// 不自动打开——防恶意页面触发下载后自动执行本地代码（S2 修复）。</summary>
    private static void NotifyDownloadComplete(string filePath)
    {
        try
        {
            if (WindowManager.Instance.TrayIcon is null) return;
            WindowManager.Instance.ShowBalloonTip(8000, "下载完成",
                "文件已保存：\n" + filePath + "\n（点击 dsh-launcher 托盘图标查看）",
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
            WindowManager.Instance.EnsureTrayIcon(_pendingForm);
            var tray = WindowManager.Instance.TrayIcon;
            if (tray is null) return;
            tray.BalloonTipClicked -= OnPendingBalloonClicked;
            tray.BalloonTipClicked += OnPendingBalloonClicked;
            var (title, body) = type == PendingUpdate.LauncherSecurity
                ? ("dsh-launcher 安全更新", $"检测到重要安全更新 {latest}（当前 {local}）。点击查看下载。\n如有严重漏洞请尽快更新。")
                : ("dsh 有新版本", $"检测到 dsh {latest}（当前 {local}）。点击此处在后台下载更新。");
            tray.ShowBalloonTip(25000, title, body, ToolTipIcon.Info); // 驻留 25s，安全更新要让人看到
        }
        catch { /* 气泡提示失败忽略 */ }
    }

    /// <summary>质量治理 P1-6/P1-8："已下载待应用"更新的一次性气泡提示（无点击行为）。
    /// 触发条件：pending-update.json 存在（服务健康跳过应用，或应用失败保留）。
    /// v0.3.1 降噪：应用失败达到 MaxNotifyFailures 次后不再每次启动弹气泡
    /// （持续失败会重复打扰），降级为仅日志（手动 npm 命令提示保留在日志文案）。</summary>
    private static void NotifyPendingApply(string version)
    {
        try
        {
            var (_, failCount) = StagedUpdate.ReadPending();
            if (failCount >= StagedUpdate.MaxNotifyFailures)
            {
                Logger.Warn($"staged dsh update {version} kept failing to apply ({failCount} tries); " +
                    "suppressing balloon. Manual: npm install -g @deepseek-ai/dsh@" + version);
                return;
            }
            if (_pendingForm is null) return;
            WindowManager.Instance.EnsureTrayIcon(_pendingForm, force: true); // 无插件/无待通知更新时也临时建托盘提示
            var tray = WindowManager.Instance.TrayIcon;
            if (tray is null) return;
            tray.ShowBalloonTip(15000, "dsh 更新待应用",
                $"已下载 dsh {version}，重启 dsh-launcher 后自动应用（或手动执行：npm install -g @deepseek-ai/dsh@{version}）。",
                ToolTipIcon.Info);
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
    /// 写 pending-update.json，下次启动时自动应用（延迟应用，v0.3.0，绝不打断当前会话）。
    /// v0.3.1：用户拒绝 → 持久化跳过该版本（下次启动不再提示，除非检测到更新的版本）。</summary>
    private static void PromptDshUpdate(Form form, string latest, string local)
    {
        var r = MessageBox.Show(
            $"检测到 dsh 新版本 {latest}（当前 {local}）。\n\n是否在后台下载并安排更新？\n" +
            "（下载完成不打扰当前会话；下次启动 dsh-launcher 时自动应用新版本）",
            "dsh 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes)
        {
            MarkSkippedDshVersion(latest); // 用户拒绝：跳过此版本，避免每次启动重复提示
            return;
        }
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
                    // log:false —— 显式 Logger.Error 已写（窗体可能已关闭，日志不能丢），弹窗不再重复写（P1-7）
                    form.BeginInvoke(() => ShowError(ErrorCodes.E4001,
                        $"dsh {latest} 下载失败。\n\n可稍后重试，或在命令行手动执行：\nnpm install -g @deepseek-ai/dsh@{latest}",
                        log: false));
                }
                catch { /* 窗体已关闭 */ }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("staged dsh update download error: " + ex.Message, ErrorCodes.E4001);
            try { form.BeginInvoke(() => ShowError(ErrorCodes.E4001, ex.Message, log: false)); } catch { /* 窗体已关闭 */ }
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
            CleanupStagingCache(); // 应用成功：清空 staging（下载缓存不留残余）
            Logger.Info($"staged dsh update applied: {version}");
        }
        else
        {
            StagedUpdate.MarkApplyFailed(); // v0.3.1：累计失败次数，持续失败降级为仅日志
            Logger.Warn("staged dsh update apply failed; continuing with current version", ErrorCodes.E4002,
                new { version, tail = errorTail });
        }
    }

    /// <summary>下载缓存管理：清理 DataDir\staging 中修改时间超过 7 天的文件。
    /// 下载中的当前包（刚写入）不受影响；应用成功后调用方再整体清空。</summary>
    private static void CleanupStagingCache()
    {
        try
        {
            var staging = Path.Combine(DataDir, "staging");
            if (!Directory.Exists(staging)) return;
            var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
            foreach (var file in Directory.GetFiles(staging))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                    {
                        File.Delete(file);
                        Logger.Info("staging cache cleaned (expired)", ctx: new { file = Path.GetFileName(file) });
                    }
                }
                catch { /* 单文件清理失败跳过 */ }
            }
        }
        catch { /* 清理失败不影响启动 */ }
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
        if (NoUiMode) return; // 测试钩子：不弹确认框（自动化环境不打断）
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

        // 清理孤儿自启：HKCU Run 的 dsh-launcher 条目。
        // 1) 指向 start-dsh.vbs 的旧版条目（0.2.x）一律删除——新版 autostart 应指向 DshWeb.exe，
        //    且 VBS 直接拉起时 %USERPROFILE%\.dsh\dsh-launcher\ 目录可能尚未创建，导致 800A01A8 弹窗。
        //    若用户在新版勾选了 autostart，EnsureAutoStartRequested 会重写正确条目。
        // 2) 指向 DshWeb.exe 但文件已不存在的（per-machine 提权卸载跳过 per-user 组件时残留），
        //    避免下次登录白启一个死项。
        try
        {
            using var runKey = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (runKey?.GetValue("dsh-launcher") is string runValue)
            {
                var m = Regex.Match(runValue, "\"([^\"]+(?:start-dsh\\.vbs|DshWeb\\.exe))\"",
                    RegexOptions.IgnoreCase);
                var targetPath = m.Success ? m.Groups[1].Value : null;
                // start-dsh.vbs 条目一律删除（旧版残留）；DshWeb.exe 条目仅文件不存在时删除
                if (targetPath is null ||
                    targetPath.EndsWith("start-dsh.vbs", StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(targetPath))
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
            return ShellLogic.IsHttpReady(Target.Url, http); // 契约纯函数（P1-6，可注入测试）
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
        // 质量治理：settings.json 存在但非法 JSON / 非对象 → 记 Warn（此前静默回退默认模式，
        // 用户"常驻"配置为何失效无法诊断）。仅在文件内容非空且非合法 JSON 对象时告警。
        if (!string.IsNullOrWhiteSpace(json) && !ShellLogic.HasServiceLifetimeKey(json))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    Logger.Warn("settings.json is not a JSON object; lifetime defaults apply",
                        ctx: new { path = SettingsPath });
            }
            catch
            {
                Logger.Warn("settings.json is not valid JSON; lifetime defaults apply",
                    ctx: new { path = SettingsPath });
            }
        }
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
                    if (KillProcess(pid)) ClearServicePidFile(); // P2-10：杀不干净则保留 pid 文件
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
                // P1-3（质量治理）：记录过但未监听目标端口的 node 大概率是 PID 复用（无关进程）——
                // 不杀（KillProcess 的端口校验也会拒绝），只清 pid 文件；进程本身不是我们管理的服务。
                Logger.Warn($"stale service pid={pid} alive but not listening on port {Target.Port}; clearing pid file (possible PID reuse)",
                    ErrorCodes.E2005, new { port = Target.Port });
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

    // ---- P2：SIGINT 尽力而为优雅终止（Windows 无控制台进程没有可靠 Ctrl+C 通道） ----
    // 策略：先尝试附加目标进程的控制台并投递 CTRL_BREAK（node 映射为 SIGBREAK，若 dsh
    // 注册了信号处理器则有机会清理）；AttachConsole 失败（wscript 隐藏启动的 node 无控制台，
    // 常态）自动降级温和 taskkill；仍不退则 /f。绝不改变服务启动链路（不引入可见控制台窗口）。

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    private const uint CTRL_BREAK_EVENT = 1;

    /// <summary>尽力而为：目标进程有控制台时投递 CTRL_BREAK；无控制台返回 false（走温和 taskkill）。</summary>
    private static bool TryGracefulStop(int pid)
    {
        try
        {
            if (!AttachConsole((uint)pid)) return false;
            try
            {
                SetConsoleCtrlHandler(IntPtr.Zero, true); // 本进程忽略 Ctrl 事件，避免波及自身
                return GenerateConsoleCtrlEvent(CTRL_BREAK_EVENT, 0); // 发给共享该控制台的进程组
            }
            finally
            {
                FreeConsole();
            }
        }
        catch { return false; }
    }

    /// <summary>停止指定 PID（v0.3.1 P2 优雅终止 + 质量治理 P1-2/P2-10）：
    /// ① 杀前身份校验（防 PID 复用误杀无辜进程）：非 node 进程/进程不存在 → 不杀，返回 false；
    /// ② 尽力而为优雅终止（CTRL_BREAK，无控制台自动降级温和 taskkill）；
    /// ③ 短等待未退则强制 /f，强杀后短暂确认；仍活 → 返回 false
    ///   （调用方保留 pid 文件，下次启动由 SweepStaleServicePid 认领，避免无主残留）。
    /// 全程限时，不卡调用方。</summary>
    private static bool KillProcess(int pid)
    {
        try
        {
            // 质量治理 P1-2：PID 复用防护——pid 文件/端口反查得到的 PID 可能已被系统
            // 复用给无关进程，杀前必须确认它是 dsh 服务（node）进程。
            if (!ShellLogic.IsLikelyDshService(pid))
            {
                Logger.Warn($"refusing to kill pid={pid}: not a dsh (node) process (possible PID reuse)",
                    ErrorCodes.E2005, new { pid });
                return false;
            }
            // P1-3（质量治理）：端口归属校验——记录过的 PID 必须正在监听目标端口。
            // "node 进程但不在监听"几乎必然是 PID 复用给了无关 node（我们的服务就绪时才写
            // pid 文件，就绪即监听；进程活着却丢监听不符合 dsh 运行特征），拒绝误杀。
            if (FindPidListeningOn(Target.Port) != pid)
            {
                Logger.Warn($"refusing to kill pid={pid}: not listening on port {Target.Port} (possible PID reuse)",
                    ErrorCodes.E2005, new { pid, port = Target.Port });
                return false;
            }
            if (!TryGracefulStop(pid))
            {
                Process.Start(new ProcessStartInfo("taskkill", "/pid " + pid + " /T")
                { UseShellExecute = false, CreateNoWindow = true });
            }
            var deadline = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < deadline && IsProcessAlive(pid))
                Thread.Sleep(100);
            if (IsProcessAlive(pid))
            {
                Process.Start(new ProcessStartInfo("taskkill", "/f /pid " + pid + " /T")
                { UseShellExecute = false, CreateNoWindow = true });
                // 质量治理 P2-10：强杀后确认；仍活则不删 pid 文件，留待下次启动认领
                var hardDeadline = DateTime.UtcNow.AddMilliseconds(500);
                while (DateTime.UtcNow < hardDeadline && IsProcessAlive(pid))
                    Thread.Sleep(100);
                if (IsProcessAlive(pid))
                {
                    Logger.Warn($"process pid={pid} still alive after force kill; pid file kept for next-start sweep",
                        ErrorCodes.E2005, new { pid });
                    return false;
                }
            }
            return true;
        }
        catch { return false; }
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
            if (KillProcess(pid)) ClearServicePidFile(); // P2-10：杀不干净则保留 pid 文件，下次启动认领
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

    /// <summary>v0.3.0 托盘按需策略：默认隐藏；仅当需要时创建。
    /// v0.3.1 修复：托盘只在**托盘驻留**模式下常驻显示（关窗藏到托盘，必须靠托盘唤窗）；
    /// "常驻"模式关窗即退出壳（服务保留，下次启动自动开窗），"跟随窗口"关窗全退，
    /// 两者都不需要托盘。另有待通知的更新时临时创建（更新气泡依赖托盘）。
    /// 未装插件时默认"跟随窗口"，托盘无存在意义。</summary>
    private static bool IsTrayWanted()
    {
        if (_pendingUpdate != PendingUpdate.None) return true;
        return ShellLogic.IsLifetimePluginInstalled(DshHomeDir)
            && ReadLifetimeMode() == ShellLogic.ServiceLifetime.Tray;
    }

    // Step 5：EnsureTrayIcon/ShowTrayMenu 已迁入 WindowManager.Instance（委托注入 IsTrayWanted
    // /TrayWhaleIcon/TrayExitAction/TrayMenuFactory）。此处仅保留 IsTrayWanted 供委托引用。

    // Step 6：TrayMenuForm 已迁出至 Windows/TrayMenuForm.cs（纯搬迁，行为逐位不变）。
    // Step 5：ShowMainWindow/TryReloadWebViewDeferred 已迁入 WindowManager.Instance
    //（托盘唤起：先 SW_RESTORE 再 Activate；崩溃/长隐藏延迟重载）。

    /// <summary>
    /// 服务就绪轮询（并行开窗 Step5 抽取）：后台线程等待 dsh 服务 TCP+HTTP 就绪。
    /// 逻辑与旧内联轮询逐位一致；由状态窗提前创建后的 pollTask 承载。
    /// 返回 "ready"/"canceled"/"logerror"/"timeout"。
    /// </summary>
    private static string WaitServiceReady(CancellationToken token, int port, string url, string logPath, bool e2eMode)
    {
        var lastLogCheck = DateTime.MinValue;
        var logErrorSeen = false;
        var logErrorSince = DateTime.MinValue;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        // e2e 探针模式（E2EMode）：轮询上限 20s 自动结束（不弹状态窗、不无限等服务）。
        // 否则无头 CI 上服务未就绪时会一直转圈，探针路径卡死。
        for (var i = 0; i < (e2eMode ? 20 : 180); i++)
        {
            if (token.IsCancellationRequested) return "canceled";
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
            if (PortOpen(port))
            {
                if (ShellLogic.IsHttpReady(url, http)) // 契约纯函数（P1-6）
                {
                    Trace("poll: ready (tcp + http)");
                    return "ready"; // TCP + HTTP 都已就绪
                }
                // HTTP 尚未就绪（前端还在启动），继续等
            }
            if (logErrorSeen && DateTime.Now - logErrorSince >= TimeSpan.FromSeconds(15))
            {
                Trace("poll: log error markers persisted 15s, giving up");
                return "logerror";
            }
            // 启动延迟优化（Step4d）：前 8 次快速轮询（200ms）——node 服务往往在启动
            // 临界点就绪，固定 1s 粒度会让"已就绪"最多白等 1s；快速期后恢复 1s（服务
            // 尚未就绪说明在下载/初始化，低频即可，避免空转）。
            Thread.Sleep(i < 8 ? 200 : 1000);
        }
        Trace("poll: timeout after 180s");
        return "timeout";
    }

    /// <summary>
    /// 服务启动状态窗：显示"正在启动 dsh 服务"（含首次下载提示；v0.3.0 亦可显示
    /// 便携 Node 下载进度文案），可取消。由外部任务完成后调用 Close() 自动关闭；
    /// 取消按钮设 DialogResult.Cancel 并关闭。
    /// </summary>
    private static Form CreateStartupStatusForm(string? caption = null, Action? onCancel = null)
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
            // 并行开窗（Step5）：加载窗必须 TopMost——否则用户前台有其他窗口时，
            // 加载窗藏在后面根本看不到，用户以为双击没反应。
            TopMost = true,
            ShowInTaskbar = true, // 在任务栏可见，配合 TopMost 让用户明确"正在启动"
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
            // 质量治理 P1-1：取消必须同时撤销后台轮询/下载任务（此前仅关窗，
            // "canceled" 分支不可达，UI 线程仍同步等待最长 180s 造成假死）。
            try { onCancel?.Invoke(); } catch { /* 取消回调失败不影响关窗 */ }
        };
        f.Controls.Add(label);
        f.Controls.Add(bar);
        f.Controls.Add(cancel);
        return f;
    }

    /// <summary>
    /// WebView2 初始化已迁入 <see cref="DshWeb.Managers.WebViewManager.InitializeAsync"/>
    ///（Step 4，static 字段语义映射见 docs/refactor-static-mapping.md）。
    /// 此方法保留为调用点兼容转发（Program.Main 编排最终形态前的过渡壳）。
    /// </summary>
    internal static Task InitWebViewAsync(WebView2 web, string userDataFolder)
        => DshWeb.Managers.WebViewManager.InitializeAsync(web, userDataFolder);

    /// 插件内部弹窗用的轻量窗口（与主窗口共享 WebView2 用户数据，保持登录态/会话）。
    internal static (Form Form, WebView2 Web) CreatePopupForm()
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
    internal static Icon? LoadIconResource(string resourceSuffix)
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
    internal static Icon? _darkWhaleIcon;

    /// <summary>白色鲸鱼图标（窗口深色主题/托盘深色背景时用）。</summary>
    internal static Icon? _lightWhaleIcon;

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
    internal static bool ResolveDarkMode()
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
    internal static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetSystemMenu(IntPtr hWnd, bool bRevert);

    [DllImport("user32.dll")]
    internal static extern IntPtr TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    internal const uint TPM_RETURNCMD = 0x0100;
    internal const uint TPM_RIGHTBUTTON = 0x0002;
    internal const int WM_NCLBUTTONDOWN = 0x00A1;
    internal const int HTCAPTION = 0x0002;

    /// <summary>给无边框窗口加 DWM 阴影（DWMWA_NCRENDERING_POLICY=ENABLED）。</summary>
    internal static void ApplyWindowShadow(IntPtr hwnd)
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
    /// 无边框主窗口：处理最大化限制在工作区（WM_GETMINMAXINFO）与边缘缩放（WM_NCHITTEST）。
    /// 标题栏由 <see cref="CustomTitleBar"/> 自绘。
    /// </summary>
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
        if (WindowManager.Instance.TrayIcon is not null)
        {
            try { WindowManager.Instance.TrayIcon.Icon = TrayWhaleIcon ?? SystemIcons.Application; } catch { /* ignore */ }
        }
    }

    // Step 5b：RegisterThemeWatcher/SafeFileMtime/ReleaseThemeWatcher 已迁入
    // WindowManager.Instance（主题监听：FSW+系统事件+2s 轮询，P2-7 统一释放）。

    private static bool PortOpen(int port) => ShellLogic.PortOpen("127.0.0.1", port); // 契约纯函数（P1-6）
}
