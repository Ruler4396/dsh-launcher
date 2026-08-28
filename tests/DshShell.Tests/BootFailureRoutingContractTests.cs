using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 启动失败恢复路由契约测试（2026-08-25 插件致崩事故回归的纯逻辑构件）：
/// - <see cref="ShellLogic.BootRecoveryPolicy"/>：安全模式 vs 重启服务的路由决策表；
/// - <c>BootGuard.LogEvidenceIndicatesPlugin</c>：日志层证据的插件归因签名；
/// - <c>PluginConfig.BundlesDeclareThirdParty</c>：用户清单是否声明第三方插件。
/// 事故链条：插件把 node 服务进程搞崩（exit=1）→ 页面层无证据、日志层因管道 Bug 失明 →
/// 分类闸门判"与插件无关"→ 三次会话循环弹"重启服务"。本组测试锁定修复后的判定语义。
/// </summary>
public class BootFailureRoutingContractTests
{
    // ==================== BootRecoveryPolicy 决策表 ====================

    [Fact]
    public void Policy_PluginInvolved_AlwaysRoutesToSafeMode()
    {
        Assert.Equal(ShellLogic.BootRecoveryPolicy.RecoveryAsk.AskSafeMode,
            ShellLogic.BootRecoveryPolicy.Decide(pluginInvolved: true, consecutiveFailures: 1));
        Assert.Equal(ShellLogic.BootRecoveryPolicy.RecoveryAsk.AskSafeMode,
            ShellLogic.BootRecoveryPolicy.Decide(pluginInvolved: true, consecutiveFailures: 99));
    }

    [Fact]
    public void Policy_AnonymousFailures_BelowThreshold_RouteToRestart()
    {
        // 单次/两次匿名失败：重启是合理轻量恢复（2026-08 用户回归：无插件弹安全模式是误导）
        Assert.Equal(ShellLogic.BootRecoveryPolicy.RecoveryAsk.AskRestartService,
            ShellLogic.BootRecoveryPolicy.Decide(pluginInvolved: false, consecutiveFailures: 1));
        Assert.Equal(ShellLogic.BootRecoveryPolicy.RecoveryAsk.AskRestartService,
            ShellLogic.BootRecoveryPolicy.Decide(pluginInvolved: false, consecutiveFailures: 2));
    }

    [Fact]
    public void Policy_AnonymousFailures_AtThreshold_EscalateToSafeMode()
    {
        // 事故实测形态：连续 ≥3 次失败仍只问重启——重启对确定性配置崩溃必然无效
        var threshold = ShellLogic.BootRecoveryPolicy.AnonymousFailureSafeModeThreshold;
        Assert.Equal(ShellLogic.BootRecoveryPolicy.RecoveryAsk.AskSafeMode,
            ShellLogic.BootRecoveryPolicy.Decide(pluginInvolved: false, consecutiveFailures: threshold));
        Assert.Equal(ShellLogic.BootRecoveryPolicy.RecoveryAsk.AskSafeMode,
            ShellLogic.BootRecoveryPolicy.Decide(pluginInvolved: false, consecutiveFailures: threshold + 5));
    }

    // ==================== 日志层插件归因签名 ====================

    [Theory]
    [InlineData("服务日志命中启动错误签名「Cannot find module」", "Error: Cannot find module 'dsh-notification'")]
    [InlineData("服务日志命中启动错误签名「plugin load failed」", null)]
    [InlineData("", "MODULE_NOT_FOUND")]
    [InlineData("PLUGIN FATAL: boom", null)]
    public void LogEvidence_PluginMarkers_Detected(string summary, string? detail)
    {
        Assert.True(ShellLogic.BootGuard.LogEvidenceIndicatesPlugin(summary + " " + detail));
    }

    [Fact]
    public void LogEvidence_NonPluginMarkers_NotAttributed()
    {
        // 通用环境错误不归因插件（保守防误导：EACCES/npm ERR/FATAL ERROR 属于 BootErrorMarkers
        // 但不在插件子集里）
        Assert.False(ShellLogic.BootGuard.LogEvidenceIndicatesPlugin(
            "服务日志命中启动错误签名「EACCES」 permission denied"));
        Assert.False(ShellLogic.BootGuard.LogEvidenceIndicatesPlugin(
            "npm error code ELIFECYCLE"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LogEvidence_EmptyInput_SafeFalse(string? text)
    {
        Assert.False(ShellLogic.BootGuard.LogEvidenceIndicatesPlugin(text));
    }

    // ==================== 第三方插件在场判定（真实事故清单形态） ====================

    [Fact]
    public void Bundles_IncidentManifest_DeclaresThirdParty()
    {
        // 2026-08-25 事故现场 package.json 原文形态：file: 本地依赖 + 混合 bundles
        const string incident =
            """
            {
              "name": "dsh-profile-web",
              "private": true,
              "dependencies": {
                "dsh-launcher-lifetime": "file:E:/dsh-plugins/dsh-launcher-lifetime",
                "dsh-notification": "file:E:/dsh-plugins/dsh-notification",
                "dsh-web-search-anysearch": "file:E:/dsh-plugins/dsh-web-search-anysearch",
                "dsh-zh-guide": "file:E:/dsh-plugins/dsh-zh-guide"
              },
              "dsh": { "profile": { "bundles": [
                "@deepseek-ai/dsh-base",
                "@deepseek-ai/dsh-web-app",
                "dsh-launcher-lifetime",
                "dsh-notification",
                "dsh-web-search-anysearch",
                "dsh-zh-guide"
              ] } }
            }
            """;
        Assert.True(ShellLogic.PluginConfig.BundlesDeclareThirdParty(incident));
    }

    [Fact]
    public void Bundles_CoreOnlyManifest_NoThirdParty()
    {
        const string coreOnly =
            """{"dsh":{"profile":{"bundles":["@deepseek-ai/dsh-base","@deepseek-ai/dsh-web-app"]}}}""";
        Assert.False(ShellLogic.PluginConfig.BundlesDeclareThirdParty(coreOnly));

        const string coreDeps =
            """{"dependencies":{"@deepseek-ai/dsh":"^1.0.0"}}""";
        Assert.False(ShellLogic.PluginConfig.BundlesDeclareThirdParty(coreDeps));
    }

    [Fact]
    public void Bundles_ThirdPartyInDependenciesAlone_Detected()
    {
        // 用户可能只在 dependencies 声明而 bundles 尚未同步——同样算"第三方在场"
        const string depsOnly =
            """{"dependencies":{"dsh-notification":"file:E:/dsh-plugins/dsh-notification"}}""";
        Assert.True(ShellLogic.PluginConfig.BundlesDeclareThirdParty(depsOnly));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{ this is not valid json ")]
    [InlineData("[]")]
    public void Bundles_InvalidManifest_SafeFalse(string? json)
    {
        Assert.False(ShellLogic.PluginConfig.BundlesDeclareThirdParty(json));
    }
}
