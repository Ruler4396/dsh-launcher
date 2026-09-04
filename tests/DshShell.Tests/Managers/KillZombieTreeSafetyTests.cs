using System;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Managers;

/// <summary>
/// 【2026-09 杀伤代码审计加固】KillZombieTree 安全契约（Headless，注入 fake 探针/杀手）：
///   Z1 只对端口归属进程 pid 执行 taskkill（/T 覆盖其子进程即 dsh 服务树本身）；
///   Z2 **绝不**杀伤祖先链——ADR-024 直启后 node 的父进程是启动器自身/用户终端，旧"cmd/npx
///      外壳"中间层已不存在。若实现回退为"杀祖先"，此测试立即红（防误杀终端/自杀回归）；
///   Z3 端口已无占用者（pid&lt;=0）→ 直接成功，不发任何 kill；
///   Z4 杀完仍占端口 → 返回 false（清理失败如实上报，不误报成功）。
/// </summary>
public class KillZombieTreeSafetyTests
{
    private sealed class KillRecorder
    {
        public System.Collections.Generic.List<int> Killed = new();
        public bool Result = true;
        public bool Invoke(int pid) { Killed.Add(pid); return Result; }
    }

    [Fact]
    public void KillZombieTree_KillsOnlyPortOwner_NotAncestors()
    {
        var killer = new KillRecorder();
        var svc = new ServiceManager(
            tcpProbe: (_, _) => false,                       // 端口立即释放
            pidLookup: _ => 111,
            killProcessTree: killer.Invoke,
            ancestors: _ => new System.Collections.Generic.List<int> { 222, 333, 444 },
            portReleaseTimeout: TimeSpan.FromMilliseconds(200));

        var ok = svc.KillZombieTree(3080);

        Assert.True(ok);
        // Z1/Z2：只杀端口归属进程；祖先（222/333/444，可代表启动器自身/终端）绝不沾手
        Assert.Equal(new[] { 111 }, killer.Killed);
    }

    [Fact]
    public void KillZombieTree_NoPortOwner_NoKill_ReturnsTrue()
    {
        var killer = new KillRecorder();
        var svc = new ServiceManager(
            tcpProbe: (_, _) => true,
            pidLookup: _ => 0,
            killProcessTree: killer.Invoke,
            ancestors: _ => new System.Collections.Generic.List<int> { 999 });

        var ok = svc.KillZombieTree(3080);

        Assert.True(ok);
        Assert.Empty(killer.Killed); // Z3：无占用者 → 零杀伤
    }

    [Fact]
    public void KillZombieTree_PortStillOccupied_ReturnsFalse()
    {
        var killer = new KillRecorder { Result = true };
        var svc = new ServiceManager(
            tcpProbe: (_, _) => true,                        // 端口一直占着
            pidLookup: _ => 111,
            killProcessTree: killer.Invoke,
            ancestors: _ => new System.Collections.Generic.List<int>(),
            portReleaseTimeout: TimeSpan.FromMilliseconds(150));

        var ok = svc.KillZombieTree(3080);

        Assert.False(ok); // Z4：清理失败如实上报
        Assert.Equal(new[] { 111 }, killer.Killed);
    }
}