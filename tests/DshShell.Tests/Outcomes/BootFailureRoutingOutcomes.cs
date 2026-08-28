using System.Text.Json;
using DshWeb;
using DshWeb.Domain;
using DshWeb.Lifecycle;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 启动失败恢复路由】2026-08-25 插件致崩事故的最终物理状态契约。
///
/// 事故用户任务级不变量（修复前全部被违反）：
///   1. "插件把服务搞崩后，系统应当在合理次数内引导用户进入安全模式，而不是永远问重启"；
///   2. "服务恢复正常后，失败升级计数必须归零，不得残留粘滞状态"。
/// 零 Mock：真实 BootVerdict/BootHealthMonitor 序列化、真实 safe-mode.json 落盘与重载、
/// 真实事故形态的 package.json 文本。只断言系统的最终物理状态（文件内容 + 路由决策）。
/// </summary>
public class BootFailureRoutingOutcomes
{
    private static string NewTempFile()
        => Path.Combine(Path.GetTempPath(), "dsh-route-" + Guid.NewGuid().ToString("N") + ".json");

    /// <summary>用真实 BootHealthMonitor 序列化通道构造 E2007 进程层裁决记录（与生产落盘同构）。</summary>
    private static JsonElement ProcessCrashRecord()
    {
        var verdict = new BootVerdict
        {
            ErrorCode = ErrorCodes.E2007,
            Summary = "dsh 服务进程异常退出（exit code=1）",
        };
        verdict.AddEvidence(new BootEvidence(
            BootLayer.Process, "dsh 服务进程异常退出（exit code=1）", "pid exit code=1", ErrorCodes.E2007));
        var record = BootHealthMonitor.BuildFailureRecord(verdict);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(record));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void Outcome_AnonymousCrashLoop_EscalatesToSafeMode_AfterThreeConsecutiveFailures()
    {
        var file = NewTempFile();
        try
        {
            // 三次会话（每次重开壳 = 从磁盘重载 SafeModeState），每次都匿名崩溃（无插件证据）
            var decisions = new List<ShellLogic.BootRecoveryPolicy.RecoveryAsk>();
            for (var session = 0; session < 3; session++)
            {
                var state = new SafeModeState(file); // 会话开始：从磁盘恢复计数
                state.RecordFailure(ProcessCrashRecord());
                state.RegisterBootFailure();
                decisions.Add(ShellLogic.BootRecoveryPolicy.Decide(
                    pluginInvolved: false, consecutiveFailures: state.ConsecutiveBootFailures));

                // 物理终态：计数已持久化（下次重开壳仍可读）
                var onDisk = JsonDocument.Parse(File.ReadAllText(file));
                Assert.Equal(session + 1,
                    onDisk.RootElement.GetProperty("consecutiveBootFailures").GetInt32());
            }

            // 用户任务级不变量：第 3 次起必须停止无效的"问重启"，升级安全模式询问
            Assert.Equal(new[]
            {
                ShellLogic.BootRecoveryPolicy.RecoveryAsk.AskRestartService,
                ShellLogic.BootRecoveryPolicy.RecoveryAsk.AskRestartService,
                ShellLogic.BootRecoveryPolicy.RecoveryAsk.AskSafeMode,
            }, decisions);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }

    [Fact]
    public void Outcome_ThirdPartyPluginsPresent_ProcessCrash_AttributedToPlugins_OnFirstFailure()
    {
        // 事故现场清单（第三方 file: 依赖在场）：首次匿名进程崩溃即应按插件嫌疑路由，
        // 而不是让用户先空转两轮重启
        var manifest = Path.Combine(Path.GetTempPath(), "dsh-web-manifest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(manifest);
        var pkgPath = Path.Combine(manifest, "package.json");
        try
        {
            File.WriteAllText(pkgPath,
                """
                {
                  "dependencies": { "dsh-notification": "file:E:/x", "@deepseek-ai/dsh-base": "^1" },
                  "dsh": { "profile": { "bundles": [ "@deepseek-ai/dsh-base", "dsh-notification" ] } }
                }
                """);

            var thirdPartyPresent = ShellLogic.PluginConfig.ProfileHasThirdPartyBundles(pkgPath);
            var verdictIsProcessCrash = ProcessCrashRecord()
                .GetProperty("layers").EnumerateArray()
                .Any(l => l.GetProperty("layer").GetString() == "Process"
                          && l.GetProperty("code").GetString() == ErrorCodes.E2007);

            // 与 Program.VerdictIndicatesPluginInvolvement 相同的组合语义：
            // 第三方在场 + 进程层异常退出 ⇒ 插件相关 ⇒ 无论计数多少都进安全模式阶梯
            var pluginInvolved = thirdPartyPresent && verdictIsProcessCrash;
            Assert.True(pluginInvolved, "incident-shaped crash with third-party plugins must be plugin-attributed");
            Assert.Equal(ShellLogic.BootRecoveryPolicy.RecoveryAsk.AskSafeMode,
                ShellLogic.BootRecoveryPolicy.Decide(pluginInvolved: pluginInvolved, consecutiveFailures: 1));
        }
        finally
        {
            try { Directory.Delete(manifest, recursive: true); } catch { /* temp 清理 */ }
        }
    }

    [Fact]
    public void Outcome_HealthyConfirmation_ResetsEscalationCounter_Physically()
    {
        var file = NewTempFile();
        try
        {
            var state = new SafeModeState(file);
            state.RegisterBootFailure();
            state.RegisterBootFailure();
            Assert.Equal(2, state.ConsecutiveBootFailures);

            // 好符号确认健康（HealthyDetected 接线）→ 计数物理归零并落盘
            state.ResetFailureStreak();

            var reloaded = new SafeModeState(file); // 下个会话从零开始，不残留粘滞升级态
            Assert.Equal(0, reloaded.ConsecutiveBootFailures);
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}
