using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// ServiceReadiness 就绪脚本扩展的契约测试（issue #26：服务进程就绪前退出 → 快速失败）。
/// 锁定：
/// - 第五态裁决串 <see cref="ShellLogic.ServiceReadiness.ServiceExitedVerdict"/> 的精确值；
/// - <see cref="ShellLogic.ServiceReadiness.IsServiceExitFailFast"/> 的退出码语义（&lt;0=未观察到退出）；
/// - <see cref="ShellLogic.ServiceReadiness.MapVerdictErrorCode"/> 的裁决→错误码映射
///   （logerror→E2003 / timeout→E2002 / canceled→E2006 / service-exited→E2010 / 未知→E9001），
///   供 Program.HandleStartupFailure 委托，杜绝组合根重复 switch 漂移。
/// </summary>
public class ServiceReadinessContractTests
{
    // ---------------- ServiceExitedVerdict 值域（与 LauncherApp/PollReadiness 共用字符串契约） ----------------

    [Fact]
    public void ServiceExitedVerdict_ExactValue()
        => Assert.Equal("service-exited", ShellLogic.ServiceReadiness.ServiceExitedVerdict);

    [Fact]
    public void ServiceExitedVerdict_DistinctFromOtherVerdicts()
    {
        Assert.NotEqual("ready", ShellLogic.ServiceReadiness.ServiceExitedVerdict);
        Assert.NotEqual("timeout", ShellLogic.ServiceReadiness.ServiceExitedVerdict);
        Assert.NotEqual("logerror", ShellLogic.ServiceReadiness.ServiceExitedVerdict);
        Assert.NotEqual("canceled", ShellLogic.ServiceReadiness.ServiceExitedVerdict);
    }

    // ---------------- IsServiceExitFailFast：退出码语义 ----------------

    [Theory]
    [InlineData(-1, false)]  // 未观察到退出（无追踪进程/进程仍存活）→ 不快速失败
    [InlineData(-2, false)]  // 极端负值同上
    [InlineData(0, true)]    // 已退出（含退出码 0——长驻服务就绪前退出仍是异常）
    [InlineData(1, true)]
    [InlineData(7, true)]    // 常见 node 崩溃退出码
    [InlineData(139, true)]  // SIGKILL
    public void IsServiceExitFailFast_ExitCodeSemantics(int exitCode, bool expected)
        => Assert.Equal(expected, ShellLogic.ServiceReadiness.IsServiceExitFailFast(exitCode));

    // ---------------- MapVerdictErrorCode：裁决 → 错误码映射 ----------------

    [Fact]
    public void MapVerdictErrorCode_Logerror_MapsE2003()
        => Assert.Equal(ErrorCodes.E2003, ShellLogic.ServiceReadiness.MapVerdictErrorCode("logerror"));

    [Fact]
    public void MapVerdictErrorCode_Timeout_MapsE2002()
        => Assert.Equal(ErrorCodes.E2002, ShellLogic.ServiceReadiness.MapVerdictErrorCode("timeout"));

    [Fact]
    public void MapVerdictErrorCode_Canceled_MapsE2006()
        => Assert.Equal(ErrorCodes.E2006, ShellLogic.ServiceReadiness.MapVerdictErrorCode("canceled"));

    [Fact]
    public void MapVerdictErrorCode_ServiceExited_MapsE2010()
        => Assert.Equal(ErrorCodes.E2010,
            ShellLogic.ServiceReadiness.MapVerdictErrorCode(ShellLogic.ServiceReadiness.ServiceExitedVerdict));

    [Fact]
    public void MapVerdictErrorCode_Ready_IsNotAMappedFailure()
        => Assert.Equal(ErrorCodes.E9001, ShellLogic.ServiceReadiness.MapVerdictErrorCode("ready"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("bogus-verdict")]
    public void MapVerdictErrorCode_UnknownOrNull_FallsBackToE9001(string? unknown)
        => Assert.Equal(ErrorCodes.E9001, ShellLogic.ServiceReadiness.MapVerdictErrorCode(unknown));

    [Fact]
    public void E2010_RegisteredWithUserVisibleDescription()
        => Assert.Contains("就绪前已退出", ErrorCodes.Describe(ErrorCodes.E2010));

    // ---------------- StartupFailureBody：按裁决拼的失败正文（2026-09-20 自组合根搬入） ---------

    private const string Tail = "  [18:01:14.462] [dsh] Error: profile bundle 报错";
    private const string Hint = "Error: dsh: profile bundle \"dsh-broken-demo\" declares no dsh.bundle";

    [Fact]
    public void StartupFailureBody_ServiceExited_ShowsExitCodeHintAndLogPath()
    {
        var body = ShellLogic.ServiceReadiness.StartupFailureBody(
            ShellLogic.ServiceReadiness.ServiceExitedVerdict, Tail, Hint, 1, "C:\\dsh.log");
        Assert.Contains("退出码 1", body);          // issue #26 的全部意义：把真实退出码交给用户
        Assert.Contains(Hint, body);                // 首条异常线索
        Assert.Contains(Tail, body);                // 日志尾部
        Assert.Contains("C:\\dsh.log", body);       // 完整日志路径
    }

    /// <summary>
    /// 误导文案回归：就绪前退出绝不能再说成"首次下载较慢/网络问题"——那是 timeout 分支的话，
    /// 用户照着它去查网络，而真实原因是插件把进程打死了。
    /// </summary>
    [Theory]
    [InlineData("service-exited")]
    [InlineData("logerror")]
    public void StartupFailureBody_CrashVerdicts_NeverBlameTheNetwork(string verdict)
        => Assert.DoesNotContain("首次下载 dsh 组件较慢",
            ShellLogic.ServiceReadiness.StartupFailureBody(verdict, Tail, Hint, 1, "C:\\dsh.log"));

    [Fact]
    public void StartupFailureBody_Logerror_KeepsErrorClueAndTail()
    {
        var body = ShellLogic.ServiceReadiness.StartupFailureBody("logerror", Tail, Hint, -1, "C:\\dsh.log");
        Assert.Contains("报错线索", body);
        Assert.Contains(Tail, body);
        Assert.DoesNotContain("退出码", body);      // 只有"进程自己退了"才谈退出码
    }

    [Fact]
    public void StartupFailureBody_NoClueLine_OmitsTheClueSectionInsteadOfPrintingEmpty()
    {
        var body = ShellLogic.ServiceReadiness.StartupFailureBody("logerror", Tail, null, -1, "C:\\dsh.log");
        Assert.DoesNotContain("报错线索：\n\n", body);
        Assert.Contains(Tail, body);
    }

    [Fact]
    public void StartupFailureBody_Canceled_SaysCanceledNotFailure()
    {
        var body = ShellLogic.ServiceReadiness.StartupFailureBody("canceled", Tail, null, -1, "C:\\dsh.log");
        Assert.StartsWith("已取消启动", body);
        Assert.DoesNotContain("报错", body);
    }

    [Fact]
    public void StartupFailureBody_Timeout_IsTheOnlyBranchTalkingAboutSlowDownloads()
    {
        var body = ShellLogic.ServiceReadiness.StartupFailureBody("timeout", Tail, null, -1, "C:\\dsh.log");
        Assert.Contains("启动超时", body);
        Assert.Contains("首次下载 dsh 组件较慢", body);
        Assert.Contains("C:\\dsh.log", body);
    }
}