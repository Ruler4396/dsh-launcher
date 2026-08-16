using System.Text.Json;

namespace DshWeb;

/// <summary>
/// dsh 延迟应用更新（非侵入式，v0.3.0）：
/// - 本次会话：确认后仅下载（后台 npm pack 到 DataDir\staging），写 pending-update.json；
/// - 下次启动拉起服务前：应用（npm install -g 固定版本），绝不打断当前会话。
/// 版本固定为已检测到的具体版本（非 latest），消除"检测→应用"漂移。
/// </summary>
public static class StagedUpdate
{
    public const string Package = "@deepseek-ai/dsh";

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
            }));
        }
        catch { /* 记录失败：下次启动转 latest 也可接受 */ }
    }

    /// <summary>读取待应用版本；无记录/损坏返回 null。</summary>
    public static string? ReadPendingVersion()
    {
        if (_pendingPath.Length == 0 || !File.Exists(_pendingPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_pendingPath));
            return doc.RootElement.TryGetProperty("version", out var v)
                && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch { return null; }
    }

    /// <summary>清除待应用记录（应用成功或放弃时调用）。</summary>
    public static void ClearPending()
    {
        if (_pendingPath.Length == 0) return;
        try { if (File.Exists(_pendingPath)) File.Delete(_pendingPath); } catch { /* 清理失败忽略 */ }
    }
}