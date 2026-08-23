using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// ServiceLaunch 契约测试（2026-08 用户回归：更新后每次启动拉起系统浏览器）。
/// 根因：SelfContained node.exe 直启分支漏传 --no-open（start-dsh.vbs 三条路径均带）。
/// 锁定契约：常规与安全模式两条参数路径都必须包含 --no-open。
/// </summary>
public class ServiceLaunchContractTests
{
    [Fact]
    public void NormalMode_WebSubcommand_ContainsNoOpen()
    {
        var args = ShellLogic.ServiceLaunch.BuildSelfContainedArgs(@"C:\rt\bin.js", 3080, null);
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
        var args = ShellLogic.ServiceLaunch.BuildSelfContainedArgs(@"C:\rt\bin.js", 3999, ".dsh-safe");
        Assert.Contains("\"C:\\rt\\bin.js\"", args);
        Assert.Contains("--profile .dsh-safe", args);
        Assert.Contains("--port 3999", args);
        Assert.Contains("--no-open", args);          // 与 vbs 的 --profile 路径对齐
        // 安全模式走根级 --profile，不得再带 web 子命令（互斥）
        Assert.DoesNotContain(" web ", $" {args} ");
    }
}
