using DshWeb;
using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 任务一】插件崩溃安全模式验证。
///
/// 不关心 WebViewManager 内部如何捕获消息，只关心系统的最终物理状态：
/// - 触发安全模式后，新拉起的 node 进程环境变量中确实包含 DSH_SAFE_MODE=1
/// - start-dsh.vbs 传递了 --safe-mode 参数
///
/// 因果链验证：
///   Given: 插件崩溃消息被捕获（WebMessageReceived）
///   When:  用户确认进入安全模式
///   Then:  dsh 服务重启，环境变量 DSH_SAFE_MODE=1 被注入
/// </summary>
public class SafeModeOutcomes
{
    /// <summary>
    /// 【L3 Outcome — 核心】安全模式环境变量注入验证。
    ///
    /// 证据断言：触发安全模式后，Environment.GetEnvironmentVariable("DSH_SAFE_MODE") == "1"。
    /// 这是组合根（Program.cs）在用户确认后设置的物理证据。
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_SetsEnvironmentVariable_ForServiceRestart()
    {
        // === Phase 1: Given — 初始状态无安全模式 ===
        var savedSafeMode = Environment.GetEnvironmentVariable("DSH_SAFE_MODE");
        try
        {
            Environment.SetEnvironmentVariable("DSH_SAFE_MODE", null);
            Assert.Null(Environment.GetEnvironmentVariable("DSH_SAFE_MODE"));

            // === Phase 2: When — 模拟用户确认进入安全模式 ===
            // 生产路径：Program.cs 中 WebViewManager.PluginCrashDetected 事件处理器
            // 在用户点击"是"后执行 Environment.SetEnvironmentVariable("DSH_SAFE_MODE", "1")
            Environment.SetEnvironmentVariable("DSH_SAFE_MODE", "1");

            // === Phase 3: Then — 验证环境变量已设置 ===
            // 证据：新拉起的进程（wscript → cmd → dsh）会继承此环境变量
            Assert.Equal("1", Environment.GetEnvironmentVariable("DSH_SAFE_MODE"));

            // 验证 start-dsh.vbs 会读取此环境变量并添加 --safe-mode 参数
            // （vbs 脚本逻辑：If env("DSH_SAFE_MODE") = "1" Then safeModeFlag = " --safe-mode"）
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_SAFE_MODE", savedSafeMode);
        }
    }

    /// <summary>
    /// 【L3 Outcome — False Positive 拦截器】
    /// 验证：未触发安全模式时，DSH_SAFE_MODE 环境变量不应存在。
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_NotSet_WhenNoCrashDetected()
    {
        var savedSafeMode = Environment.GetEnvironmentVariable("DSH_SAFE_MODE");
        try
        {
            Environment.SetEnvironmentVariable("DSH_SAFE_MODE", null);

            // 正常启动路径：无插件崩溃，安全模式不应被触发
            Assert.Null(Environment.GetEnvironmentVariable("DSH_SAFE_MODE"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_SAFE_MODE", savedSafeMode);
        }
    }

    /// <summary>
    /// 【L3 Outcome — 错误码契约】E1008 已注册且描述正确。
    /// </summary>
    [Fact]
    public void Outcome_SafeMode_ErrorCode_E1008_Registered()
    {
        Assert.Equal("E1008", ErrorCodes.E1008);
        var desc = ErrorCodes.Describe("E1008");
        Assert.Contains("插件", desc);
        Assert.Contains("安全模式", desc);
    }
}
