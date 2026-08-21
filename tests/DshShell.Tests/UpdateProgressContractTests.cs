using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// UpdateProgress 契约测试（2026-08 用户回归：pnpm 进度长期钉 50% + 文案闪烁）。
/// 锁定纯函数语义：ndjson 按 packageId 自归一化、单调不回退、封顶 90、无数据回退脉冲。
/// </summary>
public class UpdateProgressContractTests
{
    private static ShellLogic.UpdateProgress.PnpmAggregator NewAgg() => new();

    private static string Progress(string packageId, string status = "resolving")
        => $$"""{"name":"pnpm:progress","status":"{{status}}","packageId":"{{packageId}}"}""";

    [Fact]
    public void EmptyAggregator_NoData_FallsBackToPulseMode()
    {
        var agg = NewAgg();
        var (percent, hasData) = agg.Snapshot();
        Assert.False(hasData, "未解析到任何 packageId 时不得显示伪百分比");
        Assert.Equal(0, percent);
    }

    [Fact]
    public void NonJsonAndIrrelevantLines_AreIgnored_Safely()
    {
        var agg = NewAgg();
        agg.OnLine(null);
        agg.OnLine("");
        agg.OnLine("   ");
        agg.OnLine("{not json !!");
        agg.OnLine("""{"name":"pnpm:stage","stage":"resolution_started"}"""); // 无 packageId
        agg.OnLine("npm warn deprecated pkg@1.0.0");
        Assert.False(agg.Snapshot().HasData);
    }

    [Fact]
    public void ProgressAdvances_WithRealCompletionRatio_NoHardcodedTotal()
    {
        var agg = NewAgg();
        // 10 个包全部已见、0 完成 → 下限 10%
        for (var i = 0; i < 10; i++) agg.OnLine(Progress($"pkg{i}"));
        Assert.Equal(10, agg.Snapshot().Percent);
        // 全部完成（link 阶段）→ 封顶 90%
        for (var i = 0; i < 10; i++) agg.OnLine(Progress($"pkg{i}", "linking"));
        Assert.Equal(90, agg.Snapshot().Percent);
    }

    [Fact]
    public void PartialCompletion_MapsLinearly()
    {
        var agg = NewAgg();
        for (var i = 0; i < 8; i++) agg.OnLine(Progress($"pkg{i}"));
        for (var i = 0; i < 4; i++) agg.OnLine(Progress($"pkg{i}", "linking"));
        // done=4 seen=8 → 10 + 80*0.5 = 50
        Assert.Equal(50, agg.Snapshot().Percent);
    }

    [Fact]
    public void PercentIsMonotonic_WhenSeenSetGrowsFasterThanDone()
    {
        var agg = NewAgg();
        for (var i = 0; i < 4; i++)
        {
            agg.OnLine(Progress($"pkg{i}"));
            agg.OnLine(Progress($"pkg{i}", "linking"));
        }
        var atFull = agg.Snapshot().Percent; // 4/4 完成
        // 新发现一批未完成包：done/seen 比值回落，但显示必须单调不回退
        for (var i = 100; i < 110; i++) agg.OnLine(Progress($"pkg{i}"));
        var after = agg.Snapshot().Percent;
        Assert.True(after >= atFull, $"百分比回退：{atFull} → {after}");
    }

    [Fact]
    public void PnpmLinkEventName_AlsoCountsAsDone()
    {
        var agg = NewAgg();
        agg.OnLine(Progress("pkgA"));
        agg.OnLine("""{"name":"pnpm:link","packageId":"pkgA"}""");
        Assert.Equal(90, agg.Snapshot().Percent);
    }

    [Fact]
    public void DuplicateEvents_CountOnce_PerPackageId()
    {
        var agg = NewAgg();
        for (var i = 0; i < 5; i++) agg.OnLine(Progress("pkgA")); // 同包多次事件只计一次
        agg.OnLine(Progress("pkgB"));
        // seen={A,B}=2, done=0 → 仍为下限 10（旧实现会把 6 次事件计成 6 个"包"而虚高）
        Assert.Equal(10, agg.Snapshot().Percent);
    }
}
