using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace DshWeb;

/// <summary>
/// 版本更新检测（**预留接口，暂未接入 UI/启动流程**）：
/// - 本项目（dsh-launcher）：对比 GitHub Releases 最新 tag 与当前版本
/// - DeepSeek Harness（dsh）：对比 npm registry latest 与本地版本
/// 后续接入点：定时检查（如每日一次）+ 托盘气泡/设置页提示"有新版本"。
/// 上线前注意：GitHub API 匿名限流（60 次/小时/IP），检查频率要克制；
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
