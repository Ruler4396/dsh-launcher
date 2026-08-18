using System.IO;
using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 诊断导出纯函数单测（用户高频依赖：出 bug 时一键 --diagnose 汇报）。
/// 覆盖脱敏规则（防用户名/路径泄漏）、日志尾部/级别过滤、错误码汇总、参数解析。
/// </summary>
public class DiagnoseExportTests : IDisposable
{
    private readonly string _tmp;
    private readonly string _logPath;

    public DiagnoseExportTests()
    {
        _tmp = Path.Combine(Path.GetTempPath(), "dsh-dia-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmp);
        _logPath = Path.Combine(_tmp, "dsh.log");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); } catch { }
    }

    private static string JsonLine(string level, string msg, string? code = null) =>
        "{\"ts\":\"2026-08-16 12:00:00.000\",\"level\":\"" + level + "\",\"pid\":1"
        + (code is null ? "" : ",\"code\":\"" + code + "\"") + ",\"msg\":\"" + msg + "\"}";

    // ---------- Sanitize 脱敏 ----------

    [Fact]
    public void Sanitize_ReplacesUserProfileFullPath()
    {
        // Sanitize 用 Environment.GetFolderPath(UserProfile)（真实用户目录），
        // 不能用 USERPROFILE 环境变量模拟——直接用真实路径构造输入。
        var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var s = DiagnoseExport.Sanitize(up + @"\secret.log: boom");
        Assert.DoesNotContain(up, s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USER%", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_ReplacesUserProfileEnvLiteral()
    {
        var s = DiagnoseExport.Sanitize("%USERPROFILE%\\AppData\\x");
        Assert.DoesNotContain("%USERPROFILE%", s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USER%", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_ReplacesTildeSlash()
    {
        var s = DiagnoseExport.Sanitize("path ~\\AppData leak");
        Assert.Contains("%USER%\\AppData", s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_ReplacesUserNamePathSegment()
    {
        // 用户名取真实用户目录最后一段（Sanitize 内部逻辑），用真实值构造输入
        var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userName = Path.GetFileName(up.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        Assert.False(string.IsNullOrEmpty(userName), "测试前提：本机用户目录非空");
        var s = DiagnoseExport.Sanitize(@"\other\" + userName + @"\deep\path");
        Assert.DoesNotContain(userName, s, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("USERNAME", s);
    }

    [Fact]
    public void Sanitize_UserNameNotInPathContext_Untouched()
    {
        var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userName = Path.GetFileName(up.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        // 普通文本中的用户名单词（非 \用户名\ 路径上下文）不应被替换（防过度脱敏）
        var s = DiagnoseExport.Sanitize("hello " + userName + " world");
        Assert.Contains(userName, s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sanitize_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", DiagnoseExport.Sanitize(null));
        Assert.Equal("", DiagnoseExport.Sanitize(""));
    }

    [Fact]
    public void Sanitize_BareTilde_NotInPathContext_Untouched()
    {
        // 仅替换 "~\"（反斜杠后缀路径缩写）；独立波浪号（普通文本中的 ~ 符号）应保留
        var s = DiagnoseExport.Sanitize("size ~100MB and ~ alone");
        Assert.Contains("~100MB", s, StringComparison.Ordinal);
        Assert.Contains("~ alone", s, StringComparison.Ordinal);
        // "~\" 路径缩写被替换
        var s2 = DiagnoseExport.Sanitize(@"~\.dsh\x");
        Assert.DoesNotContain("~\\", s2, StringComparison.Ordinal);
    }

    // ---------- TailLines 日志尾部 ----------

    [Fact]
    public void TailLines_MissingFile_ReturnsNote()
    {
        Assert.Contains("不存在", DiagnoseExport.TailLines(Path.Combine(_tmp, "nope.log"), 10));
    }

    [Fact]
    public void TailLines_KeepsOnlyLastLines()
    {
        File.WriteAllLines(_logPath, Enumerable.Range(1, 20).Select(i => "line" + i));
        var tail = DiagnoseExport.TailLines(_logPath, 5);
        var lines = tail.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(5, lines.Length);
        Assert.Contains("line16", tail);
        Assert.Contains("line20", tail);
        Assert.DoesNotContain("line15", tail);
    }

    [Fact]
    public void TailLines_FileLockedByOtherProcess_StillReads()
    {
        File.WriteAllText(_logPath, "locked content");
        // 模拟 cmd >> 重定向独占写（允许读共享、拒绝写）——v0.3.1 共享读修复回归断言
        using var fs = new FileStream(_logPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        var tail = DiagnoseExport.TailLines(_logPath, 10);
        Assert.Contains("locked content", tail);
    }

    // ---------- FilterByLevel 级别过滤 ----------

    [Fact]
    public void FilterByLevel_KeepsOnlyMatchingLevels()
    {
        File.WriteAllText(_logPath, JsonLine("INFO", "i1") + "\n" + JsonLine("WARN", "w1") + "\n" + JsonLine("ERROR", "e1"));
        var warn = DiagnoseExport.FilterByLevel(_logPath, Logger.Level.Warn);
        Assert.DoesNotContain("i1", warn);
        Assert.Contains("w1", warn);
        Assert.Contains("e1", warn);
        var err = DiagnoseExport.FilterByLevel(_logPath, Logger.Level.Error);
        Assert.Contains("e1", err);
        Assert.DoesNotContain("w1", err);
    }

    [Fact]
    public void FilterByLevel_NonJsonServiceLines_KeptAsWarnWhenFlagged()
    {
        // 原始服务输出命中启动错误标志按告警计（npm ERR / EACCES 等）
        File.WriteAllText(_logPath, "some output\nnpm ERR! code EACCES\nplain line");
        var warn = DiagnoseExport.FilterByLevel(_logPath, Logger.Level.Warn);
        Assert.Contains("npm ERR", warn);
        Assert.DoesNotContain("plain line", warn);
    }

    [Fact]
    public void FilterByLevel_MissingFile_ReturnsNote()
    {
        Assert.Contains("不存在", DiagnoseExport.FilterByLevel(Path.Combine(_tmp, "nope.log"), Logger.Level.Warn));
    }

    // ---------- TryGetJsonLevel ----------

    [Theory]
    [InlineData("INFO", Logger.Level.Info)]
    [InlineData("WARN", Logger.Level.Warn)]
    [InlineData("ERROR", Logger.Level.Error)]
    public void TryGetJsonLevel_ReadsLevel(string level, Logger.Level expected)
    {
        Assert.Equal(expected, DiagnoseExport.TryGetJsonLevel(JsonLine(level, "x")));
    }

    [Fact]
    public void TryGetJsonLevel_NonJson_ReturnsNull()
    {
        Assert.Null(DiagnoseExport.TryGetJsonLevel("plain text"));
        Assert.Null(DiagnoseExport.TryGetJsonLevel("{broken"));
    }

    // ---------- SummarizeErrors 错误码汇总 ----------

    [Fact]
    public void SummarizeErrors_CountsAndOrdersByFrequency()
    {
        File.WriteAllText(_logPath,
            JsonLine("ERROR", "a", "E2004") + "\n" + JsonLine("ERROR", "b", "E2004") + "\n"
            + JsonLine("ERROR", "c", "E1006") + "\n" + "not json\n");
        var sum = DiagnoseExport.SummarizeErrors(_logPath);
        Assert.Contains("[E2004] x2", sum);
        Assert.Contains("[E1006] x1", sum);
        // E2004 出现更多 → 排前面
        Assert.True(sum.IndexOf("[E2004]", StringComparison.Ordinal) < sum.IndexOf("[E1006]", StringComparison.Ordinal));
    }

    [Fact]
    public void SummarizeErrors_NoCodes_ReturnsNote()
    {
        File.WriteAllText(_logPath, JsonLine("INFO", "x") + "\nplain\n");
        Assert.Contains("无错误码", DiagnoseExport.SummarizeErrors(_logPath));
    }

    [Fact]
    public void SummarizeErrors_MissingFile_ReturnsNote()
    {
        Assert.Contains("无日志", DiagnoseExport.SummarizeErrors(Path.Combine(_tmp, "nope.log")));
    }

    // ---------- ParseMinLevel 参数解析 ----------

    [Theory]
    [InlineData(new[] { "--diagnose", "--min-level", "warn" }, Logger.Level.Warn)]
    [InlineData(new[] { "--diagnose", "--min-level", "warning" }, Logger.Level.Warn)]
    [InlineData(new[] { "--diagnose", "--min-level", "ERROR" }, Logger.Level.Error)]
    [InlineData(new[] { "--diagnose", "--min-level", "bogus" }, null)]
    [InlineData(new[] { "--diagnose" }, null)]
    public void ParseMinLevel_ParsesArgs(string[] args, Logger.Level? expected)
    {
        Assert.Equal(expected, DiagnoseExport.ParseMinLevel(args));
    }
}
