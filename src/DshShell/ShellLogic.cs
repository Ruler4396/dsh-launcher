using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace DshWeb;

/// <summary>
/// 纯策略逻辑契约：所有不依赖 WinForms 句柄/系统环境的决策逻辑集中于此。
/// 
/// 【架构护栏 — 2024-Q4 局部精修】：
/// 当前按概念簇（WebViewPolicy, ServiceReadiness, RuntimeConfig 等）组织在嵌套类中。
/// 仅当某个嵌套类的代码行数超过 250 行，或需要引入外部依赖（如 HttpClient 实例、
/// Process 实例等有生命周期状态的资源）时，才允许将其物理拆分为独立的 .cs 文件。
/// 严禁按"单一函数"进行碎片化拆分（10 个 30 行的文件比 1 个 300 行的文件更难维护）。
/// </summary>
public static class ShellLogic
{
    /// <summary>弹窗目标分类。</summary>
    public enum PopupTarget
    {
        /// <summary>不拦截，保持 WebView2 默认行为（blob: / data: / about: 等）。</summary>
        Default,
        /// <summary>外部 http(s) 链接 → 系统默认浏览器。</summary>
        External,
        /// <summary>同源 http(s) 弹窗 → 壳内新建轻量窗口。</summary>
        Internal,
    }

    /// <summary>dsh 服务的停留模式（由 dsh-launcher-lifetime 插件写入 settings.json，壳执行）。</summary>
    public enum ServiceLifetime
    {
        /// <summary>常驻：服务一直运行，关窗/托盘退出都不停。</summary>
        AlwaysOn = 0,
        /// <summary>托盘驻留：关窗最小化到托盘，托盘"退出"才停服务并退出。</summary>
        Tray = 1,
        /// <summary>跟随窗口：关闭主窗口即停止服务并退出（最省内存）。</summary>
        FollowWindow = 2,
    }

    /// <summary>启动早期待应用更新的处理动作（矩阵 U2，v0.4.0 T2）。</summary>
    public enum PendingUpdateAction
    {
        /// <summary>无待应用更新。</summary>
        None = 0,
        /// <summary>服务未运行：直接应用（npm install -g 固定版本）。</summary>
        ApplyNow = 1,
        /// <summary>服务在跑且版本不一致：一次性询问[立即重启应用][稍后]。</summary>
        PromptRestart = 2,
        /// <summary>已应用但未清账的历史残留：直接清除 pending。</summary>
        ClearPending = 3,
    }

    /// <summary>端口占用状态（三重验证结果）：决定是否拉起/清理/快速失败。</summary>
    public enum ServicePortState
    {
        /// <summary>端口未开：服务未运行，需要拉起。</summary>
        Closed,
        /// <summary>端口已开且 HTTP 就绪：服务健康运行，跳过拉起。</summary>
        Healthy,
        /// <summary>端口已开但 HTTP 不通、占用进程确为 dsh（node）：僵尸服务，需清理后重启。</summary>
        Zombie,
        /// <summary>端口已开但占用进程不是 dsh（被其他程序占用）：端口冲突，快速失败（E2004）。</summary>
        Foreign,
    }

    /// <summary>读取日志文件尾部若干行（用于失败弹窗里直接展示原因）；大文件不整读（流式 + 受限队列）。
    /// 读取失败返回空列表。[INVARIANT] Must use FileShare.ReadWrite — cmd >> holds exclusive write. See ADR-010.</summary>
    internal static List<string> ReadLogTail(string logPath, int maxLines)
    {
        var result = new List<string>();
        try
        {
            if (!File.Exists(logPath)) return result;
            var kept = new Queue<string>(maxLines);
            foreach (var raw in ReadLinesShared(logPath))
            {
                kept.Enqueue(raw.TrimEnd());
                if (kept.Count > maxLines) kept.Dequeue();
            }
            while (kept.Count > 0)
                result.Add(kept.Dequeue());
        }
        catch
        {
            // 读取失败不阻断流程
        }
        return result;
    }

