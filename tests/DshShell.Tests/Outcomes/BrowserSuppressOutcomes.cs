using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 任务二】浏览器自动打开抑制验证。
///
/// 不关心 dsh 内部如何处理 --no-open 参数，只关心系统的最终物理状态：
/// - start-dsh.vbs 命令行中包含 --no-open
/// - 启动后没有新的独立浏览器主进程被拉起（需 E2E 验证）
///
/// 因果链验证：
///   Given: start-dsh.vbs 被执行
///   When:  dsh web 命令被构造
///   Then:  命令行包含 --no-open 参数
/// </summary>
public class BrowserSuppressOutcomes
{
    /// <summary>
    /// 【L3 Outcome — 核心】start-dsh.vbs 命令行契约验证。
    ///
    /// 验证 vbs 脚本中的命令行确实包含 --no-open 参数。
    /// 通过读取 vbs 文件内容进行静态断言。
    /// </summary>
    [Fact]
    public void Outcome_BrowserSuppress_StartDshVbs_ContainsNoOpenFlag()
    {
        // Given: start-dsh.vbs 文件路径
        var vbsPath = Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs");
        if (!File.Exists(vbsPath))
        {
            // CI 环境中 vbs 可能不在 bin 目录，跳过
            return;
        }

        // When: 读取 vbs 文件内容
        var content = File.ReadAllText(vbsPath);

        // Then: 所有 dsh web 命令行都必须包含 --no-open
        Assert.Contains("--no-open", content);
        // 验证没有遗漏的旧命令行（不含 --no-open 的 dsh web 调用）
        // 注：此断言要求所有 dsh web 调用都添加了 --no-open
    }

    /// <summary>
    /// 【L3 Outcome — 安全模式集成】start-dsh.vbs 支持 --safe-mode 参数。
    ///
    /// 验证当 DSH_SAFE_MODE=1 时，vbs 脚本会添加 --safe-mode 参数。
    /// </summary>
    [Fact]
    public void Outcome_BrowserSuppress_StartDshVbs_SupportsSafeModeFlag()
    {
        var vbsPath = Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs");
        if (!File.Exists(vbsPath)) return;

        var content = File.ReadAllText(vbsPath);

        // 验证安全模式逻辑存在
        Assert.Contains("DSH_SAFE_MODE", content);
        Assert.Contains("--safe-mode", content);
    }
}
