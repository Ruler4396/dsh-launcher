using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace DshWeb;

/// <summary>
/// 与 UI 无关的纯策略逻辑（文件名推导、权限策略、弹窗分类）。
/// 独立成类以便单元测试覆盖；Program.cs 只负责 UI 与事件接线。
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

    /// <summary>权限策略：自动放行的权限项（插件/DSH 依赖），其余保持默认拒绝。</summary>
    internal static bool IsAutoGrantedPermission(CoreWebView2PermissionKind kind) =>
        kind is CoreWebView2PermissionKind.Notifications
            or CoreWebView2PermissionKind.ClipboardRead
            or CoreWebView2PermissionKind.Autoplay
            or CoreWebView2PermissionKind.MultipleAutomaticDownloads
            or CoreWebView2PermissionKind.PersistentStorage;

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

    /// <summary>弹窗 URL 分类：外部链接 / 同源弹窗 / 保持默认。</summary>
    internal static PopupTarget ClassifyPopup(string? rawUri)
    {
        if (!Uri.TryCreate(rawUri, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return PopupTarget.Default;
        return uri.Host is ("127.0.0.1" or "localhost") ? PopupTarget.Internal : PopupTarget.External;
    }

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
            ? $"dsh-{DateTime.Now:yyyyMMddHHmmss}"
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
    /// 从 WM_NCHITTEST 的 64 位 lParam 拆出屏幕坐标：低 16 位有符号 = X，高 16 位有符号 = Y。
    /// 左侧/上方副屏为负坐标，直接 (int)lParam 会抛 OverflowException（B1 修复）。
    /// </summary>
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

    internal static (short X, short Y) SplitLParam(long lParam)
        => ((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));

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
    /// 判断快捷方式目标是否为我们的壳程序（DshWeb.exe）。
    /// 用于孤儿清理：只有指向 DshWeb.exe 的快捷方式才删除，用户自行创建的同名内容不受影响。
    /// </summary>
    internal static bool IsOurShortcutTarget(string? targetPath) =>
        !string.IsNullOrWhiteSpace(targetPath)
        && string.Equals(Path.GetFileName(targetPath), "DshWeb.exe", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// 服务就绪契约（C3，P1-6）：端口有 HTTP 应答即视为就绪。**任何** HTTP 响应（含 4xx/5xx）
    /// 都算"有服务在应答"——dsh 前端监听后可能还需数十秒才提供 HTTP，只探 TCP 会提前"成功"
    /// 导致主窗白屏（历史"要二次点击"根因）；网络异常/超时/拒绝连接 → 未就绪。
    /// 契约测试锁定此语义，防上游 dsh 行为变更无声破坏（FakeHttpMessageHandler 注入，不碰网络）。
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

    /// <summary>端口可连（TCP）契约（C3 前半段）：connect 成功即 true；失败/超时 false。</summary>
    internal static bool PortOpen(string host, int port)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            c.Connect(host, port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>异步端口探测（v0.4.2 卡顿修复）：ConnectAsync 不再阻塞调用线程；3s 超时兜底。
    /// 与 <see cref="PortOpen"/> 语义一致（契约 C3），仅异步化——ServiceManager 轮询使用。</summary>
    internal static async Task<bool> PortOpenAsync(string host, int port, CancellationToken ct = default)
    {
        try
        {
            using var c = new System.Net.Sockets.TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));
            await c.ConnectAsync(host, port, timeoutCts.Token).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false; // 探测失败 = 端口未开（预期操作失败，非异常）
        }
    }

    /// <summary>读取日志文件尾部若干行（用于失败弹窗里直接展示原因）；大文件不整读（流式 + 受限队列）。
    /// 读取失败返回空列表。P1-1（质量治理）：改用 FileShare.ReadWrite 共享读——运行中的 dsh 服务以
    /// cmd >> 重定向持有 dsh.log（独占写共享），默认 FileShare.Read 会被拒（--diagnose 曾因此失败）；
    /// 与 <see cref="ReadLinesShared"/> 共用同一读取实现，消除与 DiagnoseExport.TailLines 的双实现。</summary>
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

    /// <summary>dsh 服务的停留模式（由 dsh-launcher-lifetime 插件写入 settings.json，壳执行）。</summary>
    internal enum ServiceLifetime
    {
        /// <summary>常驻：服务一直运行，关窗/托盘退出都不停。</summary>
        AlwaysOn = 0,
        /// <summary>托盘驻留：关窗最小化到托盘，托盘"退出"才停服务并退出。</summary>
        Tray = 1,
        /// <summary>跟随窗口：关闭主窗口即停止服务并退出（最省内存）。</summary>
        FollowWindow = 2,
    }

    /// <summary>启动早期待应用更新的处理动作（矩阵 U2，v0.4.0 T2）。</summary>
    internal enum PendingUpdateAction
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

    /// <summary>
    /// FormClosing 托盘拦截决策（矩阵 L1）：是否拦截关闭、隐藏到托盘。
    /// 决策 = 生命周期模式为"托盘驻留" 且 未请求真正退出。
    /// 铁律（0.1.10 血泪）：此拦截判定必须**先于** WebView2 销毁——WebView2 一旦 Dispose，
    /// 从托盘唤起时控件已销毁，窗口只剩空白（历史上销毁在拦截之前 → 必然白屏）。
    /// 调用方保证语句顺序：ShouldInterceptCloseToTray → (拦截则 return) → 才走销毁/退出路径。
    /// </summary>
    internal static bool ShouldInterceptCloseToTray(ServiceLifetime mode, bool trayExitRequested)
        => mode == ServiceLifetime.Tray && !trayExitRequested;

    /// <summary>
    /// 关窗/托盘退出时**是否停止 dsh 服务**决策（矩阵 M1，v0.4.0 T1）：
    /// - FollowWindow 且服务由本壳管理（本次拉起**或**接管了上次残留）且非外部托管 → true；
    /// - AlwaysOn（服务常驻）→ false；Tray（托盘退出才停）→ false；external 托管 → 恒 false。
    /// 关键语义："接管即负责"——TryAdoptOrphanService 成功接管后 shellManaged=true，
    /// 关窗必须停掉被接管的服务，否则 node 常驻（issue：关窗后 node 未被杀、重开秒进复用旧服务）。
    /// </summary>
    internal static bool ShouldStopServiceOnClose(ServiceLifetime mode, bool externallyManaged, bool shellManaged)
        => mode == ServiceLifetime.FollowWindow && shellManaged && !externallyManaged;

    /// <summary>
    /// 启动早期"待应用更新"处理决策（矩阵 U2，v0.4.0 T2）：
    /// 1. pending 且端口关 → ApplyNow（服务未运行，直接应用，保持 v0.3 行为）；
    /// 2. pending 且端口开且运行版本 != 待应用版本 → PromptRestart（服务在跑，不能现场换版本，
    ///    一次性询问[立即重启应用][稍后]）；
    /// 3. pending 且端口开且运行版本 == 待应用版本 → ClearPending（已应用但未清账的历史残留）；
    /// 4. 无 pending → None。
    /// 端口开着时绝不允许静默跳过——否则"下载成功→重开又弹更新"死循环（根因 A）。
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

    /// <summary>PID 身份校验（防复用误杀，质量治理 P1-2）：pid 文件里的 PID 可能被系统
    /// 复用给无关进程——杀进程前必须确认该 PID 确为 dsh 服务（node 进程）。
    /// dsh 服务由 wscript→cmd→node 链路拉起，监听端口的进程名是 node（便携/系统均为）。
    /// 校验失败（进程不存在/名字不符）返回 false，调用方不得执行 taskkill。</summary>
    internal static bool IsLikelyDshService(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return string.Equals(p.ProcessName, "node", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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
        var mode = ParseLifetimeMode(settingsJson, fallback);
        // 值合法则保留（不 purge）；键在但值非法/越界 → 回退并提示清理。
        var shouldPurge = HasServiceLifetimeKey(settingsJson) && !IsValidLifetimeValue(settingsJson);
        return (mode, shouldPurge);
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
}
