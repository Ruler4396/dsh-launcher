using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// [臃肿审计 Phase 4 · T6] 瞬态导航自愈的决策契约（纯函数）。
///
/// 这套决策此前在 Program.cs 的 Load 处理器里**抄了两份**（首次初始化 / WebView2 修复后重试），
/// 并且已经漂移：第二份漏掉了重试留痕 Trace——同一套规则两份实现时，修一处漏一处是必然结果。
/// 现在决策收敛为本纯函数，执行收敛为 WebViewManager.ArmNavigationRetry 单一实现。
/// </summary>
public class NavigationResilienceContractTests
{
    private const int Max = ShellLogic.NavigationResiliencePolicy.DefaultMaxRetries;

    private static (bool Retry, bool Escalate) Decide(bool isSuccess, int attemptsLeft)
        => ShellLogic.NavigationResiliencePolicy.Decide(isSuccess, attemptsLeft);

    /// <summary>成功导航既不重试也不上报（且这是取消已挂起弹窗的信号源）。</summary>
    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    public void Success_NeitherRetriesNorEscalates(int left)
        => Assert.Equal((false, false), Decide(isSuccess: true, left));

    [Theory]
    [InlineData(5)]
    [InlineData(2)]
    [InlineData(1)]
    public void Failure_WithBudget_RetriesOnly(int left)
        => Assert.Equal((true, false), Decide(isSuccess: false, left));

    /// <summary>预算用尽才升级为用户可见错误——不能提前弹窗打扰。</summary>
    [Fact]
    public void Failure_OutOfBudget_EscalatesOnly()
        => Assert.Equal((false, true), Decide(isSuccess: false, attemptsLeft: 0));

    /// <summary>
    /// 端到端形状：从满预算到用尽，恰好重试 DefaultMaxRetries 次、只升级一次。
    /// 锁的是"重试次数与升级次数"这两个用户可感知量，不是内部循环写法。
    /// </summary>
    [Fact]
    public void FullDescent_RetriesExactlyMaxTimes_EscalatesOnce()
    {
        var retries = 0;
        var escalations = 0;
        for (var left = Max; ; )
        {
            var (retry, escalate) = Decide(isSuccess: false, left);
            if (escalate) { escalations++; break; }
            Assert.True(retry);
            retries++;
            left--;
        }
        Assert.Equal(Max, retries);
        Assert.Equal(1, escalations);
    }

    /// <summary>静默窗必须为正：为 0 就退化成"失败立刻弹模态"，那会阻塞后续导航完成回调。</summary>
    [Fact]
    public void QuietWindow_IsPositive()
    {
        Assert.True(ShellLogic.NavigationResiliencePolicy.EscalateQuietWindowMs > 0);
        Assert.True(Max > 0);
    }

    /// <summary>
    /// 结构闸：自愈实现全仓只许有一份实现点、两处调用（首次初始化 + WebView2 修复后重试）。
    /// 出现第三份 NavigationCompleted 里的重试循环 = 抄写漂移复发。
    /// </summary>
    [Fact]
    public void RetryLoop_ExistsInExactlyOneImplementation()
    {
        var prog = File.ReadAllText(Full("src", "DshShell", "Program.cs"));
        var mgr = File.ReadAllText(Full("src", "DshShell", "Managers", "WebViewManager.cs"));

        Assert.Equal(0, Count(prog, "var navRetries"));
        Assert.DoesNotContain("_navSucceededSinceFailure", prog);
        Assert.Equal(2, Count(prog, "WebViewManager.ArmNavigationRetry("));
        Assert.Equal(1, Count(mgr, "NavigationResiliencePolicy.Decide("));
    }

    private static string Full(params string[] parts)
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "DshShell"))) d = d.Parent;
        return Path.Combine(new[] { d!.FullName }.Concat(parts).ToArray());
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
