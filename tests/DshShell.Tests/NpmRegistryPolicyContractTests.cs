using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// NpmRegistryPolicy 契约测试（2026-08 用户回归：npmjs 直连不稳导致更新构建失败）。
/// 策略：优先走最快的源，失败才降级——
///   ① DSH_NPM_MIRROR 环境变量（显式指定，最高优先）；
///   ② npmmirror（国内实测可达性/速度远优于官方源，作为默认首选）；
///   ③ npm 官方源（空参 = 默认 registry，最后手段；新版本未同步到镜像时的兜底）。
/// 去重保证：同一源在序列中最多出现一次。
/// </summary>
public class NpmRegistryPolicyContractTests
{
    private const string Mirror = ShellLogic.NpmRegistryPolicy.FallbackMirror;

    private static string[] SourcesOf(string? env) =>
        ShellLogic.NpmRegistryPolicy.RegistrySources(env);

    [Fact]
    public void NoEnv_MirrorFirst_OfficialLast()
    {
        var sources = SourcesOf(null);
        Assert.Equal(2, sources.Length);
        Assert.Equal(" --registry=" + Mirror, sources[0]); // 最快可达的镜像优先
        Assert.Equal("", sources[1]);                       // 官方默认源垫底兜底
    }

    [Fact]
    public void EnvMirror_TakesHighestPriority_ThenMirrorThenOfficial()
    {
        var sources = SourcesOf("https://reg.example.com");
        Assert.Equal(3, sources.Length);
        Assert.Equal(" --registry=https://reg.example.com", sources[0]);
        Assert.Equal(" --registry=" + Mirror, sources[1]);
        Assert.Equal("", sources[2]);
    }

    [Fact]
    public void EnvIsNpmmirror_Deduplicated_NoOfficialDuplicate()
    {
        // 环境变量就是 npmmirror（含尾斜杠/大小写变体）→ 与②合并为一条（保留用户原始写法），
        // 只剩 [镜像(用户写法), 官方]
        foreach (var variant in new[] { Mirror, Mirror + "/", Mirror.ToUpperInvariant() })
        {
            var sources = SourcesOf(variant);
            Assert.Equal(2, sources.Length);
            var expectedArg = (" --registry=" + variant).Trim().TrimEnd('/');
            Assert.Equal(expectedArg, sources[0].Trim().TrimEnd('/'), ignoreCase: true);
            Assert.Equal("", sources[1]);
        }
    }

    [Fact]
    public void Sources_NeverContainDuplicates()
    {
        var sources = SourcesOf(Mirror);
        Assert.Equal(sources.Length, sources.Distinct().Count());
    }

    [Fact]
    public void BlankEnv_TreatedAsUnset()
    {
        Assert.Equal(SourcesOf(null), SourcesOf(""));
        Assert.Equal(SourcesOf(null), SourcesOf("   "));
    }
}
