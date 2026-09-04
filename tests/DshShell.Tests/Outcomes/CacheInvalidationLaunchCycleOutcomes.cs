using DshWeb;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 缓存失效跨会话启动循环】真实账本（%TEMP% 文件系统）+ 真实决策策略。
///
/// 引擎级"清理物理行为"已由 WebbCacheVersionChangeOutcomes（RealOS 真实 WebView2）锁定；
/// 本测试锁定**编排层跨会话语义**（物理状态 = 账本文件内容 + 决策结果 + 清理次数）：
///   C1 首启无基线 → 不清、写基线；同版本后续会话永不重复清；
///   C2 升级/降级各触发恰好一次清理，基线随之更新；
///   C3 探测失败（current=null）→ 不清且**保留**旧基线（漏清方向安全，绝不误清方向）；
///   C4 崩溃窗口（清理已执行、基线未写）→ 下一会话按旧基线**重清**，幂等无害；
///   C5 账本被写坏（不可信基线）→ 不清（K6），但本轮仍写回正确基线自愈。
/// </summary>
public class CacheInvalidationLaunchCycleOutcomes
{
    private static string NewDir()
        => Path.Combine(Path.GetTempPath(), "dsh-cachecycle-" + Guid.NewGuid().ToString("N"));

    /// <summary>复刻组合根编排（Program.InvalidateWebCacheOnVersionChangeAsync 的三步薄链）。</summary>
    private static void RunSession(string? currentVersion, ref int clears)
    {
        var lastSeen = WebCacheVersionLedger.Read();
        if (ShellLogic.CacheInvalidationPolicy.ShouldInvalidate(lastSeen, currentVersion))
            clears++;
        if (currentVersion is not null)
            WebCacheVersionLedger.Write(currentVersion);
    }

    [Fact]
    public void Outcome_LaunchCycle_UpgradeDowngradeEachClearOnce_AndTransitionCountMatches()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            WebCacheVersionLedger.Init(dir);
            var clears = 0;

            // C1：首启 5 次同版本 —— 一次都不清
            for (var i = 0; i < 5; i++) RunSession("1.0.0", ref clears);
            Assert.Equal(0, clears);
            Assert.Equal("1.0.0", WebCacheVersionLedger.Read());

            // C2：升级到 1.0.1（5 次）—— 恰好清 1 次
            for (var i = 0; i < 5; i++) RunSession("1.0.1", ref clears);
            Assert.Equal(1, clears);
            Assert.Equal("1.0.1", WebCacheVersionLedger.Read());

            // C2b：降级回 1.0.0 —— 也恰好清 1 次（方向无关）
            for (var i = 0; i < 5; i++) RunSession("1.0.0", ref clears);
            Assert.Equal(2, clears);
            Assert.Equal("1.0.0", WebCacheVersionLedger.Read());

            // 20 次会话：期望清理次数 = 版本跃迁次数（程序自算，杜绝人力数错）
            var versions = new[] { "2.0.0", "2.0.0", "2.1.0", "2.1.0", "2.0.0" };
            var prev = WebCacheVersionLedger.Read(); // 上一阶段基线 "1.0.0"
            long expectedTransitions = 0;
            for (var i = 0; i < 4 * versions.Length; i++)
            {
                var v = versions[i % versions.Length];
                if (v != prev) expectedTransitions++;
                RunSession(v, ref clears);
                prev = WebCacheVersionLedger.Read();
            }
            Assert.True(clears == 2 + expectedTransitions,
                $"清理次数必须等于版本跃迁次数: clears={clears} expected(2+{expectedTransitions})");
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Outcome_LaunchCycle_UnprobedKeepsBaseline_CrashWindowReclears()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            WebCacheVersionLedger.Init(dir);
            var clears = 0;

            RunSession("1.0.0", ref clears);
            // C3：探测失败（null）—— 不清，且基线保留 1.0.0
            RunSession(null, ref clears);
            Assert.Equal(0, clears);
            Assert.Equal("1.0.0", WebCacheVersionLedger.Read());
            // 恢复探测且版本变化 → 正常清
            RunSession("1.0.1", ref clears);
            Assert.Equal(1, clears);
            Assert.Equal("1.0.1", WebCacheVersionLedger.Read());

            // C4：崩溃窗口 = 清已执行但基线未写（人为把账本回退到旧版，模拟崩在 clear 与 write 之间）
            WebCacheVersionLedger.Write("1.0.0");
            RunSession("1.0.1", ref clears);
            Assert.True(clears == 2, "崩溃窗口后按旧基线重清（幂等无害，保证不因崩溃漏清）");
            Assert.Equal("1.0.1", WebCacheVersionLedger.Read());
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Outcome_LaunchCycle_CorruptBaseline_NeverClears_AndSelfHeals()
    {
        var dir = NewDir();
        try
        {
            Directory.CreateDirectory(dir);
            WebCacheVersionLedger.Init(dir);
            var clears = 0;

            RunSession("1.0.0", ref clears);
            // C5：账本被写坏成不可信版本
            File.WriteAllText(Path.Combine(dir, "webcache-version.json"),
                "{\"version\":\";rm -rf /\",\"at\":\"tampered\"}");
            RunSession("1.1.0", ref clears);
            Assert.True(clears == 0, "不可信基线 → 绝不清（K6）");
            Assert.True(WebCacheVersionLedger.Read() == "1.1.0", "本轮写回正确基线自愈（下次版本变化正常清）");

            // 自愈后：再变化一次 → 正常触发
            RunSession("1.2.0", ref clears);
            Assert.Equal(1, clears);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        }
    }
}