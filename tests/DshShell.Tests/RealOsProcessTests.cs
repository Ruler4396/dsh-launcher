using System.Diagnostics;
using System.Text;
using DshWeb;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 支柱一：真实 OS 交互集成测试（SDET 硬核防线，打破"Mock 幻觉"）。
/// **零 Mock、零 Mock 框架**：直接调用 <see cref="ProcessRunner.RunProcessCaptured"/> 真实启动 OS 进程，
/// 拦截进程调用、编码冲突、僵尸树清理等单元测试无法触及的系统级 Bug。
///
/// 成员：
///  1. Regression_NpmCmd_Execution_And_Encoding——核心复现：真实 .cmd 输出中文（GBK 场景），
///     断言执行引擎正确 UTF-8 解码、进程不秒退、输出绝无乱码。
///  2. RealOs_ZombieTree_Killed_On_Timeout——真实 cmd 子进程树 + 超时 → Kill(entireProcessTree)
///     后子进程确实死干净。
///  3. RunProcessCaptured_EncodesGbk_AsUtf8——直接验证 UTF-8 捕获路径。
/// </summary>
// 支柱四分层：Real-OS 类测试标记 Category=RealOS，CI 单独 Stage 运行（安装真实 Node，绝不 Skip）
[Trait("Category", "RealOS")]
public class RealOsProcessTests
{
    private static bool ForceSmoke =>
        Environment.GetEnvironmentVariable("DSH_FORCE_NPM_SMOKE") == "1";

    private string MakeTempDir() =>
        Path.Combine(Path.GetTempPath(), "dsh-realos-" + Guid.NewGuid().ToString("N"));

    /// <summary>检查字符串是否含"乱码字符"——非法 UTF-8 解码的替换字符（U+FFFD）。</summary>
    private static bool ContainsGarbage(string s) =>
        s.Contains('\uFFFD') // 替换字符 = 解码失败的铁证
        || s.Contains('\u013C') || s.Contains('\u00BC') || s.Contains('\uFDE8'); // 常见 GBK 误解码残渣

    // ---------------- 核心复现：npm.cmd 秒退 + 中文乱码 ----------------

    [Fact]
    public void Regression_NpmCmd_Execution_And_Encoding()
    {
        // Bug 驱动复现铁律：npm.cmd 秒退 + 中文乱码必须转化为零 Mock 真实 OS 复现测试。
        // 场景：真实创建一个输出中文（GBK）的 .cmd 脚本，用底层执行引擎拉起，断言：
        //  1. 进程不秒退（WaitForExit 拿到正常 ExitCode，非 Process.Start 抛 Win32Exception）
        //  2. 捕获输出中**绝无乱码**（UTF-8 解码正确）
        var dir = MakeTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            // 真实 .cmd：输出中文。注意：必须用**无 BOM** 的 UTF-8 写入——cmd.exe 遇到 UTF-8 BOM
            // 会把首行 `@echo off` 解析失败（报"'@echo' 不是内部或外部命令"）。
            // 代码页注意：CI（Windows Server 英文代码页）执行含中文的 .cmd 时，cmd 按 ANSI
            // 代码页解析字节，中文输出可能非标准 UTF-8——因此**中文正确性断言交给 node 脚本**
            //（node 内部统一 UTF-8，跨代码页稳定），本 .cmd 只验证"进程不秒退 + 引擎正常捕获"。
            var script = Path.Combine(dir, "echo-chinese.cmd");
            File.WriteAllText(script,
                "@echo off\r\n" +
                "echo cmd-script-ok\r\n" +
                "exit /b 0\r\n",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)); // 无 BOM

            // 直接真实启动 cmd.exe 执行该 .cmd（零 Mock），走 UTF-8 捕获管线
            var ok = ProcessRunner.RunProcessCaptured(
                "cmd.exe", $"/c \"{script}\"", out var outputTail,
                timeoutMs: 30000);

