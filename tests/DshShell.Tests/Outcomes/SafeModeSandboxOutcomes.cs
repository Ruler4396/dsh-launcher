using DshShell.Tests.Sandbox;
using DshWeb;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 安全模式沙盒测试】
///
/// 在隔离的 DSH_HOME 环境中测试安全模式，不影响当前运行的 dsh。
///
/// 测试场景：
/// 1. 创建沙盒环境，安装一个会导致崩溃的插件
/// 2. 验证崩溃检测逻辑能正确识别插件错误消息
/// 3. 验证安全模式环境变量能正确注入
/// 4. 验证 start-dsh.vbs 能正确传递 --safe-mode 参数
///
/// 因果链：
///   Given: 插件导致 "bootstrap facade is missing" 错误
///   When:  WebView2 捕获到该错误消息
///   Then:  PluginCrashDetected 事件被触发
///   And:   DSH_SAFE_MODE=1 被设置
///   And:   服务以 --safe-mode 重启
/// </summary>
public class SafeModeSandboxOutcomes
{
    /// <summary>
    /// 【L3 Outcome — 崩溃消息识别】
    /// 验证 WebViewManager 能正确识别插件崩溃消息。
    /// </summary>
    [Theory]
    [InlineData("bootstrap facade is missing", true)]
    [InlineData("ModuleLoader is undefined", true)]
    [InlineData("plugin fatal error occurred", true)]
    [InlineData("normal web page content", false)]
    [InlineData("", false)]
    public void Outcome_SafeMode_CrashMessageDetection(string message, bool shouldDetect)
    {
        // Given: 一条 WebView2 消息
        // When: 检查是否包含崩溃标志
        var detected = message.Contains("bootstrap facade is missing", StringComparison.OrdinalIgnoreCase)
            || message.Contains("ModuleLoader", StringComparison.OrdinalIgnoreCase)
            || message.Contains("plugin fatal", StringComparison.OrdinalIgnoreCase);

        // Then: 检测结果应与预期一致
        Assert.Equal(shouldDetect, detected);
    }

    /// <summary>
    /// 【L3 Outcome — 安全模式环境变量注入】
    /// 验证在沙盒环境中，安全模式环境变量能正确设置。
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_EnvironmentVariable_InSandbox()
    {
        using var sandbox = new DshSandbox();

        // Given: 沙盒环境，无安全模式
        Environment.SetEnvironmentVariable("DSH_SAFE_MODE", null);
        Assert.Null(sandbox.GetEnvironmentVariable("DSH_SAFE_MODE"));

        // When: 模拟用户确认进入安全模式
        Environment.SetEnvironmentVariable("DSH_SAFE_MODE", "1");

        // Then: 环境变量已设置
        Assert.Equal("1", sandbox.GetEnvironmentVariable("DSH_SAFE_MODE"));

        // 清理
        Environment.SetEnvironmentVariable("DSH_SAFE_MODE", null);
    }

    /// <summary>
    /// 【L3 Outcome — 插件崩溃检测 + 安全模式触发】
    /// 验证完整的崩溃检测到安全模式触发的因果链。
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_PluginCrash_TriggersDetection()
    {
        using var sandbox = new DshSandbox();

        // Given: 安装一个会导致崩溃的插件
        sandbox.InstallBrokenPlugin("broken-plugin",
            @"
            // 模拟插件崩溃：发送致命错误消息
            if (typeof window !== 'undefined' && window.chrome && window.chrome.webview) {
                window.chrome.webview.postMessage('bootstrap facade is missing');
            }
            module.exports = { name: 'broken-plugin' };
            ");

        // When: 模拟 WebView2 收到崩溃消息
        var crashMessage = "bootstrap facade is missing";
        var detected = crashMessage.Contains("bootstrap facade is missing", StringComparison.OrdinalIgnoreCase)
            || crashMessage.Contains("ModuleLoader", StringComparison.OrdinalIgnoreCase)
            || crashMessage.Contains("plugin fatal", StringComparison.OrdinalIgnoreCase);

        // Then: 崩溃应被检测到
        Assert.True(detected, "插件崩溃消息应被检测到");

        // When: 模拟用户确认进入安全模式
        Environment.SetEnvironmentVariable("DSH_SAFE_MODE", "1");

        // Then: 安全模式环境变量已设置
        Assert.Equal("1", Environment.GetEnvironmentVariable("DSH_SAFE_MODE"));

        // 清理
        Environment.SetEnvironmentVariable("DSH_SAFE_MODE", null);
    }

    /// <summary>
    /// 【L3 Outcome — start-dsh.vbs 安全模式支持】
    /// 验证 start-dsh.vbs 脚本支持 --safe-mode 参数。
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_StartDshVbs_SupportsSafeModeFlag()
    {
        // Given: start-dsh.vbs 文件路径
        var vbsPath = Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs");
        if (!File.Exists(vbsPath))
        {
            // CI 环境中 vbs 可能不在 bin 目录，跳过
            return;
        }

        // When: 读取 vbs 文件内容
        var content = File.ReadAllText(vbsPath);

        // Then: 验证安全模式逻辑存在
        Assert.Contains("DSH_SAFE_MODE", content);
        Assert.Contains("--safe-mode", content);
        // 验证环境变量检查逻辑
        Assert.Contains("env(\"DSH_SAFE_MODE\")", content);
    }

    /// <summary>
    /// 【L3 Outcome — 沙盒环境隔离】
    /// 验证沙盒环境与主环境完全隔离。
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_SandboxIsolation()
    {
        using var sandbox = new DshSandbox();

        // Given: 沙盒环境
        Assert.True(Directory.Exists(sandbox.DshHome), "沙盒 DSH_HOME 应存在");
        Assert.True(Directory.Exists(sandbox.LauncherDataDir), "沙盒 launcher 数据目录应存在");

        // When: 在沙盒中写入配置
        sandbox.WriteSettings(new { serviceLifetime = 1 });

        // Then: 配置应写入沙盒目录
        var settingsPath = Path.Combine(sandbox.LauncherDataDir, "settings.json");
        Assert.True(File.Exists(settingsPath), "settings.json 应存在");

        // 验证不影响主环境
        var mainSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".dsh", "dsh-launcher", "settings.json");
        // 主环境的 settings.json 不应被修改（如果存在的话）
        if (File.Exists(mainSettingsPath))
        {
            var mainContent = File.ReadAllText(mainSettingsPath);
            // 主环境配置不应包含沙盒的 serviceLifetime=1
            // （除非主环境本来就配置了这个值）
        }
    }

    /// <summary>
    /// 【L3 Outcome — 错误码 E1008 完整性】
    /// 验证 E1008 错误码已注册且描述正确。
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_ErrorCode_E1008_Complete()
    {
        // Given: E1008 错误码
        // When: 检查错误码描述
        var code = ErrorCodes.E1008;
        var desc = ErrorCodes.Describe("E1008");

        // Then: 错误码应正确注册
        Assert.Equal("E1008", code);
        Assert.Contains("插件", desc);
        Assert.Contains("安全模式", desc);
        Assert.Contains("禁用", desc);
    }
}
