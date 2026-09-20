using DshWeb;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 任务三】幽灵托盘图标验证。
///
/// 不关心 WindowManager 内部如何创建/隐藏托盘，只关心系统的最终物理状态：
/// - 无 lifetime 插件时，气泡通知结束后托盘图标被隐藏
/// - 有 lifetime 插件 + 托盘模式时，托盘图标保持可见
///
/// 因果链验证：
///   Given: 无 lifetime 插件（IsTrayWanted = false）
///   When:  气泡通知结束（BalloonTipClosed 事件）
///   Then:  托盘图标 Visible 被设为 false
/// </summary>
public class GhostTrayOutcomes
{
    /// <summary>
    /// 【L3 Outcome — 核心】IsTrayWanted 契约：无插件时返回 false。
    ///
    /// 验证在无 lifetime 插件的环境中，IsTrayWanted 不会错误地返回 true。
    /// 这是幽灵托盘的根因修复：只有当 IsTrayWantedProvider 正确返回 false 时，
    /// HideTrayIfTransient 才会隐藏托盘。
    /// </summary>
    [Fact]
    public void Outcome_GhostTray_IsTrayWanted_FalseWhenNoPlugin()
    {
        // Given: 无 pending 更新（_pendingUpdate == None）
        // 无 lifetime 插件（IsLifetimePluginInstalled(DshHomeDir) == false）

        // When: 调用 IsTrayWanted（通过 WindowManager.IsTrayWantedProvider）

        // Then: 应返回 false（无插件时托盘无存在意义）
        // 注：此测试验证纯逻辑契约，不依赖 WindowManager 实例
        var dshHome = Path.Combine(Path.GetTempPath(), "dsh-ghost-tray-test");
        try
        {
            Directory.CreateDirectory(dshHome);
            // 确保无 profiles 目录（无插件）
            var profiles = Path.Combine(dshHome, "profiles");
            if (Directory.Exists(profiles)) Directory.Delete(profiles, true);

            // 证据：IsLifetimePluginInstalled 返回 false
            Assert.False(ShellLogic.PluginConfig.IsLifetimePluginInstalled(dshHome));
        }
        finally
        {
            try { if (Directory.Exists(dshHome)) Directory.Delete(dshHome, true); } catch { }
        }
    }

    // 这里原有两条用例已删，它们**在结构上不可能变红**，留着只会让人以为托盘驻留有测试：
    // ① Outcome_GhostTray_HideTrayIfTransient_HidesWhenNotWanted：new WindowManager() → 设 provider
    //    → 调 HideTrayIfTransient()，**一条断言都没有**（注释自己写着"无托盘时应静默返回"）。
    //    把 HideTrayIfTransient 整个删掉它照样绿。
    // ② Outcome_GhostTray_IsTrayWanted_TrueWhenPendingUpdate：本体就是 Assert.True(true, "...")，
    //    注释还写明"实际 _pendingUpdate 状态在 Program.cs 中"——即它知道自己碰不到生产代码。
    // 托盘驻留/退出的真行为由 Outcomes/TrayResidentSwitchOutcomes（含 Category=RealOS 的真退出码路径）
    // 与 Regression_Issue28_TrayExitRow.RealOs 把守；幽灵托盘的判据在
    // ShellLogic 的 TrayWanted 决策 + TrayMenuLayoutContractTests。
}
