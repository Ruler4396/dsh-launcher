using System.IO;
using System.Text.Json;
using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 串行集合（P1-4 起）：触碰进程级静态 Logger 状态（Logger._path/_minLevel）的测试类
/// 必须加入本集合，避免 xunit 类间默认并行导致跨类写路径串扰（结构性 flaky 隐患治理）。
/// 当前成员：LoggerTests、V030FeaturesTests（WindowStateStore/StagedUpdate 损坏告警测试写 Logger）。
/// </summary>
[CollectionDefinition("LoggerState", DisableParallelization = true)]
public class LoggerStateCollection { }

/// <summary>
/// 统一日志单测（用户高频：所有运行诊断都依赖 dsh.log 的结构正确、级别过滤合理）。
/// 覆盖：级别阈值（DSH_LOG_LEVEL）、JSON 结构、错误码字段、写失败静默（日志失败
/// 绝不能影响启动——N4 负向已测进程级，这里测函数级）。
/// </summary>
[Collection("LoggerState")]
public class LoggerTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _logPath;
    private readonly string? _savedLevel;

    public LoggerTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "dsh-log-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _logPath = Path.Combine(_tmp, "dsh.log");
        _savedLevel = Environment.GetEnvironmentVariable("DSH_LOG_LEVEL");
        Environment.SetEnvironmentVariable("DSH_LOG_LEVEL", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DSH_LOG_LEVEL", _savedLevel);
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private void Init() => Logger.Init(_logPath);

    [Fact]
    public void InitThenInfo_WritesJsonLine()
    {
        Init();
        Logger.Info("hello");
        var line = File.ReadAllText(_logPath).Trim();
        using var doc = JsonDocument.Parse(line);
        Assert.Equal("INFO", doc.RootElement.GetProperty("level").GetString());
        Assert.Equal("hello", doc.RootElement.GetProperty("msg").GetString());
        Assert.True(doc.RootElement.TryGetProperty("ts", out _));
        Assert.True(doc.RootElement.TryGetProperty("pid", out _));
    }

    [Fact]
    public void Error_WithCode_WritesCodeField()
    {
        Init();
        Logger.Error("boom", "E2004");
        using var doc = JsonDocument.Parse(File.ReadAllText(_logPath).Trim());
        Assert.Equal("E2004", doc.RootElement.GetProperty("code").GetString());
        Assert.Equal("ERROR", doc.RootElement.GetProperty("level").GetString());
    }

    [Fact]
    public void Warn_WithContext_WritesCtx()
    {
        Init();
        Logger.Warn("w", ctx: new { port = 3080 });
        using var doc = JsonDocument.Parse(File.ReadAllText(_logPath).Trim());
        Assert.Equal(3080, doc.RootElement.GetProperty("ctx").GetProperty("port").GetInt32());
    }

    [Fact]
    public void LevelWarn_InfoSuppressed_WarnAndErrorKept()
    {
        Environment.SetEnvironmentVariable("DSH_LOG_LEVEL", "WARN");
        Init();
        Logger.Info("info-line");
        Logger.Warn("warn-line");
        Logger.Error("error-line");
        var text = File.ReadAllText(_logPath);
        Assert.DoesNotContain("info-line", text);
        Assert.Contains("warn-line", text);
        Assert.Contains("error-line", text);
    }

    [Fact]
    public void LevelError_OnlyErrorsKept()
    {
        Environment.SetEnvironmentVariable("DSH_LOG_LEVEL", "ERROR");
        Init();
        Logger.Warn("warn-line");
        Logger.Error("error-line");
        var text = File.ReadAllText(_logPath);
        Assert.DoesNotContain("warn-line", text);
        Assert.Contains("error-line", text);
    }

    [Fact]
    public void LevelCaseInsensitive_LowercaseWorks()
    {
        Environment.SetEnvironmentVariable("DSH_LOG_LEVEL", "error");
        Init();
        Logger.Warn("warn-line");
        Logger.Error("error-line");
        var text = File.ReadAllText(_logPath);
        Assert.DoesNotContain("warn-line", text);
        Assert.Contains("error-line", text);
    }

    [Fact]
    public void WriteBeforeInit_SilentlyDropped()
    {
        // 未 Init 时写入静默丢弃（不抛、不落盘）
        Logger.Info("pre-init");
        Assert.False(File.Exists(_logPath));
    }

    [Fact]
    public void WriteWhenPathBlocked_SilentlyIgnored()
    {
        // 日志路径不可写（DSH_HOME 被文件占位）→ 静默不抛（与 N4 负向语义一致）
        var blocker = Path.Combine(_tmp, "blocker-file");
        File.WriteAllText(blocker, "i am a file");
        Logger.Init(Path.Combine(blocker, "sub", "dsh.log"));
        var ex = Record.Exception(() => Logger.Error("should not throw"));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData(31L * 1024 * 1024, 0, true)]           // 超 30MB → 轮转
    [InlineData(30L * 1024 * 1024, 0, false)]          // 恰好 30MB → 不轮转（严格大于）
    [InlineData(30L * 1024 * 1024 + 1, 0, true)]
    [InlineData(1024, 0, false)]
    [InlineData(1024, 4, true)]                        // 超 3 天 → 轮转
    [InlineData(1024, 3, false)]                       // 恰好 3 天 → 不轮转（严格大于）
    [InlineData(1024, 3.00001, true)]                  // 略超 3 天 → 轮转
    [InlineData(1024, 2, false)]
    public void ShouldRotate_Thresholds(long lengthBytes, double ageDays, bool expected)
    {
        // now 只取一次：两次 DateTime.UtcNow 之间相差几个 tick，会让"恰好 3 天"误判为超时
        var now = DateTime.UtcNow;
        Assert.Equal(expected, Logger.ShouldRotate(lengthBytes, now.AddDays(-ageDays), now));
    }
}
