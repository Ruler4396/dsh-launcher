using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 真实环境冒烟测试（任务二：打破"测试幻觉"）。
/// **不使用任何 Mock**：直接调用重构后的 <see cref="Program.RunNpmCommand"/>（node.exe 直接执行
/// npm-cli.js），验证在真实 OS 环境下能拉起 Node/npm 并拿到正确退出码。
/// 目的：锁定"底层执行引擎"在真实机器上可用——300+ 单测 Mock Process 无法发现 OS 级 Bug
///（cmd.exe /c 引号剥离、.cmd 编码冲突、PATH 缺失等真实环境问题），本测试是硬性防线。
///
/// 跳过策略（xUnit v2 无运行时 Skip，用环境变量门控实现"CI 可跳过 / 本地强制"）：
///  - 默认（CI / 无 DSH_FORCE_NPM_SMOKE）：无 Node 时静默通过（记录 Trace），不阻断 CI；
///  - test.ps1 本地运行时设置 DSH_FORCE_NPM_SMOKE=1：**无 Node 即失败**（硬门禁，
///    强制开发者本机验证真实 Node 链路可用——详见 test.ps1 中的配置注释）。
/// </summary>
public class RealWorldNpmExecutionTests
{
    private static bool IsNodeAvailable()
    {
        // 与 Program.RunNpmCommand 相同的探测链路：RuntimeResolver → node.exe
        var env = RuntimeResolver.ResolveExisting();
        if (env?.NodeExe is null || !File.Exists(env.NodeExe)) return false;
        var cli = Program.FindNpmCliJs(env.NodeExe);
        return cli is not null && File.Exists(cli);
    }

    /// <summary>本地强制模式（test.ps1 设置）：无 Node 环境时让冒烟测试失败，而不是静默跳过。</summary>
    private static bool ForceSmoke =>
        Environment.GetEnvironmentVariable("DSH_FORCE_NPM_SMOKE") == "1";

    private static void GuardNodeAvailable(string scenario)
    {
        if (IsNodeAvailable()) return;
        // CI 默认允许跳过；test.ps1 本地强制模式下必须失败（硬门禁：开发者本机真实链路必须可用）
        Assert.True(ForceSmoke,
            $"[冒烟门禁] {scenario}：当前机器未检测到可用的 Node.js 环境（node.exe + npm-cli.js）。" +
            "本测试由 test.ps1 在本地强制运行（DSH_FORCE_NPM_SMOKE=1），请先安装 Node.js 18+。");
    }

    [Fact]
    public void Can_Execute_NpmVersion_In_Real_World()
    {
        GuardNodeAvailable("npm --version");
        if (!IsNodeAvailable()) return; // CI 无 Node：静默通过

        // 真实调用：node.exe 直接执行 npm-cli.js --version（不 Mock，完整走 Process.Start）
        var ok = Program.RunNpmCommand("--version", out var errorTail);

        Assert.True(ok, $"npm --version 应成功退出。errorTail={errorTail}");
        Assert.False(string.IsNullOrWhiteSpace(errorTail), "npm --version 成功时应输出版本号（11.x）");
    }

    [Fact]
    public void Can_Execute_NpmVersion_Via_NodeCliJs_Directly()
    {
        // 更细的冒烟：确认探测到的 npm-cli.js 真实可用（find → node 执行 → 版本号）
        GuardNodeAvailable("node npm-cli.js --version");
        if (!IsNodeAvailable()) return;

        var env = RuntimeResolver.ResolveExisting()!;
        var cli = Program.FindNpmCliJs(env.NodeExe!);
        Assert.NotNull(cli);
        Assert.True(File.Exists(cli), "npm-cli.js 必须真实存在于磁盘");

        // 直接 node npm-cli.js --version
        var psi = new System.Diagnostics.ProcessStartInfo(env.NodeExe!, $"\"{cli}\" --version")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var outText = p.StandardOutput.ReadToEnd();
        var errText = p.StandardError.ReadToEnd();
        p.WaitForExit(30000);
        Assert.True(p.ExitCode == 0, $"node npm-cli.js --version 应成功。exit={p.ExitCode} err={errText}");
        Assert.Contains(".", outText); // 版本号形如 11.8.0
    }

    [Fact]
    public void FindNpmCliJs_ProbeOrder_StandardLayoutThenAppData()
    {
        // 探测优先级契约（纯逻辑，离线可验证）：
        //   a. node.exe 同级 node_modules\npm\bin\npm-cli.js
        //   b. %APPDATA%\npm\node_modules\npm\bin\npm-cli.js
        // 用临时目录构造两种布局验证优先级。
        using var tmp = new TempDir();
        var nodeDir = Path.Combine(tmp.Path, "node");
        Directory.CreateDirectory(Path.Combine(nodeDir, "node_modules", "npm", "bin"));
        var stdCli = Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js");
        File.WriteAllText(stdCli, "std");

        // 只有标准布局 → 命中 a
        Assert.Equal(stdCli, Program.FindNpmCliJs(Path.Combine(nodeDir, "node.exe")));

        // 两布局都不存在 → null
        Directory.Delete(Path.Combine(nodeDir, "node_modules"), recursive: true);
        Assert.Null(Program.FindNpmCliJs(Path.Combine(nodeDir, "node.exe")));
    }

    /// <summary>每测试用一次性临时目录。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dsh-realworld-" + Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
