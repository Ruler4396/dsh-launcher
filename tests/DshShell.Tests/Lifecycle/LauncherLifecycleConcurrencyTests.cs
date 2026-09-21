using System.Collections.Concurrent;
using DshWeb;
using DshWeb.Lifecycle;
using Xunit;

namespace DshShell.Tests.Lifecycle;

/// <summary>
/// [审查 N15/N18 2026-09-21] 状态机并发契约 + 弹窗错误码注册。
/// 真线程竞态用例会写入统一日志（Logger 以 FileShare.ReadWrite 打开）——测试进程退出即释放，
/// 属 %TEMP%/DSH_HOME 级瞬态副作用，有界。
/// </summary>
public class LauncherLifecycleConcurrencyTests
{
    private static LauncherLifecycle ToRunning()
    {
        var lc = new LauncherLifecycle();
        lc.Fire(LifecycleTrigger.StartRequested);
        lc.Fire(LifecycleTrigger.InstanceConfirmed);
        lc.Fire(LifecycleTrigger.RuntimeResolved);
        lc.Fire(LifecycleTrigger.ServiceStarted);
        lc.Fire(LifecycleTrigger.ServiceReady);
        lc.Fire(LifecycleTrigger.UIInitialized);
        Assert.Equal(LifecycleState.Running, lc.State);
        return lc;
    }

    /// <summary>N15：UI 线程与后台事务线程并发投递——TryFire 的"不抛"契约在竞态下也必须成立。
    /// 修复前 CanFire→Fire 是两段读观，交错即可把非法转移的 InvalidOperationException 抛给调用方
    /// （审查现场：安全模式阶梯 Task.Run 与关窗 RequestShutdown 并发）。</summary>
    [Fact]
    public void TryFire_ConcurrentFromManyThreads_NeverThrows_AndEndsInLegalTerminalState()
    {
        var lc = ToRunning();
        var errors = new ConcurrentBag<Exception>();
        using var start = new ManualResetEventSlim(false);
        var threads = new Thread[8];
        for (var t = 0; t < threads.Length; t++)
        {
            var id = t;
            threads[t] = new Thread(() =>
            {
                start.Wait();
                for (var i = 0; i < 300; i++)
                {
                    try
                    {
                        switch ((id + i) % 4)
                        {
                            case 0: lc.TryFire(LifecycleTrigger.RestartRequested); break;
                            case 1: lc.TryFire(LifecycleTrigger.WebViewCrashed); break;
                            case 2: lc.TryFire(LifecycleTrigger.RestartCompleted); break;
                            case 3: lc.TryFire(LifecycleTrigger.ShutdownRequested); break;
                        }
                    }
                    catch (Exception ex) { errors.Add(ex); }
                }
            });
            threads[t].Start();
        }
        start.Set();
        foreach (var th in threads) th.Join(TimeSpan.FromSeconds(30));
        Assert.True(errors.IsEmpty,
            $"TryFire 契约失守 {errors.Count} 次：{errors.FirstOrDefault()?.Message}");
        Assert.True(lc.State is LifecycleState.ShuttingDown or LifecycleState.Running
            or LifecycleState.RestartingService, $"终态非法：{lc.State}");
    }

    /// <summary>N15 形状钉：被拒的投递零副作用（状态不动、不抛）。</summary>
    [Fact]
    public void TryFire_RefusedTrigger_ReturnsFalse_StateUnchanged()
    {
        var lc = ToRunning();
        Assert.False(lc.TryFire(LifecycleTrigger.RestartCompleted)); // Running+Completed 非法
        Assert.Equal(LifecycleState.Running, lc.State);
    }

    /// <summary>N18：弹窗初始化失败的错误码必须注册且带 Describe（错误码契约化，docs/00 三.3）。</summary>
    [Fact]
    public void E1013_PopupInitFailure_RegisteredWithDescribe()
    {
        Assert.Equal("E1013", ErrorCodes.E1013);
        var desc = ErrorCodes.Describe("E1013");
        Assert.Contains("弹窗", desc);
        Assert.Contains("主窗口", desc);
    }
}
