using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests.Domain;

/// <summary>
/// DshDiscovery 版本探测的三必须合规与记忆化契约（2026-09 启动时延修复配套）：
/// - 旧实现同步 ReadToEnd 可无限阻塞且超时不杀树（违反进程三必须）→ RealOS 回归锁定有界终止；
/// - 会话内重复探测（组合根/拉起链/就绪探针多次调用）曾致 Splash 冻结 → 记忆化只缓存昂贵探测，
///   环境钩子（DSH_VERSION 等）保持即时生效；InvalidateCache 清除记忆供写侧（首装安装成功等）调用。
/// </summary>
public class DshDiscoveryProbeTests
{
    private static string? FindNodeExeOrSkip()
    {
        var exe = DshWeb.RuntimeResolver.ResolveExisting().NodeExe;
        if (exe is null && Environment.GetEnvironmentVariable("DSH_FORCE_NPM_SMOKE") != "1")
            return null; // 无 Node 环境：跳过（CI 无 Node 时与 RealWorldNpmExecutionTests 同策略）
        return exe ?? throw new InvalidOperationException("DSH_FORCE_NPM_SMOKE=1 但未解析到 node.exe");
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "dsh-discovery-probe-" + Guid.NewGuid().ToString("N"));
        public TempDir() => System.IO.Directory.CreateDirectory(Path);
        public void Dispose() { try { System.IO.Directory.Delete(Path, recursive: true); } catch { } }
    }

    private static string WriteProbeScript(TempDir dir, string printed)
    {
        var p = System.IO.Path.Combine(dir.Path, "probe.js");
        File.WriteAllText(p, $"console.log('{printed}');");
        return p;
    }

    [Fact]
    public void ProbeVersionOutput_NormalChild_ReturnsTrimmedVersion()
    {
        var node = FindNodeExeOrSkip();
        if (node is null) return; // skip
        DshDiscovery.InvalidateCache();
        using var tmp = new TempDir();
        var script = WriteProbeScript(tmp, "9.9.9-probe");
        var v = DshDiscovery.ProbeVersionOutput(node!, $"\"{script}\"", timeoutMs: 5000);
        Assert.Equal("9.9.9-probe", v);
    }

    [Fact]
    public void ProbeVersionOutput_MultilineBannerChild_ExtractsVersionLine_RealOS()
    {
        // F3 端到端：真实子进程先打 banner 行再打版本行——旧行为会返回多行脏版本，
        // 新契约取首个版本形态行。唯一临时路径天然绕过探测记忆。
        var node = FindNodeExeOrSkip();
        if (node is null) return; // skip
        using var tmp = new TempDir();
        var script = System.IO.Path.Combine(tmp.Path, "probe-multi.js");
        File.WriteAllText(script,
            "console.log('DeepSeek Harness CLI');\nconsole.log('0.1.1-rc.8');\n");
        var v = DshDiscovery.ProbeVersionOutput(node!, $"\"{script}\"", timeoutMs: 5000);
        Assert.Equal("0.1.1-rc.8", v);
    }

    [Fact]
    public void ProbeVersionOutput_HangingChild_KilledWithinBound_RealOS()
    {
        // 零 Mock 回归：子进程持有 stdout 不关（旧实现会在此无限 ReadToEnd 阻塞），
        // 探测必须按时限杀整树并返回 null，且调用耗时有上界。
        var node = FindNodeExeOrSkip();
        if (node is null) return; // skip
        DshDiscovery.InvalidateCache();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var v = DshDiscovery.ProbeVersionOutput(
            node!, "-e \"process.stdout.write('x'); setInterval(()=>{},1000)\"", timeoutMs: 1500);
        sw.Stop();
        Assert.Null(v);
        Assert.True(sw.ElapsedMilliseconds < 10_000,
            $"挂起子进程应在时限后被杀（实际 {sw.ElapsedMilliseconds}ms）——三必须合规回归");
    }

    [Fact]
    public void ProbeMemo_SecondCallServedFromMemory_InvalidateCacheForcesRepro()
    {
        // 记忆化契约：同 (fileName,arguments) 第二次调用不再 spawn；InvalidateCache 后重新探测，
        // 能看到脚本内容变化（模拟"dsh 被更新后版本变化必须可见"）。
        var node = FindNodeExeOrSkip();
        if (node is null) return; // skip
        using var tmp = new TempDir();
        var script = WriteProbeScript(tmp, "1.0.0-before");

        DshDiscovery.InvalidateCache();
        var first = DshDiscovery.ProbeVersionOutput(node!, $"\"{script}\"", timeoutMs: 5000);
        Assert.Equal("1.0.0-before", first);

        File.WriteAllText(script, "console.log('1.0.0-after');"); // 底层已变
        var memoed = DshDiscovery.ProbeVersionOutput(node!, $"\"{script}\"", timeoutMs: 5000);
        Assert.Equal("1.0.0-before", memoed); // 记忆生效（不重 spawn）

        DshDiscovery.InvalidateCache(); // 写侧（如首装成功/更新应用）通知失效
        var fresh = DshDiscovery.ProbeVersionOutput(node!, $"\"{script}\"", timeoutMs: 5000);
        Assert.Equal("1.0.0-after", fresh); // 失效后重新探测可见新状态
    }

    [Fact]
    public void DiscoverCurrentRuntime_RespectsDshVersionHook_EveryCall()
    {
        // 身份层不做整级缓存：DSH_VERSION 钩子每次调用都即时生效（既有 UpdateOutcomes 语义）。
        var saved = Environment.GetEnvironmentVariable("DSH_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("DSH_VERSION", "7.7.7-test");
            Assert.Equal("7.7.7-test", DshDiscovery.DiscoverCurrentRuntime().Version);
            Environment.SetEnvironmentVariable("DSH_VERSION", "8.8.8-test");
            Assert.Equal("8.8.8-test", DshDiscovery.DiscoverCurrentRuntime().Version);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_VERSION", saved);
            DshDiscovery.InvalidateCache();
        }
    }
}
