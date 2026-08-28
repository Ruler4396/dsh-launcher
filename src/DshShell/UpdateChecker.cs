using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace DshWeb;

/// <summary>
/// 版本更新检测：
/// - 本项目（dsh-launcher）：对比 GitHub Releases 最新 tag 与当前版本
/// - DeepSeek Harness（dsh）：对比 npm registry latest 与本地版本
/// 已接入：壳启动后异步检查 dsh 新版本 → 托盘气泡提示 → 一键 npm 更新（见 Program.cs
/// ScheduleDshUpdateCheck）。注意：GitHub API 匿名限流（60 次/小时/IP），检查频率要克制；
/// 失败要静默（网络/限流都不该打扰用户）。
/// </summary>
public static class UpdateChecker
{
    public const string LauncherRepo = "Ruler4396/dsh-launcher";
    public const string DshNpmPackage = "@deepseek-ai/dsh";

    /// <summary>当前壳版本：取自程序集信息版本（发布时由构建注入），非法时回退 null。</summary>
    public static readonly string? CurrentLauncherVersion =
        typeof(UpdateChecker).Assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .Cast<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?
            .InformationalVersion
            ?.Split('+')[0];

    /// <summary>拉取本项目 GitHub Releases 最新版本号（去掉 v 前缀）；失败返回 null。</summary>
    public static async Task<string?> FetchLatestLauncherVersionAsync(HttpClient http)
    {
        try
        {
            using var resp = await http.GetAsync($"https://api.github.com/repos/{LauncherRepo}/releases/latest");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("tag_name", out var tag)
                ? tag.GetString()?.TrimStart('v')
                : null;
        }
        catch
        {
            return null; // 网络失败静默
        }
    }

    /// <summary>GitHub 最新 Release 信息：版本号 + 是否安全/重要更新
    /// （约定：Release body 含 "SECURITY" 或 tag 含 "-sec" 标记为安全更新）。</summary>
    public sealed record LauncherRelease(string Version, bool IsSecurity);

    /// <summary>拉取本项目 GitHub 最新 Release 并判断是否安全更新；失败返回 null。</summary>
    public static async Task<LauncherRelease?> FetchLatestLauncherReleaseAsync(HttpClient http)
    {
        try
        {
            using var resp = await http.GetAsync($"https://api.github.com/repos/{LauncherRepo}/releases/latest");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tag)) return null;
            var version = tag.GetString()?.TrimStart('v');
            if (string.IsNullOrEmpty(version)) return null;
            var body = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var isSecurity = body.Contains("SECURITY", StringComparison.OrdinalIgnoreCase)
                || version.Contains("-sec", StringComparison.OrdinalIgnoreCase);
            return new LauncherRelease(version, isSecurity);
        }
        catch
        {
            return null; // 网络失败静默
        }
    }

    /// <summary>npm registry 基址：优先 DSH_NPM_REGISTRY 环境变量（沙盒/测试覆盖），
    /// 未设置时回退 https://registry.npmjs.org（生产路径）。</summary>
    internal static string NpmRegistryBase
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("DSH_NPM_REGISTRY");
            return !string.IsNullOrWhiteSpace(env) ? env.TrimEnd('/') : "https://registry.npmjs.org";
        }
    }

    /// <summary>拉取 dsh（@deepseek-ai/dsh）npm 最新版本；失败返回 null。</summary>
    public static async Task<string?> FetchLatestDshVersionAsync(HttpClient http)
    {
        try
        {
            using var resp = await http.GetAsync(
                $"{NpmRegistryBase}/{Uri.EscapeDataString(DshNpmPackage)}/latest");
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
        }
        catch
        {
            return null; // 网络失败静默
        }
    }

    /// <summary>
    /// 语义化版本比较（完整 SemVer，含 prerelease）：a &gt; b → 1，a == b → 0，a &lt; b → -1。
    /// 非法/空版本按 0.0.0 处理（缺失信息不产生"有新版本"的误报）。
    ///
    /// v0.4.0 修复：dsh 实际以 SemVer prerelease 发布（如 0.1.0-rc.7），旧实现用 Version.TryParse
    /// 对含 '-' 的版本解析失败 → 双方都落成 0.0.0 → 永远检测不到 rc7 更新（rc6→rc7 无提示的根因）。
    /// 【F1 统一】v0.4.x 起委托 ShellLogic.VersionPolicy 全系统唯一实现——历史教训：
    /// 本方法修复 prerelease 比较时，DshDiscovery（发现层）仍持旧序数比较器，
    /// "更新检测判对了、运行时挑选仍判反"，两套比较器漂移即"更新成功但永远启动旧版"。
    /// </summary>
    public static int CompareVersions(string? a, string? b)
        => ShellLogic.VersionPolicy.CompareVersions(a, b);

    /// <summary>
    /// 本地 dsh 版本：委托 DshDiscovery 统一发现（与启动链同源，ADR-024）。
    /// 返回 Identity.Version（GlobalNpm/SelfContained 时为实际版本，NpxCache 时可能为 null）。
    ///
    /// 【身份统一】此前本方法独立执行 cmd /c dsh —version，仅检测全局 npm 安装，
    /// 与服务启动的回退链脱节 → "更新了全局 npm 包，但实际运行的是 npx 缓存"的幽灵 Bug。
    /// 现统一委托 DshDiscovery.DiscoverCurrentRuntime()，确保检查与启动同源。
    /// </summary>
    public static string? ResolveLocalDshVersion()
        => DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime().Version;
}
