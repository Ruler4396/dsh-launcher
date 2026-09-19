using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// [臃肿审计 Phase 3] 运行期自愈重启预算的契约测试（纯函数）。
///
/// 锁住三件事：冷却窗外预算重置、连续超限升级、"从未重启过"必须是 null 而不是
/// <c>DateTime.MinValue</c>——后者是原实现用来绕开"严禁 static bool 控流程"的写法，
/// 它把标志藏进时间戳里，语义上仍是流程控制标志。
/// </summary>
public class ServiceRestartBudgetContractTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(10);
    private const int Max = 3;
    private static readonly DateTime T0 = new(2026, 9, 19, 12, 0, 0, DateTimeKind.Utc);

    private static ShellLogic.ServiceRestartPolicy.RestartBudget Budget(
        DateTime? last, int soFar, DateTime now, bool shuttingDown = false)
        => ShellLogic.ServiceRestartPolicy.DecideRestartBudget(
            last, soFar, now, Cooldown, Max, shuttingDown).Budget;

    private static int Attempts(DateTime? last, int soFar, DateTime now, bool shuttingDown = false)
        => ShellLogic.ServiceRestartPolicy.DecideRestartBudget(
            last, soFar, now, Cooldown, Max, shuttingDown).Attempts;

    [Fact]
    public void FirstEverRestart_CountsAsAttemptOne()
        => Assert.Equal(1, Attempts(last: null, soFar: 0, now: T0));

    [Fact]
    public void WithinBudget_IsQuiet()
        => Assert.Equal(ShellLogic.ServiceRestartPolicy.RestartBudget.QuietRestart,
            Budget(last: null, soFar: 0, now: T0));

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EscalatesOnlyAfterBudgetExhausted(int soFar)
        => Assert.Equal(ShellLogic.ServiceRestartPolicy.RestartBudget.QuietRestart,
            Budget(T0, soFar, T0.AddMinutes(1)));

    [Theory]
    [InlineData(3)]
    [InlineData(7)]
    public void OverBudget_EscalatesToVisibleAsk(int soFar)
        => Assert.Equal(ShellLogic.ServiceRestartPolicy.RestartBudget.EscalateToUser,
            Budget(T0, soFar, T0.AddMinutes(1)));

    /// <summary>超过冷却窗 → 偶发重启不该耗尽预算：计数从 1 重来。</summary>
    [Fact]
    public void AfterCooldown_BudgetResets()
    {
        Assert.Equal(1, Attempts(T0, soFar: 9, now: T0.AddMinutes(11)));
        Assert.Equal(ShellLogic.ServiceRestartPolicy.RestartBudget.QuietRestart,
            Budget(T0, soFar: 9, now: T0.AddMinutes(11)));
    }

    /// <summary>恰好在冷却窗内 → 不重置（边界：> 而非 >=，与既有实现一致）。</summary>
    [Fact]
    public void ExactlyAtCooldownBoundary_DoesNotReset()
        => Assert.Equal(10, Attempts(T0, soFar: 9, now: T0.Add(Cooldown)));

    /// <summary>退出编排已开始：一律吸收，不得再拉起服务。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    public void ShuttingDown_AlwaysAbsorbed(int soFar)
    {
        Assert.Equal(ShellLogic.ServiceRestartPolicy.RestartBudget.AbsorbShuttingDown,
            Budget(last: null, soFar, T0, shuttingDown: true));
        // 吸收路径不得改动计数（否则退出后再启动会把旧预算带过来）
        Assert.Equal(soFar, Attempts(last: null, soFar, T0, shuttingDown: true));
    }

    /// <summary>
    /// 回归形状：把"从未重启"表达成 DateTime.MinValue 必须与 null 同义
    /// ——这是原代码的隐式契约，抽成纯函数后仍要成立，否则改写会引入行为变化。
    /// </summary>
    [Fact]
    public void MinValueSentinel_IsEquivalentToNull()
    {
        Assert.Equal(Attempts(null, 0, T0), Attempts(DateTime.MinValue, 0, T0));
        Assert.Equal(Budget(null, 0, T0), Budget(DateTime.MinValue, 0, T0));
    }
}
