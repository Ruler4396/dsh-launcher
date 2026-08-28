using DshWeb;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Managers;

/// <summary>
/// F4 契约锁定：端口占用者的"僵尸判定"必须**账本优先**——账本外的 node 进程
/// 绝不强杀（改判 Foreign 快速失败）。旧行为凡 node 即杀，用户自己的 node 程序
/// 占用 3080 且 HTTP 3s 无响应时会被 KillProcessTree 误杀。
/// F21 契约锁定：单实例 mutex 名格式（字符串漂移曾无门禁）。
/// </summary>
public class ServiceIdentityGuardTests
{
    private static ServiceManager Create(Func<int, int, bool>? knownPid, bool httpReady = false)
        => new(
            tcpProbe: (_, _) => true,                 // 端口已开
            httpProbe: (_, _) => httpReady,
            pidLookup: _ => 4242,                      // node 进程占用
            identityCheck: _ => true,
            knownServicePid: knownPid);

    [Fact]
    public void ProbePort_HttpDead_UnknownNode_ReturnsForeign_NotZombie_F4()
    {
        // 核心回归：账本外的 node + HTTP 不通 → Foreign（快速失败），绝不进 KillZombieTree。
        var svc = Create(knownPid: (_, _) => false);
        Assert.Equal(ShellLogic.ServicePortState.Foreign, svc.ProbePort(3080, "http://127.0.0.1:3080"));
    }

    [Fact]
    public void ProbePort_HttpDead_LedgeredPid_ReturnsZombie_SelfHealPreserved()
    {
        // 账本内（本会话拉起/pid 文件）的僵尸服务：清理重启自愈语义保持。
        var svc = Create(knownPid: (pid, port) => pid == 4242 && port == 3080);
        Assert.Equal(ShellLogic.ServicePortState.Zombie, svc.ProbePort(3080, "http://127.0.0.1:3080"));
    }

    [Fact]
    public void ProbePort_HttpAlive_UnknownNode_ReturnsHealthy()
    {
        // 无账本但 HTTP 健康的 node（F19 场景：壳崩溃于记录 pid 前）→ 仍判 Healthy，
        // 不杀不动——由启动链 TryAdoptOrphanService 兜底认领。
        var svc = Create(knownPid: (_, _) => false, httpReady: true);
        Assert.Equal(ShellLogic.ServicePortState.Healthy, svc.ProbePort(3080, "http://127.0.0.1:3080"));
    }

    [Fact]
    public void ProbePort_DefaultCtor_PreservesLegacyBehavior()
    {
        // 缺省（未注入账本）= 旧行为：凡 node 皆可管理（既有测试/非组合根路径零回归）。
        var svc = Create(knownPid: null);
        Assert.Equal(ShellLogic.ServicePortState.Zombie, svc.ProbePort(3080, "http://127.0.0.1:3080"));
    }

    // ---------------- F21：单实例 mutex 名 ----------------

    [Theory]
    [InlineData(3080, @"Local\DshWeb.SingleInstance.3080")]
    [InlineData(8080, @"Local\DshWeb.SingleInstance.8080")]
    public void SingleInstanceMutexName_ContainsPort_WithStablePrefix(int port, string expected)
        => Assert.Equal(expected, ShellLogic.LifecycleDecisions.SingleInstanceMutexName(port));

    [Fact]
    public void SingleInstanceMutexName_DifferentPorts_Differ()
        => Assert.NotEqual(
            ShellLogic.LifecycleDecisions.SingleInstanceMutexName(3080),
            ShellLogic.LifecycleDecisions.SingleInstanceMutexName(8080));

    // ---------------- F16：插件致命消息判定 ----------------

    [Theory]
    [InlineData("{\"foo\":\"bootstrap facade is missing\"}", true)]           // 精确致命短语
    [InlineData("{\"msg\":\"plugin fatal: addon crashed\"}", true)]
    [InlineData("{\"msg\":\"dsh-boot-failed\"}", true)]
    [InlineData("{\"type\":\"pluginFatal\",\"detail\":\"...\"}", true)]       // 结构化标志
    [InlineData("{\"module\":\"ModuleLoader\",\"mode\":\"boot\"}", false)]    // 良性提及 ModuleLoader（误报根除）
    [InlineData("{\"text\":\"the ModuleLoader queue is a dsh internal\"}", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("{\"msg\":\"hello world\"}", false)]
    public void IsPluginCrashMessage_OnlyMatchesExactFatalPhrases_F16(string? json, bool expected)
        => Assert.Equal(expected, ShellLogic.WebViewPolicy.IsPluginCrashMessage(json));
}
