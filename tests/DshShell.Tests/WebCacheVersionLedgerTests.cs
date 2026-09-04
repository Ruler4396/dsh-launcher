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

    // ---------- 极端边缘：账本被写坏/被篡改/路径碰撞时的读安全（L1 扩展） ----------

    private static void WriteRaw(string dir, string content)
        => File.WriteAllText(Path.Combine(dir, "webcache-version.json"), content);

    [Theory]
    [InlineData("{\"version\":123}")]          // 版本为数字
    [InlineData("{\"version\":true}")]         // 版本为布尔
    [InlineData("{\"version\":[\"1.0.0\"]}")]  // 版本为数组
    [InlineData("{\"version\":{\"x\":1}}")]    // 版本为对象
    [InlineData("{\"version\":\"   \"}")]      // 版本为纯空白 → 无基线
    [InlineData("{\"other\":\"1.0.0\"}")]      // 缺 version 字段
    [InlineData("[1,2,3]")]                    // 根为数组
    [InlineData("\"1.0.0\"")]                  // 根为字符串
    [InlineData("null")]                       // 根为 null
    public void CorruptLedgerShapes_ReadNull_AndDoNotThrow(string raw)
    {
        // L1: 任何"非对象+字符串 version"的账本形状 → 一律视为无基线（决策层据此不清），绝不抛。
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            WebCacheVersionLedger.Init(dir);
            WriteRaw(dir, raw);
            Assert.Null(WebCacheVersionLedger.Read());
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void ExtraFutureFields_StillReadsVersion()
    {
        // 前向兼容：未来版本追加字段不影响读取基线。
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            WebCacheVersionLedger.Init(dir);
            WriteRaw(dir, "{\"version\":\"1.0.1\",\"futureFeature\":{\"nested\":[1,2]}}");
            Assert.Equal("1.0.1", WebCacheVersionLedger.Read());
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void LedgerPathCollidesWithDirectory_ReadReturnsNull_NoThrow()
    {
        // 极端：webcache-version.json 被同名目录占据 → 读取失败必须降级为 null（无基线），绝不抛。
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "webcache-version.json"));
            WebCacheVersionLedger.Init(dir);
            Assert.Null(WebCacheVersionLedger.Read());
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void DataDirIsAFile_WriteDoesNotThrow_AndReadStaysNull()
    {
        // 极端：dataDir 路径实际上是一个文件（无法建目录）→ 写入静默失败、读取 null，启动链路不受影响。
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            var blocker = Path.Combine(dir, "blocker");
            File.WriteAllText(blocker, "i am a file");
            WebCacheVersionLedger.Init(blocker); // 账本路径落在"文件"下
            WebCacheVersionLedger.Write("1.0.0"); // 绝不抛
            Assert.Null(WebCacheVersionLedger.Read());
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void VeryLongAndUnicodeVersion_ReadRoundTrips_NoThrow()
    {
        // 极长/Unicode 字符串只参与比较与展示；账本级不做语义判断（判断在策略层 K6），但不得抛异常。
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            WebCacheVersionLedger.Init(dir);
            var weird = "1.0.0-" + new string('x', 5000) + "-中文." + new string('y', 5000);
            WebCacheVersionLedger.Write(weird);
            Assert.Equal(weird, WebCacheVersionLedger.Read());
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}