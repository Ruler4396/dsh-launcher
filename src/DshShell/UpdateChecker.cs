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

    /// <summary>拉取 dsh（@deepseek-ai/dsh）npm 最新版本；失败返回 null。</summary>
    public static async Task<string?> FetchLatestDshVersionAsync(HttpClient http)
    {
        try
        {
            using var resp = await http.GetAsync(
                $"https://registry.npmjs.org/{Uri.EscapeDataString(DshNpmPackage)}/latest");
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
    /// 语义化版本比较：a &gt; b → 1，a == b → 0，a &lt; b → -1。
    /// 非法/空版本按 0.0.0 处理（缺失信息不产生"有新版本"的误报）。
    /// </summary>
    public static int CompareVersions(string? a, string? b)
    {
        if (!Version.TryParse(a, out var va)) va = new Version(0, 0);
        if (!Version.TryParse(b, out var vb)) vb = new Version(0, 0);
        return va.CompareTo(vb);
    }

    /// <summary>本地 dsh 版本：优先环境变量 DSH_VERSION，否则尝试 `dsh --version`（找不到返回 null）。</summary>
    public static string? ResolveLocalDshVersion()
    {
        try
        {
            var env = Environment.GetEnvironmentVariable("DSH_VERSION");
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            var psi = new System.Diagnostics.ProcessStartInfo("dsh", "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return null;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return output.Trim();
        }
        catch
        {
            return null;
        }
    }
}
