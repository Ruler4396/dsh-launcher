using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 更新通知可达性契约测试（2026-08-29 安全通知回归）：
/// - IsPortableInstall：MSI（Program Files）→ false（走 Toast/气泡链）；便携/自定义目录 → true（模态弹窗兜底）；
/// - ParseNpmrcRegistry：跟随用户真实 npm 镜像配置（替代硬编码 registry.npmjs.org）。
/// </summary>
public class UpdateNoticeReachabilityContractTests
{
    [Fact]
    public void IsPortableInstall_MsiProgramFiles_False()
    {
        Assert.False(ShellLogic.RuntimeConfig.IsPortableInstall(
            @"C:\Program Files\dsh-launcher", @"C:\Program Files", @"C:\Program Files (x86)"));
    }

    [Fact]
    public void IsPortableInstall_ProgramFilesX86_False()
    {
        Assert.False(ShellLogic.RuntimeConfig.IsPortableInstall(
            @"C:\Program Files (x86)\dsh-launcher", @"C:\Program Files", @"C:\Program Files (x86)"));
    }

    [Fact]
    public void IsPortableInstall_RelativeOrPortableDir_True()
    {
        Assert.True(ShellLogic.RuntimeConfig.IsPortableInstall(
            @"D:\Tools\dsh-launcher-windows-0.4.3", @"C:\Program Files", @"C:\Program Files (x86)"));
        Assert.True(ShellLogic.RuntimeConfig.IsPortableInstall(
            @"E:\dsh-compat-sandbox\dsh-launcher-windows-0.4.3", @"C:\Program Files", @"C:\Program Files (x86)"));
        Assert.True(ShellLogic.RuntimeConfig.IsPortableInstall(
            null, @"C:\Program Files", @"C:\Program Files (x86)"));
    }

    [Fact]
    public void IsPortableInstall_ProgramFilesSimilarName_NotFooledByPrefix()
    {
        // "C:\Program Files_bak" 不应被当 MSI 安装（前缀误判防护）
        Assert.True(ShellLogic.RuntimeConfig.IsPortableInstall(
            @"C:\Program Files_bak\dsh-launcher", @"C:\Program Files", @"C:\Program Files (x86)"));
    }

    [Fact]
    public void ParseNpmrcRegistry_StandardAndCommentLines()
    {
        var npmrc = "; registry comment\nregistry=https://registry.npmmirror.com/\n# trailing\n";
        Assert.Equal("https://registry.npmmirror.com", ShellLogic.NpmRegistryPolicy.ParseNpmrcRegistry(npmrc));
    }

    [Fact]
    public void ParseNpmrcRegistry_CaseInsensitiveAndFirstHit()
    {
        var npmrc = "REGISTRY = https://registry.npmjs.org\nregistry=https://registry.npmmirror.com\n";
        Assert.Equal("https://registry.npmjs.org", ShellLogic.NpmRegistryPolicy.ParseNpmrcRegistry(npmrc));
    }

    [Fact]
    public void ParseNpmrcRegistry_NonHttpOrAbsent_Null()
    {
        Assert.Null(ShellLogic.NpmRegistryPolicy.ParseNpmrcRegistry("registry=file:/x"));
        Assert.Null(ShellLogic.NpmRegistryPolicy.ParseNpmrcRegistry("; all comments\n"));
        Assert.Null(ShellLogic.NpmRegistryPolicy.ParseNpmrcRegistry(null));
        Assert.Null(ShellLogic.NpmRegistryPolicy.ParseNpmrcRegistry("foo=bar"));
    }

    [Fact]
    public void LocalProxyCandidates_WellKnownLocalPorts()
    {
        var c = ShellLogic.UpdateProxyPolicy.LocalProxyCandidates();
        Assert.Contains("http://127.0.0.1:7890", c); // Clash
        Assert.Contains("http://127.0.0.1:10809", c); // v2rayN
        Assert.All(c, u => Assert.StartsWith("http://127.0.0.1:", u));
    }

    [Fact]
    public void DshRegistryCandidates_OrderAndDedupe()
    {
        var c = UpdateChecker.DshRegistryCandidates("https://registry.npmmirror.com", "https://registry.npmjs.org");
        // env 与 npmrc 均命中 → 去重后仍含 npmmirror 与 npmjs，且各自只出现一次
        Assert.Equal(2, c.Length);
        Assert.Contains("https://registry.npmmirror.com", c);
        Assert.Contains("https://registry.npmjs.org", c);
    }
}