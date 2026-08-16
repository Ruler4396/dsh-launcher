using System.Text.Json;

namespace DshWeb;

/// <summary>
/// 统一日志（DSH_HOME\dsh-launcher\dsh.log)：壳的 JSON Lines + dsh 服务原始输出同文件追加，
/// 只保留一个日志文件便于管理与上传（v0.3.0 起替代 shell.log 与 .dsh-web.&lt;port&gt;.log）。
/// - 级别：Info / Warn / Error；DSH_LOG_LEVEL（INFO/WARN/ERROR）控制最小输出级别。
/// - 轮转：唯一所有权归壳——启动早段按大小/时长滚动（.1/.2，保留 ≤3 份），
///   start-dsh.vbs 不再截断、不再自行轮转（消除双所有权冲突）。
/// - 克制：无第三方库、无后台线程、无遥测；写失败静默（日志失败绝不能影响启动）。
/// </summary>
public static class Logger
{
    public enum Level { Info, Warn, Error }

    private static readonly object Sync = new();
    private static string _path = "";
    private static Level _minLevel = Level.Info;

    /// <summary>统一日志文件路径（Main 最早期调用 <see cref="Init"/> 设置；未初始化时写入静默丢弃）。
    /// 注意：此属性名会遮蔽 System.IO.Path，本类内一律用全限定名。</summary>
    public static string Path
    {
        get { lock (Sync) return _path; }
    }

    public static void Init(string path)
    {
        lock (Sync)
        {
            _path = path;
            _minLevel = Environment.GetEnvironmentVariable("DSH_LOG_LEVEL")?.Trim().ToUpperInvariant() switch
            {
                "WARN" or "WARNING" => Level.Warn,
                "ERROR" => Level.Error,
                _ => Level.Info,
            };
        }
    }

    public static void Info(string msg, string? code = null, object? ctx = null) => Write(Level.Info, msg, code, ctx);

    public static void Warn(string msg, string? code = null, object? ctx = null) => Write(Level.Warn, msg, code, ctx);

    public static void Error(string msg, string? code = null, object? ctx = null) => Write(Level.Error, msg, code, ctx);

    private static void Write(Level level, string msg, string? code, object? ctx)
    {
        lock (Sync)
        {
            if (string.IsNullOrWhiteSpace(_path) || level < _minLevel) return;
            try
            {
                var entry = new Dictionary<string, object?>
                {
                    ["ts"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    ["level"] = level.ToString().ToUpperInvariant(),
                    ["pid"] = Environment.ProcessId,
                };
                if (!string.IsNullOrWhiteSpace(code)) entry["code"] = code;
                entry["msg"] = msg;
                if (ctx is not null) entry["ctx"] = ctx;
                var line = JsonSerializer.Serialize(entry) + Environment.NewLine;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                File.AppendAllText(_path, line);
            }
            catch
            {
                // 日志失败静默：不影响启动/功能
            }
        }
    }

    /// <summary>轮转判定（纯函数，可单测）：超过 30MB 或最后写入超过 3 天 → 滚动。</summary>
    internal static bool ShouldRotate(long lengthBytes, DateTime lastWriteUtc, DateTime nowUtc)
    {
        var tooBig = lengthBytes > 30L * 1024 * 1024;
        var tooOld = nowUtc - lastWriteUtc > TimeSpan.FromDays(3);
        return tooBig || tooOld;
    }

    /// <summary>按策略滚动：dsh.log → *.1（旧 .1 → *.2），保留 ≤3 份；超 30 天的滚动旧档顺手清除。
    /// 仅壳调用（启动早段、拉起服务前）。</summary>
    public static void RotateIfNeeded()
    {
        string path;
        lock (Sync) path = _path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (!ShouldRotate(info.Length, info.LastWriteTimeUtc, DateTime.UtcNow)) return;
            lock (Sync)
            {
                var one = path + ".1";
                var two = path + ".2";
                if (File.Exists(two)) File.Delete(two);
                if (File.Exists(one)) File.Move(one, two, overwrite: true);
                File.Move(path, one, overwrite: true);
                foreach (var p in new[] { one, two })
                {
                    try
                    {
                        if (File.Exists(p) && DateTime.UtcNow - File.GetLastWriteTimeUtc(p) > TimeSpan.FromDays(30))
                            File.Delete(p);
                    }
                    catch { /* 单文件清理失败忽略 */ }
                }
            }
        }
        catch { /* 轮转失败不影响启动 */ }
    }
}