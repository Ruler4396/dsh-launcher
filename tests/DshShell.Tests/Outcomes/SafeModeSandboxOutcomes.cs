using DshShell.Tests.Sandbox;
using DshWeb;
using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 安全模式沙盒契约】
///
/// 只保留**碰得到生产符号**的用例。2026-09-21 质量审查（B2）删掉了本族其余形状，
/// 判决与替代把守者（删掉之后 CI 少挡什么——逐条答）：
/// ① CrashMessageDetection Theory（5 行 InlineData，含 "ModuleLoader is undefined"→true）：
///    在测试里重写**已被 F16 删除**的旧匹配规则自比对——生产判据是
///    <c>ShellLogic.WebViewPolicy.IsPluginCrashMessage</c>，对 "ModuleLoader" 精确判**否**；
///    这条"回归钉"会把误报缺陷钉回来（可红性铁律点名的最坏形状）。真判据由
///    <c>ServiceIdentityGuardTests.IsPluginCrashMessage_OnlyMatchesExactFatalPhrases_F16</c> 把守。
/// ② EnvironmentVariable_InSandbox / PluginCrash_TriggersDetection / （同族 SafeModeOutcomes.cs、
///    SafeModeE2EOutcomes.cs 全部）：断言的是 <c>DSH_SAFE_MODE</c> 这个**生产全仓零引用**的幽灵
///    环境变量（Set/Get 自比 = 测 BCL）。安全模式真形态 = <c>SafeModeState</c> 粘滞落盘 →
///    <c>SafeModeLaunchPolicy.Decorate</c> 套 <c>--profile</c>，由 SafeModeStateTests /
///    SafeModeSymmetryOutcomes / SafeModeLifecycleTests 把守。
/// ③ CompleteCausalChain_E2E：路径守卫 <c>if (File.Exists(BaseDirectory\start-dsh.vbs))</c> 使
///    断言从未执行，而断言的 "--safe-mode" 在真实 vbs 里根本不存在（真跑必红）——铁律 2026-09-20
///    事故清单里"已删"的那条**原位复发**。vbs 契约改由
///    <c>BrowserSuppressOutcomes.StartDshVbs_SafeMode_UsesProfileFlagNotASafeModeSwitch</c>
///    经 RepoFile 真读真断言（本文件不再重复）。
/// ④ E1008 describe 断言曾在三个文件各一份（逐字节重复型），收口为本文件唯一一份。
///
/// 本文件保住的两条：
/// - 沙盒隔离的物理形状：生产 SafeModeState 写沙盒 DSH_HOME，主环境字节分毫不动；
/// - E1008 错误码注册与描述契约。
/// </summary>
public class SafeModeSandboxOutcomes
{
    /// <summary>
    /// 【L3 Outcome — 沙盒环境隔离】生产的 SafeModeState 落盘只准碰沙盒 DSH_HOME：
    /// 沙盒内原子写 + 重载可读 + 梯级保真是生产行为；主环境 safe-mode.json 字节序列
    /// 不得被创建/改写——这是 [SANDBOX] 门控的**可红**表达（门控回归即红，不靠肉眼）。
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_SandboxIsolation()
    {
        using var sandbox = new DshSandbox();

        var mainStorePath = SafeModeState.DefaultStorePath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh"));
        var mainBefore = File.Exists(mainStorePath) ? File.ReadAllBytes(mainStorePath) : null;
        try
        {
            // When: 用生产实现激活安全模式粘滞状态（落盘在沙盒 DSH_HOME 下）
            var storePath = SafeModeState.DefaultStorePath(sandbox.DshHome);
            new SafeModeState(storePath).Activate(SafeProfileTier.Tier2Minimal);

            // Then 1: 物理文件落在沙盒路径，且经构造函数重载后状态保真
            Assert.True(File.Exists(storePath), "safe-mode.json 应由生产 SafeModeState 原子落到沙盒 DSH_HOME");
            var reloaded = new SafeModeState(storePath);
            Assert.True(reloaded.IsActive, "重载后仍应处于激活态（粘滞语义）");
            Assert.Equal(SafeProfileTier.Tier2Minimal, reloaded.Tier);

            // Then 2: 主环境零触碰——不存在保持不存在，存在则字节分毫不动
            var mainAfter = File.Exists(mainStorePath) ? File.ReadAllBytes(mainStorePath) : null;
            Assert.True(mainBefore is null
                    ? mainAfter is null
                    : mainBefore.SequenceEqual(mainAfter ?? Array.Empty<byte>()),
                "沙盒内的 SafeModeState 落盘触碰/改写了主环境 safe-mode.json —— 沙盒门控失效");
        }
        finally
        {
            var leaked = File.Exists(mainStorePath) ? File.ReadAllBytes(mainStorePath) : null;
            Assert.True(mainBefore is null ? leaked is null
                : mainBefore.SequenceEqual(leaked ?? Array.Empty<byte>()),
                "用例失败也不得留下被改写的用户配置");
        }
    }

    /// <summary>
    /// 【L3 Outcome — 错误码 E1008 完整性】E1008 已注册且描述带得出"插件/安全模式/禁用"三要素。
    /// （全仓唯一一份——见头注 ④。）
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_ErrorCode_E1008_Complete()
    {
        var code = ErrorCodes.E1008;
        var desc = ErrorCodes.Describe("E1008");

        Assert.Equal("E1008", code);
        Assert.Contains("插件", desc);
        Assert.Contains("安全模式", desc);
        Assert.Contains("禁用", desc);
    }
}
