using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 更新提示文案的契约（<see cref="ShellLogic.UpdateNotice"/>）。
///
/// 【为什么需要它】v0.5.0 起，启动器的安全更新卡片要同时承担一件事：**告知用户这可能是最后一个
/// 版本**。这句话必须出现在用户真的会看到的那条通知里，而不是只写在 GitHub Release 页上——
/// 便携版用户点的是同一个决策对话框，两者文案同源，所以文案整体从组合根下沉到这里锁死。
/// 判据刻意做成"一对"：末版公告只属于启动器安全更新那条，dsh 自身的版本更新**不该**带它
/// （那是另一个包的新功能，不是本项目停止维护），否则这条闸就退化成"两个字符串都Contains同一个东西"
/// 那种永远测不出错的形式。
/// </summary>
public class UpdateNoticeCopyContractTests
{
    [Fact]
    public void LauncherSecurityBody_CarriesEndOfMaintenanceNotice()
    {
        var body = ShellLogic.UpdateNotice.LauncherSecurityBody("0.5.0", "0.4.5");
        Assert.Contains("0.5.0", body);
        Assert.Contains("0.4.5", body);
        Assert.Contains(ShellLogic.UpdateNotice.EndOfMaintenanceLine, body);
    }

    [Fact]
    public void DshUpdateBody_DoesNotCarryEndOfMaintenanceNotice()
    {
        var body = ShellLogic.UpdateNotice.DshBody("0.1.6-rc.1", "0.1.5-rc.2");
        Assert.Contains("0.1.6-rc.1", body);
        Assert.DoesNotContain(ShellLogic.UpdateNotice.EndOfMaintenanceLine, body);
    }

    /// <summary>本地版本解析不出来时正文不许写出"当前 ）"这种半截话——用户读到的必须是明确结论。</summary>
    [Fact]
    public void LauncherSecurityBody_StatesUnknownLocalVersionExplicitly()
    {
        var body = ShellLogic.UpdateNotice.LauncherSecurityBody("0.5.0", null);
        Assert.Contains("当前版本未知", body);
        Assert.DoesNotContain("（当前 ）", body);
    }

    [Fact]
    public void Titles_DistinguishSecurityUpdateFromRoutineUpdate()
    {
        Assert.Contains("安全", ShellLogic.UpdateNotice.LauncherSecurityTitle);
        Assert.DoesNotContain("安全", ShellLogic.UpdateNotice.DshTitle);
    }
}
