using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — issue #24】任务级不变量：无论 dsh 全局安装落在哪个 npm/pnpm 前缀，
/// 启动器都能自动定位它的 JS 入口并产出"可直接拉起服务"的身份。
///
///   Given: dsh 全局安装位于自定义 prefix（非 %APPDATA%\npm），shim 与 node_modules 同父
///   When:  发现层 DiscoverCurrentRuntime
///   Then:  身份 Source=GlobalNpm 且 CanLaunchDirectly=true，DshEntryJsPath 物理真实存在
///
/// 回归背景：旧实现硬编码 %APPDATA%\npm 导致自定义前缀用户永远 E2001（"缺少 start-dsh.vbs"
/// 误导文案），issue #24 报告者 `dsh --version` 正常却无法拉起服务。
/// </summary>
public class GlobalNpmEntryResolutionOutcomes
{
    [Fact]
    public void CustomPrefixGlobalLayout_DiscoveryYieldsLaunchableIdentity_EntryFileExists()
    {
        var pathSave = Environment.GetEnvironmentVariable("PATH");
        var homeSave = Environment.GetEnvironmentVariable("DSH_HOME");
        var urlSave = Environment.GetEnvironmentVariable("DSH_WEB_URL");
        var verSave = Environment.GetEnvironmentVariable("DSH_VERSION");
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "dsh-outcome-prefix-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Given: 自定义前缀（路径刻意含非 %APPDATA% 的目录），shim + sibling node_modules
            var prefix = System.IO.Path.Combine(root, "tools", "npm-global");
            var pkgDir = System.IO.Path.Combine(prefix, "node_modules", "@deepseek-ai", "dsh");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(pkgDir, "lib"));
            File.WriteAllText(System.IO.Path.Combine(pkgDir, "package.json"),
                "{ \"version\": \"3.3.3-fake\", \"bin\": { \"dsh\": \"lib/bin.js\" } }");
            var entryJs = System.IO.Path.Combine(pkgDir, "lib", "bin.js");
            File.WriteAllText(entryJs, "console.log('3.3.3-fake');");
            File.WriteAllText(System.IO.Path.Combine(prefix, "dsh.cmd"), "@echo off\n");
            File.WriteAllBytes(System.IO.Path.Combine(prefix, "node.exe"), Array.Empty<byte>()); // 身份要件

            // When: 该前缀为 PATH 上唯一 shim 来源（隔离宿主全局 dsh 干扰）
            Environment.SetEnvironmentVariable("PATH", prefix);
            Environment.SetEnvironmentVariable("DSH_HOME", System.IO.Path.Combine(root, "home"));
            Environment.SetEnvironmentVariable("DSH_WEB_URL", null);
            Environment.SetEnvironmentVariable("DSH_VERSION", null);
            DshDiscovery.InvalidateCache();
            var identity = DshDiscovery.DiscoverCurrentRuntime();

            // Then: 可直启身份 + 物理入口真实存在
            Assert.Equal(DshSource.GlobalNpm, identity.Source);
            Assert.True(identity.CanLaunchDirectly, "自定义 prefix 布局必须可直启（issue #24 不变量）");
            Assert.Equal(entryJs, identity.DshEntryJsPath);
            Assert.True(File.Exists(identity.DshEntryJsPath));
            Assert.Contains("lib" + System.IO.Path.DirectorySeparatorChar + "bin.js", identity.DshEntryJsPath!,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", pathSave);
            Environment.SetEnvironmentVariable("DSH_HOME", homeSave);
            Environment.SetEnvironmentVariable("DSH_WEB_URL", urlSave);
            Environment.SetEnvironmentVariable("DSH_VERSION", verSave);
            DshDiscovery.InvalidateCache();
            try { System.IO.Directory.Delete(root, recursive: true); } catch { }
        }
    }
}