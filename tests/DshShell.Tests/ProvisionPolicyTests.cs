using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 首装全局安装预算策略与启动失败映射的纯函数契约（2026-09 静默失败收口配套）：
/// - 旧首装链每源 20 分钟 × N 源失控等待 → 共享总预算 + 单次上限，剩余不足即放弃；
/// - 首装安装失败曾被通用 E2001 文案（"缺少 start-dsh.vbs"）掩盖真实根因 → 映射为 E1012。
/// </summary>
public class ProvisionPolicyTests
{
    // ---------------- ProvisionPolicy.RemainingInstallTimeoutMs ----------------

    [Fact]
    public void FreshStart_ReturnsPerAttemptCap()
    {
        var t = ShellLogic.ProvisionPolicy.RemainingInstallTimeoutMs(
            elapsedMs: 0, totalBudgetMs: 600_000, perAttemptCapMs: 420_000);
        Assert.Equal(420_000, t);
    }

    [Fact]
    public void MidwayBudget_LimitsToRemaining()
    {
        // 已耗用 300s：剩余 300s < 单次上限 420s → 只给剩余
        var t = ShellLogic.ProvisionPolicy.RemainingInstallTimeoutMs(
            elapsedMs: 300_000, totalBudgetMs: 600_000, perAttemptCapMs: 420_000);
        Assert.Equal(300_000, t);
    }

    [Fact]
    public void BelowMinAttempt_SignalsExhaustion()
    {
        // 剩余不足最小可尝试时长 → 返回值 < MinAttemptMs，调用方放弃后续源
        var t = ShellLogic.ProvisionPolicy.RemainingInstallTimeoutMs(
            elapsedMs: 570_000, totalBudgetMs: 600_000, perAttemptCapMs: 420_000);
        Assert.True(t < ShellLogic.ProvisionPolicy.MinAttemptMs);
    }

    [Fact]
    public void OverBudget_NegativeRemainder_Exhausted()
    {
        var t = ShellLogic.ProvisionPolicy.RemainingInstallTimeoutMs(
            elapsedMs: 700_000, totalBudgetMs: 600_000, perAttemptCapMs: 420_000);
        Assert.True(t < ShellLogic.ProvisionPolicy.MinAttemptMs);
    }

    [Fact]
    public void NegativeElapsed_ClampedToZero()
    {
        var t = ShellLogic.ProvisionPolicy.RemainingInstallTimeoutMs(
            elapsedMs: -5, totalBudgetMs: 600_000, perAttemptCapMs: 420_000);
        Assert.Equal(420_000, t);
    }

    // ---------------- StartupFailurePolicy.MapFirstRunInstallFailure ----------------

    [Fact]
    public void E2001_WithInstallError_MapsToE1012_WithDetail()
    {
        var mapped = ShellLogic.StartupFailurePolicy.MapFirstRunInstallFailure(
            "E2001", "npm 全局安装失败：ETIMEDOUT");
        Assert.NotNull(mapped);
        Assert.Equal("E1012", mapped!.Value.Code);
        Assert.Contains("npm install -g @deepseek-ai/dsh", mapped.Value.Detail);
        Assert.Contains("ETIMEDOUT", mapped.Value.Detail); // 真实根因必须可见（不藏原因）
    }

    [Fact]
    public void E2001_WithoutInstallError_StaysUntouched()
    {
        // 真"缺 start-dsh.vbs"场景没有首装错误 → 不改写通用文案
        Assert.Null(ShellLogic.StartupFailurePolicy.MapFirstRunInstallFailure("E2001", null));
        Assert.Null(ShellLogic.StartupFailurePolicy.MapFirstRunInstallFailure("E2001", "  "));
    }

    [Theory]
    [InlineData("E2002")]
    [InlineData("E2004")]
    [InlineData(null)]
    public void OtherCodes_NeverMapped(string? outcomeCode)
    {
        Assert.Null(ShellLogic.StartupFailurePolicy.MapFirstRunInstallFailure(outcomeCode, "some error"));
    }
}
