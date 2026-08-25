namespace DshWeb.Managers;

/// <summary>
/// UI 自测结果落盘（ADR-024 双轨制收敛：自 Program.cs 迁出）——
/// CI 可靠取回通道：GUI 子系统应用的 stdout/退出码在 pwsh 里未必可靠回传，
/// 故 --ui-selftest 把结论写入结果文件（默认当前目录 ui-selftest-result.txt，
/// 可用 DSH_TEST_RESULT 覆盖）。组合根只保留薄转发。
/// </summary>
internal static class SelftestReporter
{
    internal static void Write(bool pass, string detail)
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("DSH_TEST_RESULT") ?? "ui-selftest-result.txt";
            try { System.IO.Path.GetFullPath(path); } catch { path = "ui-selftest-result.txt"; }
            File.WriteAllText(path, $"pass={pass}\n{detail}\n");
        }
        catch { /* 落盘失败不阻断 */ }
    }
}
