using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// v0.4.0 T1/T2 纯函数矩阵：
/// - <see cref="ShellLogic.ShouldStopServiceOnClose"/>：关窗/托盘退出是否停服务（"接管即负责"）；
/// - <see cref="ShellLogic.ResolvePendingUpdateAction"/>：启动早期待应用更新处理决策（端口开着不静默跳过）。
/// 全部纯函数、Headless、无真实进程/网络依赖。
/// </summary>
public class ShellLogicServiceLifecycleTests
{
    // ---------------- T1: ShouldStopServiceOnClose（矩阵 M1，≥6 例） ----------------

    [Theory]
    [InlineData(2, false, true, true)]   // FollowWindow + 壳管理 + 非外部托管 → 停
    [InlineData(2, true, true, false)]   // 外部托管 → 恒不停（服务归外部管）
    [InlineData(2, false, false, false)] // 非壳管理（外部手动起）→ 不停
    [InlineData(0, false, true, false)]  // AlwaysOn → 不停
    [InlineData(1, false, true, false)]  // Tray（关窗隐藏）→ 不停
    [InlineData(0, true, true, false)]   // AlwaysOn + 外部托管 → 不停
    public void ShouldStopServiceOnClose_Matrix(int modeInt, bool external, bool shellManaged, bool expected)
    {
        var mode = (ShellLogic.ServiceLifetime)modeInt; // ServiceLifetime 为 internal，参数用 int 规避 CS0051
        Assert.Equal(expected, ShellLogic.ShouldStopServiceOnClose(mode, external, shellManaged));
    }

    // 语义回归：接管即负责——TryAdoptOrphanService 成功后 shellManaged=true，跟随窗口关窗必须停
    [Fact]
    public void AdoptedOrphan_FollowWindow_StopsService()
        => Assert.True(ShellLogic.ShouldStopServiceOnClose(
            ShellLogic.ServiceLifetime.FollowWindow, externallyManaged: false, shellManaged: true));

    // ---------------- T2: ResolvePendingUpdateAction（矩阵 U2，≥4 例） ----------------

    [Fact]
    public void NoPending_ReturnsNone()
        => Assert.Equal(ShellLogic.PendingUpdateAction.None,
            ShellLogic.ResolvePendingUpdateAction(false, true, "0.1.0-rc.6", null));

    [Fact]
    public void Pending_PortClosed_ReturnsApplyNow()
        => Assert.Equal(ShellLogic.PendingUpdateAction.ApplyNow,
            ShellLogic.ResolvePendingUpdateAction(true, false, null, "0.1.0-rc.7"));

    [Fact]
    public void Pending_PortOpen_VersionEqual_ReturnsClearPending()
        => Assert.Equal(ShellLogic.PendingUpdateAction.ClearPending,
            ShellLogic.ResolvePendingUpdateAction(true, true, "0.1.0-rc.7", "0.1.0-rc.7"));

    [Fact]
    public void Pending_PortOpen_VersionDifferent_ReturnsPromptRestart()
        => Assert.Equal(ShellLogic.PendingUpdateAction.PromptRestart,
            ShellLogic.ResolvePendingUpdateAction(true, true, "0.1.0-rc.6", "0.1.0-rc.7"));

    // 补充边界：运行版本未知（磁盘读取失败）→ 按需询问（PromptRestart），不静默跳过
    [Fact]
    public void Pending_PortOpen_RunningVersionUnknown_ReturnsPromptRestart()
        => Assert.Equal(ShellLogic.PendingUpdateAction.PromptRestart,
            ShellLogic.ResolvePendingUpdateAction(true, true, null, "0.1.0-rc.7"));

    // 补充边界：版本字符串首尾空白不误判（Trim 语义）
    [Fact]
    public void Pending_PortOpen_VersionEqual_WithWhitespace_ReturnsClearPending()
        => Assert.Equal(ShellLogic.PendingUpdateAction.ClearPending,
            ShellLogic.ResolvePendingUpdateAction(true, true, " 0.1.0-rc.7 ", "0.1.0-rc.7"));
}
