using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using DshWeb;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace DshWeb.Managers;

/// <summary>
/// WebView2 管理（Step 4：InitWebViewAsync 全部事件接线迁入，行为逐位保持）。
///
/// static 字段语义映射（docs/refactor-static-mapping.md，commit message 必须引用）：
/// - <see cref="_sharedEnvironment"/> + <see cref="SharedEnvLock"/>：**进程级环境持有者**。
///   主窗 manager 单例、弹窗各自 new manager（实例），但共享环境不随 manager 实例化重复创建
///   （弹窗 new manager 时若重新 CreateAsync 会再开一份环境/锁）。故本类所有成员为 static，
///   环境只有一份。
/// - <see cref="_crashCount"/> / <see cref="_lastCrashTick"/> / <see cref="_lastReloadTick"/>：
///   **进程级崩溃节流**（W5）。跨所有 WebView2 实例（主窗+弹窗）共享；若降级实例级，
///   每个弹窗各计各的，主窗达 3 次上限后不再自愈或弹窗清零主窗计数 → 节流形同虚设。
/// - <see cref="MainWeb"/>（原 _mainWeb）：**主窗级引用**（托盘恢复用）。弹窗实例
///   ReferenceEquals(web, MainWeb) 为 false，不污染主窗恢复标志（P1-4）。
/// - <see cref="RecoveryNeeded"/> / <see cref="HiddenSince"/>（原 _webviewRecoveryNeeded/
///   _hiddenSince）：**主窗级恢复状态**。由 FormClosing 隐藏路径写 HiddenSince、
///   ProcessFailed 写 RecoveryNeeded，ShowMainWindow 读取并触发重载。
/// </summary>
public sealed class WebViewManager : IWebViewManager
{
    // ---- 进程级环境持有者（F 组映射）：主窗+弹窗共享一份环境/锁 ----
    private static CoreWebView2Environment? _sharedEnvironment;
    private static readonly SemaphoreSlim SharedEnvLock = new(1, 1);

    // ---- 进程级崩溃节流（A 组映射）：跨所有 WebView2 实例共享 ----
    private static int _crashCount;
    private static long _lastCrashTick;
    private static long _lastReloadTick;

    /// <summary>下载完成但非"无害扩展名"时的提示回调（由 Program 注入，经托盘气泡告知落盘位置）。
    /// 解耦：WebViewManager 不直接依赖 Program 的托盘实现（S2 防恶意自动运行提示）。</summary>
    public static Action<string>? DownloadNotifyAction { get; set; }

    /// <summary>页面恢复成功后复位崩溃计数（P1-3），由导航完成回调调用。</summary>
    public static void ResetCrashCount() => Interlocked.Exchange(ref _crashCount, 0);

    /// <summary>IWebViewManager 接口实现（组合根可注入）：静态 InitializeAsync 的实例入口。</summary>
    Task IWebViewManager.InitializeAsync(WebView2 web, string userDataFolder)
        => InitializeAsync(web, userDataFolder);

    // ---- 主窗级（B 组映射）----
    /// <summary>主窗口 WebView2 控件引用（托盘恢复检查/重载渲染用）。</summary>
    public static WebView2? MainWeb { get; set; }
    /// <summary>渲染崩溃标志：窗口隐藏期间崩溃，恢复窗口时须重载页面，否则白屏。</summary>
    public static bool RecoveryNeeded { get; set; }
    /// <summary>上次隐藏窗口时间戳（长隐藏 &gt;5min 渲染进程可能被回收 → 恢复时强制重载）。</summary>
    public static DateTime HiddenSince { get; set; }

