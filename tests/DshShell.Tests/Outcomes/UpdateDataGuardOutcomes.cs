using DshWeb;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【业务完成态契约】更新数据守卫 Outcome 测试（真实文件系统，零 Mock）。
///
/// 锁定的用户任务级不变量：
/// "更新失败（新版起不来）后，旧版本必须立即可用——共享数据保持更新前的字节，
///  新版运行时不再被发现链选中。"
///
/// 事故背景（2026-08-23）：dsh 0.1.1-rc.2 首启把 .credentials.yaml 单向迁移为
/// version+refs 布局；更新失败回退 rc.8 后旧解析器抛
/// 'the value for "version" must be a string' → 插件树加载失败 → exit(1)。
/// </summary>
public class UpdateDataGuardOutcomes
{
    private const string TargetVersion = "0.1.1-rc.2";

    /// <summary>更新前的扁平格式（rc.8 解析器唯一接受的布局）。</summary>
    private const string FlatCredentials = "OPENCODE_GO_API_KEY: sk-test-flat\nDEEPSEEK_API_KEY: as_sk_flat\n";

    /// <summary>新版"迁移"后的格式（rc.2 写入；对 rc.8 是剧毒：version 是数字、refs 是映射）。</summary>
    private const string MigratedCredentials = "version: 1\nrefs:\n  OPENCODE_GO_API_KEY: sk-test-flat\n  DEEPSEEK_API_KEY: as_sk_flat\n";

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dsh-guard-outcome-" + Guid.NewGuid().ToString("N"));
        public string DshHome { get; }
        public TempDir()
        {
            Directory.CreateDirectory(Path);
            DshHome = System.IO.Path.Combine(Path, "home");
            Directory.CreateDirectory(DshHome);
        }
        public void Dispose()
        {
            try { if (Directory.Exists(Path)) Directory.Delete(Path, true); } catch { }
        }
    }

    /// <summary>构造有效自包含运行时目录（bin 可解析 + 文件存在 + 版本一致），等价 DshDiscovery 认可。</summary>
    private static void WriteValidRuntime(string dir, string version)
    {
        var libDir = System.IO.Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(System.IO.Path.Combine(libDir, "bin.js"), "// entry");
        File.WriteAllText(
            System.IO.Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "package.json"),
            $"{{\"name\":\"@deepseek-ai/dsh\",\"version\":\"{version}\",\"bin\":{{\"dsh\":\"lib/bin.js\"}}}}");
    }

    /// <summary>
    /// 【回归核心】完整复现事故并验证守卫闭环：
    /// 扁平凭据 → apply 前首拍 → 新版首启"迁移"改写数据 + 运行时进入 runtimes\ →
    /// 启动自检失败回滚 → 凭据字节还原、污染件留档、新运行时被隔离出 runtimes\。
    /// </summary>
    [Fact]
    public void Regression_CredentialsMigration_RollbackRestoresPreUpdateBytes_AndQuarantinesRuntime()
    {
        using var tmp = new TempDir();
        var credsPath = System.IO.Path.Combine(tmp.DshHome, ".credentials.yaml");
        File.WriteAllText(credsPath, FlatCredentials);
        UpdateDataGuard.Init(System.IO.Path.Combine(tmp.Path, "dsh-launcher"), tmp.DshHome);

        // --- apply 前：版本首拍 ---
        Assert.True(UpdateDataGuard.SnapshotBeforeApply(TargetVersion));

        // --- 新版首启：单向迁移数据 + 运行时原子切换进 runtimes\（事故现场重建）---
        File.WriteAllText(credsPath, MigratedCredentials);
        var runtimesDir = System.IO.Path.Combine(tmp.Path, "dsh-launcher", "runtimes");
        WriteValidRuntime(System.IO.Path.Combine(runtimesDir, TargetVersion), TargetVersion);

        // 观察期：快照未确认健康前必须持续武装（跨会话）
        Assert.Equal(TargetVersion, UpdateDataGuard.UnconfirmedSnapshotVersion(TargetVersion));

        // --- 启动自检失败 → 回滚 ---
        var result = UpdateDataGuard.RollbackAfterFailedUpdate(TargetVersion, "boot self-check failed [E2008]");

        // 物理不变量 1：凭据按字节还原为更新前状态（旧版解析器可读——这就是"不爆炸"）
        Assert.True(result.DataRestored);
        Assert.Equal(new[] { ".credentials.yaml" }, result.RestoredFiles);
        Assert.Equal(FlatCredentials, File.ReadAllText(credsPath));

        // 物理不变量 2：被新版污染的文件留档可追责（内容=迁移后的格式）
        var bak = Directory.GetFiles(tmp.DshHome, ".credentials.yaml.rollback-bak-*");
        Assert.Single(bak);
        Assert.Equal(MigratedCredentials, File.ReadAllText(bak[0]));

        // 物理不变量 3：新运行时已不在 runtimes\（DshDiscovery 立即失活，重启落回旧版）
        Assert.NotNull(result.QuarantinedRuntimeDir);
        Assert.False(Directory.Exists(System.IO.Path.Combine(runtimesDir, TargetVersion)));
        var quarantinedPkg = Directory.GetFiles(
            System.IO.Path.Combine(tmp.Path, "dsh-launcher", "update-guard", "quarantine"),
            "package.json", SearchOption.AllDirectories);
        Assert.NotEmpty(quarantinedPkg); // 整棵运行时树被搬进隔离区

        // 物理不变量 4：回滚历史落盘（用户反馈材料）
        var history = System.IO.Path.Combine(
            tmp.Path, "dsh-launcher", "update-guard", "rollback-history.jsonl");
        Assert.True(File.Exists(history));
        Assert.Contains(TargetVersion, File.ReadAllText(history));
    }

    /// <summary>【首拍优先】同一目标版本重复 apply 绝不覆盖首拍：两次快照之间原文件再变化，
    /// 回滚仍还原"最早的应用前状态"。若允许覆盖，第二次拍到的可能已是迁移后的剧毒格式。</summary>
    [Fact]
    public void Regression_RepeatedApply_NeverOverwritesFirstShot()
    {
        using var tmp = new TempDir();
        var credsPath = System.IO.Path.Combine(tmp.DshHome, ".credentials.yaml");
        File.WriteAllText(credsPath, FlatCredentials);
        UpdateDataGuard.Init(System.IO.Path.Combine(tmp.Path, "dsh-launcher"), tmp.DshHome);

        Assert.True(UpdateDataGuard.SnapshotBeforeApply(TargetVersion)); // 首拍（真源）

        // 模拟第一次尝试已把数据迁移坏，随后用户重试更新（第二次 apply 前的快照调用）
        File.WriteAllText(credsPath, MigratedCredentials);
        Assert.False(UpdateDataGuard.SnapshotBeforeApply(TargetVersion)); // 首拍 wins，不重拍

        var result = UpdateDataGuard.RollbackAfterFailedUpdate(TargetVersion, "second attempt failed");

        Assert.Equal(FlatCredentials, File.ReadAllText(credsPath)); // 还原的是最早的健康状态
        Assert.True(result.DataRestored);
    }

    /// <summary>【观察期闭环】好符号确认健康后解除武装；确认前后查询结果翻转。</summary>
    [Fact]
    public void ObservationWindow_ConfirmHealthy_Disarms()
    {
        using var tmp = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(tmp.DshHome, ".credentials.yaml"), FlatCredentials);
        UpdateDataGuard.Init(System.IO.Path.Combine(tmp.Path, "dsh-launcher"), tmp.DshHome);

        UpdateDataGuard.SnapshotBeforeApply(TargetVersion);
        Assert.Equal(TargetVersion, UpdateDataGuard.UnconfirmedSnapshotVersion(TargetVersion));

        UpdateDataGuard.MarkConfirmedHealthy(TargetVersion);

        Assert.Null(UpdateDataGuard.UnconfirmedSnapshotVersion(TargetVersion));
        // 幂等：重复确认无害
        UpdateDataGuard.MarkConfirmedHealthy(TargetVersion);
        Assert.Null(UpdateDataGuard.UnconfirmedSnapshotVersion(TargetVersion));
    }

    /// <summary>【npm 路径】无运行时目录可隔离：回滚不炸、如实上报（调用方据此走 npm 降级分支）。</summary>
    [Fact]
    public void Rollback_NpmPath_NoRuntimeDir_StillRestoresData_AndReportsNoQuarantine()
    {
        using var tmp = new TempDir();
        var credsPath = System.IO.Path.Combine(tmp.DshHome, ".credentials.yaml");
        File.WriteAllText(credsPath, FlatCredentials);
        UpdateDataGuard.Init(System.IO.Path.Combine(tmp.Path, "dsh-launcher"), tmp.DshHome);

        UpdateDataGuard.SnapshotBeforeApply(TargetVersion);
        File.WriteAllText(credsPath, MigratedCredentials); // npm 全局安装的新版同样会迁移数据

        var result = UpdateDataGuard.RollbackAfterFailedUpdate(TargetVersion, "boot failed (npm path)");

        Assert.True(result.DataRestored);
        Assert.Equal(FlatCredentials, File.ReadAllText(credsPath));
        Assert.Null(result.QuarantinedRuntimeDir); // 调用方看到 null → 执行 best-effort npm downgrade
        // 污染件留档：内容=迁移后的格式
        Assert.Equal(MigratedCredentials, File.ReadAllText(Directory.GetFiles(tmp.DshHome, ".credentials.yaml.rollback-bak-*").Single()));
    }

    /// <summary>【无快照兜底】快照缺失（如首次快照就 IO 失败）→ 回滚不得崩、数据不动、
    /// 但运行时隔离照常生效（半保护也好过裸奔），告警如实上报。</summary>
    [Fact]
    public void Rollback_WithoutSnapshot_LeavesData_ButStillQuarantinesRuntime()
    {
        using var tmp = new TempDir();
        var credsPath = System.IO.Path.Combine(tmp.DshHome, ".credentials.yaml");
        File.WriteAllText(credsPath, MigratedCredentials);
        var runtimesDir = System.IO.Path.Combine(tmp.Path, "dsh-launcher", "runtimes");
        WriteValidRuntime(System.IO.Path.Combine(runtimesDir, TargetVersion), TargetVersion);
        UpdateDataGuard.Init(System.IO.Path.Combine(tmp.Path, "dsh-launcher"), tmp.DshHome);

        var result = UpdateDataGuard.RollbackAfterFailedUpdate(TargetVersion, "no-snapshot scenario");

        Assert.False(result.DataRestored);
        Assert.Empty(result.RestoredFiles);
        Assert.Contains(result.Warnings, w => w.Contains("no snapshot"));
        Assert.Equal(MigratedCredentials, File.ReadAllText(credsPath)); // 数据未被乱动
        Assert.False(Directory.Exists(System.IO.Path.Combine(runtimesDir, TargetVersion)));
        Assert.NotNull(result.QuarantinedRuntimeDir);
    }

    /// <summary>【跨版本修剪】快照合计超上限时删最旧（按目录名时间序），最近 3 个版本始终可用。</summary>
    [Fact]
    public void Snapshots_ArePruned_ToLatestThree()
    {
        using var tmp = new TempDir();
        File.WriteAllText(System.IO.Path.Combine(tmp.DshHome, ".credentials.yaml"), FlatCredentials);
        var dataDir = System.IO.Path.Combine(tmp.Path, "dsh-launcher");
        UpdateDataGuard.Init(dataDir, tmp.DshHome);

        foreach (var v in new[] { "0.1.0-rc.6", "0.1.0-rc.7", "0.1.0-rc.8", TargetVersion })
            UpdateDataGuard.SnapshotBeforeApply(v);

        var snapRoot = System.IO.Path.Combine(dataDir, "update-guard", "snapshots");
        var remaining = Directory.GetDirectories(snapRoot).Select(System.IO.Path.GetFileName).OrderBy(n => n).ToArray();

        Assert.Equal(3, remaining.Length);
        Assert.DoesNotContain(remaining, n => n!.Contains("0.1.0-rc.6")); // 最旧的被修剪
        Assert.NotNull(UpdateDataGuard.UnconfirmedSnapshotVersion(TargetVersion));
    }
}
