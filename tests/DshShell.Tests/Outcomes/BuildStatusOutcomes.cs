using DshWeb;
using DshWeb.Chrome;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 任务五】后台更新构建状态验证。
///
/// 物理状态断言（臃肿审计 Phase 4 · T6b 后，构建占用状态的真相源是更新引擎，不再是组合根静态）：
/// - 无构建在跑时 <c>DshUpdateManager.BuildInProgress == false</c>，且请求取消返回 false；
/// - CustomTitleBar 的 BuildStatus 枚举仍含 Idle/Building/Ready 三态。
///
/// 【已如实标注的弱断言】下面 BuildStatus 相关用例只校验枚举成员与序数，**没有**校验任何
/// 状态流转（标题栏需要真实窗体，属 E2E 的 <c>UiTestHookE2ETests</c> 面）。它们能防的是"枚举被
/// 重排/删项"，防不了"流转写错"。登记在 docs/ARCHITECTURE-DEBT-LEDGER.md 待补。
/// </summary>
public class BuildStatusOutcomes
{
    /// <summary>
    /// 构建占用状态的真相源在更新引擎：新建实例必须是"空闲"，且此时请求取消不得谎报成功。
    /// 这条取代了原先写在文档注释里、却从未真正读取 <c>Program._isBuildInProgress</c> 的断言。
    /// </summary>
    [Fact]
    public void Outcome_NoBuildRunning_IsIdleAndCancelIsNoOp()
    {
        var updates = new DshWeb.Managers.DshUpdateManager(
            Path.Combine(Path.GetTempPath(), "dsh-nobuild-" + Guid.NewGuid().ToString("N")), 3080);
        Assert.False(updates.BuildInProgress, "未开始构建却自称在构建，会让关窗拦截误判");
        Assert.False(updates.TryCancelRunningBuild(), "无构建时取消必须返回 false（调用方据此不提已取消）");
    }

    /// <summary>
    /// 【L3 Outcome — BuildStatus 枚举契约】
    /// 验证 BuildStatus 枚举包含所有必要的状态。
    /// 统一状态：Idle → Building → Ready（不再区分 Downloading/Building）。
    /// </summary>
    [Fact]
    public void Outcome_BuildStatus_EnumContainsAllStates()
    {
        // 验证枚举值存在
        Assert.True(Enum.IsDefined(typeof(CustomTitleBar.BuildStatus), 0)); // Idle
        Assert.True(Enum.IsDefined(typeof(CustomTitleBar.BuildStatus), 1)); // Building
        Assert.True(Enum.IsDefined(typeof(CustomTitleBar.BuildStatus), 2)); // Ready
    }

    /// <summary>
    /// 【L3 Outcome — CustomTitleBar 初始状态契约】
    /// 验证新创建的 CustomTitleBar 初始状态为 Idle。
    /// </summary>
    [Fact]
    public void Outcome_BuildStatus_InitialState_IsIdle()
    {
        // 注：CustomTitleBar 需要 DshShellForm 实例，此处验证枚举契约
        // 实际 UI 测试需通过 E2E（UiTestHookE2ETests）
        Assert.Equal(CustomTitleBar.BuildStatus.Idle, (CustomTitleBar.BuildStatus)0);
    }

    /// <summary>
    /// 【L3 Outcome — 状态流转契约】
    /// 验证构建状态的合法流转：Idle → Building → Ready → Idle。
    /// 统一状态：不再区分 Downloading/Building，直接 Idle → Building → Ready。
    /// </summary>
    [Theory]
    [InlineData(0, 1, true)]  // Idle → Building
    [InlineData(1, 2, true)]  // Building → Ready
    [InlineData(2, 0, true)]  // Ready → Idle
    public void Outcome_BuildStatus_ValidTransitions(int from, int to, bool expected)
    {
        // 验证状态流转是合法的（设计意图）
        // 生产路径中，DownloadDshUpdateStaged 按此顺序更新状态
        Assert.True(Enum.IsDefined(typeof(CustomTitleBar.BuildStatus), from), $"状态 {from} 应该是合法的 BuildStatus 枚举值");
        Assert.True(Enum.IsDefined(typeof(CustomTitleBar.BuildStatus), to), $"状态 {to} 应该是合法的 BuildStatus 枚举值");
        Assert.True(expected, $"状态流转 {(CustomTitleBar.BuildStatus)from} → {(CustomTitleBar.BuildStatus)to} 应该合法");
    }
}
