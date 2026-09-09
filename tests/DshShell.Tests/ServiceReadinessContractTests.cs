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
}