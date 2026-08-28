using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests.Domain;

/// <summary>
/// F3 契约锁定：版本探测的 stdout 提取（ExtractVersionLine）。
/// 旧行为把整段 stdout 当版本号（仅校验"含点+有数字"），dsh 输出任何 banner/
/// 升级提示即产生多行脏版本——更新比较退化为 0.0.0 误报循环。新契约：
/// 首个匹配宽松 SemVer 前缀（v 可选、2-4 段数字、可带 -pre/+meta）的行即版本号；
/// 找不到 → null（fail-open，版本未知不阻断启动）。
/// </summary>
public class VersionProbeContractTests
{
    [Theory]
    [InlineData("0.1.1-rc.8", "0.1.1-rc.8")]
    [InlineData("v1.2.3", "v1.2.3")]
    [InlineData("1.2.3\n", "1.2.3")]
    [InlineData("\n\n1.2.3\n", "1.2.3")]
    // banner 在前、版本在后（golden 样本同款形态）
    [InlineData("DeepSeek Harness CLI\n0.1.1-rc.8\n", "0.1.1-rc.8")]
    // 非版本输出 → null（fail-open）
    [InlineData("", null)]
    [InlineData("   \n", null)]
    [InlineData("DeepSeek Harness CLI\n(no version)\n", null)]
    [InlineData("dsh version: 1.2.3", null)]  // 刻意不做松散 token 搜索（防 "node >= 18.0.0" 误认）
    [InlineData("requires node >= 18.0.0", null)]
    [InlineData(null, null)]
    public void ExtractVersionLine_ReturnsFirstVersionShapedLine(string? output, string? expected)
        => Assert.Equal(expected, DshDiscovery.ExtractVersionLine(output));

    private static string LoadGolden(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "GoldenFiles", "dsh", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir)
                  ?? throw new FileNotFoundException($"golden sample not found: {name}");
        }
        throw new FileNotFoundException($"golden sample not found: {name}");
    }

    [Fact]
    public void Golden_BannerPlusVersion_ExtractsVersion()
    {
        var expected = LoadGolden("ProbeVersionOutput_bannerPlusVersion.txt")
            .Split('\n').Select(l => l.Trim()).First(l => l.Length > 0 && char.IsDigit(l[0]));
        Assert.Equal(expected, DshDiscovery.ExtractVersionLine(LoadGolden("ProbeVersionOutput_bannerPlusVersion.txt")));
    }

    [Fact]
    public void Golden_BannerOnly_ReturnsNull_FailOpen()
        => Assert.Null(DshDiscovery.ExtractVersionLine(LoadGolden("ProbeVersionOutput_bannerOnly.txt")));
}
