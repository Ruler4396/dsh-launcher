using DshWeb;
using DshWeb.Lifecycle;
using Xunit;

namespace DshShell.Tests.Lifecycle;

/// <summary>
/// [臃肿审计 Phase 3c] "本会话是否由壳拉起/接管了服务"只允许一个真相源。
///
/// 此前这个事实同时存在于 <c>LauncherApp.ServiceStartedByShell</c> 与
/// <c>Program._serviceStartedByShell</c>：启动路径从前者拷进后者，孤儿接管路径只写后者。
/// 两份状态一旦不同步，后果直接落在用户身上——<c>ShouldStopServiceOnClose</c> 拿到假的
/// false 就会在退出时**留下无主 node 服务**（端口占用、下次启动被判僵尸/误杀）。
/// 现接管改经 <see cref="LauncherApp.MarkServiceAdoptedByShell"/> 唯一写入口。
/// </summary>
public class ServiceOwnershipSingleSourceTests
{
    /// <summary>接管孤儿服务后，壳必须自认持有该服务——否则退出时不会停它。</summary>
    [Fact]
    public void AdoptOrphan_FlipsOwnershipAndStopsServiceOnClose()
    {
        var app = new LauncherApp();
        Assert.False(app.ServiceStartedByShell, "未拉起前不得自称持有服务");

        app.MarkServiceAdoptedByShell();

        Assert.True(app.ServiceStartedByShell, "接管后必须记为壳持有");
        // 退出决策：跟随窗口模式 + 非外部托管 + 壳持有 → 必须停服务
        Assert.True(ShellLogic.LifecycleDecisions.ShouldStopServiceOnClose(
            ShellLogic.ServiceLifetime.FollowWindow,
            externallyManaged: false, shellManaged: app.ServiceStartedByShell,
            trayExitRequested: false));
    }

    /// <summary>外部托管（DSH_WEB_URL 由别人起服务）时，即使壳持有过也不得停别人的服务。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ExternalHosting_NeverStopsSomeoneElsesService(bool startedByShell)
        => Assert.False(ShellLogic.LifecycleDecisions.ShouldStopServiceOnClose(
            ShellLogic.ServiceLifetime.FollowWindow,
            externallyManaged: true, shellManaged: startedByShell, trayExitRequested: true));

    /// <summary>
    /// 组合根不得再自带第二份该事实：Program.cs 里出现 <c>_serviceStartedByShell</c>
    /// 即说明双真相源复活（G5 冻结清单也已把该名字移出，新增会直接红）。
    /// </summary>
    [Fact]
    public void CompositionRoot_HoldsNoDuplicateOwnershipFlag()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DshShell", "Program.cs"));
        Assert.DoesNotContain("_serviceStartedByShell", src);
        Assert.Contains("SessionApp?.MarkServiceAdoptedByShell", src);
        Assert.Contains("SessionApp?.ServiceStartedByShell", src);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            if (Directory.Exists(Path.Combine(d.FullName, "src", "DshShell"))) return d.FullName;
            d = d.Parent;
        }
        throw new InvalidOperationException("找不到仓库根");
    }
}
