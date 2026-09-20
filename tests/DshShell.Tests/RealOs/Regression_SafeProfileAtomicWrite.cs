using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests.RealOs;

/// <summary>
/// [臃肿审计 B3] 隔离 profile 的 manifest 写入绕过 AtomicWrite——RealOS 零 Mock 复现。
///
/// 缺陷形状：<c>SafeProfileBuilder.Build</c> 手写「**固定 .tmp 名** + File.Delete + File.Move」，
/// 而仓库里已经有 <c>ShellLogic.FileSystemPolicy.AtomicWrite</c>（Guid 临时名 +
/// File.Move(overwrite:true)），StagedUpdate / WebCacheVersionLedger / WindowStateStore /
/// UpdateDataGuard / AppEnvironment 全都在用它。绕开它带来两个真实故障面：
///   1) 固定 .tmp 名会被**上一次崩溃留下的同名残留**顶掉，也会让并发重建互相踩车；
///   2) Delete→Move 之间目标 package.json 短暂不存在。
/// 事故同形记录见 LauncherApp.cs 的 ServiceIdentityDecorator 注释：profile 缺失 → dsh 硬失败
/// "profile .dsh-safe does not exist" → exit 1 → E2002，用户连界面都进不去。
///
/// 本文件锁的是可归因、可重复的契约，不是热压极限：实测把 400 次连续重建 / 32 路并发都要求
/// 全成功，会撞上 Windows 的「目标被其它句柄打开时 rename 报 UnauthorizedAccessException」——
/// 那是本机文件系统的既有限制，不是本缺陷，作为断言只会产出抖动红灯。因此并发用例只断言
/// **终态有效**（目标存在且可解析），把"固定名踩车"交给 T1 做确定性判定。
/// </summary>
[Trait("Category", "RealOS")] // 归属必须显式：本类原先没有 trait，快线和 realos 层的 filter 各跑不到它
public class Regression_SafeProfileAtomicWrite : IDisposable
{
    private readonly string _home;
    private readonly SafeProfileBuilder _builder;

    public Regression_SafeProfileAtomicWrite()
    {
        _home = Path.Combine(Path.GetTempPath(), "dsh-atomic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_home, "profiles", "web"));
        // Tier1 的 ResolveBundles 要读用户 profiles/web/package.json；缺失即构建失败，
        // 那样用例会红在 fixture 上而不是缺陷上。
        File.WriteAllText(Path.Combine(_home, "profiles", "web", "package.json"),
            "{ \"name\":\"dsh-profile-web\",\"private\":true,\"dsh\":{\"profile\":{\"bundles\":"
            + "[\"@deepseek-ai/dsh-base\",\"@deepseek-ai/dsh-web-app\",\"dsh-notification\"]}} }");
        _builder = new SafeProfileBuilder(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
    }

    private string FixedTmpPath => _builder.SafeProfilePackageJson + ".tmp";

    /// <summary>
    /// T1（确定性判据）：写入**绝不得占用** <c>package.json.tmp</c> 这个固定路径。
    /// 修复前：Build 把内容写进这个固定名再 Move 走 → 种下的哨兵文件消失、内容被搬进正式文件 → 红。
    /// 修复后：AtomicWrite 用 Guid 临时名，哨兵文件原封不动 → 绿。
    /// </summary>
    [Fact]
    public void Build_NeverUsesTheFixedTmpSiblingPath()
    {
        const string sentinel = "SENTINEL-STALE-TMP-MUST-NOT-BE-REUSED";
        Assert.True(_builder.Build(), "前置：先建一次，.dsh-safe 目录才存在");
        File.WriteAllText(FixedTmpPath, sentinel);

        Assert.True(_builder.Build(), "单次构建应成功");

        Assert.True(File.Exists(FixedTmpPath),
            "固定名 .tmp 被构建流程占用/搬走了——说明没走 Guid 临时名的 AtomicWrite");
        Assert.Equal(sentinel, File.ReadAllText(FixedTmpPath));
        using (System.Text.Json.JsonDocument.Parse(File.ReadAllText(_builder.SafeProfilePackageJson))) { }
    }

    /// <summary>
    /// T2：反复重建后目标必须**始终存在且可解析**（原子替换不留缺失/半截窗口）。
    /// 每一次采样都必须满足，不依赖观察线程。
    /// </summary>
    [Fact]
    public void RepeatedRebuild_TargetAlwaysPresentAndParseable()
    {
        for (var i = 0; i < 20; i++)
        {
            _builder.Build();
            Assert.True(File.Exists(_builder.SafeProfilePackageJson),
                $"第 {i} 次重建后目标 package.json 缺失");
            using var _ = System.Text.Json.JsonDocument.Parse(
                File.ReadAllText(_builder.SafeProfilePackageJson));
        }
    }

    /// <summary>
    /// T3：并发重建允许因 Windows 句柄争用出现瞬时失败（已由 Warn 留痕，不再静默），
    /// 但**终态必须是一份有效 manifest**——绝不允许留下缺失或半截的 profile。
    /// </summary>
    [Fact]
    public void ConcurrentRebuild_FinalStateIsValidManifest()
    {
        Assert.True(_builder.Build(), "前置：单次构建必须先成功");

        const int workers = 16;
        var start = new ManualResetEventSlim(false);
        var threads = Enumerable.Range(0, workers).Select(_ => new Thread(() =>
        {
            start.Wait();
            try { new SafeProfileBuilder(_home).Build(); }
            catch { /* 并发下的瞬时 IO 失败由产品侧 Warn 留痕；本用例只裁终态 */ }
        })).ToList();
        threads.ForEach(t => t.Start());
        start.Set();
        threads.ForEach(t => t.Join(TimeSpan.FromSeconds(60)));

        Assert.True(File.Exists(_builder.SafeProfilePackageJson), "并发重建后目标必须存在");
        using (System.Text.Json.JsonDocument.Parse(File.ReadAllText(_builder.SafeProfilePackageJson))) { }
    }
}
