using DshWeb;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【业务完成态契约】孤儿服务保护 Outcome 测试。
///
/// 不关心内部调用了哪个函数，只关心系统的最终物理状态：
/// - 非 Launcher 启动的 node 进程是否被正确识别（不误杀）
/// - 僵尸服务是否被正确清理
/// </summary>
public class OrphanServiceOutcomes
{
    // ---- Outcome 3: 孤儿服务不被误杀 ----

    /// <summary>
    /// 【Outcome 3】端口被非 dsh 进程占用时，必须判定为 Foreign（端口冲突），
    /// 而不是误判为 Zombie 并执行 taskkill。
    ///
    /// 锁定不变量：PID 身份校验（IsLikelyDshService）必须在 kill 之前执行。
    /// 此前的幽灵 Bug：PID 被系统复用给无关进程 → 误杀用户程序。
    /// </summary>
    [Fact]
    public void ForeignPort_DetectedAsConflict_NotKilled()
    {
        // Given: 模拟端口被非 node 进程占用
        // （通过注入 Fake：pidLookup 返回 PID，但 identityCheck 返回 false = 非 node 进程）
        var killCalls = 0;
        var service = new ServiceManager(
            tcpProbe: (_, _) => true, // 端口已开
            pidLookup: _ => 12345,    // 有进程占用
            identityCheck: _ => false, // 但不是 node 进程（Foreign）
            killProcessTree: _ => { killCalls++; return true; }
        );

        // When: 探测端口状态
        var state = service.ProbePort(3080, "http://127.0.0.1:3080");

        // Then: 判定为 Foreign，不执行 kill
        Assert.Equal(ShellLogic.ServicePortState.Foreign, state);
        Assert.Equal(0, killCalls); // 关键：未执行 taskkill
    }

    /// <summary>
    /// 【Outcome 3 变体】端口被 node 进程占用但 HTTP 不通 → 判定为 Zombie，
    /// 应该执行清理（但仅限 node 进程）。
    /// </summary>
    [Fact]
    public void ZombiePort_DetectedAsZombie_KilledCorrectly()
    {
        var killCalls = 0;
        var killedPids = new List<int>();
        var portOccupied = true; // 端口初始被占用
        var service = new ServiceManager(
            tcpProbe: (_, _) => portOccupied,
            httpProbe: (_, _) => false, // HTTP 不通 = Zombie
            pidLookup: _ => 12345,
            identityCheck: pid => pid == 12345, // 是 node 进程
            killProcessTree: pid => { killCalls++; killedPids.Add(pid); portOccupied = false; return true; }
        );

        var state = service.ProbePort(3080, "http://127.0.0.1:3080");

        Assert.Equal(ShellLogic.ServicePortState.Zombie, state);
        // Zombie 需要清理：KillZombieTree 被调用
        var cleaned = service.KillZombieTree(3080);
        Assert.True(cleaned);
        Assert.Contains(12345, killedPids);
    }

    /// <summary>
    /// 【Outcome 3 变体】端口未开 → Closed（需要拉起），不执行任何清理。
    /// </summary>
    [Fact]
    public void ClosedPort_NoKill_NeedsStart()
    {
        var killCalls = 0;
        var service = new ServiceManager(
            tcpProbe: (_, _) => false, // 端口未开
            killProcessTree: _ => { killCalls++; return true; }
        );

        var state = service.ProbePort(3080, "http://127.0.0.1:3080");

        Assert.Equal(ShellLogic.ServicePortState.Closed, state);
        Assert.Equal(0, killCalls);
    }

    /// <summary>
    /// 【Outcome 3 变体】端口开、是 node、HTTP 通 → Healthy（跳过拉起）。
    /// </summary>
    [Fact]
    public void HealthyPort_SkipStart_NoKill()
    {
        var killCalls = 0;
        var startCalls = 0;
        var service = new ServiceManager(
            tcpProbe: (_, _) => true,
            httpProbe: (_, _) => true, // HTTP 通 = Healthy
            pidLookup: _ => 12345,
            identityCheck: _ => true,
            killProcessTree: _ => { killCalls++; return true; }
        );

        var state = service.ProbePort(3080, "http://127.0.0.1:3080");

        Assert.Equal(ShellLogic.ServicePortState.Healthy, state);
        Assert.Equal(0, killCalls);
    }
}
