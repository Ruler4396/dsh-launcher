using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests.Domain;

/// <summary>
/// SafeModeState 落盘往返与损坏容错（2026-08 安全模式解粘滞修复的配套单测）。
/// </summary>
public class SafeModeStateTests
{
    private static string NewTempFile()
        => Path.Combine(Path.GetTempPath(), "dsh-safe-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public void Activate_Then_Deactivate_PersistsRoundTrip()
    {
        var file = NewTempFile();
        try
        {
            var s = new SafeModeState(file);
            Assert.False(s.IsActive);
            s.Activate(SafeProfileTier.Tier1KeepDeepSeekCore);
            Assert.True(s.IsActive);
            Assert.Equal(SafeProfileTier.Tier1KeepDeepSeekCore, s.Tier);

            // 模拟进程重启：从磁盘重新加载，激活态应持久化
            var reloaded = new SafeModeState(file);
            Assert.True(reloaded.IsActive);
            Assert.Equal(SafeProfileTier.Tier1KeepDeepSeekCore, reloaded.Tier);

            // 用户拒绝安全模式（修复点4）→ Deactivate 解粘滞，落盘
            reloaded.Deactivate();
            Assert.False(reloaded.IsActive);

            var reloaded2 = new SafeModeState(file);
            Assert.False(reloaded2.IsActive, "Deactivate 必须持久化，否则下个会话会静默降级启动");
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void CorruptedFile_FallsBackToInactive_NoThrow()
    {
        var file = NewTempFile();
        try
        {
            File.WriteAllText(file, "{ this is not valid json ");
            var s = new SafeModeState(file);
            // 损坏文件容错：视为未激活，绝不抛异常（否则启动流程直接崩）
            Assert.False(s.IsActive);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void Deactivate_Idempotent_WhenNotActive()
    {
        var file = NewTempFile();
        try
        {
            var s = new SafeModeState(file);
            Assert.False(s.IsActive);
            s.Deactivate(); // 未激活时重复解粘滞不应抛
            Assert.False(s.IsActive);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    // ==================== 连续失败计数（2026-08-25 事故回归） ====================

    [Fact]
    public void FailureStreak_Increments_Persists_AndResets()
    {
        var file = NewTempFile();
        try
        {
            var s = new SafeModeState(file);
            Assert.Equal(0, s.ConsecutiveBootFailures);

            // RecordFailure（吸收态融合视图重写也走这里）不得推进计数——否则单次失败会被
            // Http 层追加证据虚增 2~3 次，提前触发升级阈值（2026-08-25 事故里单会话追加了 3 次）
            using (var doc = System.Text.Json.JsonDocument.Parse("{\"utc\":\"x\"}"))
                s.RecordFailure(doc.RootElement);
            Assert.Equal(0, s.ConsecutiveBootFailures);

            s.RegisterBootFailure();
            s.RegisterBootFailure();
            Assert.Equal(2, s.ConsecutiveBootFailures);

            // 跨会话：从磁盘重载必须保留计数（事故形态是"每次重开壳都崩"）
            var reloaded = new SafeModeState(file);
            Assert.Equal(2, reloaded.ConsecutiveBootFailures);

            reloaded.ResetFailureStreak();
            Assert.Equal(0, reloaded.ConsecutiveBootFailures);
            Assert.Equal(0, new SafeModeState(file).ConsecutiveBootFailures);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void FailureStreak_OldStateFileWithoutField_LoadsAsZero()
    {
        var file = NewTempFile();
        try
        {
            // 旧版本 safe-mode.json 无 consecutiveBootFailures 字段 → 向后兼容按 0 处理
            File.WriteAllText(file, "{\r\n  \"active\": false,\r\n  \"tier\": 1\r\n}");
            var s = new SafeModeState(file);
            Assert.Equal(0, s.ConsecutiveBootFailures);
            Assert.False(s.IsActive);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void ResetFailureStreak_Idempotent_AtZero()
    {
        var file = NewTempFile();
        try
        {
            var s = new SafeModeState(file);
            s.ResetFailureStreak(); // 计数为 0 时幂等短路，不产生 IO 也不抛
            Assert.Equal(0, s.ConsecutiveBootFailures);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
