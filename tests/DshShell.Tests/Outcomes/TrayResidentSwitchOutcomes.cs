using DshWeb;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome + Regression_TrayResidentSwitchAtRuntime】托盘驻留"运行中切换"回归。
///
/// Bug 复现（v0.4.1 及之前）：托盘委托（IsTrayWantedProvider 等）只在启动时
/// mode==Tray 的分支内装配，且 FormClosing 拦截要求 TrayIcon 已存在——用户在设置页
/// 运行中切到"托盘驻留"后：托盘不出现、关窗直接整壳退出，与设置页"立即生效"承诺相悖。
///
/// 修复后契约：
/// 1. EnsureTrayIcon 的创建门由 IsTrayWantedProvider 动态决定（每次现读配置）；
/// 2. 启动时未创建（非 Tray 模式），运行中切换后再调 EnsureTrayIcon 必须真实创建；
/// 3. 决策仍由纯函数 LifecycleDecisions.ShouldInterceptCloseToTray 承担（契约测试见 ShellLogicTests）。
///
/// 零 Mock：使用真实 WinForms NotifyIcon（真实系统托盘注册），真实临时目录布局。
/// </summary>
public class TrayResidentSwitchOutcomes
{
    [Fact]
    [Trait("Category", "RealOS")]
    public void Regression_TrayResident_IconCreatedOnDemandAfterRuntimeSwitch()
    {
        var wm = new WindowManager();
        try
        {
            using var form = new System.Windows.Forms.Form(); // 真实窗体（无需显示）
            bool trayWanted = false; // 启动时配置为"跟随窗口"
            wm.IsTrayWantedProvider = () => trayWanted;
            wm.TrayWhaleIconProvider = () => System.Drawing.SystemIcons.Application;

            // When: 启动阶段调用（跟随窗口模式）→ 不创建托盘
            wm.EnsureTrayIcon(form);

            // Then: 无托盘（旧壳在此状态下关窗 = 直接退出 → 即用户报告的 Bug）
            Assert.Null(wm.TrayIcon);

            // When: 用户在设置页切到"托盘驻留"后关窗 → FormClosing 按需补建
            trayWanted = true;
            wm.EnsureTrayIcon(form);

            // Then: 托盘真实创建（真实 NotifyIcon 注册进系统托盘）
            Assert.NotNull(wm.TrayIcon);
        }
        finally
        {
            wm.DisposeTray(); // 清理真实托盘图标，不留幽灵图标
        }
    }

    [Fact]
    [Trait("Category", "RealOS")]
    public void Regression_TrayResident_ExitRequested_CloseNotInterceptedAgain()
    {
        var wm = new WindowManager();
        try
        {
            using var form = new System.Windows.Forms.Form();
            wm.IsTrayWantedProvider = () => true;
            wm.TrayWhaleIconProvider = () => System.Drawing.SystemIcons.Application;
            wm.EnsureTrayIcon(form);
            Assert.NotNull(wm.TrayIcon);

            // When: 用户在托盘菜单点"退出"后再次触发关闭流程
            wm.MarkTrayExitRequested();

            // Then: 纯决策函数放行真实关闭（不再隐藏到托盘）——矩阵 L1 契约保持
            Assert.False(ShellLogic.LifecycleDecisions.ShouldInterceptCloseToTray(
                ShellLogic.ServiceLifetime.Tray, wm.TrayExitRequested),
                "TrayExitRequested=true 时不得再拦截关窗");
        }
        finally
        {
            wm.DisposeTray();
        }
    }

    [Fact]
    public void Outcome_TrayResident_DecisionRequiresTrayMode()
    {
        // 快速契约：仅 Tray 模式且未请求退出才拦截；AlwaysOn/FollowWindow 一律放行。
        Assert.True(ShellLogic.LifecycleDecisions.ShouldInterceptCloseToTray(
            ShellLogic.ServiceLifetime.Tray, trayExitRequested: false));
        Assert.False(ShellLogic.LifecycleDecisions.ShouldInterceptCloseToTray(
            ShellLogic.ServiceLifetime.AlwaysOn, trayExitRequested: false));
        Assert.False(ShellLogic.LifecycleDecisions.ShouldInterceptCloseToTray(
            ShellLogic.ServiceLifetime.FollowWindow, trayExitRequested: false));
        Assert.False(ShellLogic.LifecycleDecisions.ShouldInterceptCloseToTray(
            ShellLogic.ServiceLifetime.Tray, trayExitRequested: true));
    }
}
