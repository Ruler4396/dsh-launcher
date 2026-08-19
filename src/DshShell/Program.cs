using System.ComponentModel;
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

    /// <summary>屏幕拓扑抽象（v0.4.0 Headless 化）：默认 WinForms Screen，测试注入 Fake 拓扑。</summary>
    internal static DshWeb.Win32.IScreenProvider ScreenProvider { get; set; } = new DshWeb.Win32.WinFormsScreenProvider();

    /// <summary>恢复窗口位置（经 IScreenProvider 取拓扑 → ShellLogic 纯函数；测试注入 Fake 验证）。</summary>
    internal static (int X, int Y) RestoreWindowPosition(int x, int y, int width, int height)
        => ShellLogic.RestoreWindowPosition(x, y, width, height,
            ScreenProvider.GetAllWorkingAreas(), ScreenProvider.PrimaryWorkingArea);

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
        // 极速启动（v0.4.1）：日志轮转/超长告警所需的端口探测（PortOpen 为同步 TCP connect，
        // 实测 127.0.0.1 回环探测在部分环境需 2s）已**移出 Main**——进入 SplashForm 后台流水线
        // 阶段 0（Task.Run），UI 线程不再有任何网络调用，窗口 <500ms 即可见。

        // P0-2（质量治理）：崩溃留痕——任何未捕获异常（UI 线程/后台线程/主线程）先写一条
        // E9001 日志再终止，杜绝"窗口突然消失但 dsh.log 无记录"的静默崩溃。只加诊断，不加恢复。
        RegisterCrashHooks();

        // 测试钩子（DSH_TEST_CRASH=1）：验证崩溃留痕钩子生效（negative N9），仅测试使用。
        if (Environment.GetEnvironmentVariable("DSH_TEST_CRASH") == "1")
            throw new InvalidOperationException("test crash hook (DSH_TEST_CRASH=1)");

        Trace($"start target={Target.Url} external={ServerManagedExternally}");
        // 极速启动（v0.4.1）：以下"无 UI 的轻量维护 IO"已全部移入 SplashForm 的后台启动流水线
        //（RunStartupPipelineAsync 阶段 0，Task.Run 执行）：WindowStateStore.Init、StagedUpdate.Init、
        // CleanupStagingCache、MigrateLegacyData、CleanupProgramDataResidue、EnsureAutoStartRequested。
        // Main 在创建窗口前只保留：DPI 感知、日志、崩溃留痕、单实例——双击后 <500ms 出现启动窗。

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

        // 升级场景：检测并提示清理旧版本（per-user 0.1.0-0.1.5 等）。刻意保留在 Main 原位而非
        // 移入 Splash 流水线：正常路径（无旧版/NoUiMode）直接 return，零开销不碰启动延迟；
        // 且它内部有提权卸载 + WaitForExit 的强交互语义，不适合塞进后台流水线。
        TryPromptOldVersionCleanup();
        CleanupOrphanShortcuts(); // 自愈孤儿快捷方式（同上：极低频升级场景）

        // ===== 极速启动模型（v0.4.1）：UI 线程立即进入 Application.Run 消息循环 =====
        // 旧模型（v0.3.x）：HttpReady() 探测（端口开但 HTTP 未就绪时最长阻塞 3s）与数据迁移等
        // IO 都同步发生在窗口创建之前，且等待期用 DoEvents + Thread.Sleep(50) 手动泵消息——
        // 用户"双击后干等几秒无反应"，状态窗刷新/取消按钮劣化（组件短暂空白/闪烁）。
        // 新模型：SplashForm.OnShown 启动后台流水线（Task.Run + IProgress<T> 回填进度），
        // UI 线程只运行消息泵；Splash 关闭后 Main 按结果接力（建主窗/失败提示/退出）。
        // 注意：Application.Run(splash) 返回时 splash.Result 必已赋值（OnShown 即启动流水线）。
        using (var splash = new SplashForm(RunLauncherAppPipelineAsync, visible: !NoUiMode && !ServerManagedExternally))
        {
            Application.Run(splash);

            var outcome = splash.Result;
            if (outcome is null) return; // 防御分支（正常路径不可达）
            if (splash.CancelledByUser)
            {
                Trace("startup canceled by user");
                return;
            }
            if (!outcome.Ready)
            {
                HandleStartupFailure(outcome); // 失败/取消的统一处理（含日志尾部 + 错误码归类）
                return;
            }
            _serviceStartedByShell = outcome.ServiceStartedByShell;
            if (outcome.ServiceStartedByShell)
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
            // 修复 0xc0000005（ImmSetOpenStatus 访问违规，用户 20:53 更新后主窗崩溃真凶）：
            // WinForms 对 WebView2 宿主控件的 IME 状态管理会调用 ImmSetOpenStatus，在 WebView2
            // 抢占输入法上下文时偶发无效 HIMC → 崩溃。WebView2 内部自带 IME 处理（Chromium），
            // 不需要 WinForms 的 ImeMode 介入，置 Disable 让 WinForms 完全跳过 IME 管理。
            ImeMode = ImeMode.Disable,
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
            // v0.4.0 T1：统一用纯函数决策（FollowWindow 且壳管理 且 非外部托管才停）
            if (ShellLogic.ShouldStopServiceOnClose(
                    ReadLifetimeMode(), ServerManagedExternally, _serviceStartedByShell))
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
        // v0.4.2 解耦：WindowManager 不再回调 Program 静态方法（切断 Program↔WindowManager 隐式环），
        // 这些行为全部经组合根注入的委托提供。
        WindowManager.Instance.PopupFactory = CreatePopupForm;
        WindowManager.Instance.ApplyShadowAction = ApplyWindowShadow;
        WindowManager.Instance.ShowWindowAction = ShowWindowNative;
        WindowManager.Instance.TraceAction = Trace;
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

            if (ShellLogic.ShouldStopServiceOnClose(
                    mode, ServerManagedExternally, _serviceStartedByShell))
            {
                // 跟随窗口：关窗即停服务——含本次拉起的与被接管的孤儿服务（接管即负责，T1）
                StopShellService();
            }
            WindowManager.Instance.DisposeTray();
        };

        form.Load += async (_, _) =>
        {
            // v0.4.0 T2 规则 2：服务在跑且待应用版本不一致 → 主窗就绪后一次性询问[立即重启应用][稍后]
            if (_applyRestartPendingVersion is { } applyVersion && !_applyRestartDeferred)
            {
                PromptApplyRestart(form, applyVersion);
            }

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
                // v0.4.0：经 IScreenProvider 取屏幕拓扑（Headless 可注入 Fake 拓扑测试，
                // 不再直接依赖 Screen.AllScreens 的进程级缓存）。
                var (x, y) = RestoreWindowPosition(
                    savedWindow.X, savedWindow.Y, form.Width, form.Height);
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
            // 与真实主窗对齐（见本文件建窗处的 HandleCreated 订阅）：启用 DWM NC 渲染后，
            // 最大化窗口才会向四周外扩 frame——WM_GETMINMAXINFO 的 frame 补偿（pos=work+frame,
            // size=work-2*frame）才成立。探针此前缺此行 → CI（Server runner）上 DWM 不外扩、
            // 补偿落空 → 最大化后四周留 8px 缝隙（e2e-geo G1/G10 回归根因）。
            form.HandleCreated += (_, _) => ApplyWindowShadow(form.Handle);

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

            // TestHook（Task 2 维度三）：DSH_TEST_MODE=1 时启动 NamedPipe 几何控制服务。
            // 生产路径零接触（Enabled 恒 false 即不建 pipe 不开线程）；供 E2E 发 ToggleMaximize/
            // GetWindowRect/GetWorkArea 精确断言"最大化 0px 间隙"。
            using var hookCts = new CancellationTokenSource();
            Task? hookTask = null;
            if (DshWeb.Win32.UiTestHook.Enabled)
            {
                hookTask = Task.Run(() => DshWeb.Win32.UiTestHook.RunAsync(
                    form.Handle, hookCts.Token,
                    onShutdown: () => form.BeginInvoke(() => form.Close())));
                Trace($"ui-probe: test hook listening ({DshWeb.Win32.UiTestHook.PipeName(Environment.ProcessId)})");
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
    /// <summary>本次会话已下载过（MarkPending）的 dsh 版本（v0.4.0 T3：下载成功后又弹"有更新"去重）。</summary>
    private static readonly HashSet<string> _sessionStagedVersions = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>
    /// 统一启动流水线（v0.4.2 收尾）：由 LauncherApp（状态机 + Manager）驱动，替代旧
    /// RunStartupPipelineAsync。组合根职责：装配 Manager 副作用委托（维护 IO/拉起/就绪探针/
    /// 僵尸清理）并桥接 SplashForm 的 IProgress&lt;Message&gt; 与内联确认面板。LauncherApp 自身
    /// 不引用 Program（无循环依赖），Headless 可测（tests/DshShell.Tests/Managers/LauncherApp*Tests.cs）。
    /// </summary>
    private static async Task<SplashForm.Outcome> RunLauncherAppPipelineAsync(
        IProgress<SplashForm.Message> progress,
        Func<string, string, Task<bool>> confirm,
        CancellationToken ct)
    {
        var app = CreateLauncherApp(confirm);
        // IProgress<string>（LauncherApp 进度）→ IProgress<Message>（SplashForm 状态标签）。
        // 任务二："[warn] " 前缀 → 黄色告警（日志 fallback 提示），其余为普通进度。
        // 任务一："[apply] " 前缀 → 更新安装进度（Splash 据此禁用取消按钮 + 更新 Label）。
        IProgress<string> textProgress = new Progress<string>(t =>
        {
            if (t.StartsWith("[apply] ", StringComparison.Ordinal))
                progress.Report(new SplashForm.Message("apply", t.Substring("[apply] ".Length), IsApplyingUpdate: true));
            else if (t.StartsWith("[warn] ", StringComparison.Ordinal))
                progress.Report(new SplashForm.Message("probe", t.Substring("[warn] ".Length), IsError: false, IsWarn: true));
            else
                progress.Report(new SplashForm.Message("probe", t));
        });
        // 任务一：把 Splash 进度桥接进 RunBackgroundMaintenance → ApplyPendingDshUpdate → npm 实时日志。
        // 应用更新阶段用 "[apply] " 前缀标记（Splash 更新 Label 并禁用取消按钮）。
        _updateApplyProgress = s => textProgress.Report("[apply] " + s);
        try
        {
            var ok = await app.RunStartupAsync(textProgress, ct);
            return new SplashForm.Outcome(
                ok,
                app.WaitResult,
                app.ServiceStartedByShell,
                UnifiedLogPath,
                ok ? null : app.LastErrorCode,
                ok ? null : app.LastErrorDetail);
        }
        finally
        {
            _updateApplyProgress = null; // 本次会话结束，清理桥接（防跨会话污染）
        }
    }

    /// <summary>装配 LauncherApp：注入真实副作用（与 Program 静态状态解耦，组合根接线）。</summary>
    private static LauncherApp CreateLauncherApp(Func<string, string, Task<bool>> confirm)
    {
        return new LauncherApp(
            runtime: new RuntimeManager(confirmDownload: () =>
                // 自动化环境不打断（原 EnsureNodeForStartupAsync 顶部语义）
                NoUiMode
                    ? Task.FromResult(false)
                    : confirm(
                        "dsh-launcher - 需要 Node.js",
                        "检测到 Node.js 问题（dsh 服务运行必需）。\n\n" +
                        (RuntimeResolver.NodeMissingReason() == "too-old"
                            ? "系统 Node.js 版本过低或不可用（需要 18 或更高版本）。\n"
                            : "未检测到 Node.js。\n") +
                        "是否自动下载便携版 Node.js 到用户目录？\n" +
                        "（约 30MB，仅用于本启动器，不改动系统环境；版本采用 LTS 固定版）")),
            service: new ServiceManager(),
            staleCleanup: _ => SweepStaleServicePid())
        {
            BackgroundMaintenance = ct => RunBackgroundMaintenance(ct, _updateApplyProgress), // 阶段 0 可取消；进度桥接到 Splash
            SweepStaleAndApplyUpdate = () =>
            {
                // v0.4.0：ApplyPendingDshUpdate 已上移到 BackgroundMaintenance（阶段 0）——
                // npm install -g 应用更新可能耗时 30-60s，原在"正在启动 dsh 服务…"阶段会让用户
                // 误以为服务卡死且取消无效。阶段 0 完成后用户看到的"启动服务"即真实拉起。
                if (!PortOpen(Target.Port))
                {
                    SweepStaleServicePid();   // 僵尸清扫：上次崩溃记录过、已不在监听的进程
                }
            },
            StartService = StartDshServiceViaVbs,
            ReadinessProbe = ct => Task.Run(() => WaitServiceReady(ct, Target.Port, Target.Url, UnifiedLogPath, E2EMode), ct),
        };
    }

    /// <summary>阶段 0 后台维护 IO（原 Main 同步项：日志轮转/数据迁移/自启落地等，由 LauncherApp 后台驱动）。
    /// v0.4.0：延迟更新应用（npm install -g）也在此执行——属耗时 IO（30-60s），放阶段 0 后
    /// 用户看到的"正在启动 dsh 服务…"即真实拉起，不再有"卡住"的误导。
    /// <paramref name="ct"/> 传给 ApplyPendingDshUpdate → npm 安装可被取消（Splash 取消立即生效）。
    /// <paramref name="progress"/>（任务一）：由组合根桥接 Splash 的 IProgress，把"正在应用更新
    /// (vX)…"与 npm 实时安装日志（"added 50 packages"）滚动上报，消除更新期间 UI 静默/卡死错觉。</summary>
    private static void RunBackgroundMaintenance(CancellationToken ct, Action<string>? progress = null)
    {
        if (!PortOpen(Target.Port)) Logger.RotateIfNeeded(); // 仅无活服务占用时轮转
        Logger.WarnIfOversized(); // P2：常驻超长日志（>50MB 且 >24h）告警
        WindowStateStore.Init(DataDir);
        StagedUpdate.Init(DataDir);
        HandlePendingUpdateAtStartup(ct, progress); // v0.4.0 T2：按决策处理，端口开着不再静默跳过
        CleanupStagingCache();         // 下载缓存管理：清理 DataDir\staging 中 >7 天的过期包
        MigrateLegacyData();           // 旧版 %LOCALAPPDATA% 数据迁移到 DSH_HOME
        CleanupProgramDataResidue();   // 清理卸载后 ProgramData 空目录残留
        EnsureAutoStartRequested();    // 自启落地：MSI 机器级意图标志 → 当前用户 HKCU Run
    }

    /// <summary>本次会话"稍后"标记：PromptRestart 拒绝后同会话不再弹（T2 规则 2）。</summary>
    private static bool _applyRestartDeferred;

    /// <summary>等待主窗就绪后一次性弹"立即重启应用"提示的版本（T2 规则 2，由主窗 Load 消费）。</summary>
    private static string? _applyRestartPendingVersion;

    /// <summary>更新安装进度桥接（任务一）：RunLauncherAppPipelineAsync 装配时指向 Splash 进度转发，
    /// RunBackgroundMaintenance → ApplyPendingDshUpdate → npm 实时日志逐行上报。会话结束清空防污染。</summary>
    private static Action<string>? _updateApplyProgress;

    /// <summary>
    /// 启动早期待应用更新决策（v0.4.0 T2，纯函数矩阵 U2 的接线）：
    /// - ApplyNow：服务未运行 → 直接应用（可取消）；
    /// - ClearPending：运行版本 == 待应用版本 → 清账（历史残留）；
    /// - PromptRestart：服务在跑且版本不一致 → 记版本，主窗就绪后一次性询问；
    /// - None：无 pending。
    /// 端口开着时绝不静默跳过（根因 A 修复）。
    /// </summary>
    private static void HandlePendingUpdateAtStartup(CancellationToken ct, Action<string>? progress = null)
    {
        // 测试钩子（DSH_TEST_FAKE_APPLY=1）：E2E 模拟"确认更新→重启→应用"全流程（DshUpdateFlowTests）。
        // 直接走 ApplyPendingDshUpdate（其内部 fake 分支清 pending），**不依赖端口状态**——否则
        // 本地残留服务（端口开）时决策走 PromptRestart，pending 保留，测试不稳定。
        if (Environment.GetEnvironmentVariable("DSH_TEST_FAKE_APPLY") == "1")
        {
            ApplyPendingDshUpdate(ct, progress);
            return;
        }
        var (pendingVersion, _, _) = StagedUpdate.ReadPending();
        if (string.IsNullOrWhiteSpace(pendingVersion)) return;
        var action = ShellLogic.ResolvePendingUpdateAction(
            pendingExists: true,
            portOpen: PortOpen(Target.Port),
            runningVersion: ReadGlobalDshVersion(),
            pendingVersion: pendingVersion);
        switch (action)
        {
            case ShellLogic.PendingUpdateAction.ApplyNow:
                ApplyPendingDshUpdate(ct, progress); // 可取消；失败按策略保留/清理 pending
                break;
            case ShellLogic.PendingUpdateAction.ClearPending:
                StagedUpdate.ClearPending();
                Logger.Info($"pending dsh update {pendingVersion} already running; cleared stale pending");
                break;
            case ShellLogic.PendingUpdateAction.PromptRestart:
                _applyRestartPendingVersion = pendingVersion;
                Logger.Info($"dsh update {pendingVersion} pending while service running; will prompt once on main window");
                break;
        }
    }

    /// <summary>
    /// 读取磁盘上**全局安装的 dsh 版本**（npm root -g 下 package.json 的 version）。
    /// 不碰网络；npm 不可用/未装 dsh 时返回 null（决策回退 PromptRestart/ApplyNow）。
    /// 会话级缓存：npm root -g 冷启动可达 1-2s，本次会话只查一次（启动流水线多处决策复用）。
    /// </summary>
    private static string? _cachedGlobalDshVersion = "unset";

    private static string? ReadGlobalDshVersion()
    {
        if (_cachedGlobalDshVersion != "unset") return _cachedGlobalDshVersion;
        string? version = null;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c npm root -g")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var root = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(3000); // npm 冷启动限制 3s，超时按"未知"处理不拖慢启动
                if (root.Length > 0)
                {
                    var pkg = Path.Combine(root, "@deepseek-ai", "dsh", "package.json");
                    if (File.Exists(pkg))
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(pkg));
                        if (doc.RootElement.TryGetProperty("version", out var v)) version = v.GetString();
                    }
                }
            }
        }
        catch
        {
            // 读不到按"未知"处理（决策回退 PromptRestart/ApplyNow，不阻塞启动）
        }
        _cachedGlobalDshVersion = version; // 缓存：本次会话只查一次（含 null，避免重复慢查询）
        return version;
    }

    /// <summary>拉起 dsh 服务（wscript start-dsh.vbs）。返回 false = 拉起失败（E2001）。</summary>
    private static bool StartDshServiceViaVbs()
    {
        var vbs = Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs");
        if (!File.Exists(vbs))
        {
            Logger.Error($"missing {vbs}, cannot start dsh service", ErrorCodes.E2001);
            return false;
        }
        // 端口与统一日志路径透传给 start-dsh.vbs（进程级环境变量，wscript → cmd → dsh 依次继承）；
        // DSH_PORT 不设时 vbs 默认 3080。DSH_HOME 等环境变量同理自动继承。
        Environment.SetEnvironmentVariable("DSH_PORT", Target.Port.ToString());
        Environment.SetEnvironmentVariable("DSH_LOG", UnifiedLogPath);
        try
        {
            Process.Start(new ProcessStartInfo("wscript.exe", "\"" + vbs + "\"") { UseShellExecute = true });
            Trace("service start requested via start-dsh.vbs");
            return true;
        }
        catch (Exception ex)
        {
            Logger.Error("failed to start dsh service: " + ex.Message, ErrorCodes.E2001);
            return false;
        }
    }

    /// <summary>启动失败/取消的统一处理（v0.4.1 从 Main 内联块提取，逻辑与原 v0.3.x 一致）。</summary>
    private static void HandleStartupFailure(SplashForm.Outcome outcome)
    {
        var logPath = outcome.LogPath;
        // v0.3.0：启动失败时清理"本次拉起但未就绪"的半启动服务（避免残留占端口）
        if (outcome.WaitResult is "logerror" or "timeout" && outcome.ServiceStartedByShell && PortOpen(Target.Port))
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
        else if (outcome.WaitResult == "canceled" && outcome.ServiceStartedByShell && PortOpen(Target.Port))
        {
            RecordServicePid();
            Trace("canceled: service left running; pid recorded for next-start adoption");
        }

        // Node 缺失/下载失败：错误码随 outcome 直达，无需再读日志
        if (outcome.ErrorCode is not null)
        {
            ShowError(outcome.ErrorCode, outcome.ErrorDetail ?? "启动失败。",
                level: outcome.ErrorCode == ErrorCodes.E1002 ? Logger.Level.Info : Logger.Level.Error);
            return;
        }

        var waitResult = outcome.WaitResult ?? "timeout";
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
                // 诊断留痕（v0.4.0）：检测未命中时记录原因，用户可查 dsh.log 判断是"无更新"还是
                // "取不到本地版本/远端版本"（此前完全静默，难排查）。
                if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(local))
                    Trace($"dsh update check: latest={latest ?? "<null>"} local={local ?? "<null>"} (skip)");
                if (!string.IsNullOrWhiteSpace(latest) && !string.IsNullOrWhiteSpace(local)
                    && UpdateChecker.CompareVersions(latest, local) > 0)
                {
                    // v0.4.0 T3 去重：已下载待应用（pending）且 pending.Version >= 检测版本 → 不弹
                    // "有更新"（更新死循环根因 C：下载成功 → pending → 重开又弹）。气泡已由上方
                    // NotifyPendingApply 提示过一次。
                    if (!string.IsNullOrWhiteSpace(pendingVersion)
                        && UpdateChecker.CompareVersions(pendingVersion, latest) >= 0)
                    {
                        Trace($"dsh update {latest} already staged (pending={pendingVersion}); skip");
                        return;
                    }
                    // v0.4.0 T3：本次会话已下载过同版本 → 不再重复提示（下载成功后又弹）
                    if (_sessionStagedVersions.Contains(latest))
                    {
                        Trace($"dsh update {latest} already downloaded this session; skip");
                        return;
                    }
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
            var (_, failCount, _) = StagedUpdate.ReadPending();
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
                $"dsh {version} 主程序已下载。下次重启启动器后自动安装（需联网解析依赖，预计 1-2 分钟）。",
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
        // 带 owner 的 MessageBox 会居中于 owner 且置于其上层；调用前先 Activate 把主窗提到前台，
        // 避免"询问弹窗被其他窗口遮挡/不跳到前台"（v0.4.0 用户反馈）。
        try { form.Activate(); } catch { /* 窗体已关闭则忽略 */ }
        var r = MessageBox.Show(
            form,
            $"检测到 dsh 新版本 {latest}（当前 {local}）。\n\n是否在后台静默下载，下次重启时自动安装？\n" +
            "（下载在后台进行，不影响你当前使用；下次重启启动器时自动安装，需联网解析依赖，预计 1-2 分钟）",
            "dsh 更新", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes)
        {
            MarkSkippedDshVersion(latest); // 用户拒绝：跳过此版本，避免每次启动重复提示
            return;
        }
        _ = Task.Run(() => DownloadDshUpdateStaged(form, latest));
    }

    /// <summary>
    /// v0.4.0 T2 规则 2：服务在跑且待应用版本不一致时的一次性询问（主窗 Load 后调用）。
    /// [立即重启应用] = 停服务 → npm install -g → 拉起服务 → Reload 页面（版本即刻生效）；
    /// [稍后] = 本次会话不再提示（仅日志，服务保持当前版本）。
    /// 取消时**不** MarkSkipped（pending 仍在，下次启动继续按决策处理）。
    /// </summary>
    private static void PromptApplyRestart(Form form, string version)
    {
        try
        {
            form.Activate();
            var r = MessageBox.Show(
                form,
                $"已下载 dsh {version}，但 dsh 服务正在运行中。\n\n" +
                "是否立即重启服务以应用新版本？\n" +
                "（立即 = 停止服务 → 安装 → 自动重新拉起并刷新页面）",
                "dsh 更新待应用", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes)
            {
                _applyRestartDeferred = true; // 本次会话不再提示（仅日志）
                Logger.Info($"user deferred applying staged dsh update {version}");
                return;
            }
            _ = Task.Run(async () =>
            {
                StopShellService(); // 停当前服务（含接管/本次拉起的）
                // 重启即应用路径同样优先本地 tarball（不 npx 现场拉主包）；tarball 缺失回退线上
                var pending = StagedUpdate.ReadPending();
                var localTarball = StagedUpdate.LocateTarball(pending.Version, pending.Tarball);
                var installSpec = localTarball ?? $"@deepseek-ai/dsh@{version}";
                // 任务二一致性：--no-audit --no-fund + registry 与预热匹配（秒级 cache 命中）
                if (RunNpmCommand($"install -g \"{installSpec}\" --no-audit --no-fund" + GetNpmRegistryArgs(),
                    out var errorTail))
                {
                    StagedUpdate.ClearPending();
                    Logger.Info($"staged dsh update applied (restart): {version}");
                    StartDshServiceViaVbs(); // 拉起新版本服务
                    // 等待就绪后刷新页面（最长 60s）
                    var deadline = DateTime.UtcNow.AddSeconds(60);
                    while (DateTime.UtcNow < deadline && !HttpReady())
                        await Task.Delay(500);
                    try
                    {
                        form.BeginInvoke(() =>
                        {
                            if (WebViewManager.MainWeb?.CoreWebView2 is not null)
                            {
                                try { WebViewManager.MainWeb.CoreWebView2.Reload(); } catch { /* 页面已关 */ }
                            }
                        });
                    }
                    catch { /* 窗体已关闭 */ }
                }
                else
                {
                    Logger.Warn("staged dsh update apply (restart) failed: " + errorTail,
                        ErrorCodes.E4002, new { version });
                    try { form.BeginInvoke(() => ShowError(ErrorCodes.E4002,
                        $"dsh {version} 更新安装失败。\n\n可稍后重试，或在命令行手动执行：\nnpm install -g @deepseek-ai/dsh@{version}",
                        log: false)); } catch { /* 窗体已关闭 */ }
                }
            });
        }
        catch { /* 弹窗失败：记日志不打断启动 */ }
    }

    /// <summary>后台执行（任务一：依赖预热 Staging Pipeline）：npm pack 下载主包 tarball → 在
    /// prefetch_temp 中完整 npm install 预热全局 npm cache → MarkPending（下次启动应用）。
    /// 核心收益：预热把所有 @deepseek-ai/* 依赖子包拉进 npm cache，重启时的 npm install -g
    /// **完全命中本地缓存**（秒级），从根上消除"重启卡 1-2 分钟"（用户痛点）。
    /// 预热失败（网络/超时）**不中断流程**：Warn 降级，仍保留 tarball，重启回退在线安装（任务三）。
    /// 下载/预热全程后台，不打断当前会话（v0.4.0：不弹 Modal，仅托盘气泡轻提示）。
    /// </summary>
    private static void DownloadDshUpdateStaged(Form form, string latest)
    {
        var staging = Path.Combine(DataDir, "staging");
        var prefetchDir = Path.Combine(staging, "prefetch_temp");
        try
        {
            Directory.CreateDirectory(staging);
            // 根因修复（用户 22:0x 报"文件名、目录名或卷标语法不正确" E4001）：prefetch_temp 目录
            // 从未被创建，npm pack --pack-destination 指向不存在的目录 → Windows 中文系统底层 fs
            // 返回 ERROR_INVALID_NAME（本地化中文错误）。pack 目标目录必须先存在。
            Directory.CreateDirectory(prefetchDir);
            // ---- 步骤 1/2：pack 主包 tarball 到 prefetch_temp ----
            var ok = RunNpmCommand(
                $"pack @deepseek-ai/dsh@{latest} --pack-destination \"" + prefetchDir + "\"",
                out var errorTail);
            if (!ok)
            {
                Logger.Error("staged dsh update download failed: " + errorTail, ErrorCodes.E4001, new { latest });
                try
                {
                    // 任务二 UX：暴露真实 errorTail（不再硬编码"下载失败"把原因藏进日志）；
                    // 区分错误类型——npm 环境缺失 vs 网络/registry 问题（不同建议文案）。
                    var reason = string.IsNullOrWhiteSpace(errorTail) ? "未知原因" : errorTail;
                    var hint = IsNpmNotFoundError(errorTail)
                        ? "未检测到 npm 环境，请确保已安装 Node.js 18+ 并将其加入 PATH。"
                        : "可稍后重试；如持续失败，请检查网络/代理后手动执行：npm install -g @deepseek-ai/dsh@";
                    form.BeginInvoke(() => ShowError(ErrorCodes.E4001,
                        $"dsh {latest} 下载失败。\n\n原因：{reason}\n\n{hint}{latest}",
                        log: false));
                }
                catch { /* 窗体已关闭 */ }
                return;
            }

            // npm pack 的 scoped 包产物名：@deepseek-ai/dsh → deepseek-ai-dsh-{version}.tgz
            var tarballName = $"deepseek-ai-dsh-{latest}.tgz";
            var tarballPath = Path.Combine(prefetchDir, tarballName);
            var prefetched = false;

            // ---- 步骤 3（关键）：在 prefetch_temp 中完整安装一次，把全部依赖拉进 npm cache。
            // --prefix ./deps：安装到临时 deps 目录（不在全局），仅借 npm 的解析+下载把依赖包
            // 写入 ~/.npm/_cacache。registry 与重启安装一致，确保 cache 命中。超时 180s。
            // 预热失败仅降级（Warn + 保留 tarball），绝不中断更新流程（任务三容错）。----
            if (File.Exists(tarballPath))
            {
                try
                {
                    Logger.Info($"dependency prefetch starting: {latest}"); // 后台静默预热，进度经日志留痕
                    // 修复（用户 21:19/21:40 下载秒败 ENOENT）：预热必须在 prefetch_temp 工作目录
                    // 执行——`./<tarball>`、`--prefix ./deps` 相对路径依赖该目录，默认 WorkingDirectory
                    // 是 DshWeb.exe 目录，npm 找不到 tarball 直接 ENOENT。传入 prefetchDir 修正。
                    prefetched = RunNpmCommand(
                        $"install \"./{tarballName}\" --prefix \"./deps\" --no-audit --no-fund"
                            + GetNpmRegistryArgs(),
                        out var prefetchTail, timeoutMs: 180000, workingDirectory: prefetchDir);
                    if (prefetched)
                    {
                        Logger.Info($"dependency prefetch complete: {latest}",
                            ctx: new { version = latest, registry = GetNpmRegistryArgs() });
                    }
                    else
                    {
                        // 任务三：预热失败降级——不中断，保留 tarball，重启回退在线安装
                        Logger.Warn("Dependency prefetch failed, will fallback to online install on next restart",
                            ErrorCodes.E4001, new { version = latest, tail = prefetchTail });
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn("Dependency prefetch error, will fallback to online install on next restart",
                        ErrorCodes.E4001, new { version = latest, error = ex.Message });
                }
            }

            // ---- 步骤 4：把 tarball 移到 staging（避免后续清理 prefetch_temp 时误删），
            // 记录到 pending-update.json；重启时优先本地 tarball + 已预热 cache → 秒级安装。----
            var finalTarballPath = Path.Combine(staging, tarballName);
            try
            {
                if (File.Exists(finalTarballPath)) File.Delete(finalTarballPath);
                File.Move(tarballPath, finalTarballPath);
            }
            catch { /* 移动失败：保留在 prefetch_temp 也可用，LocateTarball 兜底 staging */ }

            StagedUpdate.MarkPending(latest, tarballName);
            _sessionStagedVersions.Add(latest); // v0.4.0 T3：会话级去重，下载成功不再重复提示

            // 清理 prefetch_temp 中的临时安装目录（deps/node_modules），仅保留 staging 根下的 tarball。
            // 注意：prefetch_temp 本身在 CleanupStagingCache 中按 7 天过期清理，这里只删体积大的 deps。
            TryDeleteDir(Path.Combine(prefetchDir, "deps"));
            TryDeleteDir(Path.Combine(prefetchDir, "node_modules"));

            // 后台静默下载：仅托盘气泡轻提示（不打断当前 harness 使用），不弹 Modal。
            // 文案（任务一 UX）：如实区分"预热成功/失败"——预热成功 → 重启秒装；失败 → 重启需联网。
            var balloon = prefetched
                ? $"dsh {latest} 主程序与依赖包已就绪。下次重启启动器时秒级安装（无需等待下载）。"
                : $"dsh {latest} 主程序已下载（依赖未完全预热）。重启后安装需联网解析依赖，预计 1-2 分钟。";
            try
            {
                form.BeginInvoke(() =>
                {
                    if (WindowManager.Instance.TrayIcon is null) return;
                    WindowManager.Instance.ShowBalloonTip(8000, "dsh 更新已就绪", balloon, ToolTipIcon.Info);
                });
            }
            catch { /* 窗体已关闭则下次启动再说 */ }
            Logger.Info($"staged dsh update downloaded: {latest}",
                ctx: new { tarball = tarballName, prefetched });
        }
        catch (Exception ex)
        {
            Logger.Error("staged dsh update download error: " + ex.Message, ErrorCodes.E4001);
            try { form.BeginInvoke(() => ShowError(ErrorCodes.E4001, ex.Message, log: false)); } catch { /* 窗体已关闭 */ }
        }
    }

    /// <summary>递归删除目录（幂等）；失败静默（清理临时目录不阻塞主流程）。</summary>
    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* 清理失败忽略 */ }
    }

    /// <summary>v0.3.0 延迟应用：下次启动拉起服务前，应用已下载的 dsh 新版。
    /// 失败不阻塞启动（继续用旧版，错误码 E4002，下次启动重试，幂等）。
    /// <paramref name="progress"/>（任务一）：把"正在应用更新 (vX)…"与 npm 实时安装日志
    /// （"added 50 packages"）滚动上报到 Splash；任务三：失败时弹模态提示 + 按策略保留/清理 pending。</summary>
    private static void ApplyPendingDshUpdate(CancellationToken ct = default, Action<string>? progress = null)
    {
        var version = StagedUpdate.ReadPendingVersion();
        if (string.IsNullOrWhiteSpace(version)) return;
        // 测试钩子（DSH_TEST_FAKE_APPLY=1）：E2E 在无 node 环境模拟"应用成功"——直接清 pending，
        // 验证"重启后 pending 清账 + 不再重复弹更新"链路（DshUpdateFlowTests）。生产不设置。
        if (Environment.GetEnvironmentVariable("DSH_TEST_FAKE_APPLY") == "1")
        {
            StagedUpdate.ClearPending();
            Logger.Info($"fake apply staged dsh update (test hook): {version}");
            return;
        }
        // 任务一：更新安装阶段显式上报——Splash Label 更新为"正在应用更新 (vX)…"，
        // 并在后台应用期间持续上报 npm 实时日志，缓解"卡死"焦虑。
        // 任务（v0.4.0 改）：优先用下载时落地的本地 tarball 安装（npm install -g <tarball>，
        // 秒级、不现场拉取，真正"已下载完成"）；tarball 缺失（缓存被清/旧记录）才回退线上拉取，
        // 此时文案如实"将现场下载"（用户：下载≠npx 现场拉，必须诚实）。
        var pending = StagedUpdate.ReadPending();
        var localTarball = StagedUpdate.LocateTarball(pending.Version, pending.Tarball);
        // 任务四：Splash 实时文案（本地安装中）。后台已预热依赖 → npm install -g 命中本地 cache，
        // 秒级完成；tarball 缺失（旧记录/缓存被清）→ 如实"在线下载 dsh 组件"。
        // 任务二：--no-audit --no-fund --registry=镜像 与预热一致（防 cache miss）。
        progress?.Invoke(localTarball is not null
            ? $"正在应用更新 (v{version})…（本地安装中，依赖已预热，预计 5-10 秒）"
            : $"正在应用更新 (v{version})…（需要在线下载 dsh 组件，预计 1-2 分钟）");
        Logger.Info($"applying staged dsh update to {version}",
            ctx: new { version, source = localTarball is not null ? "local-tarball" : "registry" });
        if (ct.IsCancellationRequested) return; // 用户取消：保留 pending，下次启动再应用
        var installSpec = localTarball ?? $"@deepseek-ai/dsh@{version}";
        // 任务二：安装参数与预热完全一致（--no-audit --no-fund + registry）——若预热已把依赖
        // 拉进 npm cache，本命令跳过网络仅解压拷贝，秒级完成；预热失败则自然回退在线安装（慢但可靠）。
        if (RunNpmCommand($"install -g \"{installSpec}\" --no-audit --no-fund" + GetNpmRegistryArgs(),
            out var errorTail, ct, progress))
        {
            progress?.Invoke($"更新 v{version} 已应用完成。");
            StagedUpdate.ClearPending();
            CleanupStagingCache(); // 应用成功：清空 staging 过期文件
            // 任务二：彻底清理 prefetch_temp（预热临时目录：deps/node_modules，释放磁盘空间）
            TryDeleteDir(Path.Combine(DataDir, "staging", "prefetch_temp"));
            Logger.Info($"staged dsh update applied: {version}");
        }
        else
        {
            // 取消导致的失败不算 E4002（否则误累计 ApplyFailed）
            if (ct.IsCancellationRequested) { Logger.Info("staged dsh update apply canceled; will retry next launch"); return; }
            StagedUpdate.MarkApplyFailed(); // v0.3.1：累计失败次数，持续失败降级为仅日志
            Logger.Warn("staged dsh update apply failed; continuing with current version", ErrorCodes.E4002,
                new { version, tail = errorTail });
            // 任务三：安装失败必须给用户明确反馈（此前仅记日志，用户"更新莫名其妙失败了"）。
            NotifyUpdateApplyFailed(version, errorTail);
        }
    }

    /// <summary>
    /// 任务三：更新安装失败的 UI 反馈 + pending 保留/清理策略。
    /// - 网络/超时类失败（errorTail 含 timeout/ETIMEDOUT/ECONNRESET/registry error）→ 保留 pending，
    ///   下次启动自动重试（不打扰）——仅记录日志；
    /// - 其他失败（权限/包损坏等，重试无意义）→ 清 pending（防死循环）+ 模态弹窗明确告知。
    /// </summary>
    private static void NotifyUpdateApplyFailed(string version, string errorTail)
    {
        var retryable = IsRetryableNpmError(errorTail);
        // 网络/超时类：保留 pending，下次启动重试；日志记录（避免每次启动都打扰）
        if (retryable)
        {
            Logger.Info($"update {version} apply failed with retryable error; pending kept for next launch",
                ctx: new { version, tail = errorTail });
            return;
        }
        // 非重试类（权限/包损坏）：清 pending 防死循环 + 模态弹窗
        StagedUpdate.ClearPending();
        Logger.Error($"update {version} apply failed with non-retryable error; pending cleared",
            ErrorCodes.E4002, new { version, tail = errorTail });
        try
        {
            var detail = string.IsNullOrWhiteSpace(errorTail) ? "未知原因" : errorTail;
            if (!NoUiMode)
            {
                // 任务三：显示主窗口之前必须弹模态，明确告知失败原因与后续动作
                var dlg = _pendingForm; // 更新提示托盘宿主（可能为 null，回退无 owner）
                var text = $"自动应用更新失败 (v{version})。\n\n将继续使用旧版本启动。\n\n原因：{detail}\n\n" +
                           "您可以稍后在设置中重试更新。";
                if (dlg is not null)
                {
                    dlg.BeginInvoke(() => MessageBox.Show(dlg, text, "dsh 更新失败",
                        MessageBoxButtons.OK, MessageBoxIcon.Error));
                }
                else
                {
                    MessageBox.Show(text, "dsh 更新失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
        catch { /* 弹窗失败不影响启动 */ }
    }

    /// <summary>判定 npm 失败是否为可重试的网络类错误（任务三 pending 保留依据）。委托 ShellLogic 纯函数。</summary>
    private static bool IsRetryableNpmError(string tail) => ShellLogic.IsRetryableNpmError(tail);

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

    /// <summary>
    /// npm registry 镜像参数（与 start-dsh.vbs 的 DSH_NPM_MIRROR 约定一致）：
    /// 设置 → 追加 "--registry=&lt;mirror&gt;"；未设置 → 返回空串（用 npm 默认 registry）。
    /// 预热与安装都用同一镜像，保证 cache 命中（任务一/二：不同 registry 会 miss 缓存）。</summary>
    private static string GetNpmRegistryArgs()
    {
        var mirror = Environment.GetEnvironmentVariable("DSH_NPM_MIRROR");
        return string.IsNullOrWhiteSpace(mirror) ? "" : " --registry=" + mirror;
    }

    /// <summary>
    /// 解析 npm.cmd 的绝对路径（任务一：环境隔离与回退——GUI 进程从桌面启动时 PATH 可能不含
    /// Node 目录，`cmd /c npm` 会报"'npm' 不是内部或外部命令"）。解析顺序：
    ///   ① RuntimeResolver.ResolveExisting().RootDir（PATH/注册表/便携三源解析出的 Node 根目录，
    ///      Node 安装目录自带 npm.cmd）→ 拼接 npm.cmd；
    ///   ② PATH 中 where npm.cmd（Fallback）；
    ///   ③ 都失败返回 null（调用方回退 `cmd /c npm` 并靠 cmd PATHEXT 解析，errorTail 会给出
    ///      "'npm' 不是内部或外部命令" 供错误报告区分"未检测到 npm 环境"）。
    /// 带引号返回（含空格路径安全）。</summary>
    private static string? ResolveNpmCmdPath()
    {
        string? fromPath = null;
        try
        {
            // Fallback：PATH 中定位 npm.cmd（where 命令，与 ResolveLocalDshVersion 的 cmd shim 同款）
            var psi = new ProcessStartInfo("where", "npm.cmd")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is not null)
            {
                var first = p.StandardOutput.ReadLine();
                p.WaitForExit(3000);
                if (!string.IsNullOrWhiteSpace(first)) fromPath = first.Trim();
            }
        }
        catch { /* 忽略：交给纯函数判定 */ }
        // 纯函数（任务一/四）：优先 Node 根目录 → where 结果；均无效返回 null（回退 cmd /c npm）
        try
        {
            var env = RuntimeResolver.ResolveExisting();
            return ShellLogic.ResolveNpmCmdPath(env.RootDir, fromPath);
        }
        catch { return ShellLogic.ResolveNpmCmdPath(null, fromPath); }
    }

    /// <summary>运行 npm 命令（v0.3.0 起唯一 npm 执行点）：输出重定向避免死锁。
    /// <paramref name="ct"/> 取消时**立即 Kill 进程树**返回 false——保证启动阶段应用更新
    /// （npm install -g 可达 30-60s）可被用户取消，Splash 取消按钮不失效（v0.4.0）。
    /// <paramref name="timeoutMs"/>：默认 120s；后台依赖预热（prefetch，任务一）网络重负载
    /// 场景放宽到 180s，超时强制 kill 进程树并保留已下载的 tarball（任务三超时控制）。
    /// <paramref name="progress"/>（任务一进阶）：逐行转发 npm 实时安装日志（如 "added 50 packages"）
    /// 到 Splash，滚动显示消除"更新卡死"焦虑。收集的 stdout+stderr 仍用于 errorTail 诊断。</summary>
    private static bool RunNpmCommand(string args, out string errorTail, CancellationToken ct = default,
        Action<string>? progress = null, int timeoutMs = 120000, string? workingDirectory = null)
    {
        errorTail = "";
        try
        {
            // v0.4.0 修复：npm 全局包在 Windows 是 .cmd/.ps1 shim（npm.cmd），
            // Process.Start("npm.cmd", ...) + UseShellExecute=false 走 CreateProcess，
            // **不解析 batch shim**（抛 ERROR_BAD_EXE_FORMAT）→ 点击更新后 E4001 的根因
            //（与 ResolveLocalDshVersion 用 dsh 直接启动同一类 bug）。现改经 cmd.exe /c
            // 执行，由 cmd 按 PATHEXT 解析 npm.cmd；重定向 stdout/stderr。
            // v0.4.0 任务一：GUI 进程 PATH 可能不含 Node 目录 → 优先用已解析的 Node 根目录
            // 拼 npm.cmd 绝对路径（ResolveNpmCmdPath），避免 "'npm' 不是内部或外部命令"。
            // cmd /c 引号规则（实测锁定，用户 22:2x 下载 E4001 根因）：
            //   - `cmd /c "D:\node\npm.cmd" pack ...`（npm 路径带引号、外层无包裹）→ cmd 剥离
            //     首尾引号后引号计数错乱 → ERROR_INVALID_NAME（中文"文件名、目录名或卷标语法不正确"）
            //   - `cmd /c ""D:\node\npm.cmd" pack ..."`（整行双层引号包裹）→ cmd 剥离最外层，
            //     内部引号保留给 npm → 正常执行；含空格路径亦安全（实测验证）。
            // ResolveNpmCmdPath 现返回不带引号的裸路径，这里按 cmd 标准形式包裹。
            var npmCmd = ResolveNpmCmdPath();
            var cmdLine = npmCmd is not null
                ? "/c \"\"" + npmCmd + "\" " + args + "\""
                : "/c npm " + args; // Fallback：PATH + cmd PATHEXT 解析（errorTail 报"'npm' 不存在"）
            var psi = new ProcessStartInfo("cmd.exe", cmdLine)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                // 编码：不显式设置（.NET 默认用系统 ANSI 代码页解码，中文 Windows 即 GBK，
                // 中文错误可读）。曾尝试 StandardErrorEncoding=UTF8 反致 GBK 中文变 U+FFFD 乱码
                //（实测 stderr 变 \uFFFD...），故保持默认，避免二次损坏。
                // 预热（prefetch）必须在该工作目录执行，否则 `./<tarball>`、`--prefix ./deps`
                // 相对路径会指向 DshWeb.exe 目录（ENOENT 根因：用户 21:19/21:40 下载秒败）。
                WorkingDirectory = workingDirectory,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            // 任务一进阶：逐行读取 stdout/stderr 实时转发到 Splash（异步事件，不阻塞主循环）。
            var outLines = new List<string>();
            var errLines = new List<string>();
            var outLock = new object();
            var errLock = new object();
            p.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                lock (outLock) outLines.Add(e.Data);
                progress?.Invoke(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                lock (errLock) errLines.Add(e.Data);
                progress?.Invoke(e.Data);
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            // WaitForExit 期间可被外部取消：注册回调 Kill 进程树，避免"点取消无效"（用户反馈：
            // 重启卡在启动服务、点取消几十秒才自己关——阶段 0 npm install 不可取消所致）。
            using var reg = ct.Register(() =>
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* 尽力 */ }
            });
            // 任务一进阶：process=null 时不传 progress 也会走完（读取线程仍收集，errorTail 不受影响）
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 尽力 */ }
                return false;
            }
            if (ct.IsCancellationRequested) return false;
            // 事件回调可能落后于 WaitForExit 返回，短暂同步读一次剩余流（防 errorTail 缺行）
            var combined = "";
            lock (outLock) combined += string.Join("\n", outLines);
            lock (errLock) { if (combined.Length > 0) combined += "\n"; combined += string.Join("\n", errLines); }
            var lines = combined.Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            if (lines.Count > 0)
                errorTail = string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 6)));
            // 任务一/二：npm 未找到（GUI PATH 无 Node）→ errorTail 转明确提示（用户可诊断"请装 Node"）
            if (p.ExitCode != 0 && IsNpmNotFoundError(errorTail))
                errorTail = "未检测到 npm 环境（'npm' 不是内部或外部命令）。请确保已安装 Node.js 18+，并确认其 bin 目录在 PATH 中。";
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex)
        {
            // CreateProcess 失败（cmd.exe 异常环境）：转明确 npm 环境提示而非裸异常
            errorTail = "无法启动 npm（" + ex.Message + "）。请确保已安装 Node.js 18+。";
            return false;
        }
        catch (Exception ex)
        {
            errorTail = ex.Message;
            return false;
        }
    }

    /// <summary>判定 npm 输出是否为"找不到 npm/cmd"类错误（'不是内部或外部命令'/'not recognized'）。
    /// 委托 ShellLogic 纯函数（任务四：契约测试锁定，NpmCmd_NotFound_FailsGracefully 语义）。</summary>
    private static bool IsNpmNotFoundError(string tail) => ShellLogic.IsNpmNotFoundError(tail);

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
            // v0.4.0：等待从 1.5s 缩短至 800ms——taskkill /T 已发出即任务完成，OS 回收进程
            // 需要的时间短于此；缩短同步等待消除"关窗卡两秒"（用户反馈，曾修过同类问题）。
            var deadline = DateTime.UtcNow.AddMilliseconds(800);
            while (DateTime.UtcNow < deadline && IsProcessAlive(pid))
                Thread.Sleep(100);
            if (IsProcessAlive(pid))
            {
                Process.Start(new ProcessStartInfo("taskkill", "/f /pid " + pid + " /T")
                { UseShellExecute = false, CreateNoWindow = true });
                // 质量治理 P2-10：强杀后确认；仍活则不删 pid 文件，留待下次启动认领
                var hardDeadline = DateTime.UtcNow.AddMilliseconds(300);
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
            // KillProcess 内部：PID 校验 → 温和（CTRL_BREAK/taskkill）→ 同步等待 → /f /T 兜底
            if (KillProcess(pid))
            {
                // v0.4.0 T1：端口释放探测——进程已死但端口未释放（子进程/TIME_WAIT）时同步
                // 等待，确保关窗后 node 不残留、不占端口。等待上限 1s（原 2s）：TIME_WAIT 由
                // SO_REUSEADDR 自动收敛，超过即记日志不阻塞关窗（消除"关窗卡两秒"）。
                var deadline = DateTime.UtcNow.AddSeconds(1);
                while (DateTime.UtcNow < deadline && FindPidListeningOn(Target.Port) > 0)
                    Thread.Sleep(80);
                if (FindPidListeningOn(Target.Port) > 0)
                    Logger.Warn($"service pid={pid} killed but port {Target.Port} still occupied",
                        ErrorCodes.E2005, new { pid, port = Target.Port });
                else
                    ClearServicePidFile();
            }
            // P2-10：杀不干净则保留 pid 文件，下次启动认领
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
                // 任务三：主日志被锁时（cmd >> 独占）读取 fallback 日志，错误标志检查不失效——
                // 两者任一出现启动错误标志都会触发 15s 宽限期提前退出（诊断盲区消除）。
                var content = SafeReadText(logPath);
                if (string.IsNullOrWhiteSpace(content)
                    && !string.Equals(logPath, Logger.FallbackPath, StringComparison.OrdinalIgnoreCase))
                {
                    var fb = SafeReadText(Logger.FallbackPath);
                    if (!string.IsNullOrWhiteSpace(fb)) content = fb;
                }
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

    // 服务启动状态窗已迁移为 Windows/SplashForm.cs（v0.4.1 极速启动模型）：
    // 双缓冲 + 内联确认面板 + IProgress<T> 回填进度，替代旧的 CreateStartupStatusForm
    //（DoEvents 手动消息泵 + ShowDialog 嵌套模态循环的方案已整体废弃）。

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
