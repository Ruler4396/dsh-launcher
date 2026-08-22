using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// StagedApplyPolicy 契约测试（2026-08-22 用户回归：更新应用"原子切换失败: already exists"）。
///
/// 根因：目标 runtimes\&lt;ver&gt; 已存在时 Directory.Move 直接抛异常；且半成品目标
/// （构建中被并发清场+重启强制应用搬出的残骸）让版本发现持续读旧版本。
/// 决策矩阵（纯函数锁定）：
///   目标不存在                          → ProceedFresh（常规首次安装）
///   存在 + bin 可解析 + 版本一致        → AlreadyApplied（幂等短路，视为已成功）
///   存在但 bin 不可解析 / 版本不一致    → ReplaceStale（挪走备份后换新）
/// </summary>
public class StagedApplyPolicyContractTests
{
    private static ShellLogic.StagedApplyPolicy.ExistingTargetAction Decide(
        bool exists, bool binOk, bool verMatch) =>
        ShellLogic.StagedApplyPolicy.DecideExistingTarget(exists, binOk, verMatch);

    [Fact]
    public void TargetAbsent_ProceedsFresh()
    {
        Assert.Equal(ShellLogic.StagedApplyPolicy.ExistingTargetAction.ProceedFresh,
            Decide(exists: false, binOk: false, verMatch: false));
    }

    [Fact]
    public void TargetValidSameVersion_AlreadyApplied_ShortCircuit()
    {
        // 幂等核心：重复应用同版本绝不报错
        Assert.Equal(ShellLogic.StagedApplyPolicy.ExistingTargetAction.AlreadyApplied,
            Decide(exists: true, binOk: true, verMatch: true));
    }

    [Fact]
    public void TargetHalfBuilt_BinMissing_ReplaceStale()
    {
        // 现场复现：12:23 被搬走的半成品（只有半个 node_modules）
        Assert.Equal(ShellLogic.StagedApplyPolicy.ExistingTargetAction.ReplaceStale,
            Decide(exists: true, binOk: false, verMatch: false));
    }

    [Fact]
    public void TargetValidButDifferentVersion_ReplaceStale()
    {
        Assert.Equal(ShellLogic.StagedApplyPolicy.ExistingTargetAction.ReplaceStale,
            Decide(exists: true, binOk: true, verMatch: false));
        Assert.Equal(ShellLogic.StagedApplyPolicy.ExistingTargetAction.ReplaceStale,
            Decide(exists: true, binOk: false, verMatch: true));
    }
}
