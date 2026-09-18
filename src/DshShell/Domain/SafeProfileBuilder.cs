using System.Security.Cryptography;
using System.Text.Json;

namespace DshWeb.Domain;

/// <summary>安全模式隔离 profile 的降级梯级（ADR-022 分级策略）。</summary>
public enum SafeProfileTier
{
    /// <summary>第一级（默认入口）：保留用户 bundle 中全部 @deepseek-ai 核心 + dsh 官方 web 核心，
    /// 剥离所有非 @deepseek-ai 第三方/本地插件。</summary>
    Tier1KeepDeepSeekCore = 1,
    /// <summary>第二级（兜底底线）：仅 dsh 官方 web 模板核心（dsh-base + dsh-web-app）。</summary>
    Tier2Minimal = 2,
}

/// <summary>
/// 安全模式的隔离 profile 构建器。
///
/// 【核心契约】：在不改动用户任何文件（<c>~/.dsh/profiles/*</c>）的前提下，生成一个
/// "剥离导致崩溃的第三方插件、保留 dsh 核心"的隔离 profile（<c>.dsh-safe</c>），
/// 供安全模式用 <c>dsh --profile .dsh-safe</c> 正常启动（其余行为完全交给 dsh 本体）。
///
/// 【分级策略】：默认第一级（Tier1）保留全部 @deepseek-ai 核心 bundle；若第一级启动后
/// 物理证据（readiness + 崩溃签名）不过关，降到第二级（Tier2）最小核心。
/// 用户原文件只读不写（零污染由 <see cref="CaptureUserProfilesHash"/> 证据把关）。
/// </summary>
public sealed class SafeProfileBuilder
{
    /// <summary>隔离 profile 的名字（--profile 只收 name，无分隔符）。</summary>
    public const string SafeProfileName = ".dsh-safe";

    /// <summary>dsh 官方 web profile 模板核心（dsh-app-boot 的 PROFILE_TEMPLATES.web）。</summary>
    public static readonly IReadOnlyList<string> WebCoreMinimal = new[]
    {
        "@deepseek-ai/dsh-base",
        "@deepseek-ai/dsh-web-app",
    };

    /// <summary>@deepseek-ai scope 前缀：判定"核心 bundle"的依据。</summary>
    private const string DeepSeekScope = "@deepseek-ai/";

    private readonly string _dshHome;
    private readonly string _userProfilesDir;

    /// <summary>构造。文件系统操作全部真实调用（铁律：禁止 Mock OS 边界）。</summary>
    public SafeProfileBuilder(string dshHome)
    {
        _dshHome = dshHome;
        _userProfilesDir = Path.Combine(dshHome, "profiles");
    }

    /// <summary>隔离 profile 的包 JSON 路径。</summary>
    public string SafeProfilePackageJson => Path.Combine(_userProfilesDir, SafeProfileName, "package.json");

    /// <summary>隔离 profile 目录。</summary>
    public string SafeProfileDir => Path.Combine(_userProfilesDir, SafeProfileName);

    /// <summary>是否已存在隔离 profile。</summary>
    public bool SafeProfileExists() => File.Exists(SafeProfilePackageJson);

