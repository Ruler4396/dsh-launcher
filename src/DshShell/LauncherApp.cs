namespace DshWeb;

using DshWeb.Lifecycle;
using DshWeb.Managers;

/// <summary>
/// 组合根（Composition Root）：手写装配各 Manager、把 LauncherLifecycle 的"状态→副作用"
/// 接线（ADR-008）。当前为骨架 + 就绪路径演示——未接入 Program.Main，先建立可测的装配边界；
/// 逐步迁移 Runtime/Service 真实接线后，再替代 Main 的面条代码。
/// </summary>
public sealed class LauncherApp
{
    private readonly IRuntimeManager _runtime;
    private readonly IServiceManager _service;
    private readonly LauncherLifecycle _lifecycle = new();

    // 供 Headless 测试观察状态机的当前状态
    public LifecycleState State => _lifecycle.State;

    // 组合根内默认装配（测试可换自身构建的 Manager）
    public LauncherApp(IRuntimeManager? runtime = null, IServiceManager? service = null)
    {
        _runtime = runtime ?? new RuntimeManager();
        _service = service ?? new ServiceManager();
        _lifecycle.StateChanged += OnStateEntered;
    }

    /// <summary>驱动一次启动尝试，返回是否进入 Running（纯逻辑/就绪路径，Headless 可测）。</summary>
    public async Task<bool> RunStartupAsync(CancellationToken ct = default)
    {
        _lifecycle.Fire(LifecycleTrigger.StartRequested);
        _lifecycle.Fire(LifecycleTrigger.InstanceConfirmed);

        // 运行时解析
        var rt = await _runtime.EnsureRuntimeAsync(ct);
        if (!rt.Ready)
        {
            _lifecycle.Fire(LifecycleTrigger.RuntimeFailed);
            return false;
        }
        _lifecycle.Fire(LifecycleTrigger.RuntimeResolved);

        // 服务就绪（端口 3080，超时 180s —— 与 Main 现状一致）
        if (!_service.NeedsStart(TargetPort()))
        {
            _lifecycle.Fire(LifecycleTrigger.ServiceStarted);
            var ready = await _service.WaitReadyAsync(TargetPort(), TimeSpan.FromSeconds(180), ct);
            if (!ready)
            {
                _lifecycle.Fire(LifecycleTrigger.ReadinessTimedOut); // → ShuttingDown，组合根映射 E2002
                Logger.Error("service readiness timed out", ErrorCodes.E2002);
                return false;
            }
            _lifecycle.Fire(LifecycleTrigger.ServiceReady);
            // UI 装配（WebView/Window/Tray 后续迁移）→ Running
            _lifecycle.Fire(LifecycleTrigger.UIInitialized);
            return true;
        }
        return false; // 需要拉起服务的 UI 副路径后续迁移
    }

    private static int TargetPort() =>
        // 与 Main 的 Target 保持同源：默认 3080（DSH_WEB_URL/PORT 由组合根最终收敛）
        3080;

    private void OnStateEntered(object? sender, LifecycleState state)
        => Logger.Info($"lifecycle: {state}");
}