    /// <summary>
    /// 统一的 WebView2 初始化：设置 + 权限 + 下载 + 弹窗 + 崩溃自愈。
    /// 主窗口与插件弹出的内部窗口共用，保证行为一致。
    /// </summary>
    public static async Task InitializeAsync(WebView2 web, string userDataFolder)
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
            try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                        catch (Exception ex) { Logger.Warn("open external link in default browser failed: " + ex.Message); }
        };

        // 下载：固定保存到系统"下载"文件夹（自动避开同名文件），完成后用默认程序打开
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
                                try { Process.Start(new ProcessStartInfo(e.DownloadOperation.ResultFilePath) { UseShellExecute = true }); }
                                catch (Exception ex) { Logger.Warn("open downloaded file failed: " + ex.Message); }
                            }
                            else
                            {
                                try { DownloadNotifyAction?.Invoke(e.DownloadOperation.ResultFilePath); }
                                catch (Exception ex) { Logger.Warn("download notify balloon failed: " + ex.Message); }
                            }
                        }
                        catch (Exception ex) { Logger.Warn("download completion handling failed: " + ex.Message); }
                    }
                };
            }
            catch (Exception ex) { Logger.Warn("download handling failed; falling back to default behavior: " + ex.Message); }
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
                    try { Process.Start(new ProcessStartInfo(e.Uri) { UseShellExecute = true }); }
                        catch (Exception ex) { Logger.Warn("open external link in default browser failed: " + ex.Message); }
                    return;
                case ShellLogic.PopupTarget.Internal:
                {
                    var deferral = e.GetDeferral();
                    try
                    {
                        var popup = DshWeb.Program.CreatePopupForm();
                        await InitializeAsync(popup.Web, userDataFolder);
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
            Logger.Info($"webview process failed: {e.ProcessFailedKind}");
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
                or CoreWebView2ProcessFailedKind.GpuProcessExited)
            {
                // 质量治理 P1-4：非主窗实例（插件弹窗）崩溃只记录，不污染主窗恢复标志、
                // 不参与主窗 reload 节流。
                if (!ReferenceEquals(web, MainWeb))
                {
                    Logger.Info("webview process failed on a non-main instance; main recovery state untouched");
                    return;
                }

                // 质量治理 P1-3：连续崩溃计数（10s 窗口）——确定性崩溃页面不再无限崩→重载
                // 循环；达到上限停止自动重载（仍置 recovery 标志，保留托盘唤窗/重开的手动恢复入口）。
                var now = Environment.TickCount64;
                var lastCrash = Interlocked.Read(ref _lastCrashTick);
                if (now - lastCrash > 10_000)
                    Interlocked.Exchange(ref _crashCount, 1);
                else
                    Interlocked.Increment(ref _crashCount);
                Interlocked.Exchange(ref _lastCrashTick, now);

                if (Volatile.Read(ref _crashCount) >= 3)
                {
                    Logger.Error($"renderer keeps crashing ({Volatile.Read(ref _crashCount)} crashes in window); auto-reload stopped, manual recovery via tray/show remains",
                        ErrorCodes.E1007, new { kind = e.ProcessFailedKind.ToString() });
                    RecoveryNeeded = true;
                    return;
                }

                RecoveryNeeded = true;
                if (MainWeb is { Visible: true, IsDisposed: false })
                {
                    var last = Interlocked.Read(ref _lastReloadTick);
                    if (now - last > 10_000
                        && Interlocked.CompareExchange(ref _lastReloadTick, now, last) == last)
                    {
                        try { web.CoreWebView2.Reload(); }
                        catch (Exception ex) { Logger.Warn("webview reload after process failure failed: " + ex.Message); }
                    }
                }
            }
        };

        // Task 0.2 e2e 白屏断言钩子（纯诊断，不改行为）：DSH_WEBVIEW2_READYSTATE=路径 时，
        // 仅对主窗 WebView2，每次导航成功后把 document.readyState 写入该文件。供 e2e 探针
        // 验证"主窗非白屏"（托盘恢复用例复用此钩子）。弹窗不触发（ReferenceEquals 门控）。
        var readyStatePath = Environment.GetEnvironmentVariable("DSH_WEBVIEW2_READYSTATE");
        if (!string.IsNullOrEmpty(readyStatePath) && ReferenceEquals(web, MainWeb))
        {
            web.CoreWebView2.NavigationCompleted += async (_, e) =>
            {
                if (!e.IsSuccess) return;
                try
                {
                    var state = await web.CoreWebView2.ExecuteScriptAsync("document.readyState");
                    File.WriteAllText(readyStatePath, state);
                }
                catch (Exception ex) { Logger.Warn("ready-state test hook failed: " + ex.Message); }
            };
        }
    }

    /// <summary>
    /// 共享 WebView2 环境（进程级持有者，F 组映射）：主窗 + 弹窗共用 user-data 保持会话。
    /// 铁律：弹窗 new manager 时**不得**重新 CreateAsync——环境/锁只有一份，重复创建会开多份
    /// 环境/user-data 锁死。与 0.3.4 语义逐位一致（双检锁 + SemaphoreSlim(1,1)）。
    /// </summary>
    private static async Task<CoreWebView2Environment> GetSharedEnvironmentAsync(string userDataFolder)
    {
        if (_sharedEnvironment is not null) return _sharedEnvironment;
        await SharedEnvLock.WaitAsync();
        try
        {
            if (_sharedEnvironment is null)
            {
                var options = new CoreWebView2EnvironmentOptions
                {
                    // --autoplay-policy=no-user-gesture-required 是唯一可用的开关（声音类插件依赖）。
                    AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required",
                };
                // 内部续延只做字段赋值/返回，无 UI 依赖：ConfigureAwait(false) 避免无谓的
                // 线程切换（弹窗/主窗并发首次初始化时的调度抖动）。
                // 参数顺序：CreateAsync(browserExecutableFolder, userDataFolder, options)——
                // 第一个传 null（用系统 WebView2 Runtime），第二个才是隔离 user-data 目录。
                _sharedEnvironment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options)
                    .ConfigureAwait(false);
            }
            return _sharedEnvironment;
        }
        finally { SharedEnvLock.Release(); }
    }

    /// <summary>复位主窗恢复状态（隐藏后清理，避免下次误重载）。</summary>
    public static void ClearRecoveryState()
    {
        RecoveryNeeded = false;
        HiddenSince = DateTime.MinValue;
    }
}
