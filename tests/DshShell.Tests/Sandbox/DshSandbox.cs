namespace DshShell.Tests.Sandbox;

/// <summary>
/// dsh 沙盒测试环境：%TEMP% 下一次性隔离的 DSH_HOME（瞬态隔离，非持久沙盒——
/// 见 docs/00 核心约束六第 3 条）。用于验证"生产实现只碰沙盒路径"的落盘类契约。
/// 2026-09-21 质量审查 B2：原有的 Install*/WriteSettings/GetLog*/LogContains/
/// GetEnvironmentVariable 等 helper 全部零调用方（其消费者是断言幽灵契约的假绿用例，
/// 已随那批用例一并删除），夹具随之收瘦到只剩目录脚手架 + 确定性清理。
/// </summary>
public sealed class DshSandbox : IDisposable
{
    public string SandboxRoot { get; }
    public string DshHome { get; }
    public string LauncherDataDir { get; }

    public DshSandbox()
    {
        SandboxRoot = Path.Combine(Path.GetTempPath(), $"dsh-sandbox-{Guid.NewGuid():N}");
        DshHome = Path.Combine(SandboxRoot, ".dsh");
        LauncherDataDir = Path.Combine(DshHome, "dsh-launcher");

        Directory.CreateDirectory(DshHome);
        Directory.CreateDirectory(LauncherDataDir);
    }

    /// <summary>清理沙盒目录。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(SandboxRoot))
                Directory.Delete(SandboxRoot, recursive: true);
        }
        catch { /* 清理失败忽略 */ }
    }
}
