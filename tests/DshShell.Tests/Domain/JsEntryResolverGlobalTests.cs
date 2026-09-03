using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests.Domain;

/// <summary>
/// 全局 npm/pnpm 布局的 JS 入口自动定位契约（issue #24 根因修复）：
/// 不再硬编码 %APPDATA%\npm——自定义 prefix（npm）与 pnpm 全局虚拟目录布局必须都能解析。
/// 布局全部构造在 %TEMP% 瞬态目录（既有 DshDiscoveryProbeTests 同策略）。
/// </summary>
public class JsEntryResolverGlobalTests
{
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dsh-entry-probe-" + Guid.NewGuid().ToString("N"));
        public TempDir() => System.IO.Directory.CreateDirectory(Path);
        public void Dispose() { try { System.IO.Directory.Delete(Path, recursive: true); } catch { } }
    }

    /// <summary>构造一个"shim 与 node_modules 同父"的 npm 风格全局前缀。</summary>
    private static string InstallPackageAt(string prefixDir, string binJson = "\"lib/bin.js\"")
    {
        var pkgDir = System.IO.Path.Combine(prefixDir, "node_modules", "@deepseek-ai", "dsh");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(pkgDir, "lib"));
        File.WriteAllText(System.IO.Path.Combine(pkgDir, "package.json"),
            $"{{ \"version\": \"1.2.3-fake\", \"bin\": {{ \"dsh\": {binJson} }} }}");
        File.WriteAllText(System.IO.Path.Combine(pkgDir, "lib", "bin.js"), "// fake entry");
        return System.IO.Path.Combine(pkgDir, "lib", "bin.js");
    }

    [Fact]
    public void ResolveGlobalPackageEntry_NpmDefaultLayout_ResolvesSiblingNodeModules()
    {
        using var tmp = new TempDir();
        var expected = InstallPackageAt(tmp.Path);
        var shim = System.IO.Path.Combine(tmp.Path, "dsh.cmd");
        File.WriteAllText(shim, "@echo off\n");

        var entry = JsEntryResolver.ResolveGlobalPackageEntry(shim, "@deepseek-ai/dsh", out var probed);

        Assert.Equal(expected, entry);
        // 成功路径命中即返回：probed 可在命中前保持为空（下游 EntryProbeFailures=null），
        // 不为失败归因断言——负例（MissingPackage）才锁 probed 非空。
    }

    [Fact]
    public void ResolveGlobalPackageEntry_CustomPrefix_ResolvesSiblingNodeModules()
    {
        // issue #24 场景：prefix 不在 %APPDATA%\npm（自定义前缀）——就近策略必须命中
        using var tmp = new TempDir();
        var prefix = System.IO.Path.Combine(tmp.Path, "D", "my-npm-global");
        System.IO.Directory.CreateDirectory(prefix);
        var expected = InstallPackageAt(prefix);
        var shim = System.IO.Path.Combine(prefix, "dsh.cmd");
        File.WriteAllText(shim, "@echo off\n");

        var entry = JsEntryResolver.ResolveGlobalPackageEntry(shim, "@deepseek-ai/dsh", out _);

        Assert.Equal(expected, entry);
    }

    [Fact]
    public void ResolveGlobalPackageEntry_NpmShimEmbeddedDp0Path_ResolvesFromShimText()
    {
        // 真实 npm shim 形态：入口内嵌为 "%dp0%\node_modules\@deepseek-ai\dsh\lib\bin.js"（无 ..\ 段）。
        // 本布局不给 shim 放 sibling node_modules，专测策略 2（shim 文本）独立命中。
        using var tmp = new TempDir();
        var realEntry = System.IO.Path.Combine(tmp.Path, "virtual", "node_modules", "@deepseek-ai", "dsh", "lib", "bin.js");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(realEntry)!);
        File.WriteAllText(realEntry, "// fake");
        var shim = System.IO.Path.Combine(tmp.Path, "dsh.cmd");
        File.WriteAllText(shim,
            $@"@echo off
SET ""_prog=node""
""%_prog%"" ""%dp0%\virtual\node_modules\@deepseek-ai\dsh\lib\bin.js"" %*
");

        var entry = JsEntryResolver.ResolveGlobalPackageEntry(shim, "@deepseek-ai/dsh", out var probed);

        Assert.True(entry == realEntry, $"probed=[{string.Join(" || ", probed)}] entry=[{entry}] realEntry=[{realEntry}]");
        Assert.Contains(probed, p => p.StartsWith("near-shim(missing):", StringComparison.Ordinal)
                                     || p.StartsWith("near-shim(bin-unresolvable):", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveGlobalPackageEntry_TildeDp0ShimForm_ResolvesFromShimText()
    {
        // %~dp0 变体（部分生成器形态），同样无 sibling node_modules——策略 2 独立命中。
        using var tmp = new TempDir();
        var realEntry = System.IO.Path.Combine(tmp.Path, "not-package", "node_modules", "@deepseek-ai", "dsh", "bin", "dsh.mjs");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(realEntry)!);
        File.WriteAllText(realEntry, "// fake");
        var shim = System.IO.Path.Combine(tmp.Path, "dsh.cmd");
        File.WriteAllText(shim,
            $@"@ECHO off
""%~dp0\not-package\node_modules\@deepseek-ai\dsh\bin\dsh.mjs"" %*
");

        var entry = JsEntryResolver.ResolveGlobalPackageEntry(shim, "@deepseek-ai/dsh", out var probed);

        Assert.True(entry == realEntry, $"probed=[{string.Join(" || ", probed)}] entry=[{entry}] realEntry=[{realEntry}]");
    }

    [Fact]
    public void ResolveGlobalPackageEntry_MissingPackage_ReturnsNullWithProbed()
    {
        // 负例只验解析机制：用不存在的包名，避免宿主机 %APPDATA%\npm 里若有真实全局 dsh
        // 被 legacy 策略命中而污染（issue #24 的"shim 在但包缺失"形态回归）。
        using var tmp = new TempDir();
        var shim = System.IO.Path.Combine(tmp.Path, "dsh.cmd");
        File.WriteAllText(shim, "@echo off\n"); // shim 在，包缺失

        var entry = JsEntryResolver.ResolveGlobalPackageEntry(shim, "@deepseek-ai/dsh-does-not-exist", out var probed);

        Assert.Null(entry);
        Assert.NotEmpty(probed); // 归因材料必须可展示（E2001 弹窗/日志）
        Assert.Contains(probed, p => p.StartsWith("near-shim(missing):", StringComparison.Ordinal));
        Assert.Contains(probed, p => p.StartsWith("legacy:", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveGlobalPackageEntry_NullShim_PathScanFindsShimAndResolves()
    {
        // 调用方（发现层）没给出 shim 时，PATH 扫描必须兜底——临时目录前置进 PATH
        using var tmp = new TempDir();
        var expected = InstallPackageAt(tmp.Path);
        File.WriteAllText(System.IO.Path.Combine(tmp.Path, "dsh.cmd"), "@echo off\n");
        var savedPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", tmp.Path + System.IO.Path.PathSeparator + savedPath);
            var entry = JsEntryResolver.ResolveGlobalPackageEntry(null, "@deepseek-ai/dsh", out _);
            Assert.Equal(expected, entry);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", savedPath);
        }
    }

    // ---------- bin 三态 golden（ResolveEntryFromPkgDir 契约） ----------

    [Fact]
    public void ResolveEntryFromPkgDir_BinObjectFirstKey_Resolves()
    {
        using var tmp = new TempDir();
        var pkgDir = System.IO.Path.Combine(tmp.Path, "p1");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(pkgDir, "bin"));
        File.WriteAllText(System.IO.Path.Combine(pkgDir, "package.json"),
            "{ \"bin\": { \"other\": \"bin/other.js\", \"dsh\": \"bin/dsh.js\" } }");
        File.WriteAllText(System.IO.Path.Combine(pkgDir, "bin", "dsh.js"), "// x");

        Assert.Equal(System.IO.Path.Combine(pkgDir, "bin", "dsh.js"),
            JsEntryResolver.ResolveEntryFromPkgDir(pkgDir, "@deepseek-ai/dsh"));
    }

    [Fact]
    public void ResolveEntryFromPkgDir_BinString_Resolves()
    {
        using var tmp = new TempDir();
        var pkgDir = System.IO.Path.Combine(tmp.Path, "p2");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(pkgDir, "lib"));
        File.WriteAllText(System.IO.Path.Combine(pkgDir, "package.json"),
            "{ \"bin\": \"lib/bin.js\" }");
        File.WriteAllText(System.IO.Path.Combine(pkgDir, "lib", "bin.js"), "// x");

        Assert.Equal(System.IO.Path.Combine(pkgDir, "lib", "bin.js"),
            JsEntryResolver.ResolveEntryFromPkgDir(pkgDir, "@deepseek-ai/dsh"));
    }

    [Fact]
    public void ResolveEntryFromPkgDir_BinExtensionless_ResolvesJs()
    {
        // bin 无扩展名（如 "lib/dsh"）→ 兜底补 .js
        using var tmp = new TempDir();
        var pkgDir = System.IO.Path.Combine(tmp.Path, "p3");
        System.IO.Directory.CreateDirectory(System.IO.Path.Combine(pkgDir, "lib"));
        File.WriteAllText(System.IO.Path.Combine(pkgDir, "package.json"),
            "{ \"bin\": { \"dsh\": \"lib/dsh\" } }");
        File.WriteAllText(System.IO.Path.Combine(pkgDir, "lib", "dsh.js"), "// x");

        Assert.Equal(System.IO.Path.Combine(pkgDir, "lib", "dsh.js"),
            JsEntryResolver.ResolveEntryFromPkgDir(pkgDir, "@deepseek-ai/dsh"));
    }

    [Fact]
    public void ResolveEntryFromPkgDir_MissingBin_ReturnsNull()
    {
        using var tmp = new TempDir();
        var pkgDir = System.IO.Path.Combine(tmp.Path, "p4");
        System.IO.Directory.CreateDirectory(pkgDir);
        File.WriteAllText(System.IO.Path.Combine(pkgDir, "package.json"), "{ \"name\": \"x\" }");

        Assert.Null(JsEntryResolver.ResolveEntryFromPkgDir(pkgDir, "@deepseek-ai/dsh"));
    }

    [Fact]
    public void ResolveEntryFromPkgDir_MissingPackageJson_ReturnsNull()
    {
        using var tmp = new TempDir();
        var pkgDir = System.IO.Path.Combine(tmp.Path, "p5");
        System.IO.Directory.CreateDirectory(pkgDir);

        Assert.Null(JsEntryResolver.ResolveEntryFromPkgDir(pkgDir, "@deepseek-ai/dsh"));
    }
}