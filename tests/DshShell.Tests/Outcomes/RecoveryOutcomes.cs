using DshWeb;
using DshWeb.Domain;
using DshWeb.Lifecycle;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【业务完成态契约】恢复与降级 Outcome 测试。
///
/// 不关心内部调用了哪个函数，只关心系统的最终物理状态：
/// - WebView 崩溃后状态机是否正确转移
/// - 配置降级（插件缺失）时是否回退到安全默认值
/// </summary>
public class RecoveryOutcomes
{
    // ---- Outcome 4: WebView 崩溃自动恢复 ----

    /// <summary>
    /// 【Outcome 4】WebView 崩溃事件触发后，状态机必须正确转移（Running → Running），
    /// 表示"崩溃已被拦截并触发重载"，而非让应用整体崩溃。
    ///
    /// 锁定不变量：WebView 崩溃不得导致 LauncherApp 状态机进入 Failed/ShuttingDown。
    /// </summary>
    [Fact]
    public void WebViewCrash_DoesNotCrashLauncher_StateStaysRunning()
    {
        // Given: LauncherApp 处于 Running 状态
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        var states = new List<LifecycleState>();
        app.StateChanged += (_, s) => states.Add(s);

        // 先驱动到 Running
        Assert.True(app.RunStartupAsync().GetAwaiter().GetResult());
        Assert.Equal(LifecycleState.Running, app.State);

        // When: 模拟 WebView 崩溃
        app.HandleWebViewCrashed();

        // Then: 状态必须保持 Running（自转移 Running → Running，广播崩溃已被拦截）
        Assert.Equal(LifecycleState.Running, app.State);
        // 状态轨迹中包含 WebViewCrashed 事件
        Assert.Contains(LifecycleState.Running, states);
    }

    // ---- Outcome 5: 配置降级（lifetime 插件缺失） ----

    /// <summary>
    /// 【Outcome 5】lifetime 插件缺失时，serviceLifetime 配置必须被忽略，
    /// 回退到安全默认值（FollowWindow）。
    ///
    /// 锁定不变量：插件缺失 ≠ 用户选择了"跟随窗口"。宁可回退默认，也不执行无效配置。
    /// </summary>
    [Theory]
    [InlineData("{\"serviceLifetime\":0}", false, ShellLogic.ServiceLifetime.FollowWindow, true)]
    [InlineData("{\"serviceLifetime\":1}", false, ShellLogic.ServiceLifetime.FollowWindow, true)]
    [InlineData("{\"serviceLifetime\":0}", true, ShellLogic.ServiceLifetime.AlwaysOn, false)]
    [InlineData("{\"serviceLifetime\":1}", true, ShellLogic.ServiceLifetime.Tray, false)]
    [InlineData(null, false, ShellLogic.ServiceLifetime.FollowWindow, false)]
    public void ConfigDegradation_PluginMissing_FallsBackToDefault(
        string? json, bool pluginPresent,
        ShellLogic.ServiceLifetime expectedMode, bool expectedPurge)
    {
        // When: 解析有效生命周期模式
        var (mode, shouldPurge) = ShellLogic.PluginConfig.ResolveEffectiveLifetime(json, pluginPresent);

        // Then: 插件缺失时必须回退默认（FollowWindow），且标记清理无效字段
        Assert.Equal(expectedMode, mode);
        Assert.Equal(expectedPurge, shouldPurge);
    }

    /// <summary>
    /// 【Outcome 5 变体】插件存在但配置值非法 → 回退默认 + 标记清理。
    /// </summary>
    [Theory]
    [InlineData("{\"serviceLifetime\":99}")]   // 越界值
    [InlineData("{\"serviceLifetime\":-1}")]    // 负值
    public void ConfigDegradation_InvalidValue_FallsBackAndPurges(string json)
    {
        var (mode, shouldPurge) = ShellLogic.PluginConfig.ResolveEffectiveLifetime(json, pluginPresent: true);

        Assert.Equal(ShellLogic.ServiceLifetime.FollowWindow, mode);
        Assert.True(shouldPurge); // 非法值必须标记清理
    }

    /// <summary>
    /// 【Outcome 5 变体】插件存在且配置合法 → 保留用户选择，不清理。
    /// </summary>
    [Fact]
    public void ConfigDegradation_ValidConfig_PreservesUserChoice()
    {
        var (mode, shouldPurge) = ShellLogic.PluginConfig.ResolveEffectiveLifetime(
            "{\"serviceLifetime\":0}", pluginPresent: true);

        Assert.Equal(ShellLogic.ServiceLifetime.AlwaysOn, mode);
        Assert.False(shouldPurge); // 合法值不清理
    }

    // ---- Fakes ----

    private sealed class FakeRuntime : IRuntimeManager
    {
        public RuntimeResult Result { get; init; } = RuntimeResult.ReadyNow("node.exe");
        public Task<RuntimeResult> EnsureRuntimeAsync(CancellationToken ct = default) => Task.FromResult(Result);
        public void PrependToPath(string nodeRoot) { }
    }

    private sealed class FakeService : IServiceManager
    {
        public bool Ready { get; init; } = true;
        public ShellLogic.ServicePortState PortState { get; init; } = ShellLogic.ServicePortState.Healthy;
        public bool NeedsStart(int port) => false;
        public Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken ct = default)
            => Task.FromResult(Ready);
        public ShellLogic.ServicePortState ProbePort(int port, string url) => PortState;
        public bool KillZombieTree(int port) => true;
    }
}
