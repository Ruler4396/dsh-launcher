using System.Diagnostics;
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

    /// <summary>launcher 最新版下载页（版本信息弹窗 / 安全更新提示共用，单一事实源）。
    /// [2026-09] 此前两处（ShowPortableUpdateDialog）硬编码同一 URL 字符串，统一收口于此。</summary>
    public const string LauncherLatestReleaseUrl = "https://github.com/" + LauncherRepo + "/releases/latest";

    /// <summary>
    /// 当前壳版本：发布构建由 CI 注入真实版本（AssemblyInformationalVersion ≥ 0.x）；
    /// **本地/开发构建 .NET SDK 未设 Version 时默认 1.0.0**（本仓库版本线 0.x，1.0.0 必为默认值）——
    /// 若显示 1.0.0 会误导"当前版本已是最新/版本号异常"（2026-09 用户反馈），故回退
    /// 读取 git 最近 tag（有界探测，静默失败保持 null → 展示"未知"）。非法时回退 null。
    /// </summary>
    public static readonly string? CurrentLauncherVersion = ResolveLauncherVersion();

    /// <summary>剥离 AssemblyInformationalVersion 的 +metadata 尾段；SDK 默认 1.0.0 → null（触发 git 回退）。</summary>
    internal static string? StripDevDefaultVersion(string? informationalVersion)
    {
        var v = informationalVersion?.Split('+')[0];
        if (string.IsNullOrWhiteSpace(v) || string.Equals(v.Trim(), "1.0.0", StringComparison.Ordinal))
            return null;
        return v.Trim();
    }

    private static string? ResolveLauncherVersion()
    {
        var info = typeof(UpdateChecker).Assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .Cast<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;
        var released = StripDevDefaultVersion(info);
        if (released is not null) return released; // 发布构建（CI 注入真实版本号）
        return ProbeGitDescribeVersion();          // 开发构建：最近 git tag（如 v0.4.3 → 0.4.3）
    }

    /// <summary>
    /// 从可执行目录向上找仓库根（含 .git）后执行 git describe --tags --abbrev=0（最近 tag，去 v 前缀）。
    /// 进程三必须合规：stdout/stderr 异步排空 + 限时等待 + 超时 Kill(entireProcessTree)（同 DshDiscovery 版本探测）。
    /// 仅开发 checkout（无 .git 的安装版根本不会走到这里）调用；失败静默返回 null。
    /// </summary>
    private static string? ProbeGitDescribeVersion()
    {
        try
        {
            var repoRoot = FindRepoRoot();
            if (repoRoot is null) return null;
            var psi = new ProcessStartInfo("git", "describe --tags --abbrev=0")
            {
                WorkingDirectory = repoRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outputTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(2000); } catch { /* 尽力回收 */ }
                Logger.Warn("git describe probe timed out; launcher version stays unknown");
                return null;
            }
            var tag = outputTask.Result.Trim(); // 进程已退出 → 管道已关闭，任务必已完成
            return string.IsNullOrWhiteSpace(tag) ? null : tag.TrimStart('v', 'V');
        }
        catch (Exception ex)
        {
            Logger.Warn($"git describe probe failed: {ex.Message}");
            return null;
        }
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 10 && dir is not null; i++, dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git"))) return dir.FullName;
        }
        return null;
    }

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

    // ==================== [2026-08-29 安全通知可达性] 多出口回退链 ====================

    /// <summary>会话级粘住的成功网络出口：null=未知，""=直连/系统代理，其他=代理 URI。</summary>
    private static volatile string? _workingExit;

    /// <summary>用户 .npmrc 路径（版本检查 HTTP 端跟随用户真实 registry 配置）。</summary>
    internal static string UserNpmrcPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npmrc");

    /// <summary>读用户 .npmrc 的 registry=（纯解析在 ShellLogic.NpmRegistryPolicy；读失败/无值 → null）。</summary>
    internal static string? ReadUserNpmrcRegistry()
    {
        try
        {
            if (!System.IO.File.Exists(UserNpmrcPath)) return null;
            return ShellLogic.NpmRegistryPolicy.ParseNpmrcRegistry(System.IO.File.ReadAllText(UserNpmrcPath));
        }
        catch { return null; }
    }

    /// <summary>dsh 版本检查的 registry 候选序（去重）：env → 用户 .npmrc → npmmirror → npmjs。
    /// 注：裸 npmjs 直连在部分网络不可达，靠出口回退（本地代理）兜底。</summary>
    internal static string[] DshRegistryCandidates(string? envRegistry, string? npmrcRegistry)
    {
        var list = new System.Collections.Generic.List<string>();
        void Add(string? uri)
        {
            if (string.IsNullOrWhiteSpace(uri)) return;
            var norm = uri.Trim().TrimEnd('/');
            if (!list.Contains(norm, StringComparer.OrdinalIgnoreCase)) list.Add(norm);
        }
        Add(envRegistry);
        Add(npmrcRegistry);
        Add(ShellLogic.NpmRegistryPolicy.FallbackMirror);
        Add("https://registry.npmjs.org");
        return list.ToArray();
    }

    /// <summary>按出口序构造 HttpClient 列表（上次成功出口优先，随后直连/系统代理，再存活本地代理）。
    /// 每个 HttpClient 用完即弃、由调用方 Dispose。</summary>
    internal static System.Collections.Generic.List<(System.Net.Http.HttpClient Client, string? ExitKey)> CreateUpdateHttpClients(
        int timeoutSec, System.Collections.Generic.IReadOnlyList<string> exitCandidates)
    {
        var list = new System.Collections.Generic.List<(System.Net.Http.HttpClient, string?)>();
        var seen = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddExit(string? key)
        {
            if (key is null) return;
            var norm = key.Trim();
            if (norm == "direct") norm = "";
            if (!seen.Add(norm)) return;
            list.Add((MakeUpdateClient(norm, timeoutSec), norm));
        }
        if (_workingExit is not null) AddExit(_workingExit);
        foreach (var c in exitCandidates) AddExit(c);
        // 本地代理存活探测（回环连接毫秒级；失败不上链）
        foreach (var p in ShellLogic.UpdateProxyPolicy.LocalProxyCandidates())
        {
            if (seen.Contains(p)) continue;
            if (ShellLogic.UpdateProxyPolicy.LocalProxyAlive(p))
            {
                seen.Add(p);
                list.Add((MakeUpdateClient(p, timeoutSec), p));
            }
        }
        return list;
    }

    private static System.Net.Http.HttpClient MakeUpdateClient(string? proxyUri, int timeoutSec)
    {
        var handler = new System.Net.Http.HttpClientHandler { UseProxy = true };
        if (!string.IsNullOrEmpty(proxyUri)) handler.Proxy = new System.Net.WebProxy(new Uri(proxyUri));
        var client = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSec) };
        // [2026-08-29 403 回归] GitHub API 强制要求 User-Agent：.NET 默认不发 → 所有出口一律
        // 403 "missing User-Agent" → 更新检查全链路静默 null。版本号可随发布注入。
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "dsh-launcher/" + (CurrentLauncherVersion ?? "0.0.0"));
        return client;
    }

    /// <summary>launcher 安全更新检查回退版：多出口依次尝试，首个成功即粘住该出口。</summary>
    public static async Task<LauncherRelease?> FetchLatestLauncherReleaseFallbackAsync()
    {
        var envProxy = Environment.GetEnvironmentVariable("https_proxy")
            ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var candidates = ShellLogic.UpdateProxyPolicy.ExitCandidates(envProxy);
        foreach (var (client, key) in CreateUpdateHttpClients(15, candidates))
        {
            try
            {
                Logger.Info($"update network: launcher check attempting exit={(key is { Length: > 0 } ? key : "direct")}");
                var r = await FetchLatestLauncherReleaseAsync(client);
                if (r is not null)
                {
                    _workingExit = key;
                    var exitLabel = key is { Length: > 0 } ? key : "direct";
                    Logger.Info($"update network: launcher check exit={exitLabel} version={r.Version}");
                    return r;
                }
                Logger.Warn($"update network: launcher check null via exit={(key is { Length: > 0 } ? key : "direct")}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"update network: launcher check failed via exit={key ?? "?"} ({ex.Message})");
            }
            finally { client.Dispose(); }
        }
        return null;
    }

    /// <summary>dsh 版本检查回退版：registry 候选 × 网络出口依次尝试。</summary>
    public static async Task<string?> FetchLatestDshVersionFallbackAsync()
    {
        var envProxy = Environment.GetEnvironmentVariable("https_proxy")
            ?? Environment.GetEnvironmentVariable("HTTPS_PROXY");
        var exits = ShellLogic.UpdateProxyPolicy.ExitCandidates(envProxy);
        foreach (var registry in DshRegistryCandidates(
                     Environment.GetEnvironmentVariable("DSH_NPM_REGISTRY"), ReadUserNpmrcRegistry()))
        {
            foreach (var (client, key) in CreateUpdateHttpClients(15, exits))
            {
                try
                {
                    using (var resp = await client.GetAsync($"{registry}/{Uri.EscapeDataString(DshNpmPackage)}/latest"))
                    {
                        if (!resp.IsSuccessStatusCode) continue;
                        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                        if (doc.RootElement.TryGetProperty("version", out var v) && v.GetString() is { Length: > 0 } version)
                        {
                            _workingExit = key;
                            var exitLabel = key is { Length: > 0 } ? key : "direct";
                            Logger.Info($"update network: dsh check ok via registry={registry} exit={exitLabel} version={version}");
                            return version;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Warn($"update network: dsh check failed ({registry} / {key ?? "?"}): {ex.Message}");
                }
                finally { client.Dispose(); }
            }
        }
        return null;
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
