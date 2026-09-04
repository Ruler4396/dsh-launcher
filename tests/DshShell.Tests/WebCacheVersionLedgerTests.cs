using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// WebCacheVersionLedger 持久化契约测试（真实文件系统，%TEMP% 瞬态隔离）。
///
/// 安全铁律延伸：
///   L1 基线缺失/损坏 → Read 返回 null（保守：决策层据此**不清**缓存）；
///   L2 Write(null) 是 API 级禁止操作：**不得**抹掉既有基线（否则一次探测失败
///      就会让后续版本变更漏清缓存——宁可多清一次，不可漏清一次）；
///   L3 写入必须经 ShellLogic.FileSystemPolicy.AtomicWrite（.tmp + File.Move）；
///   L4 任何读失败只降级、绝不抛异常（启动链路不允许被账本拖垮）。
/// </summary>
public class WebCacheVersionLedgerTests
{
    private static string NewDir()
        => Path.Combine(Path.GetTempPath(), "dsh-cacheledger-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void MissingLedger_ReadsNull()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            WebCacheVersionLedger.Init(dir);
            Assert.Null(WebCacheVersionLedger.Read());
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            WebCacheVersionLedger.Init(dir);
            WebCacheVersionLedger.Write("1.0.1-rc.2");
            Assert.Equal("1.0.1-rc.2", WebCacheVersionLedger.Read());
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void WriteNull_DoesNotDestroyExistingBaseline()
    {
        // L2：探测失败（current==null）时组合根不得写空基线——账本 API 直接拒绝，
        // 把"一次探测失败导致漏清"从源头掐死。
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            WebCacheVersionLedger.Init(dir);
            WebCacheVersionLedger.Write("1.0.0");
            WebCacheVersionLedger.Write(null!);
            Assert.Equal("1.0.0", WebCacheVersionLedger.Read());
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void CorruptLedger_ReadsNull_AndDoesNotThrow()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            WebCacheVersionLedger.Init(dir);
            File.WriteAllText(Path.Combine(dir, "webcache-version.json"), "{ 这不是 JSON !!");
            Assert.Null(WebCacheVersionLedger.Read());
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Overwrite_SameFile_IsAtomicAndTruncationSafe()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            WebCacheVersionLedger.Init(dir);
            WebCacheVersionLedger.Write("1.0.0");
            WebCacheVersionLedger.Write("1.0.1");
            Assert.Equal("1.0.1", WebCacheVersionLedger.Read());
            // L3：写入后不应留下 .tmp 半成品（AtomicWrite 清理自身临时文件）
            Assert.Empty(Directory.GetFiles(dir, "webcache-version.json.*.tmp"));
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}