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
    /// 供 DSH_WEB_URL 环境变量覆盖目标地址（免重建）时使用。
    /// </summary>
    internal static (string Url, int Port) ResolveTarget(string? envUrl)
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

    /// <summary>已安装的 dsh-launcher 产品（ProductCode + 版本）。</summary>
    public sealed record InstalledDsh(string ProductCode, Version Version);

    /// <summary>
    /// 枚举注册表中已安装的 dsh-launcher 产品（HKLM / HKLM WOW6432 / HKCU / HKCU WOW6432）。
    /// 供升级场景识别 per-user 旧版本（0.1.0-0.1.5）——MSI 的跨作用域 MajorUpgrade 在标准
    /// 机器上找不到 HKCU 里的旧版，壳程序据此提示用户提权卸载（提权卸载不触发 Config.Msi 1926）。
    /// </summary>
    internal static List<InstalledDsh> ReadInstalledDshProducts()
    {
        var result = new List<InstalledDsh>();
        var roots = new[]
        {
            Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            Registry.LocalMachine.OpenSubKey(@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"),
            Registry.CurrentUser.OpenSubKey(@"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var root in roots)
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
