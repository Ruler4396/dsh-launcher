using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// [真机 T14 实测缺口] 坏插件把 dsh 服务在**就绪之前**打死时，该不该给出安全模式入口。
///
/// 事故形态（2026-09-20 沙盒真机）：profile 的 dsh.profile.bundles 里多了一个 resolve 不了的
/// 插件 → dsh 在 prepareProfile 阶段抛错 exit 1 → 壳拿到 service-exited → 弹 E2010
/// "dsh 服务未能就绪"，然后**没有任何下一步**。而安全模式询问只挂在两条路上：运行期进程退出
/// (E2007) 与页面插件致命消息 (E1008)。日志里明写着模块解析失败、profile 也确实声明了第三方
/// bundle——唯一能救用户的证据就在手上，却没人读。
///
/// 判据必须保守：安全模式的作用是"禁用第三方插件"，所以**只有同时满足**
/// ①进程自己死了（service-exited / logerror，超时与取消不算——那不该误导用户去动插件）
/// ②日志里有模块/插件加载失败签名
/// ③profile 真的声明了第三方 bundle
/// 才值得问一句。任一不成立就维持原 E2010 文案。
/// </summary>
public class StartupFailureRecoveryContractTests
{
    // 真机现场（scene-T15，2026-09-20 16:51:49）：dsh 在 prepareProfile 阶段拒绝坏 bundle。
    // 逐字取自统一日志的服务管道行——我先前照猜写的夹具是 ERR_MODULE_NOT_FOUND，
    // 真机第一次跑就纠正了这个消息形态，所以这条 fixture 必须是真的。
    private const string RealCrash = """
        [16:51:49.329] [dsh] file:///C:/Users/x/.../dsh-app-boot/lib/index.js:862
        [16:51:49.331] [dsh] 		if (declared === void 0) throw new Error(`${binName}: profile bundle ${JSON.stringify(packageName)} declares no dsh.bundle in its package.json`);
        [16:51:49.332] [dsh] Error: dsh: profile bundle "dsh-broken-demo" declares no dsh.bundle in its package.json
        [16:51:49.334] [dsh]     at loadProfile (file:///C:/Users/x/.../dsh-app-boot/lib/index.js:859:25)
        [16:51:49.335] [dsh]     at prepareProfile (file:///C:/Users/x/.../lib/profile-boot-BTzzdrGY.js:162:18)
        [16:51:49.339] [dsh] Node.js v24.21.0
        """;

    /// <summary>另一族真实插件故障：bundle 装了但模块解析不到。</summary>
    private const string ModuleMissingCrash = """
        [16:52:01.100] [dsh] Error [ERR_MODULE_NOT_FOUND]: Cannot find package 'dsh-gone' imported from bin.js
        [16:52:01.101] [dsh]     at prepareProfile (...)
        """;

    private const string NetworkishCrash = """
        [16:51:49.332] [dsh] Error: connect ECONNREFUSED 127.0.0.1:443
        [16:51:49.333] [dsh]     at TCPConnectWrap.afterConnect [as oncomplete] (node:net:1645:16)
        """;

    private static bool Offers(string verdict, string? tail, bool thirdParty)
        => ShellLogic.StartupFailureRecoveryPolicy.ShouldOfferSafeMode(verdict, tail, thirdParty);

    [Theory]
    [InlineData(ShellLogic.ServiceReadiness.ServiceExitedVerdict)]
    [InlineData("logerror")]
    public void PluginSignatureAndThirdPartyBundle_OfferSafeMode(string verdict)
    {
        Assert.True(Offers(verdict, RealCrash, thirdParty: true));
        Assert.True(Offers(verdict, ModuleMissingCrash, thirdParty: true));
    }

    /// <summary>
    /// 自证触发防线：插件字样只出现在**壳自己写的** JSON 行/诊断文案里时不算证据
    /// （统一日志混排，运行期归因同一条纪律）。
    /// </summary>
    [Fact]
    public void MarkerInShellAuthoredLine_DoesNotOffer()
        => Assert.False(Offers(ShellLogic.ServiceReadiness.ServiceExitedVerdict,
            """
            {"ts":"2026-09-20 16:51:49.777","level":"ERROR","pid":27204,"code":"E2010","msg":"... profile bundle \\"x\\" declares no dsh.bundle ..."}
            诊断导出：profile bundle 相关文案
            """, thirdParty: true));


    /// <summary>没有第三方 bundle 就没有可禁用的东西——安全模式帮不上，别误导。</summary>
    [Fact]
    public void NoThirdPartyBundle_DoesNotOffer()
        => Assert.False(Offers(ShellLogic.ServiceReadiness.ServiceExitedVerdict, RealCrash, thirdParty: false));

    /// <summary>日志里没有插件签名（这里是网络错误）→ 不 offer，维持原 E2010 归因。</summary>
    [Fact]
    public void CrashWithoutPluginSignature_DoesNotOffer()
        => Assert.False(Offers(ShellLogic.ServiceReadiness.ServiceExitedVerdict, NetworkishCrash, thirdParty: true));

    /// <summary>超时/取消不是"进程自己崩了"，不该把用户支去动插件。</summary>
    [Theory]
    [InlineData("timeout")]
    [InlineData("canceled")]
    [InlineData("ready")]
    public void NonCrashVerdicts_NeverOffer(string verdict)
        => Assert.False(Offers(verdict, RealCrash, thirdParty: true));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyLogTail_DoesNotOffer(string? tail)
        => Assert.False(Offers(ShellLogic.ServiceReadiness.ServiceExitedVerdict, tail, thirdParty: true));

    /// <summary>
    /// 诚实边界：只剩堆栈帧、报错首行已被日志窗口截掉时**不猜**。
    /// 宁可少问一次，也不要在"其实不是插件问题"的场合把用户支去禁用插件。
    /// </summary>
    [Fact]
    public void StackFramesWithoutErrorMessageLine_DoesNotGuess()
        => Assert.False(Offers(ShellLogic.ServiceReadiness.ServiceExitedVerdict,
            "    at loadProfile (...)\n    at prepareProfile (...)", thirdParty: true));
}
