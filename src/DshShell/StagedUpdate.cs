using System.Text.Json;

namespace DshWeb;

/// <summary>
/// dsh 延迟应用更新（非侵入式，v0.3.0）：
/// - 本次会话：确认后仅下载（后台 npm pack 到 DataDir\staging），写 pending-update.json；
/// - 下次启动拉起服务前：应用（npm install -g 固定版本），绝不打断当前会话。
/// 版本固定为已检测到的具体版本（非 latest），消除"检测→应用"漂移。
/// v0.3.1：记录应用失败次数（failCount）——持续失败的更新降级为仅日志提示，
/// 避免每次启动重复打扰用户（质量治理：更新失败打扰降噪）。
/// </summary>
public static class StagedUpdate
{
    public const string Package = "@deepseek-ai/dsh";

    /// <summary>应用失败达到该次数后，启动气泡降级为仅日志（仍保留手动 npm 命令提示）。</summary>
    public const int MaxNotifyFailures = 2;

    private static string _pendingPath = "";

    public static void Init(string dataDir) => _pendingPath = Path.Combine(dataDir, "pending-update.json");

    /// <summary>记录待应用版本（下载阶段成功后调用）。tarball=本地安装包文件名（scoped 包
    /// npm pack 去 @ 和 /，形如 deepseek-ai-dsh-0.1.0-rc.7.tgz）；下次启动优先本地安装、
    /// 不现场拉取（"已下载完成"名副其实），tarball 缺失才回退线上。null 兼容旧记录。</summary>
    public static void MarkPending(string version, string? tarball = null)
    {
        if (_pendingPath.Length == 0 || string.IsNullOrWhiteSpace(version)) return;
        try
        {
            ShellLogic.AtomicWrite(_pendingPath, JsonSerializer.Serialize(new
            {
                version,
                tarball = string.IsNullOrWhiteSpace(tarball) ? null : tarball,
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                failCount = 0,
            }));
        }
        catch { /* 记录失败：下次启动转 latest 也可接受 */ }
    }

    /// <summary>应用失败时递增 failCount（用于气泡降级）。</summary>
    public static void MarkApplyFailed()
    {
        if (_pendingPath.Length == 0) return;
        try
        {
            var (version, failCount, tarball) = ReadPending();
            if (string.IsNullOrWhiteSpace(version)) return;
            ShellLogic.AtomicWrite(_pendingPath, JsonSerializer.Serialize(new
            {
                version,
                tarball = string.IsNullOrWhiteSpace(tarball) ? null : tarball,
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                failCount = failCount + 1,
            }));
        }
        catch { /* 计数失败忽略（下次仍按旧值提示） */ }
    }

    /// <summary>读取待应用记录（版本/失败次数/本地 tarball 名）；无记录/损坏返回 (null, 0, null)。</summary>
    public static (string? Version, int FailCount, string? Tarball) ReadPending()
    {
        if (_pendingPath.Length == 0 || !File.Exists(_pendingPath)) return (null, 0, null);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_pendingPath));
            var root = doc.RootElement;
            var version = root.TryGetProperty("version", out var v)
                && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
            var fail = root.TryGetProperty("failCount", out var f) && f.TryGetInt32(out var n)
                ? Math.Max(0, n)
                : 0;
            var tarball = root.TryGetProperty("tarball", out var t)
                && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            return (version, fail, tarball);
        }
        catch
        {
            // P1-4（质量治理）：损坏不静默——待应用更新记录失效要可诊断（此前静默按"无待应用"处理，
            // 用户"说好的自动更新去哪了"无从查证；按无记录处理不影响功能，仅补诊断）。
            Logger.Warn("pending-update.json is corrupt or unreadable; treating as no pending update",
                ctx: new { path = _pendingPath });
            return (null, 0, null);
        }
    }

    /// <summary>读取待应用版本；无记录/损坏返回 null（兼容旧调用）。</summary>
    public static string? ReadPendingVersion() => ReadPending().Version;

    /// <summary>
    /// 定位已下载的本地安装包（任务：真正"已下载完成"——应用时优先本地 tarball，不现场拉取）。
    /// 优先用 pending 记录的 tarball 文件名；缺失则按 scoped 包命名规则在 staging 兜底匹配。
    /// 找不到返回 null（调用方回退线上拉取，文案如实说明"将现场下载"）。
    /// </summary>
    public static string? LocateTarball(string? version, string? tarballName)
    {
        if (version is null) return null;
        var staging = _pendingPath.Length > 0 ? Path.GetDirectoryName(_pendingPath) : null;
        if (staging is null) return null;
        staging = Path.Combine(staging, "staging");
        if (!Directory.Exists(staging)) return null;
        // ① 优先 pending 记录的精确文件名
        if (!string.IsNullOrWhiteSpace(tarballName))
        {
            var exact = Path.Combine(staging, tarballName);
            if (File.Exists(exact)) return exact;
        }
        // ② 按命名规则兜底：deepseek-ai-dsh-{version}.tgz
        var fallback = Path.Combine(staging, $"deepseek-ai-dsh-{version}.tgz");
        if (File.Exists(fallback)) return fallback;
        // ③ staging 中模糊匹配该版本的 .tgz（防 npm pack 文件名大小写/后缀差异）
        foreach (var file in Directory.GetFiles(staging, "*.tgz"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Contains(version, StringComparison.OrdinalIgnoreCase)) return file;
        }
        return null;
    }

    /// <summary>清除待应用记录（应用成功或放弃时调用）。</summary>
    public static void ClearPending()
    {
        if (_pendingPath.Length == 0) return;
        try { if (File.Exists(_pendingPath)) File.Delete(_pendingPath); } catch { /* 清理失败忽略 */ }
    }

    /// <summary>staging 根目录（下载/预热缓存所在；无 pending 路径记录时返回 null）。</summary>
    public static string? StagingDir =>
        _pendingPath.Length > 0 ? Path.Combine(Path.GetDirectoryName(_pendingPath)!, "staging") : null;

    /// <summary>后台依赖预热临时目录（任务一：prefetch_temp）——预热在 staging 下进行，应用成功后整体清理。</summary>
    public static string? PrefetchTempDir =>
        StagingDir is null ? null : Path.Combine(StagingDir, "prefetch_temp");
}