    /// <summary>以 FileShare.ReadWrite 共享模式逐行读取（可读被运行中服务锁定的日志文件）。
    /// 统一读取实现（P1-1）：DiagnoseExport 的 FilterByLevel/SummarizeErrors 同用此实现。</summary>
    internal static IEnumerable<string> ReadLinesShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) is not null) yield return line;
    }

    /// <summary>
    /// 恢复窗口位置（多显示器容灾，v0.3.0，纯函数）：
    /// - 目标矩形与任一屏幕工作区有 ≥120×60 可见交集 → 采用并在该工作区内整格钳制
    ///   （任务栏移动/工作区缩小后窗口仍完全可见）；
    /// - 完全越界（副屏拔掉等）→ 回退主屏工作区居中并钳制。
    /// workingAreas 的坐标系与 x/y 均为同一物理像素坐标（WinForms Screen.WorkingArea）。
    /// </summary>
    internal static (int X, int Y) RestoreWindowPosition(
        int x, int y, int width, int height,
        IReadOnlyList<Rectangle> workingAreas, Rectangle primaryWorkArea)
    {
        var widthSafe = Math.Max(width, 1);
        var heightSafe = Math.Max(height, 1);
        var rect = new Rectangle(x, y, widthSafe, heightSafe);
        foreach (var wa in workingAreas)
        {
            var inter = Rectangle.Intersect(rect, wa);
            if (inter.Width >= 120 && inter.Height >= 60)
            {
                var cx = Math.Clamp(x, wa.X, wa.X + Math.Max(0, wa.Width - widthSafe));
                var cy = Math.Clamp(y, wa.Y, wa.Y + Math.Max(0, wa.Height - heightSafe));
                return (cx, cy);
            }
        }
        var px = primaryWorkArea.X + (primaryWorkArea.Width - widthSafe) / 2;
        var py = primaryWorkArea.Y + (primaryWorkArea.Height - heightSafe) / 2;
        return (
            Math.Clamp(px, primaryWorkArea.X, primaryWorkArea.X + Math.Max(0, primaryWorkArea.Width - widthSafe)),
            Math.Clamp(py, primaryWorkArea.Y, primaryWorkArea.Y + Math.Max(0, primaryWorkArea.Height - heightSafe)));
    }

    /// <summary>WebView2 权限策略：自动放行、弹窗分类、安全打开判定。</summary>
    public static class WebViewPolicy
    {
        /// <summary>权限策略：自动放行的权限项（插件/DSH 依赖），其余保持默认拒绝。</summary>
        internal static bool IsAutoGrantedPermission(CoreWebView2PermissionKind kind) =>
            kind is CoreWebView2PermissionKind.Notifications
                or CoreWebView2PermissionKind.ClipboardRead
                or CoreWebView2PermissionKind.Autoplay
                or CoreWebView2PermissionKind.MultipleAutomaticDownloads
                or CoreWebView2PermissionKind.PersistentStorage;

        /// <summary>弹窗 URL 分类：外部链接 / 同源弹窗 / 保持默认。</summary>
        internal static PopupTarget ClassifyPopup(string? rawUri)
        {
            if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https"))
                return PopupTarget.Default;
            return uri.Host is ("127.0.0.1" or "localhost") ? PopupTarget.Internal : PopupTarget.External;
        }

        /// <summary>
        /// [F16] 插件致命错误消息判定（dsh 前端 → 壳 WebMessageReceived 契约，纯函数）。
        /// 只匹配**精确致命短语**："bootstrap facade is missing"/"plugin fatal"/"dsh-boot-failed"
        /// 与结构化标志 "pluginFatal"。旧行为对整条 WebMessageAsJson 做 contains "ModuleLoader"
        /// 大小写不敏感匹配——前端任何普通消息仅提及该词即误触发 E1008+安全模式询问（每会话
        /// 一次闸门），且 LastPluginCrashUtc 置位后本会话所有失败裁决都被路由向安全模式。
        /// </summary>
        internal static bool IsPluginCrashMessage(string? webMessageJson)
        {
            if (string.IsNullOrWhiteSpace(webMessageJson)) return false;
            return webMessageJson.Contains("bootstrap facade is missing", StringComparison.OrdinalIgnoreCase)
                || webMessageJson.Contains("plugin fatal", StringComparison.OrdinalIgnoreCase)
                || webMessageJson.Contains("dsh-boot-failed", StringComparison.OrdinalIgnoreCase)
                || webMessageJson.Contains("\"pluginFatal\"", StringComparison.Ordinal);
        }

        /// <summary>
        /// 下载完成后是否可以直接用默认程序打开：仅无害扩展名（图片/文本/pdf 等）自动打开，
        /// 其余（.html/.svg/.hta/.exe/.js 等可执行代码面）落盘后只提示不自动执行，
        /// 防止任意被加载页面触发下载后自动执行本地代码（S2 修复）。
        /// </summary>
        internal static bool IsSafeToOpen(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return false;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            return ext switch
            {
                // 图片
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".ico" or ".tif" or ".tiff"
                // 文本/数据
                or ".txt" or ".md" or ".csv" or ".json" or ".xml" or ".log" or ".ini" or ".yml" or ".yaml" or ".toml"
                // 文档（渲染器不执行脚本）
                or ".pdf"
                // 压缩包（打开=查看/解压，不自动执行内容）
                or ".zip" or ".7z" or ".rar" or ".gz" or ".tar" or ".xz"
                // 音视频
                or ".mp3" or ".wav" or ".flac" or ".ogg" or ".mp4" or ".mkv" or ".webm" or ".mov"
                // 字体/其他静态资源
                or ".woff" or ".woff2" or ".ttf" or ".otf"
                    => true,
                _ => false,
            };
        }
    }

    /// <summary>服务就绪判定：HTTP 探测、TCP 端口探测、启动错误日志识别。</summary>
    public static class ServiceReadiness
    {
        /// <summary>npx / 启动器日志中的明确错误标志（命中即认为服务启动失败，提前结束等待）。</summary>
        private static readonly string[] StartupErrorMarkers =
        {
            "npm ERR", "npm error",
            "EACCES", "ENOSPC", "ETIMEDOUT", "ECONNREFUSED", "ECONNRESET",
            "不是内部或外部命令", "'npx' 不是内部或外部命令",
            "Cannot find module", "MODULE_NOT_FOUND",
            "registry error", "Failed to install",
        };

        /// <summary>
        /// 服务就绪契约（C3）：端口有 HTTP 应答即视为就绪（任何响应码含 4xx/5xx）。
        /// TCP-only check causes white screen (frontend needs extra time after port listen).
        /// Network failure/timeout/refused → not ready. See ADR-005.
        /// </summary>
        internal static bool IsHttpReady(string url, System.Net.Http.HttpClient http)
        {
            try
            {
                using var resp = http.GetAsync(url).GetAwaiter().GetResult();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>端口可连（TCP）契约（C3 前半段）：connect 成功即 true；失败/超时 false。
        /// [INVARIANT] 300ms hard timeout on ConnectAsync: loopback connect is millisecond-level;
        /// system-level ~2s delay on some environments must not block startup. See ADR-006.</summary>
        internal static bool PortOpen(string host, int port)
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                var isLoopback = host is null or "127.0.0.1" or "localhost" or "::1";
                var task = isLoopback
                    ? c.ConnectAsync(System.Net.IPAddress.Loopback, port)
                    : c.ConnectAsync(host, port);
                // [INVARIANT] 300ms hard timeout: loopback connect is ms-level. See ADR-006.
                return task.Wait(300) && c.Connected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>异步端口探测（v0.4.2）：ConnectAsync 不阻塞调用线程；3s 超时兜底。
        /// 与 <see cref="PortOpen"/> 语义一致（契约 C3），仅异步化——ServiceManager 轮询使用。</summary>
        internal static async Task<bool> PortOpenAsync(string host, int port, CancellationToken ct = default)
        {
            try
            {
                using var c = new System.Net.Sockets.TcpClient();
                // [INVARIANT] 300ms hard timeout (consistent with sync PortOpen). See ADR-006.
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(300));
                var isLoopback = host is null or "127.0.0.1" or "localhost" or "::1";
                if (isLoopback)
                    await c.ConnectAsync(System.Net.IPAddress.Loopback, port, timeoutCts.Token).ConfigureAwait(false);
                else
                    await c.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false; // 探测失败 = 端口未开（预期操作失败，非异常）
            }
        }

        /// <summary>
        /// 检查启动日志内容是否包含明确的启动失败标志。
        /// 用于在轮询等待期间提前发现 npx 下载/启动失败，而不是干等超时。
        /// </summary>
        internal static bool LogShowsStartupError(string? logContent)
        {
            if (string.IsNullOrWhiteSpace(logContent)) return false;
            foreach (var marker in StartupErrorMarkers)
            {
                if (logContent.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>默认就绪轮询预算（秒）：本地直启 dsh 服务（SelfContained/全局安装）的等待上限。</summary>
        internal const int DefaultPollBudgetSeconds = 180;

        /// <summary>网络下载兜底（npx）路径的就绪轮询预算（秒）：首次运行需联网下载 dsh 包，
        /// 耗时远超本地直启，放宽预算让"慢但最终能成功的首次下载+启动"跑完，而不是被误判超时。</summary>
        internal const int NetworkFallbackPollBudgetSeconds = 360;

        /// <summary>
        /// 服务就绪轮询预算（秒）。
        /// <paramref name="networkDownloadFallback"/> = true 表示服务通过 npx/网络下载启动
        /// （本地未安装 dsh）：首次需联网拉包，放宽等待上限（180s → 360s）；否则保持 180s。
        /// </summary>
        internal static int GetPollBudgetSeconds(bool networkDownloadFallback)
            => networkDownloadFallback ? NetworkFallbackPollBudgetSeconds : DefaultPollBudgetSeconds;
    }

    /// <summary>
    /// 启动健康探针的纯决策逻辑（ADR-023，BootSignature 单点配置）。
    ///
    /// 崩溃检测采用"多源主动拉取融合"：壳坐在进程追踪者/页面宿主/日志读者/HTTP 探测者
    /// 四个观察位，不依赖 dsh 主动上报。本类只承载**可纯函数化**的部分：
    /// - 签名档（BootProfile）：good_symbol 探针表达式、bad_signatures、grace_ms、
    ///   probe_interval_ms、absent_threshold 的唯一定义点（DSH_BOOT_SIGNATURES JSON 可整体覆盖）；
    /// - 页面探针结果求值（EvaluatePageProbe）：good / 坏签名命中 / 已渲染(Rendered) / 缺席 / 无效 五分类；
    /// - 日志层签名表（MatchBootErrorSignature）：插件/boot 错误标志。
    /// 有状态的融合状态机见 Lifecycle/BootHealthMonitor.cs（依赖注入探针，Headless 可测）。
    /// </summary>
    public static class BootGuard
    {
        /// <summary>启动健康签名档。全部字段有安全默认值；DSH_BOOT_SIGNATURES 可覆盖。</summary>
        public sealed record BootProfile
        {
            /// <summary>
            /// 好符号 JS 表达式：求值为真 ⇒ 页面 boot 成功。
            /// 析取式覆盖两代 dsh 引导链（2026-08 E2008 无插件误报回归修复）：
            /// - 旧版（≤0.1.0-rc.7）：页面注入 window.__DSH_BOOT__ = { version }；
            /// - 新版（≥0.1.1-rc.2）：内联脚本注入 window.__ModuleLoader__ 队列门面，
            ///   dsh-client-modules boot 完成时将其 mode 置 "live"（client.js: target.mode="live"）。
            /// 任一支命中即 Healthy；两支都缺席才计入 AbsentThreshold。
            /// </summary>
            public string GoodSymbol { get; init; } =
                "(window.__DSH_BOOT__&&window.__DSH_BOOT__.version)"
                + "||(window.__ModuleLoader__&&window.__ModuleLoader__.mode===\"live\")";

            /// <summary>坏签名列表：页面 DOM 文本/错误原文命中任一 ⇒ failed（一次即判）。</summary>
            public IReadOnlyList<string> BadSignatures { get; init; } = new[]
            {
                "bootstrap facade is missing",
                "plugin fatal",
                "dsh-boot-failed",
            };

            /// <summary>NavigationCompleted 后的静默宽限（毫秒）：慢启动不误报的第一道闸。</summary>
            public int GraceMs { get; init; } = 12000;

            /// <summary>页面探针间隔（毫秒）。</summary>
            public int ProbeIntervalMs { get; init; } = 2000;

            /// <summary>连续缺席阈值：grace 后连续 N 次好符号缺席 ⇒ failed。</summary>
            public int AbsentThreshold { get; init; } = 5;

            /// <summary>
            /// 页面"已渲染"豁免阈值：body.innerText 长度 ≥ 该值且无坏签名 ⇒ 判 Rendered（视同健康）。
            /// [E2008 误报根治] 未配置 API key 时 dsh 会渲染出自己的欢迎/配置界面，但 boot 链
            /// （__ModuleLoader__.mode==="live"）并不完成——此前被误判"好符号持续缺席"→ E2008 弹窗。
            /// 代理特征为 innerText 长度（探针已采集，无需改协议）；空白/纯加载页远低于该值，
            /// 仍走缺席计票，慢启动/白屏保护不削弱。坏签名优先级在其上层，真崩溃错误 UI 不被豁免。
            /// </summary>
            public int RenderedMinTextChars { get; init; } = 60;

            /// <summary>日志层附加签名（在内置表之上追加；沙盒注入假签名用）。</summary>
            public IReadOnlyList<string> ExtraLogSignatures { get; init; } = Array.Empty<string>();

            /// <summary>
            /// 插件致命面板签名（2026-08-29 实机回归新增）：dsh 客户端引导遇到加载失败的
            /// 插件模块时，渲染 "Failed to load plugins / failed to import loader entry …"
            /// 错误面板**替代整个应用 UI**——但失败面板页面仍带着 ModuleLoader 门面，
            /// 好符号照常命中，若只走 DOM 坏签名（优先级在 good 之后）则永远判不死。
            /// 本表在好符号判定**之前**匹配 body 文本，命中即 E2008 一票判死 + dom[ 证据
            /// （→ 插件归因 → 安全模式询问）。默认签名取面板稳定技术文案；DSH_BOOT_SIGNATURES
            /// 的 fatal_panel_signatures 可整体覆盖，跟进 dsh 未来文案变化。
            /// </summary>
            public IReadOnlyList<string> FatalPanelSignatures { get; init; } = new[]
            {
                "failed to import loader entry",
            };

            /// <summary>由 good_symbol 组装的单点页面探针脚本：返回 JSON {good,text,err}。
            /// err 取 window.__dshLastError（WebViewManager 在文档创建时注入的错误收集器），
            /// 用于"捕获异常原文"。脚本自身 try/catch——探针永不因页面异常而抛错。</summary>
            public string BuildProbeScript()
                => "(function(){try{var t=(document.body&&document.body.innerText)?document.body.innerText.slice(0,2000):'';"
                 + "var e=(window.__dshLastError&&window.__dshLastError.message)||'';"
                 + $"return JSON.stringify({{good:!!({GoodSymbol}),text:t,err:e}});}}catch(x)"
                 + "{return JSON.stringify({good:false,text:'',err:'probe-error:'+x.message});}})()";
        }

        /// <summary>默认签名档（单例：无环境覆盖时的唯一真相）。</summary>
        internal static readonly BootProfile DefaultProfile = new();

        /// <summary>
        /// 解析生效签名档：DSH_BOOT_SIGNATURES（JSON）非空时逐字段覆盖默认值；
        /// 整体解析失败或字段类型非法 → 该字段保持默认（绝不因配置错误而失去监控）。
        /// </summary>
        internal static BootProfile ResolveProfile(string? envJson)
        {
            if (string.IsNullOrWhiteSpace(envJson)) return DefaultProfile;
            var p = DefaultProfile;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(envJson);
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return p;
                if (TryGetString(root, "good_symbol", out var gs)) p = p with { GoodSymbol = gs };
                if (TryGetStringArray(root, "bad_signatures", out var bad)) p = p with { BadSignatures = bad };
                if (TryGetStringArray(root, "fatal_panel_signatures", out var fatal)) p = p with { FatalPanelSignatures = fatal };
                if (TryGetInt(root, "grace_ms", out var grace)) p = p with { GraceMs = grace };
                if (TryGetInt(root, "probe_interval_ms", out var interval)) p = p with { ProbeIntervalMs = interval };
                if (TryGetInt(root, "absent_threshold", out var threshold)) p = p with { AbsentThreshold = threshold };
                if (TryGetInt(root, "rendered_min_text_chars", out var rendered)) p = p with { RenderedMinTextChars = rendered };
                if (TryGetStringArray(root, "log_error_signatures", out var logs)) p = p with { ExtraLogSignatures = logs };
                return p;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                return p; // 配置损坏 → 全默认（预期内操作失败，调用方记 Warn）
            }
        }

        private static bool TryGetString(System.Text.Json.JsonElement root, string name, out string value)
        {
            value = "";
            if (!root.TryGetProperty(name, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.String) return false;
            var s = v.GetString();
            if (string.IsNullOrWhiteSpace(s)) return false;
            value = s;
            return true;
        }

        private static bool TryGetInt(System.Text.Json.JsonElement root, string name, out int value)
        {
            value = 0;
            // ValueKind 守卫必须先行：TryGetInt32 对非 Number 元素抛 InvalidOperationException
            if (!root.TryGetProperty(name, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.Number) return false;
            return v.TryGetInt32(out value);
        }

        private static bool TryGetStringArray(System.Text.Json.JsonElement root, string name, out IReadOnlyList<string> value)
        {
            value = Array.Empty<string>();
            if (!root.TryGetProperty(name, out var v) || v.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
            var list = new List<string>();
            foreach (var item in v.EnumerateArray())
                if (item.ValueKind == System.Text.Json.JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                    list.Add(s);
            value = list;
            return true;
        }

        /// <summary>页面探针结果的五分类。</summary>
        public enum PageProbeKind
        {
            /// <summary>好符号出现 → healthy（停止探针）。</summary>
            GoodSymbol,
            /// <summary>页面已渲染出实质内容（无坏签名）→ 视同健康（dsh 自带流程/配置等待界面）。</summary>
            Rendered,
            /// <summary>坏签名命中 → failed（一次即判，附原文）。</summary>
            BadSignature,
            /// <summary>有效结果但好符号缺席 → 计入 absent_threshold。</summary>
            Absent,
            /// <summary>无效结果（null/解析失败）→ 只 Warn，绝不参与判定（误报防护）。</summary>
            Invalid,
        }

        /// <summary>页面探针求值结果（kind + 命中详情/原文摘录）。</summary>
        public sealed record PageProbeResult(PageProbeKind Kind, string? Detail)
        {
            public static readonly PageProbeResult Invalid = new(PageProbeKind.Invalid, null);
        }

        /// <summary>
        /// 求值 ExecuteScriptAsync 返回的 JSON（{good:bool,text:string,err:string}）：
        /// **坏签名优先于好符号**——dsh 的 boot 标志在启动早期设置、插件在其后才加载，
        /// 致命插件错误可以发生在 __DSH_BOOT__ 已存在的页面上；若好符号一票遮蔽，
        /// 崩溃会被静默掩盖（S22 实测教训）。顺序：err 原文命中坏签名 → BadSignature
        /// （detail=原文摘录，一票）→ good=true → GoodSymbol → text 命中坏签名 → Absent
        /// （dom-suspect 计票，防 E2008 误判）→ 页面已渲染（text ≥ RenderedMinTextChars）→
        /// Rendered（视同健康，dsh 自带流程/配置等待界面不判死）→ 否则 Absent；
        /// null/空/解析失败 → Invalid（探针异常路径，不判死）。
        /// </summary>
        internal static PageProbeResult EvaluatePageProbe(string? scriptJson, BootProfile profile)
        {
            if (string.IsNullOrWhiteSpace(scriptJson) || scriptJson == "undefined")
                return PageProbeResult.Invalid;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(scriptJson);
                var root = doc.RootElement;
                System.Text.Json.JsonDocument? innerDoc = null;
                try
                {
                    if (root.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        // [INVARIANT] ExecuteScriptAsync 双重编码：探针脚本 return JSON.stringify(...)
                        // 时，返回值本身是字符串，SDK 再做一次 JSON 编码 → 结果是字符串字面量，
                        // 必须解一层才能拿到 {good,text,err}（S22 实测教训）。
                        innerDoc = System.Text.Json.JsonDocument.Parse(root.GetString() ?? "");
                        root = innerDoc.RootElement;
                    }
                    if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                        return PageProbeResult.Invalid;
                    var good = root.TryGetProperty("good", out var g) && g.ValueKind == System.Text.Json.JsonValueKind.True;
                    var text = root.TryGetProperty("text", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String
                        ? t.GetString() ?? "" : "";
                    var err = root.TryGetProperty("err", out var e) && e.ValueKind == System.Text.Json.JsonValueKind.String
                        ? e.GetString() ?? "" : "";

                    // [2026-08-29 插件致命面板] 最先匹配：失败面板页面仍带着 ModuleLoader 门面
                    // （good 照常为真），若排在 good 之后永远轮不到——面板本身即"应用被插件
                    // 打断"的确定性证据，一票判死 + dom[ 证据（→ 插件归因 → 安全模式）。
                    if (MatchBadSignature(text, profile.FatalPanelSignatures) is { } hitPanel)
                        return new PageProbeResult(PageProbeKind.BadSignature,
                            "dom[" + hitPanel + "]=" + Truncate(text, 300));
                    // 坏签名匹配顺序：错误原文优先（更精确）。证据携带**异常原文**（截断），
                    // 不只带命中的签名——"捕获原文"是 S22 验收的硬要求。
                    if (err.Length > 0 && MatchBadSignature(err, profile.BadSignatures) is { } hitErr)
                        return new PageProbeResult(PageProbeKind.BadSignature,
                            "err[" + hitErr + "]=" + Truncate(err, 300));
                    // 2026-08 修复点3：实质内容已渲染（good 符号）→ 健康，豁免 DOM 文本里的隐藏
                    // 坏签名字面量（如 "bootstrap facade is missing"），防真实 UI 已正常却被误判死。
                    if (good) return new PageProbeResult(PageProbeKind.GoodSymbol, null);
                    // 2026-08 回归修复：DOM 文本坏签名**降级**为 Absent（仅当未确认渲染）。不再一票判死：
                    // 交由 BootHealthMonitor 按 AbsentThreshold 连续多轮确认后才判死，证据不丢
                    // （detail 携带 dom-suspect[签名]=原文摘录），抗单次误报。err 原文坏签名仍走上方一票路径。
                    if (MatchBadSignature(text, profile.BadSignatures) is { } hitText)
                        return new PageProbeResult(PageProbeKind.Absent,
                            "dom-suspect[" + hitText + "]=" + Truncate(text, 300));
                    // [E2008 误报根治] 渲染豁免：页面已渲染出实质内容（无坏签名）→ Rendered，视同健康。
                    // 典型场景：未配置 API key 时 dsh 渲染自己的欢迎/配置界面，boot 链不完成
                    // （__ModuleLoader__.mode 不为 "live"），此前被误判"好符号持续缺席"→ E2008 弹窗。
                    // 坏签名优先级在上方（err 一票 / DOM 计票），真崩溃错误 UI 不会被此豁免；
                    // 空白/纯加载页（innerText < RenderedMinTextChars）仍走缺席计票，慢启动保护不削弱。
                    if (text.Length >= profile.RenderedMinTextChars)
                        return new PageProbeResult(PageProbeKind.Rendered, "rendered=" + Truncate(text, 200));
                    return new PageProbeResult(PageProbeKind.Absent, err.Length > 0 ? "err=" + Truncate(err, 200) : null);
                }
                finally { innerDoc?.Dispose(); }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException)
            {
                return PageProbeResult.Invalid;
            }
        }

        /// <summary>文本是否命中任一坏签名；命中返回该签名（OrdinalIgnoreCase 包含匹配）。</summary>
        internal static string? MatchBadSignature(string text, IReadOnlyList<string> signatures)
        {
            foreach (var s in signatures)
                if (!string.IsNullOrWhiteSpace(s) && text.Contains(s, StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }

        /// <summary>截断过长证据（防 safe-mode-state.json 被整页 DOM 文本撑爆）。</summary>
        internal static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…(truncated)";

        /// <summary>日志层内置签名表（就绪后监控用；与启动期 StartupErrorMarkers 分表，
        /// 避免把启动期的良性告警带进运行期判定）。**专属签名在前**：一行同时命中多条时
        /// 返回更精确的插件/boot 签名作为证据。命中返回签名，否则 null。</summary>
        private static readonly string[] BootErrorMarkers =
        {
            "plugin load failed", "plugin fatal",
            "bootstrap facade is missing",
            "ERR_MODULE_NOT_FOUND", "MODULE_NOT_FOUND",
            "Cannot find module",
            "npm ERR", "npm error",
            "EACCES",
            "FATAL ERROR",
        };

        /// <summary>
        /// 壳自写日志条目识别：统一日志是壳 JSON Lines 与服务原始输出混排，壳自己的事件行
        /// （如 E1008 插件崩溃捕获——该事件已由页面/消息通道原生处理）若被日志层再次判定，
        /// 会造成跨层重复触发（实测 S22 教训）。契约：壳的 Warn/Error 条目必带 "code":"E####"
        /// 字段；服务原始输出不会恰好长成这样。返回 true = 壳自写行，日志层跳过。
        /// </summary>
        internal static bool IsShellAuthoredLogEntry(string line)
        {
            if (!line.StartsWith('{')) return false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(line);
                return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("code", out var c)
                    && c.ValueKind == System.Text.Json.JsonValueKind.String
                    && System.Text.RegularExpressions.Regex.IsMatch(
                        c.GetString() ?? "", "^E\\d{4}$");
            }
            catch (System.Text.Json.JsonException)
            {
                return false;
            }
        }

        /// <summary>
        /// 日志行是否命中 boot 错误签名（内置表 + DSH_BOOT_SIGNATURES.log_error_signatures 追加项）。
        /// 命中返回首个命中的签名（作为证据），否则 null。
        /// </summary>
        internal static string? MatchBootErrorSignature(string line, BootProfile profile)
        {
            if (string.IsNullOrWhiteSpace(line)) return null;
            foreach (var marker in BootErrorMarkers)
                if (line.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return marker;
            return MatchBadSignature(line, profile.ExtraLogSignatures);
        }

        /// <summary>插件归因子集（2026-08-25 事故回归）：BootErrorMarkers 中指向"模块/插件加载失败"
        /// 的标记。命中这些的服务端崩溃应归因为插件嫌疑（路由安全模式），而非通用环境错误。</summary>
        internal static readonly string[] PluginInvolvedMarkers =
        {
            "plugin load failed", "plugin fatal",
            "ERR_MODULE_NOT_FOUND", "MODULE_NOT_FOUND",
            "Cannot find module",
        };

        /// <summary>
        /// [F6] 行是否为壳管道转发的服务原始输出（PipeServiceOutputToUnifiedLog 统一加的
        /// "[HH:mm:ss.fff] [dsh] " 前缀，ADR-024 后服务输出必经该管道）。日志层签名匹配
        /// 只认服务行——统一日志混排的壳 JSON 行、诊断文案（内嵌 npm ERR 等）一律不参与
        /// 判死。与 IsShellAuthoredLogEntry 互为纵深（JSON 行理论上可内嵌该字面量）。
        /// </summary>
        internal static bool IsServicePipedLogLine(string line)
            => !string.IsNullOrEmpty(line) && line.Contains("] [dsh] ", StringComparison.Ordinal);

        /// <summary>
        /// 日志层证据是否携带插件归因签名（纯函数，契约测试锁定）。
        /// 2026-08-25 事故：插件把 node 服务进程搞崩（exit=1），页面层从未渲染、无任何
        /// 前端证据；唯一能证明"是插件"的文本证据是服务 stderr 里的加载失败堆栈——但
        /// 分类闸门只认页面层坏签名，导致安全模式从未被询问。此函数补上日志层的归因通道。
        /// 入参为证据的 Summary 与 Detail 拼接文本（不区分来源字段，包含匹配即可靠）。
        /// </summary>
        internal static bool LogEvidenceIndicatesPlugin(string? summaryAndDetail)
        {
            if (string.IsNullOrWhiteSpace(summaryAndDetail)) return false;
            foreach (var marker in PluginInvolvedMarkers)
                if (summaryAndDetail.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// 更新构建进度纯逻辑（2026-08 用户回归修复：进度长期钉 50% + 文案闪烁）。
    /// 契约（ContractTests.UpdateProgress*）：
    /// - pnpm ndjson 按 packageId 自归一化，不依赖硬编码包总数；
    /// - 百分比单调不回退、封顶 90、下限 10；
    /// - 解析不出任何 packageId 时 HasData=false（调用方回退脉冲模式，绝不显示伪百分比）。
    /// </summary>
    public static class UpdateProgress
    {
        /// <summary>
        /// pnpm 安装退出分类：ERR_PNPM_IGNORED_BUILDS（exit=1）表示包已装好、仅 build
        /// scripts 被安全策略阻止，视为成功。标记可能出现在 stdout 的 ndjson error 事件
        /// （pnpm v11 默认）或 stderr 文本里——两个流都必须参与判定。
        /// [2026-08-23 冷启动演练回归] 旧实现只查 stderr，pnpm v11 把标记发在 stdout，
        /// 新机器首次 provision 必误判为失败（pnpm 三连败后白白落 npm 兜底）。
        /// </summary>
        public static bool IsPnpmIgnoredBuildsExit(int exitCode, string stdoutTail, string stderrTail)
            => exitCode != 0
               && ((stdoutTail ?? "").Contains("ERR_PNPM_IGNORED_BUILDS", StringComparison.Ordinal)
                   || (stderrTail ?? "").Contains("ERR_PNPM_IGNORED_BUILDS", StringComparison.Ordinal));

        /// <summary>
        /// pnpm --reporter=ndjson 安装事件聚合器。旧实现把所有 pnpm:progress 事件
        /// （resolving/fetching/extracting…）混计为一个计数再除以硬编码 600，很快封顶
        /// → 进度条卡在 50% 直到结尾。新实现按 packageId 维护「已见/已完成」两集合：
        /// percent = 10 + 80 × done/max(1, seen)，随真实完成占比平滑推进。
        /// </summary>
        public sealed class PnpmAggregator
        {
            private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
            private readonly HashSet<string> _done = new(StringComparer.Ordinal);
            private int _lastPercent;

            /// <summary>消费一行 ndjson 输出。非 JSON / 无 packageId 的行安全忽略。</summary>
            public void OnLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return;
                    var pkgId = root.TryGetProperty("packageId", out var p)
                        && p.ValueKind == System.Text.Json.JsonValueKind.String
                        ? p.GetString() : null;
                    if (string.IsNullOrEmpty(pkgId)) return;
                    _seen.Add(pkgId);
                    // 完成判定：pnpm:link 事件，或 progress 事件的 status 推进到 link 阶段
                    var name = root.TryGetProperty("name", out var n)
                        && n.ValueKind == System.Text.Json.JsonValueKind.String ? n.GetString() : null;
                    var status = root.TryGetProperty("status", out var s)
                        && s.ValueKind == System.Text.Json.JsonValueKind.String ? s.GetString() : null;
                    if (name == "pnpm:link"
                        || (status is not null && status.Contains("link", StringComparison.OrdinalIgnoreCase)))
                        _done.Add(pkgId);
                }
                catch (System.Text.Json.JsonException) { /* 非 JSON 行：忽略 */ }
            }

            /// <summary>当前快照：(百分比 0/10-90, 是否有真实数据)。单调不回退。</summary>
            public (int Percent, bool HasData) Snapshot()
            {
                if (_seen.Count == 0) return (0, false);
                var pct = 10 + (int)Math.Round(80.0 * _done.Count / Math.Max(1, _seen.Count));
                pct = Math.Min(90, Math.Max(10, pct));
                if (pct > _lastPercent) _lastPercent = pct; // 分母增长可能令比值短暂回落：钳制单调
                return (_lastPercent, true);
            }
        }

        /// <summary>
        /// 构建终态标题栏文案（纯函数，ContractTests.UpdateProgressContractTests.TerminalText_* 锁定）。
        /// [2026-08 用户回归] 此前 Ready/Failed 终态从未被标题栏渲染且部分失败路径静默返回，
        /// 用户只看到进度条消失、无成功/失败结论。契约：文案自含结论 + 版本号；
        /// 失败分支必须带 [E4001] 错误码（用户可见错误铁律）。
        /// <paramref name="willRetry"/>=false 表示 tarball 未保留（下载阶段即失败），
        /// 文案不得承诺"下次启动自动重试"。
        /// </summary>
        public static string ComposeTerminalTitleText(bool success, string version, bool willRetry = true)
            => success
                ? $"已构建更新 100%（v{version}）· 重启启动器后自动切换"
                : willRetry
                    ? $"更新构建失败 [E4001]（v{version}）· 已保留下载，下次启动自动重试"
                    : $"更新下载失败 [E4001]（v{version}）· 可重新点击更新重试";
    }

    /// <summary>
    /// 更新应用的目标目录决策（纯函数，ContractTests.StagedApplyPolicyContractTests 锁定）。
    /// [2026-08-22 用户回归] 目标 runtimes\&lt;ver&gt; 已存在时 Directory.Move 抛
    /// "Cannot create ... because a file or directory with the same name already exists"
    /// → 弹"原子切换失败"。策略：
    ///   目标不存在 → ProceedFresh；
    ///   存在且 bin 可解析且版本一致 → AlreadyApplied（幂等短路，重复应用同版本静默成功）;
    ///   存在但无效（半成品/损坏/异版本）→ ReplaceStale（调用方备份挪走后换新）。
    /// </summary>
    public static class StagedApplyPolicy
    {
        public enum ExistingTargetAction { AlreadyApplied, ReplaceStale, ProceedFresh }

        public static ExistingTargetAction DecideExistingTarget(
            bool targetExists, bool binResolvable, bool versionMatches)
        {
            if (!targetExists) return ExistingTargetAction.ProceedFresh;
            return binResolvable && versionMatches
                ? ExistingTargetAction.AlreadyApplied
                : ExistingTargetAction.ReplaceStale;
        }
    }

    /// <summary>
    /// 更新数据守卫策略（纯函数，ContractTests.UpdateGuardPolicyContractTests 锁定）。
    /// [2026-08-23 用户回归] dsh 新版本首次启动会把 $HOME\.dsh 共享数据文件
    /// （实测 .credentials.yaml）单向迁移为新格式（version+refs 布局）；一旦新版起不来
    /// 而回退旧版，旧解析器读不懂新格式 → 插件树加载失败 → 服务 exit(1)，
    /// "更新失败=隔天必炸"。策略三件套：apply 前"版本首拍"快照；启动自检失败 →
    /// 回滚快照 + 隔离新运行时；好符号确认健康后解除武装。
    /// 本类只做决策与命名，全部 IO 在 UpdateDataGuard。
    /// </summary>
    public static class UpdateGuardPolicy
    {
        public enum BootFailureAction { RollbackAndRestart, ExistingRecoveryFlow }

        /// <summary>启动自检失败分支：存在已武装（已应用、未确认健康）的更新版本 → 自动回滚；
        /// 否则走既有恢复流程（安全模式询问 / 重启询问），行为与历史完全一致。</summary>
        public static BootFailureAction DecideBootFailure(string? appliedUnconfirmedVersion)
            => string.IsNullOrWhiteSpace(appliedUnconfirmedVersion)
                ? BootFailureAction.ExistingRecoveryFlow
                : BootFailureAction.RollbackAndRestart;

        /// <summary>版本号 → 文件名安全 token：非法字符替换 '_'，清理收尾点/空格，空值 → "unknown"。</summary>
        public static string SanitizeVersionToken(string? version)
        {
            if (string.IsNullOrWhiteSpace(version)) return "unknown";
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new System.Text.StringBuilder(version.Length);
            foreach (var c in version)
                sb.Append(invalid.Contains(c) ? '_' : c);
            var result = sb.ToString().Trim().TrimEnd('.', ' ');
            return result.Length == 0 ? "unknown" : result;
        }

        /// <summary>
        /// 快照目录名：pre-&lt;token&gt;-&lt;yyyyMMdd-HHmmss&gt;（UTC）。
        /// 定宽时间戳保证目录名字典序 == 时间序——挑选与修剪都依赖该不变量（契约测试锁定）。
        /// </summary>
        public static string SnapshotDirName(string version, DateTime utc)
            => $"pre-{SanitizeVersionToken(version)}-{utc:yyyyMMdd-HHmmss}";

        private static string SnapshotPrefix(string version) => "pre-" + SanitizeVersionToken(version) + "-";

        /// <summary>从候选目录名中选出该版本可回滚的最近快照；无匹配返回 null。</summary>
        public static string? PickRollbackSnapshot(IEnumerable<string> dirNames, string version)
        {
            var prefix = SnapshotPrefix(version);
            string? best = null;
            foreach (var name in dirNames)
            {
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                if (best is null || string.Compare(name, best, StringComparison.OrdinalIgnoreCase) > 0)
                    best = name;
            }
            return best;
        }

        /// <summary>快照修剪：按名称升序保留最近 <paramref name="keep"/> 个，返回应删除的较旧者（升序）。</summary>
        public static IReadOnlyList<string> PruneSnapshotDirs(IEnumerable<string> dirNames, int keep)
        {
            var sorted = dirNames.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
            return sorted.Length <= keep
                ? Array.Empty<string>()
                : sorted.Take(sorted.Length - keep).ToArray();
        }
    }

    /// <summary>
    /// 语义化版本比较（全系统唯一实现，契约测试锁定）。
    /// 规则（SemVer 2.0.0 宽松化）：v/V 前缀剥离；build metadata（+ 后缀）不参与比较（F10）；
    /// MAJOR.MINOR.PATCH 数值比较（缺失段补 0，第四段宽松忽略）；核心非法 → 0.0.0（fail-open，
    /// 不产生"有新版本"误报、不阻断启动）；prerelease：无 prerelease &gt; 有 prerelease，
    /// 分段比较——纯数字段按数值、字母数字段按字典序、数字段 &lt; 字母段、段多者大。
    /// 消费方：UpdateChecker（更新检测）与 DshDiscovery（SelfContained 运行时挑选）必须同源——
    /// 严禁再出现"两套比较器"（F1 回归根因：发现层用序数比较致 rc.10 &lt; rc.9 判反，
    /// "更新成功但永远启动旧版"）。
    /// </summary>
    public static class VersionPolicy
    {
        public static int CompareVersions(string? a, string? b)
        {
            var va = Parse(a);
            var vb = Parse(b);
            for (var i = 0; i < 3; i++)
            {
                var c = va.Num[i].CompareTo(vb.Num[i]);
                if (c != 0) return c;
            }
            // 核心相等 → 比较 prerelease：无 prerelease > 有 prerelease
            if (va.Pre.Length == 0 && vb.Pre.Length == 0) return 0;
            if (va.Pre.Length == 0) return 1;
            if (vb.Pre.Length == 0) return -1;
            var n = Math.Min(va.Pre.Length, vb.Pre.Length);
            for (var i = 0; i < n; i++)
            {
                var c = ComparePrePart(va.Pre[i], vb.Pre[i]);
                if (c != 0) return c;
            }
            return va.Pre.Length.CompareTo(vb.Pre.Length); // 段多者更大（1.0.0-rc.1 < 1.0.0-rc.1.1）
        }

        private readonly record struct SemVer(int[] Num, string[] Pre);

        private static SemVer Parse(string? raw)
        {
            var s = (raw ?? "").Trim().TrimStart('v', 'V');
            var plus = s.IndexOf('+');
            if (plus >= 0) s = s[..plus]; // build metadata 不参与比较（F10）
            var dash = s.IndexOf('-');
            var core = dash >= 0 ? s[..dash] : s;
            var pre = dash >= 0 ? s[(dash + 1)..] : "";

            var parts = core.Split('.');
            var nums = new int[3];
            var valid = false;
            for (var i = 0; i < Math.Min(parts.Length, 3); i++)
            {
                if (int.TryParse(parts[i], out var n)) { nums[i] = n; valid = true; }
            }
            if (!valid) return new SemVer(new[] { 0, 0, 0 }, Array.Empty<string>());
            if (parts.Length == 1 && parts[0].Length == 0) return new SemVer(new[] { 0, 0, 0 }, Array.Empty<string>());
            var preParts = pre.Length == 0
                ? Array.Empty<string>()
                : pre.Split('.').Where(p => p.Length > 0).ToArray();
            return new SemVer(nums, preParts);
        }

        private static int ComparePrePart(string a, string b)
        {
            var aNum = int.TryParse(a, out var ai);
            var bNum = int.TryParse(b, out var bi);
            if (aNum && bNum) return ai.CompareTo(bi);          // 纯数字段 → 数值比较
            if (aNum) return -1;                                 // 数字段 < 字母数字段（SemVer 规则）
            if (bNum) return 1;
            return string.CompareOrdinal(a, b);                  // 字母数字段 → 字典序
        }
    }

    /// <summary>
    /// npm registry 兜底策略（纯函数，ContractTests.NpmRegistryPolicyContractTests 锁定）。
    /// [2026-08 用户回归] npmjs 直连不稳且慢（HEAD 2055ms、undici 连接反复被重置），
    /// npmmirror 快且稳（264ms）。策略：优先走最快的源，失败才降级——
    ///   ① DSH_NPM_MIRROR 环境变量（显式指定最高优先）；
    ///   ② npmmirror（默认首选；公共 scope 会被同步，新版本可能有分钟级延迟）；
    ///   ③ npm 官方源（空串 = 默认 registry，垫底兜底：镜像未同步的新版本从这里拿）。
    /// 序列去重（同一源最多出现一次），调用方按序尝试、整条流粘住首个成功源。
    /// </summary>
    public static class NpmRegistryPolicy
    {
        /// <summary>兜底/默认首选镜像：阿里 npmmirror。</summary>
        public const string FallbackMirror = "https://registry.npmmirror.com";

        /// <summary>
        /// [2026-08-29 安全通知可达性] 从用户 .npmrc 文本解析 registry=（首个非注释命中）。
        /// 版本检查 HTTP 端此前硬编码 registry.npmjs.org，大陆网络直连不可达 → 更新检查永远
        /// 静默失败；现在跟随用户真实 npm 配置。非 http(s) 值（file: 等）忽略。
        /// </summary>
        public static string? ParseNpmrcRegistry(string? npmrcText)
        {
            foreach (var rawLine in (npmrcText ?? "").Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                // npmrc 允许 "registry = url"（等号两侧可有空白）：取首个 = 拆分
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line[..eq].Trim();
                if (!string.Equals(key, "registry", StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[(eq + 1)..].Trim();
                if (value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    return value.TrimEnd('/');
            }
            return null;
        }

        /// <summary>按优先级返回 --registry 参数序列（含末尾的官方源空参）。永不重复
        /// （URL 按忽略大小写、忽略尾斜杠归一后判重）。</summary>
        public static string[] RegistrySources(string? dshNpmMirror)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = new List<string>();
            void Add(string arg)
            {
                // 归一化判重：去前导空格与尾斜杠，URL 大小写不敏感（契约：EnvIsNpmmirror 变体）
                var norm = arg.Trim().TrimEnd('/').ToLowerInvariant();
                if (seen.Add(norm)) list.Add(arg);
            }
            var mirror = string.IsNullOrWhiteSpace(dshNpmMirror) ? null : dshNpmMirror.Trim();
            if (mirror is not null) Add(" --registry=" + mirror);
            Add(" --registry=" + FallbackMirror);
            Add(""); // npm 官方默认源（最后手段）
            return list.ToArray();
        }
    }

    /// <summary>
    /// dsh 服务启动参数拼装（纯函数，契约测试锁定）。
    /// [2026-08 用户回归修复] SelfContained node.exe 直启路径此前漏传 --no-open：
    /// dsh web 默认 ShellExecute 打开系统浏览器，壳自管 WebView2 窗口并不需要；
    /// 统一在此拼装保证所有路径一致。
    /// </summary>
    public static class ServiceLaunch
    {
        /// <summary>
        /// 【ADR-024 唯一入口】按身份构造 node.exe 直启 dsh 的完整参数串（不含 node 本身）：
        /// `"{entryJs}"` + （ProfilePath 为 null → `web` 子命令；非 null → 根级
        /// `--profile &lt;目录名&gt;`，web 与 --profile 互斥，ADR-022 安全模式）
        /// + `--host 127.0.0.1 --port {port} --no-open`。
        /// profile 参数只取目录名：dsh `--profile` 仅收 name、无分隔符
        /// （SafeProfileBuilder.SafeProfileName 契约）；完整物理路径由 Identity.ProfilePath 携带，
        /// 供 Outcome 测试断言"目录物理存在"。
        /// </summary>
        public static string BuildArgs(Domain.DshRuntimeIdentity identity, int port)
        {
            var entry = $"\"{identity.DshEntryJsPath}\"";
            var bootMode = identity.ProfilePath is null
                ? "web"
                : "--profile " + Path.GetFileName(identity.ProfilePath.TrimEnd('\\', '/'));
            return $"{entry} {bootMode} --host 127.0.0.1 --port {port} --no-open";
        }

        /// <summary>旧签名兼容转发（binJs + 显式安全 profile 名）——语义等价于 BuildArgs。</summary>
        public static string BuildSelfContainedArgs(string binJs, int port, string? safeProfileName)
        {
            var bootMode = safeProfileName is null ? "web" : "--profile " + safeProfileName;
            return $"\"{binJs}\" {bootMode} --host 127.0.0.1 --port {port} --no-open";
        }
    }

    /// <summary>
    /// 更新检查的网络出口策略（2026-08-29 安全通知可达性回归，纯函数 + 本机探测）。
    ///
    /// 实测：launcher 安全更新检查走 api.github.com、dsh 版本检查走 registry.npmjs.org——
    /// 大陆网络直连均不可达 → 更新检查静默失败，安全更新通知永远不到达。
    /// 策略：直连/系统代理失败后，探测常见本地代理端口（Clash 7890 / Clash Verge Rev 7897 /
    /// v2rayN 10809 / Privoxy 8118），存活者作为 HTTP 出口重试；会话级粘住首个成功出口。
    /// </summary>
    public static class UpdateProxyPolicy
    {
        /// <summary>本地代理端口连通性判定超时（毫秒）。回环连接毫秒级，400ms 足够。</summary>
        public const int ProxyProbeTimeoutMs = 400;

        /// <summary>常见本地 HTTP 代理候选（按国内占有率排序）。去重由调用方负责。</summary>
        public static IReadOnlyList<string> LocalProxyCandidates()
            => new[]
            {
                "http://127.0.0.1:7890",   // Clash / Clash for Windows
                "http://127.0.0.1:7897",   // Clash Verge Rev
                "http://127.0.0.1:10809",  // v2rayN (HTTP)
                "http://127.0.0.1:8118",   // Privoxy
            };

        /// <summary>本地代理端口连通性探测（TcpClient，400ms 硬超时）。仅回环地址。</summary>
        public static bool LocalProxyAlive(string proxyUri)
        {
            try
            {
                var uri = new Uri(proxyUri);
                if (uri.Scheme is not ("http" or "https")) return false;
                using var c = new System.Net.Sockets.TcpClient();
                var task = c.ConnectAsync(uri.Host, uri.Port);
                return task.Wait(ProxyProbeTimeoutMs) && c.Connected;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>从环境变量与本地代理候选中给出更新检查的出口序（含直连）。env 代理（
        /// http_proxy/https_proxy）优先于端口探测——用户显式配置最可信。直连键为 ""。</summary>
        public static IReadOnlyList<string> ExitCandidates(string? envHttpProxy)
        {
            var list = new List<string> { "" }; // 直连/系统代理兜底最先
            if (!string.IsNullOrWhiteSpace(envHttpProxy)) list.Add(envHttpProxy.Trim());
            foreach (var p in LocalProxyCandidates())
                if (!list.Contains(p, StringComparer.OrdinalIgnoreCase))
                    list.Add(p);
            return list;
        }
    }

    /// <summary>
    /// dsh 服务 stdout 行解析（纯函数，契约测试锁定）。
    /// [2026-08-29 token 栅栏回归] dsh ≥0.1.2 的 web-startup 给根路径加了 token 信任栅栏
    /// （0.1.1 根路径免鉴权），启动横幅形如 `dsh web: http://127.0.0.1:3080/?token=...`。
    /// 壳必须解析该行并跟随 token URL 导航，否则 WebView 停在 401 错误页（E2004）且页面
    /// 探针永久挂死。安全约束（防恶意插件伪造 stdout 行把壳 WebView 引向外站）：
    /// 仅接受回环主机 + 端口等于目标端口 + token 参数非空。
    /// </summary>
    public static class ServiceOutput
    {
        private const string WebBannerMarker = "dsh web: ";

        /// <summary>
        /// 从一行服务输出中提取带 token 的 web URL。
        /// 宽进：容忍时间戳/`[dsh] ` 渲染前缀与行尾附加文本（取标记后首个空白前的字段）。
        /// 严出：绝对 URL、http(s)、`token` 查询参数非空、端口等于 <paramref name="expectedPort"/>、
        /// 主机必须回环（Uri.IsLoopback）。任一不满足返回 false。
        /// </summary>
        public static bool TryExtractTokenUrl(string? rawLine, int expectedPort, out string tokenUrl)
        {
            tokenUrl = string.Empty;
            if (string.IsNullOrWhiteSpace(rawLine) || expectedPort <= 0) return false;
            var idx = rawLine.IndexOf(WebBannerMarker, StringComparison.Ordinal);
            if (idx < 0) return false;
            var candidate = rawLine[(idx + WebBannerMarker.Length)..].Trim();
            var spaceIdx = candidate.IndexOfAny([' ', '\t']);
            if (spaceIdx >= 0) candidate = candidate[..spaceIdx];
            if (candidate.Length == 0) return false;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme is not ("http" or "https")) return false;
            if (!uri.IsLoopback) return false;
            if (uri.Port != expectedPort) return false;
            var hasToken = false;
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = pair.IndexOf('=');
                var key = eq < 0 ? pair : pair[..eq];
                var value = eq < 0 ? string.Empty : pair[(eq + 1)..];
                if (string.Equals(key, "token", StringComparison.OrdinalIgnoreCase)
                    && value.Length > 0)
                {
                    hasToken = true;
                    break;
                }
            }
            if (!hasToken) return false;
            tokenUrl = uri.AbsoluteUri;
            return true;
        }
    }

    /// <summary>运行时配置解析：目标地址端口、生命周期模式、WebView2 版本。</summary>
    public static class RuntimeConfig
    {
        /// <summary>沙盒模式标志（纯环境读取，供 Manager/引擎层门控机器级副作用）：
        /// DSH_SANDBOX=1 时禁用自启/数据清理/首装真实网络安装等副作用。</summary>
        internal static bool IsSandboxMode =>
            string.Equals(Environment.GetEnvironmentVariable("DSH_SANDBOX"), "1", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// [2026-08-29 便携版通知分流] 判定安装形态：便携 ZIP（解压即用）vs MSI（Program Files）。
        /// MSI 安装到 Program Files（含 X86），其更新通知走系统 Toast/托盘气泡；便携版无快捷方式、
        /// 系统通知平台可能拒发 Toast（实测 0x80070490）——便携版更新通知改用模态弹窗兜底。
        /// 判定为纯函数：注入 ProgramFiles 基址可测。清单限制（如 MSI 装到自定义目录）会判为
        /// 便携——弹窗同样可达，仅通知形态不同，无功能损失。
        /// </summary>
        internal static bool IsPortableInstall(string? baseDirectory, string programFiles, string programFilesX86)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory)) return true;
            var b = baseDirectory.TrimEnd('/').TrimEnd('\\');
            bool Under(string? pf)
            {
                if (string.IsNullOrEmpty(pf)) return false;
                var root = pf.TrimEnd('/', '\\');
                // 路径段边界：根目录本身或 根+分隔符 前缀（防 "Program Files_bak" 前缀误判）
                return string.Equals(b, root, StringComparison.OrdinalIgnoreCase)
                    || b.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || b.StartsWith(root + '/', StringComparison.OrdinalIgnoreCase);
            }
            return !Under(programFiles) && !Under(programFilesX86);
        }

        internal static bool IsPortableInstall() => IsPortableInstall(
            AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86));

        /// <summary>[DSH_TEST_INSTALL_MODE] 测试覆盖：portable/msi 强制指定安装形态（沙盒/回归验证
        /// 通知分流与回退链用）；未设置走真实路径判定。</summary>
        internal static bool IsPortableInstallWithTestOverride() =>
            Environment.GetEnvironmentVariable("DSH_TEST_INSTALL_MODE") switch
            {
                "portable" => true,
                "msi" => false,
                _ => IsPortableInstall(),
            };
        /// <summary>
        /// 解析目标服务地址与端口。空值/非法值/非 http(s) 一律回退默认 3080。
        /// 供 DSH_WEB_URL / DSH_WEB_PORT 环境变量覆盖目标地址/端口（免重建）时使用。
        /// 优先级：DSH_WEB_URL（含端口）→ DSH_WEB_PORT → 默认 3080。
        /// </summary>
        internal static (string Url, int Port) ResolveTarget(string? envUrl, string? envPort = null)
        {
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
            if (!string.IsNullOrWhiteSpace(envPort) &&
                int.TryParse(envPort, out var port) && port is > 0 and < 65536)
                return ($"http://127.0.0.1:{port}", port);
            return ("http://127.0.0.1:3080", 3080);
        }

        /// <summary>
        /// 解析 settings.json 中的 serviceLifetime；缺失/非法回退到 fallback（默认"跟随窗口"，
        /// 省内存：关窗即停 dsh 服务，每次启动重新拉起；想常驻请在插件设置里改）。
        /// </summary>
        internal static ServiceLifetime ParseLifetimeMode(string? json, ServiceLifetime fallback = ServiceLifetime.FollowWindow)
        {
            if (string.IsNullOrWhiteSpace(json)) return fallback;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("serviceLifetime", out var value) && value.TryGetInt32(out var n)
                    && Enum.IsDefined(typeof(ServiceLifetime), n))
                {
                    return (ServiceLifetime)n;
                }
            }
            catch
            {
                // 解析失败回退默认
            }
            return fallback;
        }

        /// <summary>读取 Evergreen WebView2 Runtime 版本（注册表 pv 值）；未安装/读取失败返回 null。
        /// 供 WebView2 缺失兜底（静默安装 Bootstrapper）与诊断导出共用。</summary>
        internal static string? ReadWebView2Version()
        {
            try
            {
                var v = Microsoft.Win32.Registry.GetValue(
                    @"HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", "pv", null);
                if (v is string s && !string.IsNullOrWhiteSpace(s)) return s;
                v = Microsoft.Win32.Registry.GetValue(
                    @"HKLM\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}", "pv", null);
                return v as string;
            }
            catch { return null; }
        }
    }

    /// <summary>文件系统策略：下载名推导、文件名清理、原子写入。</summary>
    public static class FileSystemPolicy
    {
        /// <summary>Windows 保留设备名（这些名字带任意扩展名都不可用作文件名）。</summary>
        private static readonly string[] ReservedNames =
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        };

        /// <summary>MIME → 扩展名映射（用于 blob: 等无文件名下载的兜底）。</summary>
        private static readonly Dictionary<string, string> MimeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["text/plain"] = ".txt",
            ["text/markdown"] = ".md",
            ["text/html"] = ".html",
            ["text/csv"] = ".csv",
            ["application/json"] = ".json",
            ["application/pdf"] = ".pdf",
            ["application/zip"] = ".zip",
            ["application/x-zip-compressed"] = ".zip",
            ["application/gzip"] = ".gz",
            ["application/x-tar"] = ".tar",
            ["image/png"] = ".png",
            ["image/jpeg"] = ".jpg",
            ["image/gif"] = ".gif",
            ["image/webp"] = ".webp",
            ["image/svg+xml"] = ".svg",
            ["audio/mpeg"] = ".mp3",
            ["audio/wav"] = ".wav",
            ["video/mp4"] = ".mp4",
        };

        /// <summary>
        /// 从 Content-Disposition / 下载 URI / MIME 推导建议文件名。
        /// （当前 SDK 版本没有 SuggestedFileName API，只能自行推导。）
        /// </summary>
        internal static string SuggestDownloadName(string? disposition, string? downloadUri, string? mimeType)
        {
            string? name = null;
            if (!string.IsNullOrWhiteSpace(disposition))
            {
                var m = Regex.Match(disposition, @"filename\*?=(?:UTF-8'')?[""']?(?<name>[^""';]+)");
                if (m.Success && !string.IsNullOrWhiteSpace(m.Groups["name"].Value))
                    name = m.Groups["name"].Value.Trim();
            }
            // 仅 http(s) 用 URI 尾段；blob:/data: 的尾段是随机 UUID/内联内容，对用户无意义，
            // 一律走下面的时间戳 + MIME 扩展名兜底。
            if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(downloadUri)
                && Uri.TryCreate(downloadUri, UriKind.Absolute, out var uri)
                && uri.Scheme is "http" or "https")
            {
                var segment = Path.GetFileName(uri.AbsolutePath);
                if (!string.IsNullOrWhiteSpace(segment))
                    name = segment;
            }
            name = string.IsNullOrWhiteSpace(name)
                ? $"dsh-{DateTime.UtcNow:yyyyMMddHHmmss}"
                : Uri.UnescapeDataString(name);

            // blob: 等无扩展名下载：按 MIME 类型补一个扩展名，便于识别
            if (!Path.HasExtension(name) && !string.IsNullOrWhiteSpace(mimeType)
                && MimeExtensions.TryGetValue(mimeType.Split(';')[0].Trim(), out var ext))
                name += ext;
            return name;
        }

        /// <summary>清理文件名中的非法字符；Windows 保留设备名与结尾的点/空格一并处理。</summary>
        internal static string SanitizeFileName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (var c in name)
                sb.Append(invalid.Contains(c) ? '_' : c);
            var result = sb.ToString().Trim().TrimEnd('.', ' ');
            if (result.Length == 0)
                return $"dsh-{DateTime.Now:yyyyMMddHHmmss}";
            var stem = Path.GetFileNameWithoutExtension(result).ToUpperInvariant();
            if (Array.IndexOf(ReservedNames, stem) >= 0)
                result = "_" + result;
            return result;
        }

        /// <summary>
        /// 原子写文本：先写同目录临时文件，再 File.Move 覆盖目标——避免关窗/退出瞬间断电或崩溃
        /// 留下半截 JSON（窗口位置记忆、暂存更新、镜像记忆等）。调用方保证目录可写。
        /// </summary>
        internal static void AtomicWrite(string path, string content)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(tmp, content);
                File.Move(tmp, path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }
    }

    /// <summary>生命周期决策：托盘拦截、关窗停服务、待应用更新处理。</summary>
    public static class LifecycleDecisions
    {
        /// <summary>
        /// FormClosing 托盘拦截决策（矩阵 L1）：是否拦截关闭、隐藏到托盘。
        /// 决策 = 生命周期模式为"托盘驻留" 且 未请求真正退出。
        /// [INVARIANT] This MUST be called BEFORE WebView2 disposal. See ADR-002 (ORDER-INVARIANT).
        /// </summary>
        internal static bool ShouldInterceptCloseToTray(ServiceLifetime mode, bool trayExitRequested)
            => ShouldInterceptCloseToTray(mode, trayExitRequested, systemSessionEnding: false);

        /// <summary>
        /// FormClosing 托盘拦截决策（矩阵 L1 + F15）：系统会话终止（关机/注销）时**永不拦截**——
        /// 此时把窗口藏进托盘等于阻塞系统关机（OS 弹"阻止关机"或超时强杀，且强杀不走任何清理）。
        /// systemSessionEnding 由组合根从 CloseReason.WindowsShutDown/SessionEnding 归一传入。
        /// </summary>
        internal static bool ShouldInterceptCloseToTray(ServiceLifetime mode, bool trayExitRequested, bool systemSessionEnding)
            => mode == ServiceLifetime.Tray && !trayExitRequested && !systemSessionEnding;

        /// <summary>
        /// 关窗/托盘退出时**是否停止 dsh 服务**决策（矩阵 M1）：
        /// - FollowWindow 且服务由本壳管理（本次拉起**或**接管了上次残留）且非外部托管 → true；
        /// - AlwaysOn → false；Tray → false；external 托管 → 恒 false。
        /// 关键语义："接管即负责"——TryAdoptOrphanService 成功接管后 shellManaged=true，
        /// 关窗必须停掉被接管的服务，否则 node 常驻。
        /// </summary>
        internal static bool ShouldStopServiceOnClose(ServiceLifetime mode, bool externallyManaged, bool shellManaged)
            => mode == ServiceLifetime.FollowWindow && shellManaged && !externallyManaged;

        /// <summary>
        /// [F21] 单实例 mutex 名：绑定目标服务端口——同端口即同实例组，不同端口
        /// （DSH_WEB_PORT 覆盖）互不干扰。字符串漂移曾无门禁，契约测试锁定格式
        /// （Program 的 FindWindow 主窗定位逻辑依赖与此互恰的窗口标题约定）。
        /// </summary>
        internal static string SingleInstanceMutexName(int port) => $@"Local\DshWeb.SingleInstance.{port}";

        /// <summary>
        /// 启动早期"待应用更新"处理决策（矩阵 U2）：
        /// 1. pending 且端口关 → ApplyNow（服务未运行，直接应用）；
        /// 2. pending 且端口开且运行版本 != 待应用版本 → PromptRestart（服务在跑，一次性询问）；
        /// 3. pending 且端口开且运行版本 == 待应用版本 → ClearPending（历史残留）；
        /// 4. 无 pending → None。
        /// [INVARIANT] Port open + pending → NEVER silently skip. Otherwise update-staging death loop.
        /// </summary>
        internal static PendingUpdateAction ResolvePendingUpdateAction(
            bool pendingExists, bool portOpen, string? runningVersion, string? pendingVersion)
        {
            if (!pendingExists) return PendingUpdateAction.None;
            if (!portOpen) return PendingUpdateAction.ApplyNow;
            if (string.Equals(runningVersion?.Trim(), pendingVersion?.Trim(), StringComparison.Ordinal))
                return PendingUpdateAction.ClearPending;
            return PendingUpdateAction.PromptRestart;
        }
    }

    /// <summary>启动恢复询问路由策略（2026-08-25 事故回归，纯函数，契约测试锁定）：
    /// failed 裁决后弹「安全模式」还是「重启服务」。规则：
    /// 1. 有插件相关证据 → 安全模式（原语义）；
    /// 2. 无插件证据但连续失败已达阈值（跨会话持久计数）→ 也升级安全模式——
    ///    重启对确定性配置崩溃必然无效，「问重启」的死循环只会消耗用户耐心；
    /// 3. 其余匿名失败 → 重启服务（2026-08 用户回归：无插件时弹安全模式是误导）。
    /// </summary>
    public static class BootRecoveryPolicy
    {
        /// <summary>连续匿名启动失败达到该次数后升级为安全模式询问（事故实测 3 次会话 4 次崩溃）。</summary>
        public const int AnonymousFailureSafeModeThreshold = 3;

        /// <summary>恢复动作：进入安全模式阶梯，或仅重启 dsh 服务。</summary>
        public enum RecoveryAsk
        {
            /// <summary>询问进入安全模式（禁用第三方插件的隔离 profile 阶梯）。</summary>
            AskSafeMode,
            /// <summary>询问重启 dsh 服务（匿名单次失败的轻量恢复）。</summary>
            AskRestartService,
        }

        /// <summary>路由决策。consecutiveFailures 为**含本次失败**在内的连续失败次数（≥1）。</summary>
        public static RecoveryAsk Decide(bool pluginInvolved, int consecutiveFailures)
        {
            if (pluginInvolved) return RecoveryAsk.AskSafeMode;
            if (consecutiveFailures >= AnonymousFailureSafeModeThreshold) return RecoveryAsk.AskSafeMode;
            return RecoveryAsk.AskRestartService;
        }
    }

    /// <summary>生命周期插件配置：检测安装、解析/验证 serviceLifetime、解析有效模式。</summary>
    public static class PluginConfig
    {
        /// <summary>dsh-launcher-lifetime 插件包名（dsh plugin 生态，经 profiles 的 pnpm 安装）。</summary>
        internal const string LifetimePluginPackage = "dsh-launcher-lifetime";

        /// <summary>
        /// 检测 dsh-launcher-lifetime 插件是否物理存在（配置降级依据，v0.3.0）：
        /// 任一 profile 的 node_modules 实体存在，或任一 profile 的 package.json
        /// （dependencies / dsh.profile.bundles）声明了该包。dsh plugin add 经 pnpm 写入
        /// profiles/&lt;name&gt;/package.json 并在对应 node_modules 实体化（file: 链接安装也会
        /// 实体化，已实测）。任何读取失败一律按"未安装"处理（安全默认：宁回退不多驻）。
        /// </summary>
        internal static bool IsLifetimePluginInstalled(string dshHomeDir)
        {
            try
            {
                var profiles = Path.Combine(dshHomeDir, "profiles");
                if (!Directory.Exists(profiles)) return false;
                foreach (var profileDir in Directory.GetDirectories(profiles))
                {
                    // 1) node_modules 实体（权威：安装必然实体化）
                    if (Directory.Exists(Path.Combine(profileDir, "node_modules", LifetimePluginPackage)))
                        return true;
                    // 2) 清单声明（目录未实体化前的声明也算已装意图）
                    var manifest = Path.Combine(profileDir, "package.json");
                    if (!File.Exists(manifest)) continue;
                    try
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(manifest));
                        var root = doc.RootElement;
                        if (root.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
                        if (root.TryGetProperty("dependencies", out var deps)
                            && deps.ValueKind == System.Text.Json.JsonValueKind.Object
                            && deps.TryGetProperty(LifetimePluginPackage, out _))
                            return true;
                        if (root.TryGetProperty("dsh", out var dshSeg)
                            && dshSeg.ValueKind == System.Text.Json.JsonValueKind.Object
                            && dshSeg.TryGetProperty("profile", out var profile)
                            && profile.ValueKind == System.Text.Json.JsonValueKind.Object
                            && profile.TryGetProperty("bundles", out var bundles)
                            && bundles.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var b in bundles.EnumerateArray())
                            {
                                if (b.ValueKind == System.Text.Json.JsonValueKind.String
                                    && b.GetString() == LifetimePluginPackage)
                                    return true;
                            }
                        }
                    }
                    catch { /* 单个 manifest 损坏跳过 */ }
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>
        /// package.json 文本（dependencies / dsh.profile.bundles）是否声明了任何
        /// **非 @deepseek-ai/** 的第三方或本地（file:/相对路径）插件（纯函数，契约测试锁定）。
        /// 2026-08-25 事故回归：匿名服务进程崩溃 + 第三方插件在场 ⇒ 应优先按插件嫌疑处理
        /// （询问安全模式），而不是让用户在注定无效的「重启服务」上循环。
        /// 解析失败/空文本一律 false（安全默认：不凭损坏的清单归因插件）。
        /// </summary>
        internal static bool BundlesDeclareThirdParty(string? packageJsonText)
        {
            if (string.IsNullOrWhiteSpace(packageJsonText)) return false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(packageJsonText);
                var root = doc.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object) return false;
                if (root.TryGetProperty("dependencies", out var deps)
                    && deps.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var dep in deps.EnumerateObject())
                        if (!dep.Name.StartsWith("@deepseek-ai/", StringComparison.Ordinal))
                            return true;
                }
                if (root.TryGetProperty("dsh", out var dshSeg)
                    && dshSeg.ValueKind == System.Text.Json.JsonValueKind.Object
                    && dshSeg.TryGetProperty("profile", out var profile)
                    && profile.ValueKind == System.Text.Json.JsonValueKind.Object
                    && profile.TryGetProperty("bundles", out var bundles)
                    && bundles.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    foreach (var b in bundles.EnumerateArray())
                    {
                        if (b.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                        var name = b.GetString();
                        if (!string.IsNullOrWhiteSpace(name)
                            && !name.StartsWith("@deepseek-ai/", StringComparison.Ordinal))
                            return true;
                    }
                }
                return false;
            }
            catch { return false; }
        }

        /// <summary>读取用户 web profile 清单并判定是否声明第三方插件（文件缺失/损坏 → false）。</summary>
        internal static bool ProfileHasThirdPartyBundles(string webPackageJsonPath)
        {
            try
            {
                if (!File.Exists(webPackageJsonPath)) return false;
                return BundlesDeclareThirdParty(File.ReadAllText(webPackageJsonPath));
            }
            catch { return false; }
        }

        /// <summary>
        /// 判断 settings.json 顶层对象是否含有名为 "serviceLifetime" 的键（精确键判定，质量治理 P2-4）。
        /// 仅当能成功解析为 JSON 对象且顶层恰好存在该键时返回 true；解析失败/键不存在返回 false。
        /// 避免旧子串 Contains 判定在"别的键名/键值含 serviceLifetime 子串"时误报。
        /// </summary>
        internal static bool HasServiceLifetimeKey(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("serviceLifetime", out _);
            }
            catch { return false; }
        }

        /// <summary>
        /// 判断 settings.json 顶层 "serviceLifetime" 的值是否为合法枚举（0/1/2）。仅当键存在且
        /// 值可解析为合法的 ServiceLifetime 时返回 true；键缺失/解析失败/越界返回 false。
        /// </summary>
        internal static bool IsValidLifetimeValue(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                return root.ValueKind == System.Text.Json.JsonValueKind.Object
                    && root.TryGetProperty("serviceLifetime", out var value) && value.TryGetInt32(out var n)
                    && Enum.IsDefined(typeof(ServiceLifetime), n);
            }
            catch { return false; }
        }

        /// <summary>
        /// 有效服务存活模式解析（含插件降级，v0.3.0）：
        /// - 插件缺失 → 忽略 settings.json 里的 serviceLifetime，回退 fallback（跟随窗口）；
        ///   ShouldPurge=true 时调用方应抹除失效字段（幂等）。插件缺失且顶层含该键（无论合法与否）都标记清理。
        /// - 插件存在 → 正常解析（保留用户选择）；仅当顶层键存在但值非法/越界导致回退时才标记清理（A4 R2）。
        ///   合法值（含与 fallback 不同的 0/1）不清理——保留用户选择。
        /// </summary>
        internal static (ServiceLifetime Mode, bool ShouldPurge) ResolveEffectiveLifetime(
            string? settingsJson, bool pluginPresent, ServiceLifetime fallback = ServiceLifetime.FollowWindow)
        {
            if (!pluginPresent)
                return (fallback, HasServiceLifetimeKey(settingsJson));
            var mode = RuntimeConfig.ParseLifetimeMode(settingsJson, fallback);
            // 值合法则保留（不 purge）；键在但值非法/越界 → 回退并提示清理。
            var shouldPurge = HasServiceLifetimeKey(settingsJson) && !IsValidLifetimeValue(settingsJson);
            return (mode, shouldPurge);
        }
    }

    /// <summary>进程管理：端口反查 PID、进程树终止、祖先链收集、P/Invoke 声明。</summary>
    public static class ProcessManagement
    {
        // ---- P/Invoke：GetExtendedTcpTable（iphlpapi.dll）精确反查端口→监听 PID ----
        private const int AfInet = 2;                                    // AF_INET（IPv4）
        private const uint TcpTableOwnerPidListener = 4;                 // TCP_TABLE_OWNER_PID_LISTENER
        private const uint ErrorNoData = 232;                            // ERROR_NO_DATA（无监听表）
        private const uint ErrorInsufficientBuffer = 122;                // ERROR_INSUFFICIENT_BUFFER

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpRowOwnerPid
        {
            public uint State;
            public uint LocalAddr;
            public uint LocalPort; // 网络字节序（大端）
            public uint RemoteAddr;
            public uint RemotePort;
            public uint OwningPid;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MibTcpTableOwnerPid
        {
            public uint DwNumEntries;
            public MibTcpRowOwnerPid Table; // 数组头（首个元素，后续按 SizeOf 步进）
        }

        [DllImport("iphlpapi.dll", SetLastError = true)]
        private static extern uint GetExtendedTcpTable(
            IntPtr pTcpTable, ref int pdwSize, bool bOrder, int ulAf, uint tableClass, uint reserved);

        private const uint Th32csSnapprocess = 0x2;

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessEntry32
        {
            public uint DwSize;
            public uint CntUsage;
            public uint Th32ProcessID;
            public IntPtr Th32DefaultHeapID;
            public uint Th32ModuleID;
            public uint CntThreads;
            public uint Th32ParentProcessID;
            public int PcPriClassBase;
            public uint DwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string SzExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll")]
        private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll")]
        private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr hObject);

        /// <summary>
        /// 按端口反查监听进程 PID（任务一：精确端口归属，替代仅靠 netstat 字符串解析）。
        /// 优先 P/Invoke GetExtendedTcpTable（亚毫秒、无外部进程），失败/无结果回退 netstat -ano
        /// 解析（兼容异常环境）。找不到返回 0。</summary>
        internal static int GetProcessIdByPort(int port)
        {
            var pid = PidByPortViaTcpTable(port);
            return pid > 0 ? pid : PidByPortViaNetstat(port);
        }

        /// <summary>
        /// 拆分命令行为 (exe, args)：带引号的可执行路径取首对引号内内容，否则取首个空格前段。
        /// 供 DSH_SERVICE_CMD 测试钩子直接 ProcessStartInfo(exe, args) 启动——ADR-021 禁止
        /// cmd.exe 包装，此纯函数让"自定义服务命令"同样走 node.exe 直启路径。无法拆分返回 null。
        /// </summary>
        internal static (string Exe, string Args)? SplitCommandLine(string? command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            var trimmed = command.Trim();
            if (trimmed.StartsWith('"'))
            {
                var close = trimmed.IndexOf('"', 1);
                if (close < 0) return null; // 引号不闭合 → 拒绝（响亮失败优于误启动）
                var exe = trimmed.Substring(1, close - 1);
                var args = trimmed.Substring(close + 1).TrimStart();
                return exe.Length == 0 ? null : (exe, args);
            }
            var space = trimmed.IndexOf(' ');
            if (space < 0) return (trimmed, "");
            return (trimmed.Substring(0, space), trimmed.Substring(space + 1).TrimStart());
        }

        /// <summary>强杀进程树（taskkill /PID &lt;pid&gt; /T /F）：连同挂死的 cmd / npx 外壳一并结束。
        /// 返回是否已发起（taskkill 执行成功）；端口释放由调用方轮询确认。</summary>
        internal static bool KillProcessTree(int pid)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
                { UseShellExecute = false, CreateNoWindow = true };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null) return false;
                p.WaitForExit(3000);
                return true;
            }
            catch { return false; }
        }

        /// <summary>收集指定 PID 的祖先进程链（向上 8 层）：用于清理"cmd/npx 外壳"这类监听端口进程的
        /// 父进程（taskkill /T 只向下杀子进程，不会结束父外壳）。失败/无祖先返回空列表。</summary>
        internal static List<int> GetAncestorPids(int pid)
        {
            var result = new List<int>();
            try
            {
                var parents = SnapshotParentPids();
                var seen = new HashSet<int> { pid };
                var current = pid;
                for (var i = 0; i < 8; i++)
                {
                    if (!parents.TryGetValue(current, out var parent) || parent <= 0 || parent == current) break;
                    if (!seen.Add(parent)) break;
                    result.Add(parent);
                    current = parent;
                }
            }
            catch { }
            return result;
        }

        /// <summary>PID 身份校验（防复用误杀）：pid 文件里的 PID 可能被系统复用给无关进程——
        /// 杀进程前必须确认该 PID 确为 dsh 服务（node 进程）。See ADR-011.</summary>
        internal static bool IsLikelyDshService(int pid)
        {
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById(pid);
                return string.Equals(p.ProcessName, "node", StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 从 WM_NCHITTEST 的 64 位 lParam 拆出屏幕坐标：低 16 位有符号 = X，高 16 位有符号 = Y。
        /// 左侧/上方副屏为负坐标，直接 (int)lParam 会抛 OverflowException（B1 修复）。
        /// </summary>
        internal static (short X, short Y) SplitLParam(long lParam)
            => ((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));

        private static int PidByPortViaTcpTable(int port)
        {
            try
            {
                var size = 0;
                var rc = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (rc == ErrorNoData) return 0; // 无监听表：端口未开
                if (rc != ErrorInsufficientBuffer || size <= 0) return 0;
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    rc = GetExtendedTcpTable(buf, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                    if (rc != 0) return 0; // NO_ERROR
                    var count = Marshal.ReadInt32(buf);
                    var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
                    var rowBase = IntPtr.Add(buf,
                        Marshal.OffsetOf<MibTcpTableOwnerPid>(nameof(MibTcpTableOwnerPid.Table)).ToInt32());
                    var portBe = (uint)(((port & 0xFF) << 8) | ((port >> 8) & 0xFF)); // 端口转网络字节序
                    for (var i = 0; i < count; i++)
                    {
                        var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(IntPtr.Add(rowBase, i * rowSize));
                        if (row.LocalPort == portBe && row.OwningPid != 0) return (int)row.OwningPid;
                    }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            catch { /* 回退 netstat */ }
            return 0;
        }

        /// <summary>netstat -ano 解析（GetExtendedTcpTable 的兼容回退；Program 旧路径同款逻辑）。</summary>
        private static int PidByPortViaNetstat(int port)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("netstat", "-ano -p tcp")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using var p = System.Diagnostics.Process.Start(psi);
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
            catch { }
            return 0;
        }

        /// <summary>构建进程 PID→父 PID 快照（CreateToolhelp32Snapshot 单次枚举，无外部依赖）。</summary>
        private static Dictionary<int, int> SnapshotParentPids()
        {
            var map = new Dictionary<int, int>();
            var snap = CreateToolhelp32Snapshot(Th32csSnapprocess, 0);
            if (snap == IntPtr.Zero) return map;
            try
            {
                var entry = new ProcessEntry32 { DwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
                if (Process32First(snap, ref entry))
                {
                    do
                    {
                        map[unchecked((int)entry.Th32ProcessID)] = unchecked((int)entry.Th32ParentProcessID);
                    }
                    while (Process32Next(snap, ref entry));
                }
            }
            finally { CloseHandle(snap); }
            return map;
        }

        // ============ 进程终止链路（2026-08 僵尸清扫竞态修复） ============

        /// <summary>
        /// 可靠终止 dsh 服务进程（组合根 <c>Program.KillProcess</c> 委托到此，业务逻辑下沉）。
        /// 全程限时且不卡调用方：温和 taskkill /PID /T → 轮询 800ms → 强杀 taskkill /PID /T /F
        /// → 确认 1500ms → 仍活重试一次强杀。每次都等待 taskkill 自身退出（修复旧实现"发完即轮询"的
        /// 竞态：taskkill 未退出就被判定失效）。杀前双重身份校验（IsLikelyDshService + 端口归属 == pid），
        /// 防 PID 复用误杀无辜进程。失败（超时/仍活）返回 false 并响亮上报 E2005，调用方保留 pid 文件
        /// 留待下次启动由 SweepStaleServicePid 认领。
        /// </summary>
        public static bool KillServiceProcess(int pid, int port)
        {
            if (pid <= 0) return false;
            if (!IsLikelyDshService(pid))
            {
                // 2026-08-25 事故回归：已崩溃退出的服务 pid（如 exit=1 后壳停止链再次停止）
                // 此前被误报为 "not a dsh (node) service process"——GetProcessById 对死 pid 抛异常，
                // catch 一律返回 false，与"活着但不是 node"的真防误杀场景混为一谈。
                // 区分：pid 已不存在 = 目标已消失，无需再杀（Info + true，调用方走端口释放等待
                // 并清理 pid 文件）；活着但不是 node = 真正的防复用误杀拒绝（Warn + false）。
                if (!IsProcessAlive(pid))
                {
                    Logger.Info($"pid={pid} already exited; nothing to kill");
                    return true;
                }
                Logger.Warn($"refusing to kill pid={pid}: not a dsh (node) service process");
                return false;
            }
            if (port > 0)
            {
                var owner = GetProcessIdByPort(port);
                if (owner != pid)
                {
                    Logger.Warn($"refusing to kill pid={pid}: port {port} owner={owner} != pid (possible PID reuse)");
                    return false;
                }
            }
            // 温和阶段：taskkill /PID /T（不含 /F），等待 taskkill 自身退出
            RunTaskKill($"/PID {pid} /T", timeoutMs: 4000);
            if (WaitForProcessExit(pid, 800)) return true;
            // 强杀阶段：taskkill /PID /T /F，等待 taskkill 退出后确认 1500ms
            RunTaskKill($"/PID {pid} /T /F", timeoutMs: 4000);
            if (WaitForProcessExit(pid, 1500)) return true;
            // 仍活 → 重试一次强杀
            RunTaskKill($"/PID {pid} /T /F", timeoutMs: 4000);
            if (WaitForProcessExit(pid, 1500)) return true;
            Logger.Error($"failed to terminate dsh service pid={pid} on port {port}; pid file kept for next-start sweep",
                ErrorCodes.E2005);
            return false;
        }

        /// <summary>
        /// 直接运行 taskkill.exe（ADR-021：严禁 cmd.exe 包装——taskkill 是 System32 原生
        /// exe，无 .cmd shim 与编码陷阱，直启同样满足三必须：重定向输出并异步排空、限时
        /// 等待、超时 Kill(entireProcessTree) 清理）。等待 taskkill 自身退出；
        /// 命令是否真正杀掉目标由调用方 <see cref="WaitForProcessExit"/> 判定（taskkill 退出码不可靠）。
        /// </summary>
        internal static bool RunTaskKill(string args, int timeoutMs)
        {
            var psi = new System.Diagnostics.ProcessStartInfo("taskkill.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var proc = new System.Diagnostics.Process { StartInfo = psi, EnableRaisingEvents = false };
            try
            {
                if (!proc.Start())
                {
                    Logger.Warn($"taskkill failed to start: {args}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"taskkill start threw: {ex.Message}");
                return false;
            }
            if (!proc.WaitForExit(timeoutMs))
            {
                // taskkill 自身超时（极罕见）→ 强杀 taskkill 整树后判定失败
                try { proc.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return false;
            }
            return true; // taskkill 已退出；目标是否真死由 WaitForProcessExit 判定
        }

        /// <summary>轮询进程是否已退出（最多 timeoutMs，每 50ms 探一次）。</summary>
        internal static bool WaitForProcessExit(int pid, int timeoutMs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                if (!IsProcessAlive(pid)) return true;
                System.Threading.Thread.Sleep(50);
            }
            return !IsProcessAlive(pid);
        }

        /// <summary>pid 是否仍存活（死 pid/已被回收 → false；internal 供回归测试与
        /// KillServiceProcess 的"已消失无需杀"分支区分"已消失"与"活着但不该杀"）。</summary>
        internal static bool IsProcessAlive(int pid)
        {
            try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
            catch { return false; }
        }
    }

    /// <summary>npm 工具函数：路径解析、错误分类、PATH 查找。</summary>
    public static class NpmHelpers
    {
        /// <summary>
        /// 构建 npm.cmd 的绝对路径（纯函数：环境隔离与回退）。
        /// <paramref name="nodeRoot"/> = 已解析的 Node 根目录（可为 null）；<paramref name="fromPath"/> = `where npm.cmd` 定位到的路径（可为 null）。
        /// 返回**不带引号**的绝对路径（引号/转义由调用方按 cmd /c 规则统一处理）；均不可用返回 null。</summary>
        internal static string? ResolveNpmCmdPath(string? nodeRoot, string? fromPath)
        {
            if (!string.IsNullOrWhiteSpace(nodeRoot))
            {
                var fromRoot = Path.Combine(nodeRoot, "npm.cmd");
                if (File.Exists(fromRoot)) return fromRoot;
            }
            if (!string.IsNullOrWhiteSpace(fromPath) && File.Exists(fromPath.Trim()))
                return fromPath.Trim();
            return null;
        }

        /// <summary>
        /// 判定 npm 失败是否为可重试的网络类错误（任务三：更新安装失败 pending 保留/清理依据）。
        /// 网络/超时类（ETIMEDOUT/ECONNRESET/ECONNREFUSED/ENOTFOUND/EAI_AGAIN/timed out/registry）
        /// → 保留 pending 下次启动重试；其余（权限/包损坏）→ 非重试，调用方应清 pending 防死循环。
        /// 纯函数可单测（UpdateFlowContractTests 锁定契约）。</summary>
        internal static bool IsRetryableNpmError(string tail)
        {
            if (string.IsNullOrWhiteSpace(tail)) return false;
            return tail.Contains("ETIMEDOUT", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("ECONNREFUSED", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("timed out", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("registry", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("ENOTFOUND", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("EAI_AGAIN", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 判定 npm 输出是否为"找不到 npm/cmd"类错误（'不是内部或外部命令'/'not recognized'）：
        /// GUI 进程从桌面启动时 PATH 可能不含 Node 目录，`cmd /c npm` 会报该错误——据此在失败
        /// 弹窗中提示"请安装 Node.js 18+"而非笼统"下载失败"。纯函数可单测。</summary>
        internal static bool IsNpmNotFoundError(string tail)
        {
            if (string.IsNullOrWhiteSpace(tail)) return false;
            return tail.Contains("不是内部或外部命令", StringComparison.Ordinal)
                || tail.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("找不到文件", StringComparison.Ordinal)
                || tail.Contains("系统找不到指定的文件", StringComparison.Ordinal)
                || tail.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
                || tail.Contains("Error: Cannot find module", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 在 PATH 中查找可执行文件（如 node.exe / npx.cmd）。
        /// 用于启动前的依赖预检：缺 Node.js 时立即提示，而不是等 90 秒服务拉起超时。
        /// </summary>
        internal static bool HasExecutableOnPath(string fileName, string? pathEnv)
        {
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(pathEnv))
                return false;
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (File.Exists(Path.Combine(dir.Trim(), fileName)))
                        return true;
                }
                catch
                {
                    // 忽略不可访问的目录
                }
            }
            return false;
        }
    }

    /// <summary>已安装 dsh 产品识别与旧版本清理决策。</summary>
    public static class UpgradeProducts
    {
        /// <summary>已安装的 dsh-launcher 产品（ProductCode + 版本）。</summary>
        public sealed record InstalledDsh(string ProductCode, Version Version);

        /// <summary>本项目的固定 UpgradeCode（0.1.0 起所有版本一致），用于精确识别"我们的产品"。</summary>
        internal const string DshUpgradeCode = "{3B29D055-E142-43BD-ADA8-C5377D11BD7E}";

        /// <summary>
        /// 枚举注册表中 DisplayName 为 "dsh-launcher" 的产品（HKLM / HKLM WOW6432 / HKCU / HKCU WOW6432）。
        /// 这只是候选列表；是否真的是本产品必须再经 <see cref="FilterByUpgradeCode"/> 用 UpgradeCode 确认，
        /// 避免其他恰好同名的软件被误清理。
        /// </summary>
        internal static List<InstalledDsh> ReadCandidateProducts()
        {
            var result = new List<InstalledDsh>();
            var roots = new[]
            {
                Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                Registry.LocalMachine.OpenSubKey(@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
                Registry.CurrentUser.OpenSubKey(@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            };
            // 根键句柄逐个释放（类为 IDisposable），避免"清理旧版本"路径反复触发时泄漏句柄
            foreach (var root in roots)
            using (root as IDisposable) // using var root 不可行（元素可能 null），用 null 安全释放
            {
                if (root is null) continue;
                try
                {
                    foreach (var sub in root.GetSubKeyNames())
                    {
                        try
                        {
                            using var key = root.OpenSubKey(sub);
                            if (key?.GetValue("DisplayName") is string name
                                && string.Equals(name, "dsh-launcher", StringComparison.OrdinalIgnoreCase)
                                && key.GetValue("DisplayVersion") is string verStr
                                && Version.TryParse(verStr, out var ver))
                            {
                                result.Add(new InstalledDsh(sub, ver));
                            }
                        }
                        catch
                        {
                            // 跳过无法读取的键
                        }
                    }
                }
                catch
                {
                    // 跳过无法枚举的根
                }
            }
            return result;
        }

        /// <summary>
        /// 用 UpgradeCode 精确过滤候选产品：只有 UpgradeCode 与本项目固定值一致的产品才算
        /// dsh-launcher。upgradeCodeOf 读取失败或返回 null/不匹配时该产品被排除——
        /// 宁可不清理，也不误删其他同名软件。
        /// </summary>
        internal static List<InstalledDsh> FilterByUpgradeCode(
            List<InstalledDsh> candidates, Func<string, string?> upgradeCodeOf)
        {
            return candidates
                .Where(c => string.Equals(upgradeCodeOf(c.ProductCode), DshUpgradeCode, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        /// <summary>
        /// 从已安装产品中选出"旧版本"：
        /// - 当前产品（currentCode，安装时写入 HKLM\Software\dsh-launcher\CurrentProductCode）永远保留；
        /// - 当前产品存在时，清理所有版本不高于当前版本的其他产品（同版本重复安装也清理），
        ///   比当前更高的版本保留（异常场景，不做判断）；
        /// - 便携版等没有当前产品时，保留一个最高版本，其余清理。
        /// 返回空表示无需清理。
        /// </summary>
        internal static List<InstalledDsh> PickOldInstalls(List<InstalledDsh> products, string? currentCode = null)
        {
            var olds = new List<InstalledDsh>();
            if (products.Count <= 1) return olds;

            InstalledDsh? current = null;
            if (!string.IsNullOrWhiteSpace(currentCode))
                current = products.FirstOrDefault(p =>
                    string.Equals(p.ProductCode, currentCode, StringComparison.OrdinalIgnoreCase));

            if (current is null)
            {
                // 便携版等：保留一个最高版本，其余清理
                var max = products.Max(p => p.Version);
                var kept = false;
                foreach (var p in products)
                {
                    if (!kept && p.Version == max) { kept = true; continue; }
                    olds.Add(p);
                }
                return olds;
            }

            // 当前产品存在：清理所有不高于当前版本的其他产品
            foreach (var p in products)
            {
                if (ReferenceEquals(p, current)) continue;
                if (p.Version <= current.Version) olds.Add(p);
            }
            return olds;
        }

        /// <summary>
        /// 判断快捷方式目标是否为我们的壳程序（DshWeb.exe）。
        /// 用于孤儿清理：只有指向 DshWeb.exe 的快捷方式才删除，用户自行创建的同名内容不受影响。
        /// </summary>
        internal static bool IsOurShortcutTarget(string? targetPath) =>
            !string.IsNullOrWhiteSpace(targetPath)
            && string.Equals(Path.GetFileName(targetPath), "DshWeb.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 首装全局安装预算策略（纯函数，契约测试锁定）：跨镜像源共享一个总预算，单次尝试另有上限；
    /// 剩余预算不足最小可尝试时长时放弃后续源——替代旧实现"每源 20 分钟 × N 源"的失控等待
    /// （v0.4.x 用户回归："一段时间后静默失败"的体验根源之一）。
    /// </summary>
    public static class ProvisionPolicy
    {
        /// <summary>低于该剩余时长即视为预算耗尽（发起必然超时的尝试没有意义）。</summary>
        public const long MinAttemptMs = 60_000;

        /// <summary>默认共享总预算（秒），供进度文案引用。</summary>
        public const int TotalBudgetSeconds = 600;

        /// <summary>
        /// 计算下一次安装尝试的超时上限（ms）：min(单次上限, 总预算-已耗用)；
        /// 剩余不足 MinAttemptMs 时原样返回负差值（调用方据此放弃）。
        /// </summary>
        public static long RemainingInstallTimeoutMs(long elapsedMs, long totalBudgetMs, long perAttemptCapMs)
        {
            var remain = totalBudgetMs - Math.Max(0, elapsedMs);
            if (remain < MinAttemptMs) return remain;
            return Math.Min(Math.Max(0, perAttemptCapMs), remain);
        }
    }

    /// <summary>
    /// 启动失败错误码映射策略（纯函数，Headless 可测）：首装全局安装失败时，
    /// StartService=false 会以通用 E2001（"缺少 start-dsh.vbs"）上报，与真实原因不符——
    /// 组合根据本映射改用 E1012 与安装失败详情展示（静默失败收口：用户看到的是真实根因）。
    /// </summary>
    public static class StartupFailurePolicy
    {
        public static (string Code, string Detail)? MapFirstRunInstallFailure(
            string? outcomeErrorCode, string? firstRunInstallError)
        {
            if (outcomeErrorCode != "E2001" || string.IsNullOrWhiteSpace(firstRunInstallError))
                return null;
            return ("E1012",
                "首次运行自动安装 dsh 组件失败。\n\n" + firstRunInstallError +
                "\n\n可检查网络/代理后重试，或在命令行手动执行：npm install -g @deepseek-ai/dsh");
        }
    }

    /// <summary>
    /// 系统通知（Toast）纯策略：AUMID 与通知 XML 构造。WinRT 交互（注册/发送）在
    /// Windows/SystemToast.cs，此处只放可契约测试的纯函数。
    /// [v0.4.1] 更新类通知从托盘气泡迁移到系统 Toast——不再依赖 NotifyIcon 托盘图标
    /// （此前非托盘常驻模式下托盘为 null，更新气泡被静默丢弃）。
    /// </summary>
    public static class ToastPolicy
    {
        /// <summary>未打包应用的 AUMID：与 HKCU\SOFTWARE\Classes\AppUserModelId 注册键同名。
        /// 固定值保证系统按同一来源聚合通知（设置里可按此名关闭）。</summary>
        public const string ToastAumid = "dsh-launcher";

        /// <summary>
        /// 构造 ToastText02 模板 XML（标题 + 正文两行）。title/body 做 XML 转义，
        /// 防止版本号等外部输入破坏结构（npm 版本串理论可控，但防御性转义零成本）。
        /// duration="long"：屏幕弹窗停留约 25 秒（默认 short ≈5 秒，用户来不及点击
        /// 触发更新——2026-08-22 用户回归反馈）。注意 ExpirationTime 控制的是操作
        /// 中心留存时长，与弹窗显示时长是两个维度。
        /// </summary>
        public static string BuildToastXml(string title, string body)
        {
            var esc = System.Security.SecurityElement.Escape;
            return "<toast duration=\"long\"><visual><binding template=\"ToastText02\">"
                 + $"<text id=\"1\">{esc(title ?? "")}</text>"
                 + $"<text id=\"2\">{esc(body ?? "")}</text>"
                 + "</binding></visual></toast>";
        }
    }
}
