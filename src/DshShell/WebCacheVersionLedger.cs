using System.Text.Json;

namespace DshWeb;

/// <summary>
/// WebView2 缓存失效的版本账本（v0.4.5）：记录"上次清理决策时见到的 dsh 版本"，供
/// 版本变更的一次性磁盘缓存清理（<see cref="ShellLogic.CacheInvalidationPolicy"/>）判定基线。
///
/// 语义与安全铁律：
/// - 无文件（首次运行/壳升级后首跑）→ Read 返回 null → 决策层判定"不清"，
///   但组合根在当次启动仍会 Write(currentVersion) 建立基线，此后版本变化即触发清理；
/// - **Write(null) 是 API 级禁止操作**（直接拒绝）：一次版本探测失败绝不能抹掉既有基线，
///   否则后续真实版本变更会因"无基线"而漏清缓存——宁可多清一次，不可漏清一次；
/// - 任何读失败只降级（Warn + 返回 null），绝不抛异常打断启动链路；
/// - 写入走 <see cref="ShellLogic.FileSystemPolicy.AtomicWrite"/>（.tmp + File.Move，铁律），
///   崩溃不留半截 JSON；清理成功后写（先清后写），崩溃重跑按旧基线再清一次，幂等无害。
/// 文件：DataDir\webcache-version.json（{ version, at }，与 skipped-update.json 同型）。
/// </summary>
public static class WebCacheVersionLedger
{
    private static string _ledgerPath = "";

    public static void Init(string dataDir) => _ledgerPath = Path.Combine(dataDir, "webcache-version.json");

    /// <summary>读取上次记录的 dsh 版本；无记录/损坏 → null（保守：视为无基线，决策层据此不清）。</summary>
    public static string? Read()
    {
        if (_ledgerPath.Length == 0 || !File.Exists(_ledgerPath)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_ledgerPath));
            return doc.RootElement.TryGetProperty("version", out var v)
                && v.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()!.Trim()
                : null;
        }
        catch
        {
            Logger.Warn("webcache-version.json is corrupt or unreadable; treating as no baseline (no cache clear)",
                ctx: new { path = _ledgerPath });
            return null;
        }
    }

    /// <summary>
    /// 记录当前 dsh 版本（组合根在"当前版本可判"时调用，通常在清理决策后）。
    /// 空白/null 直接拒绝（不写、不清除既有基线——安全铁律 L2）。
    /// </summary>
    public static void Write(string currentVersion)
    {
        if (_ledgerPath.Length == 0 || string.IsNullOrWhiteSpace(currentVersion)) return;
        try
        {
            ShellLogic.FileSystemPolicy.AtomicWrite(_ledgerPath, JsonSerializer.Serialize(new
            {
                version = currentVersion.Trim(),
                at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            }));
        }
        catch { /* 记录失败：下次启动仍按旧基线决策（至多多清一次，无害；绝不抛） */ }
    }
}