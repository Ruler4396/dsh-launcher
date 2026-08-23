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
}
