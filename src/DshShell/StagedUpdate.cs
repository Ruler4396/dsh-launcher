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

    /// <summary>记录待应用版本（下载阶段成功后调用）。</summary>
    public static void MarkPending(string version)
    {
        if (_pendingPath.Length == 0 || string.IsNullOrWhiteSpace(version)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_pendingPath)!);
            File.WriteAllText(_pendingPath, JsonSerializer.Serialize(new
            {
                version,
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
            var (version, failCount) = ReadPending();
            if (string.IsNullOrWhiteSpace(version)) return;
            File.WriteAllText(_pendingPath, JsonSerializer.Serialize(new
            {
                version,
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                failCount = failCount + 1,
            }));
        }
        catch { /* 计数失败忽略（下次仍按旧值提示） */ }
    }

    /// <summary>读取待应用版本与失败次数；无记录/损坏返回 (null, 0)。</summary>
    public static (string? Version, int FailCount) ReadPending()
    {
        if (_pendingPath.Length == 0 || !File.Exists(_pendingPath)) return (null, 0);
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
            return (version, fail);
        }
        catch { return (null, 0); }
    }

    /// <summary>读取待应用版本；无记录/损坏返回 null（兼容旧调用）。</summary>
    public static string? ReadPendingVersion() => ReadPending().Version;

    /// <summary>清除待应用记录（应用成功或放弃时调用）。</summary>
    public static void ClearPending()
    {
        if (_pendingPath.Length == 0) return;
        try { if (File.Exists(_pendingPath)) File.Delete(_pendingPath); } catch { /* 清理失败忽略 */ }
    }
}
