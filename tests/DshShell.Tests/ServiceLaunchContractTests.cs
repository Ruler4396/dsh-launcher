using DshWeb;
using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// ServiceLaunch 契约测试（2026-08 用户回归：更新后每次启动拉起系统浏览器）。
/// 根因：SelfContained node.exe 直启分支漏传 --no-open（start-dsh.vbs 三条路径均带）。
/// 锁定契约：常规与安全模式两条参数路径都必须包含 --no-open。
///
/// [臃肿审计 Phase 6] 这两个用例原先打的是 <c>BuildSelfContainedArgs</c>——一个**生产零调用**的
/// 历史转发（ServiceManager 用的是 BuildArgs）。也就是说它们测的那条路径根本不会执行，
/// 而真正会执行的 BuildArgs 反而没有 --no-open 断言。现在改为直接锁活的 BuildArgs，
/// 死转发连同它的旧断言一起删除。
/// </summary>
public class ServiceLaunchContractTests
{
    private static DshRuntimeIdentity Runtime(string? profilePath) => new(
        DshSource.SelfContained, @"C:\node\node.exe", @"C:\rt\bin.js", "0.1.1-rc.8", profilePath);

    [Fact]
    public void NormalMode_WebSubcommand_ContainsNoOpen()
    {
        var args = ShellLogic.ServiceLaunch.BuildArgs(Runtime(null), 3080);
        Assert.Contains("\"C:\\rt\\bin.js\"", args); // binJs 引号包裹（路径含空格安全）
        Assert.Contains(" web ", $" {args} ");
        Assert.Contains("--host 127.0.0.1", args);
        Assert.Contains("--port 3080", args);
        Assert.Contains("--no-open", args);          // 回归锚点：缺此参数即弹浏览器
        Assert.DoesNotContain("--profile", args);
    }

    [Fact]
    public void SafeMode_RootProfile_ContainsNoOpen()
    {
        var args = ShellLogic.ServiceLaunch.BuildArgs(Runtime(@"C:\dsh\.dsh-safe"), 3999);
        Assert.Contains("\"C:\\rt\\bin.js\"", args);
        Assert.Contains("--profile .dsh-safe", args); // 只取 profile 目录名（与身份装饰同源）
        Assert.Contains("--port 3999", args);
        Assert.Contains("--no-open", args);           // 与 vbs 的 --profile 路径对齐
        // 安全模式走根级 --profile，不得再带 web 子命令（互斥）
        Assert.DoesNotContain(" web ", $" {args} ");
    }
}
