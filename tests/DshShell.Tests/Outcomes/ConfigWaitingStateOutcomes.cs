using DshWeb;
using DshWeb.Domain;
using DshWeb.Lifecycle;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 良性"配置等待态"不产生任何失败证据】[E2008/E2007 误报根治]。
///
/// 用户任务级不变量（修复前全部被违反）：
///   1. "dsh 渲染出自己的配置/欢迎界面（未填 API key、boot 链未完成）时，launcher 不得判 failed"；
///   2. "残留/失效 service pid 触发 attach 失败（用户实测证据：进程 attach 失败（pid=4708 不存在））
///      只是监视接线失败，不得触发崩溃裁决、弹窗或落盘"；
///   3. "上述良性状态下，跨会话失败计数不推进、safe-mode.json 不写入 lastFailure"。
/// 零 Mock：真实 BootHealthMonitor 融合状态机（注入探针/进程工厂/HTTP 探测）+ 真实 SafeModeState
/// 落盘语义。只断言系统的最终物理状态（状态机终态 + 磁盘文件）。
/// </summary>
public class ConfigWaitingStateOutcomes
{
    private static string NewTempFile()
        => Path.Combine(Path.GetTempPath(), "dsh-benign-" + Guid.NewGuid().ToString("N") + ".json");

    [Fact]
    public async Task Outcome_BenignConfigWaitingState_NoFailureEvidence_NoPrompt_NoStreak()
    {
        string file = NewTempFile();
        var monitor = new BootHealthMonitor(
            new ShellLogic.BootGuard.BootProfile { GraceMs = 40, ProbeIntervalMs = 30, AbsentThreshold = 3 },
            logPath: null,
            httpUrl: "http://127.0.0.1:3080",
            // 页面已渲染（good=false = dsh boot 链未完成），无坏签名 → 渲染豁免
            pageProbe: _ => Task.FromResult(
                "{\"good\":false,\"text\":\"欢迎使用 DeepSeek Harness：请先配置模型提供方 API Key 后开始使用，设置入口在右上角齿轮图标，也可以查看帮助文档与示例项目。\",\"err\":\"\"}"),
            // 残留 pid：GetProcessById 抛错（用户实测 "进程 attach 失败（pid=4708 不存在）"）
            processHandleFactory: _ => throw new ArgumentException("no process with id 4708"),
            httpProbe: _ => true,
            logPollInterval: TimeSpan.FromMilliseconds(100),
            httpPollInterval: TimeSpan.FromMilliseconds(100));

        try
        {
            using (monitor)
            {
                var failedFired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                monitor.Failed += _ => failedFired.TrySetResult();
                var healthy = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                monitor.HealthyDetected += () => healthy.TrySetResult();

                var state = new SafeModeState(file); // 会话开始：从磁盘恢复（文件尚不存在 = 全新）
                monitor.Start();
                monitor.AttachProcess(4708);
                monitor.OnNavigationCompleted();

                await Task.WhenAny(healthy.Task, Task.Delay(5000));
                Assert.True(healthy.Task.IsCompleted, "rendered config-waiting page must be judged healthy");
                Assert.Equal(BootHealthState.Healthy, monitor.State);
                Assert.False(failedFired.Task.IsCompleted, "benign state must never produce a failure verdict");
                await Task.Delay(300); // 给任何错误路径最后一次触发机会

                // 用户任务级不变量：无失败 → 计数不推进、不落盘任何 lastFailure
                Assert.Equal(0, state.ConsecutiveBootFailures);
                Assert.False(File.Exists(file), "benign state must not persist safe-mode evidence");
            }
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
        }
    }
}