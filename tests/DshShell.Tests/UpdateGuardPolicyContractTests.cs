using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 契约测试：ShellLogic.UpdateGuardPolicy（更新数据守卫纯函数策略）。
///
/// [2026-08-23 用户回归] dsh 新版本首次启动会把 $HOME\.dsh 共享数据文件
/// （实测 .credentials.yaml）单向迁移为新格式（version+refs）；一旦新版起不来而回退
/// 旧版，旧解析器读不懂新格式 → 插件树加载失败 → 服务 exit(1)，"更新失败=隔天必炸"。
///
/// 本类锁定纯函数决策面：分支决策、版本 token 净化、快照目录命名、按版本挑快照、快照修剪。
/// 文件系统副作用由 UpdateDataGuardOutcomes（真实 FS，零 Mock）覆盖。
/// </summary>
public class UpdateGuardPolicyContractTests
{
    // ---- DecideBootFailure：启动自检失败的分支决策 ----

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ", false)]
    [InlineData("0.1.1-rc.2", true)]
    public void DecideBootFailure_BranchesOnArmedVersion(string? armedVersion, bool expectRollback)
    {
        var action = ShellLogic.UpdateGuardPolicy.DecideBootFailure(armedVersion);

        var expected = expectRollback
            ? ShellLogic.UpdateGuardPolicy.BootFailureAction.RollbackAndRestart
            : ShellLogic.UpdateGuardPolicy.BootFailureAction.ExistingRecoveryFlow;
        Assert.Equal(expected, action);
    }

    // ---- SanitizeVersionToken：版本号 → 目录名安全 token ----

    [Theory]
    [InlineData("0.1.1-rc.2", "0.1.1-rc.2")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("0.1.0-RC.8", "0.1.0-RC.8")]
    public void SanitizeVersionToken_KeepsPlainSemVerIntact(string version, string expected)
        => Assert.Equal(expected, ShellLogic.UpdateGuardPolicy.SanitizeVersionToken(version));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void SanitizeVersionToken_BlankFallsBackToUnknown(string? version)
        => Assert.Equal("unknown", ShellLogic.UpdateGuardPolicy.SanitizeVersionToken(version));

    [Fact]
    public void SanitizeVersionToken_ReplacesInvalidFileNameChars()
    {
        // ':' '/' 等在 Windows 目录名非法；必须替换而不是抛异常
        var token = ShellLogic.UpdateGuardPolicy.SanitizeVersionToken("a:b/c");
        Assert.DoesNotContain(":", token);
        Assert.DoesNotContain("/", token);
        Assert.Equal("a_b_c", token);
    }

    // ---- SnapshotDirName：可排序命名不变量 ----

    [Fact]
    public void SnapshotDirName_IsPre_Token_Timestamp_AndSortsChronologically()
    {
        var earlier = ShellLogic.UpdateGuardPolicy.SnapshotDirName(
            "0.1.1-rc.2", new DateTime(2026, 8, 23, 12, 34, 56, DateTimeKind.Utc));
        var later = ShellLogic.UpdateGuardPolicy.SnapshotDirName(
            "0.1.1-rc.2", new DateTime(2026, 8, 23, 20, 41, 00, DateTimeKind.Utc));

        Assert.Equal("pre-0.1.1-rc.2-20260823-123456", earlier);
        // 定宽 UTC 时间戳 ⇒ 目录名字典序 == 时间序（挑选/修剪全部依赖该不变量）
        Assert.True(string.Compare(later, earlier, StringComparison.OrdinalIgnoreCase) > 0);
    }

    // ---- PickRollbackSnapshot：只认指定版本、取最近 ----

    [Fact]
    public void PickRollbackSnapshot_ReturnsNull_WhenNoMatch()
    {
        var names = new[] { "pre-0.1.0-rc.8-20260822-100000", "unrelated-dir" };
        Assert.Null(ShellLogic.UpdateGuardPolicy.PickRollbackSnapshot(names, "0.1.1-rc.2"));
        Assert.Null(ShellLogic.UpdateGuardPolicy.PickRollbackSnapshot(Array.Empty<string>(), "0.1.1-rc.2"));
    }

    [Fact]
    public void PickRollbackSnapshot_PicksLatest_OfTargetVersion_IgnoringOthers()
    {
        var names = new[]
        {
            "pre-0.1.1-rc.2-20260822-090000",
            "pre-0.1.0-rc.8-20260822-120000", // 别的版本：不得干扰
            "pre-0.1.1-rc.2-20260823-201530",
        };

        var picked = ShellLogic.UpdateGuardPolicy.PickRollbackSnapshot(names, "0.1.1-rc.2");

        Assert.Equal("pre-0.1.1-rc.2-20260823-201530", picked);
    }

    // ---- PruneSnapshotDirs：保留最近 keep 个、返回升序删除清单 ----

    [Fact]
    public void PruneSnapshotDirs_KeepsLatest_AndReturnsOldestFirstForDeletion()
    {
        var names = new[]
        {
            "pre-v1-20260820-000000",
            "pre-v2-20260821-000000",
            "pre-v3-20260822-000000",
            "pre-v4-20260823-000000",
        };

        var toDelete = ShellLogic.UpdateGuardPolicy.PruneSnapshotDirs(names, keep: 3);

        Assert.Equal(new[] { "pre-v1-20260820-000000" }, toDelete);
    }

    [Fact]
    public void PruneSnapshotDirs_UnderLimit_DeletesNothing()
    {
        var names = new[] { "pre-v1-20260820-000000", "pre-v2-20260821-000000" };
        Assert.Empty(ShellLogic.UpdateGuardPolicy.PruneSnapshotDirs(names, keep: 3));
    }
}
