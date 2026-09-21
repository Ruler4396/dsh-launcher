using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 启动脚本链的最终物理形态（2026-09-21 B6 反转）】
///
/// 本类原先钉的是 start-dsh.vbs 三条回退分支的 cmdline 形状（--no-open / DSH_PROFILE→--profile）。
/// 审查 N11 的判决是**除名而不是对齐**：vbs 的 where-dsh→npm-shim→npx 链缺 SelfContained
/// 一级、走 ADR-021 禁止的 cmd/shim 形态、向 UTF-8 日志写 GBK；在 vbs 里复刻版本比较只会造出
/// 第五份"发现真相源"。壳早已不经它拉服务（ADR-024 单轨 + Program 里 grep 零调用），
/// 存量用户的部署副本自包含——停发即无断裂。
/// 于是这里的契约从"vbs 长什么样"反转为"vbs 不得复活、一键入口必须直达壳"：
/// 壳侧的等价形状（--no-open/--profile）继续由 <c>ServiceLaunchContractTests</c> 把守——
/// 删旧用例少挡的东西没有丢失，只是从"脚本副本"收敛到"壳的单一实现"。
/// （历史：本文件 2026-09-20 曾从"路径守卫假绿"重写为 RepoFile 真断言，形状教训见 RepoFile.cs 头注。）
/// </summary>
public class BrowserSuppressOutcomes
{
    /// <summary>[B6 防回流] start-dsh.vbs / start-dsh.cmd 必须不在仓库——若有人再加回来，
    /// 分发清单（csproj/wxs/build-*.ps1）与这套断言会一起红。</summary>
    [Fact]
    public void LegacyVbsLaunchChain_MustStayDeleted()
    {
        Assert.False(RepoFile.Exists("scripts/start-dsh.vbs"),
            "start-dsh.vbs 复活——旧三级回退链会把 dsh 身份割裂带回来（审查 N11/B6）");
        Assert.False(RepoFile.Exists("scripts/start-dsh.cmd"),
            "start-dsh.cmd 复活——同上（where dsh/npx 的 PATH 依赖形状）");
    }

    /// <summary>一键入口 dsh-web.cmd 的最终物理形态：直接启动同目录的壳，不再伸手拉 vbs。</summary>
    [Fact]
    public void DshWebCmd_LaunchesShellDirectly_NoVbsDetour()
    {
        var cmd = RepoFile.Read("scripts/dsh-web.cmd");
        Assert.Contains("%DIR%DshWeb.exe", cmd);
        Assert.DoesNotContain("start-dsh", cmd);
        Assert.DoesNotContain("wscript", cmd);
    }
}
