using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 启动脚本命令行契约】start-dsh.vbs 的**最终物理形态**：
/// 三条回退分支（全局 dsh / npm shim / npx）拼出的 cmdline 都必须带 --no-open，
/// 且 ADR-022 安全模式必须以 `--profile &lt;name&gt;` 形态出现（由壳注入 DSH_PROFILE 驱动）。
///
/// 本文件重写自一套"永久绿"的假断言：原来它读 AppContext.BaseDirectory\start-dsh.vbs
/// 并 `if (!File.Exists) return;`，而该文件从不在测试输出目录 ⇒ 断言一次都没执行过；
/// 更糟的是它断言的 token（"DSH_SAFE_MODE"、"--safe-mode"）在真实 vbs 里根本不存在，
/// 一旦真跑必然红。现改为走 RepoFile 读真实 scripts/start-dsh.vbs，找不到就抛。
///
/// 分工说明：scripts/test.ps1 的静态闸也用正则钉 --no-open 与 bootMode 形状，那是
/// "改脚本时当场拦"；这里钉的是"启动后进程实际收到的命令行参数形状"，两者可独立失败
/// （test.ps1 只扫 vbs 文本，不校验 --profile 与 env 读取的配对关系）。
/// </summary>
public class BrowserSuppressOutcomes
{
    /// <summary>三条分支各一条 cmdline 赋值，每条都必须带 --no-open（防系统浏览器弹同窗）。</summary>
    [Fact]
    public void StartDshVbs_EveryBranchCommandLine_CarriesNoOpen()
    {
        var lines = RepoFile.Read("scripts/start-dsh.vbs")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("cmdline = ", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.True(lines.Length >= 3,
            $"start-dsh.vbs 应至少有三条分支的 cmdline 赋值（全局 dsh / npm shim / npx），实测 {lines.Length} 条");
        var missing = lines.Where(l => !l.Contains("--no-open")).ToArray();
        // 消息参数是先求值的：这里绝不能写 missing[0]，否则"全部分支都带 --no-open"这条
        // **成功路径**会自己抛 IndexOutOfRange（本用例首跑就是这样红的，被测试抓了个现行）。
        Assert.True(missing.Length == 0,
            $"有 {missing.Length}/{lines.Length} 条分支命令行缺 --no-open，dsh web 会自己弹系统浏览器：{string.Join(" | ", missing)}");
    }

    /// <summary>安全模式在 vbs 里的真形态：读壳注入的 DSH_PROFILE，拼成根级 --profile 启动。</summary>
    [Fact]
    public void StartDshVbs_SafeMode_UsesProfileFlagNotASafeModeSwitch()
    {
        var content = RepoFile.Read("scripts/start-dsh.vbs");

        Assert.Contains("DSH_PROFILE", content);
        Assert.Contains("\"--profile \"", content);
        Assert.Contains("bootMode", content);
        // 反向钉住一次真实事故：曾有人按 "--safe-mode" 这个**不存在**的开关去理解启动链，
        // 并照它写了断言（永远跑不到）。安全模式只有 --profile 这一条表达。
        Assert.DoesNotContain("--safe-mode", content);
    }
}
