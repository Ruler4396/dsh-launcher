using System.Text.Json;

namespace DshWeb;

/// <summary>
/// 统一日志（DSH_HOME\dsh-launcher\dsh.log)：壳的 JSON Lines + dsh 服务原始输出同文件追加，
/// 只保留一个日志文件便于管理与上传（v0.3.0 起替代 shell.log 与 .dsh-web.&lt;port&gt;.log）。
/// - 级别：Info / Warn / Error；DSH_LOG_LEVEL（INFO/WARN/ERROR）控制最小输出级别。
/// - 轮转：唯一所有权归壳——启动早段按大小/时长滚动（.1/.2，保留 ≤3 份），
///   start-dsh.vbs 不再截断、不再自行轮转（消除双所有权冲突）。
/// - 防锁死（任务二）：显式 FileStream + FileShare.ReadWrite 最大化共享（兼容被 cmd &gt;&gt;
///   重定向持有的场景）；仍被独占锁死（IOException）时**绝不静默吞掉**——落盘到
///   %TEMP%\dsh-launcher-fallback-{pid}.log 并向 Console.Error 输出醒目告警，
///   保证任何启动阶段的诊断信息都不会因日志锁而丢失。
/// - 克制：无第三方库、无后台线程、无遥测；fallback 也失败时静默（日志失败绝不能影响启动）。
/// </summary>
public static class Logger
{
    public enum Level { Info, Warn, Error }

    private static readonly object Sync = new();
    private static string _path = "";
    private static Level _minLevel = Level.Info;
    private static bool _fallbackUsed;
    private static bool _fallbackWarned;

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
            var line = BuildLine(level, msg, code, ctx);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
                // 任务二：显式 FileStream + FileShare.ReadWrite——cmd `>>` 重定向持有的日志
                // 默认 FileShare.Read 无法再开写句柄（历史"日志静默丢失"根因）；ReadWrite 共享
                // 读写最大化兼容，正常情况即可直接写入，无需落 fallback。
                var bytes = System.Text.Encoding.UTF8.GetBytes(line);
                using (var fs = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                {
                    fs.Write(bytes, 0, bytes.Length);
                }
            }
            catch (IOException)
            {
                // 主日志被独占锁死（FileShare.None 之类）：绝不静默吞掉 → fallback
                WriteFallback(line);
            }
            catch (UnauthorizedAccessException)
            {
                // 路径不可写（被文件占位等）：同样落入 fallback，保证诊断不丢失
                WriteFallback(line);
            }
            catch
            {
                // 其余异常仍静默：日志失败绝不能影响启动
            }
        }
    }

    private static string BuildLine(Level level, string msg, string? code, object? ctx)
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
        return JsonSerializer.Serialize(entry) + Environment.NewLine;
    }

    /// <summary>fallback 日志路径（任务二）：主日志被锁时写入 %TEMP%\dsh-launcher-fallback-{pid}.log。</summary>
    public static string FallbackPath =>
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dsh-launcher-fallback-{Environment.ProcessId}.log");

    /// <summary>测试专用：重置 fallback 会话状态与日志路径（仅 DshShell.Tests 使用，串行集合内调用，
    /// 避免跨测试 FallbackUsed/FallbackPath 相互污染）。</summary>
    internal static void ResetForTest()
    {
        lock (Sync)
        {
            _path = "";
            _fallbackUsed = false;
            _fallbackWarned = false;
        }
    }

    /// <summary>本会话是否发生过日志 fallback（任务二 UI 告警：启动窗显示黄色提示）。</summary>
    public static bool FallbackUsed => _fallbackUsed;

    /// <summary>主日志被锁时的 fallback 写入（幂等告警：只向 Console.Error 输出一次醒目警告）。</summary>
    private static void WriteFallback(string line)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FallbackPath)!);
            File.AppendAllText(FallbackPath, line);
            _fallbackUsed = true;
            if (!_fallbackWarned)
            {
                _fallbackWarned = true;
                Console.Error.WriteLine("[FATAL LOGGER] Main log locked by another process. Falling back to: " + FallbackPath);
            }
        }
        catch
        {
            // fallback 也失败（%TEMP% 不可写等极端环境）：彻底静默，不影响启动
        }
    }

    /// <summary>轮转判定（纯函数，可单测）：超过 30MB 或最后写入超过 3 天 → 滚动。</summary>
    internal static bool ShouldRotate(long lengthBytes, DateTime lastWriteUtc, DateTime nowUtc)
    {
        var tooBig = lengthBytes > 30L * 1024 * 1024;
        var tooOld = nowUtc - lastWriteUtc > TimeSpan.FromDays(3);
        return tooBig || tooOld;
    }

    /// <summary>常驻超长会话告警（P2）：日志 >50MB 且最后写入 >24h（说明服务常驻且持续输出，
    /// 热轮转被运行中的 node 句柄阻止）→ 写一条 Warn 提示用户重启后自动轮转。启动早段调用。</summary>
    public static void WarnIfOversized()
    {
        string path;
        lock (Sync) path = _path;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            var sizeMb = info.Length / (1024.0 * 1024.0);
            var ageHours = (DateTime.UtcNow - info.LastWriteTimeUtc).TotalHours;
            if (sizeMb > 50 && ageHours > 24)
            {
                Warn($"unified log oversized in a long-lived session; rotation happens on next restart",
                    ctx: new { sizeMb = Math.Round(sizeMb, 1), ageHours = Math.Round(ageHours, 1), path });
            }
        }
        catch { /* 告警失败忽略 */ }
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