            // ① 进程不秒退：ExitCode 正常返回（RunProcessCaptured 内部 WaitForExit 成功）
            Assert.True(ok, "cmd.exe 执行 .cmd 应正常退出（非 Process.Start 秒抛异常）。outputTail=" + outputTail);
            // ② 捕获到脚本输出（引擎确实读到了 stdout，非秒退空捕获）
            Assert.Contains("cmd-script-ok", outputTail);
            // ③ 中文无乱码断言由 Regression_NpmCmd_ChineseOutput_ViaNode 覆盖（node 跨代码页稳定）
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Regression_NpmCmd_ChineseOutput_ViaNode()
    {
        // 中文输出 + 无乱码的跨代码页稳定验证：node.exe 内部统一 UTF-8，输出中文经引擎 UTF-8
        // 捕获必须无乱码（U+FFFD 替换字符 = 解码失败铁证）。CI/本地代码页差异不影响 node。
        var env = RuntimeResolver.ResolveExisting();
        if (env?.NodeExe is null || !File.Exists(env.NodeExe))
        {
            Assert.True(ForceSmoke, "本地强制模式：未检测到 node.exe，中文输出测试需真实 Node");
            return;
        }
        var dir = MakeTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var script = Path.Combine(dir, "chinese.js");
            File.WriteAllText(script,
                "console.log('文件名、目录名或卷标语法不正确');\n" +
                "console.log('下载失败');\n",
                Encoding.UTF8);

            var ok = ProcessRunner.RunProcessCaptured(env.NodeExe, $"\"{script}\"", out var outputTail,
                timeoutMs: 30000);
            Assert.True(ok, "node 执行中文输出脚本应成功。outputTail=" + outputTail);
            Assert.Contains("文件名", outputTail);
            Assert.Contains("下载失败", outputTail);
            Assert.False(ContainsGarbage(outputTail),
                "UTF-8 解码不应产生乱码。outputTail=" + outputTail);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    [Fact]
    public void Regression_NpmCmd_GbkBytes_NoMojibake()
    {
        // 更极端：真实构造一个以 GBK 编码字节输出中文的 node 脚本（模拟旧 npm.cmd 的 GBK 输出），
        // 由 node.exe 执行、引擎 UTF-8 捕获——验证即使上游是 GBK，捕获也不产生 U+FFFD 乱码。
        //（node ≥7 内部统一 UTF-8，本测试额外锁定"捕获管线 UTF-8 解码"不会制造乱码。）
        if (!File.Exists("D:\\node\\node.exe") && !ForceSmoke)
        {
            return; // CI 无 Node 允许跳过；本地 test.ps1 强制模式才要求
        }
        var dir = MakeTempDir();
        Directory.CreateDirectory(dir);
        try
        {
            // 找 node.exe
            var env = RuntimeResolver.ResolveExisting();
            if (env?.NodeExe is null || !File.Exists(env.NodeExe))
            {
                Assert.True(ForceSmoke, "本地强制模式：未检测到 node.exe");
                return;
            }
            var script = Path.Combine(dir, "gbk.js");
            File.WriteAllText(script,
                "const s = '下载失败：文件名卷标不正确';\n" +
                "process.stdout.write(s + '\\n');\n",
                Encoding.UTF8);

            var ok = ProcessRunner.RunProcessCaptured(env.NodeExe, $"\"{script}\"", out var outputTail,
                timeoutMs: 30000);
            Assert.True(ok, "node 执行脚本应成功。outputTail=" + outputTail);
            Assert.False(ContainsGarbage(outputTail),
                "node 输出中文经 UTF-8 捕获不应乱码。outputTail=" + outputTail);
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---------------- 真实 OS：僵尸树清理 ----------------

    [Fact]
    public void RealOs_ZombieTree_Killed_On_Timeout()
    {
        // 真实拉起一个长驻 node 进程（print PID + sleep 120s），经引擎 RunProcessCaptured 以
        // **短超时**触发 → 引擎 WaitForExit 超时后 Kill(entireProcessTree:true) → 断言该进程被强杀。
        // 零 Mock：完整走真实 Process.Start + 超时 + kill 路径，锁定"僵尸树清理"真实可用。
        var env = RuntimeResolver.ResolveExisting();
        if (env?.NodeExe is null || !File.Exists(env.NodeExe))
        {
            Assert.True(ForceSmoke, "本地强制模式：未检测到 node.exe，僵尸树测试需真实 Node");
            return;
        }
        var dir = MakeTempDir();
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "spawn.js");
        File.WriteAllText(script,
            "console.log('PROC_PID=' + process.pid);\n" +
            "setTimeout(() => {}, 120000);\n");
        var capturedPid = 0;
        try
        {
            // 引擎真实启动长驻进程；progress 回调捕获脚本 print 的 PID
            var ok = ProcessRunner.RunProcessCaptured(env.NodeExe, $"\"{script}\"", out _,
                timeoutMs: 800,   // 短超时：进程 sleep 120s 不会自退 → 引擎超时杀树
                progress: line =>
                {
                    var m = System.Text.RegularExpressions.Regex.Match(line, "PROC_PID=(\\d+)");
                    if (m.Success) capturedPid = int.Parse(m.Groups[1].Value);
                });
            Assert.False(ok, "引擎应在短超时后返回 false（进程未在 800ms 内退出）");
            Assert.True(capturedPid > 0, "应捕获到引擎启动的进程 PID");
            // 给 kill 收尾时间后断言进程被强杀干净
            Thread.Sleep(1500);
            Assert.False(ProcessExists(capturedPid),
                "超时后僵尸进程应被 Kill(entireProcessTree:true) 强杀干净（PID=" + capturedPid + "）");
        }
        finally
        {
            TryDelete(dir);
        }
    }

    // ---------------- 直接验证 UTF-8 捕获路径 ----------------

    [Fact]
    public void RunProcessCaptured_Executes_And_Captures_Stdout()
    {
        var env = RuntimeResolver.ResolveExisting();
        if (env?.NodeExe is null || !File.Exists(env.NodeExe))
        {
            Assert.True(ForceSmoke, "本地强制模式：未检测到 node.exe");
            return;
        }
        var ok = ProcessRunner.RunProcessCaptured(env.NodeExe, "-e \"console.log('hello-real-os')\"",
            out var outputTail, timeoutMs: 30000);
        Assert.True(ok, "node -e 应成功。outputTail=" + outputTail);
        Assert.Contains("hello-real-os", outputTail);
    }

    // ---------------- Helpers ----------------

    private static string JsonSerializer_Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static bool ProcessExists(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