    /// <summary>
    /// 构建（或重建）指定梯级的隔离 profile。幂等：每次调用都重写 .dsh-safe manifest，
    /// 但**用户文件绝不触碰**。返回是否成功。
    /// </summary>
    public bool Build(SafeProfileTier tier = SafeProfileTier.Tier1KeepDeepSeekCore)
    {
        try
        {
            var safeDir = SafeProfileDir;
            Directory.CreateDirectory(safeDir);
            var bundles = ResolveBundles(tier);
            var manifest = new Dictionary<string, object?>
            {
                ["name"] = "dsh-profile-safe",
                ["private"] = true,
                ["dsh"] = new Dictionary<string, object?>
                {
                    ["profile"] = new Dictionary<string, object?> { ["bundles"] = bundles.ToArray() },
                }
            };
            // [铁律] 状态文件原子写：.tmp + File.Move，防中途崩溃留下损坏的 package.json
            var tmp = SafeProfilePackageJson + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(manifest, new JsonSerializerOptions
            {
                WriteIndented = true,
            }) + "\n");
            if (File.Exists(SafeProfilePackageJson)) File.Delete(SafeProfilePackageJson);
            File.Move(tmp, SafeProfilePackageJson);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 解析该梯级应保留的 bundles：Tier2 直接返回最小核心；
    /// Tier1 读用户 profiles/web/package.json 的 dsh.profile.bundles，
    /// 只保留 @deepseek-ai/ 前缀的核心 bundle（第三方/本地插件全部剥离），去重后
    /// 确保 WebCoreMinimal 永远在列（插在最前）。用户文件只读。
    /// </summary>
    internal IReadOnlyList<string> ResolveBundles(SafeProfileTier tier)
    {
        if (tier == SafeProfileTier.Tier2Minimal) return WebCoreMinimal;
        var webPkg = Path.Combine(_userProfilesDir, "web", "package.json");
        var kept = new List<string>();
        if (File.Exists(webPkg))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(webPkg));
                if (doc.RootElement.TryGetProperty("dsh", out var dsh)
                    && dsh.TryGetProperty("profile", out var profile)
                    && profile.TryGetProperty("bundles", out var bundles)
                    && bundles.ValueKind == JsonValueKind.Array)
                {
                    foreach (var b in bundles.EnumerateArray())
                    {
                        var name = b.GetString();
                        // 只保留 @deepseek-ai 核心；第三方/相对路径插件一律剥离（安全模式语义）
                        if (!string.IsNullOrWhiteSpace(name) && name.StartsWith(DeepSeekScope, StringComparison.Ordinal))
                            kept.Add(name);
                    }
                }
            }
            catch
            {
                // 用户 package.json 损坏 → 视为空列表，回落到最小核心（绝不抛出中断启动）
            }
        }
        var merged = kept.Distinct(StringComparer.Ordinal).ToList();
        foreach (var core in WebCoreMinimal)
            if (!merged.Contains(core, StringComparer.Ordinal)) merged.Insert(0, core);
        return merged;
    }

    /// <summary>对用户 profiles 目录做递归哈希快照（零污染证据）。安全 profile 自身排除。</summary>
    public static Dictionary<string, byte[]> CaptureUserProfilesHash(string userProfilesDir)
    {
        var result = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(userProfilesDir)) return result;
        foreach (var file in Directory.EnumerateFiles(userProfilesDir, "*", SearchOption.AllDirectories))
        {
            if (file.Replace('\\', '/').Contains("/.dsh-safe/", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                using var sha = SHA256.Create();
                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                result[Path.GetRelativePath(userProfilesDir, file)] = sha.ComputeHash(fs);
            }
            catch { /* 锁文件跳过 */ }
        }
        return result;
    }

    /// <summary>校验用户 profiles 目录两次快照间零污染（文件未增删改）。</summary>
    public static bool UsersProfilesUntouched(Dictionary<string, byte[]> before, string userProfilesDir)
    {
        var after = CaptureUserProfilesHash(userProfilesDir);
        if (before.Count != after.Count) return false;
        foreach (var file in before)
        {
            if (!after.TryGetValue(file.Key, out var other) || !file.Value.SequenceEqual(other)) return false;
        }
        return true;
    }

    /// <summary>
    /// [issue #28-4] 清理前体检：目录里是否只有壳自己写的产物、清单是否仍是壳会生成的内容。
    /// 任一为假都不得递归删除——见 <see cref="ShellLogic.SafeProfileCleanupPolicy"/>。
    ///
    /// 为什么必须体检：用户在安全模式会话里装插件时，pnpm 会把 bundle 声明与 node_modules
    /// 实体写进 <c>.dsh-safe</c> **本身**；旧实现每次正常启动无条件
    /// <c>Directory.Delete(recursive: true)</c>，等于物理销毁用户刚装的东西。
    /// 读取失败一律返回"不可删"（保守方向：宁可留下一个陈旧目录）。
    /// </summary>
    public (bool OnlyLauncherArtifacts, bool ManifestUnchanged) InspectForCleanup(SafeProfileTier tier)
    {
        try
        {
            if (!Directory.Exists(SafeProfileDir)) return (true, true);
            // 壳的 Build 只写一个 package.json（+ 原子写的 .tmp 中间态）。出现任何子目录
            // （node_modules 是主要形态）或其他文件，都不是壳写的。
            var onlyArtifacts = Directory.GetDirectories(SafeProfileDir).Length == 0;
            if (onlyArtifacts)
            {
                foreach (var f in Directory.GetFiles(SafeProfileDir))
                {
                    var name = Path.GetFileName(f);
                    var isLauncherArtifact = string.Equals(name, "package.json", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
                    if (!isLauncherArtifact) { onlyArtifacts = false; break; }
                }
            }
            var manifest = File.Exists(SafeProfilePackageJson)
                ? File.ReadAllText(SafeProfilePackageJson)
                : null;
            return (onlyArtifacts,
                ShellLogic.SafeProfileCleanupPolicy.SafeProfileManifestEquivalent(manifest, ResolveBundles(tier)));
        }
        catch (Exception ex)
        {
            Logger.Warn($"safe profile cleanup inspection failed; keeping directory (never delete blind): {ex.Message}");
            return (false, false);
        }
    }

    /// <summary>
    /// 清理隔离 profile（正常模式启动时调用：上次安全模式遗留的 .dsh-safe 已无用）。
    /// 只删 .dsh-safe 目录本身，用户 profiles/* 绝不触碰。幂等。
    /// 【调用方义务】必须先经 <see cref="InspectForCleanup"/> +
    /// <see cref="ShellLogic.SafeProfileCleanupPolicy.ShouldDelete"/> 判定，不得直接调用本方法。
    /// </summary>
    public void Cleanup()
    {
        try
        {
            if (SafeProfileExists())
            {
                Directory.Delete(SafeProfileDir, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响启动（下次启动再试）
        }
    }
}
