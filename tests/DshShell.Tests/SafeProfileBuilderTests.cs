using DshWeb;
using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// SafeProfileBuilder 契约测试（ADR-022）：真实 OS 文件交互（铁律：禁 Mock OS 边界）。
/// 锁定核心契约——隔离 profile 构建不触碰用户任何文件、Tier1 只保留 @deepseek-ai 核心、
/// Tier2 仅最小 web 核心、构建幂等。
/// </summary>
public class SafeProfileBuilderTests : IDisposable
{
    private readonly string _home;

    public SafeProfileBuilderTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "dsh-safe-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_home, "profiles", "web"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
    }

    private void WriteUserWeb(string bundlesJson)
        => File.WriteAllText(Path.Combine(_home, "profiles", "web", "package.json"),
            "{ \"name\":\"dsh-profile-web\",\"private\":true,\"dsh\":{\"profile\":{\"bundles\":" + bundlesJson + "}} }");

    [Fact]
    public void Tier1_KeepsDeepSeekCore_StripsThirdParty()
    {
        WriteUserWeb("[\"@deepseek-ai/dsh-base\",\"@deepseek-ai/dsh-web-app\",\"dsh-notification\",\"@liustack/modlens\",\"dsh-launcher-lifetime\"]");
        var b = new SafeProfileBuilder(_home);
        Assert.True(b.Build());
        var json = File.ReadAllText(b.SafeProfilePackageJson);
        Assert.Contains("@deepseek-ai/dsh-base", json);
        Assert.Contains("@deepseek-ai/dsh-web-app", json);
        Assert.DoesNotContain("dsh-notification", json);
        Assert.DoesNotContain("modlens", json);
        Assert.DoesNotContain("dsh-launcher-lifetime", json);
    }

    [Fact]
    public void Tier2_MinimalCore_Only()
    {
        WriteUserWeb("[\"@deepseek-ai/dsh-base\",\"@deepseek-ai/dsh-web-app\",\"dsh-notification\"]");
        var b = new SafeProfileBuilder(_home);
        Assert.True(b.Build(SafeProfileTier.Tier2Minimal));
        var json = File.ReadAllText(b.SafeProfilePackageJson);
        Assert.Contains("@deepseek-ai/dsh-base", json);
        Assert.Contains("@deepseek-ai/dsh-web-app", json);
        Assert.DoesNotContain("dsh-notification", json);
    }

    [Fact]
    public void Build_DoesNotMutateUserProfiles_ZeroPollution()
    {
        WriteUserWeb("[\"@deepseek-ai/dsh-base\",\"@deepseek-ai/dsh-web-app\",\"dsh-notification\"]");
        var userProfilesDir = Path.Combine(_home, "profiles");
        var before = SafeProfileBuilder.CaptureUserProfilesHash(userProfilesDir);
        var b = new SafeProfileBuilder(_home);
        Assert.True(b.Build());
        Assert.True(SafeProfileBuilder.UsersProfilesUntouched(before, userProfilesDir));
    }

    [Fact]
    public void Build_IsIdempotent_AndUserWebUnchanged()
    {
        WriteUserWeb("[\"@deepseek-ai/dsh-base\",\"@deepseek-ai/dsh-web-app\",\"dsh-notification\"]");
        var path = Path.Combine(_home, "profiles", "web", "package.json");
        var expected = File.ReadAllText(path);
        var b = new SafeProfileBuilder(_home);
        Assert.True(b.Build());
        Assert.True(b.Build());
        Assert.Equal(expected, File.ReadAllText(path));
    }
}
