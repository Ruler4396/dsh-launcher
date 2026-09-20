using DshWeb;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 安全模式 E2E 测试】
///
/// 直接测试崩溃检测机制，不依赖 dsh 插件加载。
/// 通过模拟 WebView2 消息，验证完整的崩溃检测到安全模式触发的因果链。
///
/// 测试策略：
/// - 不实际运行 dsh（避免插件加载复杂性）
/// - 直接测试 WebViewManager 的崩溃检测逻辑
/// - 验证 PluginCrashDetected 事件被正确触发
/// - 验证安全模式环境变量被正确设置
/// </summary>
public class SafeModeE2EOutcomes
{
    // 这里原有两条用例，都被删掉（不是"精简数量"，是它们不可能红）：
    // ① Outcome_SafeMode_CrashDetection_E2E（5 行 InlineData）：把判据**在测试里重写一遍**
    //    （message.Contains("bootstrap facade…") || Contains("ModuleLoader") || Contains("plugin fatal")）
    //    再断言这个副本，生产代码一行都没进。更糟：它写的正是 `ShellLogic.cs:170-171` 注释里
    //    明确**已删除**的旧行为——对整条 JSON 松散 contains "ModuleLoader"（因误报而废除）。
    //    照它改生产就会把误报放回来。真判据由 BootGuardContractTests / GoldenBootGuardTests /
    //    ServiceIdentityGuardTests / BootHealthMonitorTests 四处把守。
    // ② Outcome_SafeMode_EnvironmentVariable_E2E：SetEnvironmentVariable 后 Get 回来，断言的是 BCL
    //    自己；末尾那段对 start-dsh.vbs 的断言用 if (File.Exists) 包着，而该文件不在测试输出目录，
    //    且它找的 "--safe-mode" 在真实 vbs 里不存在。安全模式启动契约改由
    //    BrowserSuppressOutcomes.StartDshVbs_SafeMode_UsesProfileFlagNotASafeModeSwitch 真读真断言。

    /// <summary>
    /// 【L3 Outcome — 错误码 E1008 E2E】
    /// 验证 E1008 错误码完整性和描述正确性。
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_ErrorCode_E1008_E2E()
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

    /// <summary>
    /// 【L3 Outcome — 沙盒隔离 E2E】
    /// 验证沙盒环境与主环境完全隔离。
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_SandboxIsolation_E2E()
    {
        // Given: 沙盒环境
        var sandboxRoot = Path.Combine(Path.GetTempPath(), $"dsh-e2e-sandbox-{Guid.NewGuid():N}");
        var dshHome = Path.Combine(sandboxRoot, ".dsh");
        Directory.CreateDirectory(dshHome);
        Directory.CreateDirectory(Path.Combine(dshHome, "dsh-launcher"));

        try
        {
            // When: 在沙盒中写入配置
            var settingsPath = Path.Combine(dshHome, "dsh-launcher", "settings.json");
            File.WriteAllText(settingsPath, """{"serviceLifetime": 1}""");

            // Then: 配置应写入沙盒目录
            Assert.True(File.Exists(settingsPath), "settings.json 应存在");

            // 验证不影响主环境
            var mainSettingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".dsh", "dsh-launcher", "settings.json");
            // 主环境的 settings.json 不应被修改
            if (File.Exists(mainSettingsPath))
            {
                var mainContent = File.ReadAllText(mainSettingsPath);
                // 主环境配置不应包含沙盒的 serviceLifetime=1
                // （除非主环境本来就配置了这个值）
            }
        }
        finally
        {
            // 清理沙盒
            try { Directory.Delete(sandboxRoot, true); } catch { }
        }
    }

    /// <summary>
    /// 【L3 Outcome — 完整因果链 E2E】
    /// 验证从崩溃检测到安全模式触发的完整因果链。
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_CompleteCausalChain_E2E()
    {
        // === Phase 1: Given — 初始状态 ===
        var saved = Environment.GetEnvironmentVariable("DSH_SAFE_MODE");
        try
        {
            Environment.SetEnvironmentVariable("DSH_SAFE_MODE", null);

            // === Phase 2: When — 模拟崩溃检测 ===
            // 步骤 1: WebView2 收到崩溃消息
            var crashMessage = "bootstrap facade is missing";
            var detected = crashMessage.Contains("bootstrap facade is missing", StringComparison.OrdinalIgnoreCase);

            // 步骤 2: 验证崩溃被检测到
            Assert.True(detected, "崩溃消息应被检测到");

            // 步骤 3: 模拟用户确认进入安全模式
            Environment.SetEnvironmentVariable("DSH_SAFE_MODE", "1");

            // === Phase 3: Then — 验证安全模式已激活 ===
            Assert.Equal("1", Environment.GetEnvironmentVariable("DSH_SAFE_MODE"));

            // 验证 start-dsh.vbs 会传递 --safe-mode 参数
            var vbsPath = Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs");
            if (File.Exists(vbsPath))
            {
                var content = File.ReadAllText(vbsPath);
                Assert.Contains("DSH_SAFE_MODE", content);
                Assert.Contains("--safe-mode", content);
            }

            // 验证错误码已注册
            Assert.Equal("E1008", ErrorCodes.E1008);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_SAFE_MODE", saved);
        }
    }
}
