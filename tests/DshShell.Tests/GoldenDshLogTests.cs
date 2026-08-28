using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// dsh 日志契约 golden 样本测试（F31 哨兵 + F2 门禁）。
/// 样本失败 = dsh（或壳日志格式）契约变了：先确认变更可接受，再改样本，
/// commit message 必须写明"dsh 契约变更：X→Y"并同步 docs/DSH_CONTRACT_INVENTORY.md。
/// 样本来源：统一日志的真实三类行——服务原始输出（壳加 [dsh] 前缀）、壳 JSON Lines、混排会话。
/// </summary>
public class GoldenDshLogTests
{
    private static string LoadGolden(string name)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "GoldenFiles", "dsh", name);
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = Path.GetDirectoryName(dir)
                  ?? throw new FileNotFoundException($"golden sample not found: {name}");
        }
        throw new FileNotFoundException($"golden sample not found: {name}");
    }

    // ---------- LogShowsStartupError：关键字命中面（弱假设契约） ----------

    [Fact]
    public void StartupError_NpmErrServiceLine_IsDetected()
    {
        var line = LoadGolden("LogShowsStartupError_npmErrServiceLine.log")
            .Split('\n').First(l => l.Contains("npm ERR"));
        Assert.True(ShellLogic.ServiceReadiness.LogShowsStartupError(line));
    }

    [Fact]
    public void DshRuntimeEconnreset_Line_IsDetected_WhyIncrementalMatters()
    {
        // 该行是 dsh 运行期的**合法**上游重试告警——它命中关键字表正是 F2 误判的根源：
        // 全量历史扫描会把它当启动失败；增量扫描 + 宽限语义只看本轮新增，才算正确的兜底。
        var line = LoadGolden("LogShowsStartupError_dshRuntimeEconnreset.log")
            .Split('\n').First(l => l.Contains("ECONNRESET"));
        Assert.True(ShellLogic.ServiceReadiness.LogShowsStartupError(line));
    }

    // ---------- IsShellAuthoredLogEntry：壳行/服务行分类契约 ----------

    [Fact]
    public void ShellAuthored_JsonLines_AreRecognized()
    {
        var lines = LoadGolden("IsShellAuthoredLogEntry_shellJsonLines.jsonl")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.True(
            ShellLogic.BootGuard.IsShellAuthoredLogEntry(l.TrimEnd('\r')),
            $"should be shell-authored: {l}"));
    }

    [Fact]
    public void ServiceOutput_Lines_AreNotShellAuthored()
    {
        var lines = LoadGolden("LogShowsStartupError_dshRuntimeEconnreset.log")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines, l => Assert.False(
            ShellLogic.BootGuard.IsShellAuthoredLogEntry(l.TrimEnd('\r')),
            $"should NOT be shell-authored: {l}"));
    }

    [Fact]
    public void MixedSession_ServiceErrorLines_SurviveShellLineFilter()
    {
        // 混排会话 golden：服务行（含 ETIMEDOUT）必须仍能命中；壳行必须被分类出来。
        var lines = LoadGolden("LogShowsStartupError_mixedSession.log")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();
        var serviceErrorLine = lines.Single(l => l.Contains("ETIMEDOUT"));
        Assert.False(ShellLogic.BootGuard.IsShellAuthoredLogEntry(serviceErrorLine));
        Assert.True(ShellLogic.ServiceReadiness.LogShowsStartupError(serviceErrorLine));
        Assert.NotEmpty(lines.Where(ShellLogic.BootGuard.IsShellAuthoredLogEntry).ToList());
    }

    [Fact]
    public void ShellErrorLine_EmbeddingNpmErrText_IsFilteredOut()
    {
        // E1012 壳行的 message 内嵌 "npm ERR!"——整段/整文件匹配会把它误判为启动失败
        //（F2 的另一污染源）。壳行过滤 + 逐行判定必须把它排除：过滤后无任何命中。
        var lines = LoadGolden("IsShellAuthoredLogEntry_shellJsonLines.jsonl")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToList();
        Assert.Contains(lines, l => l.Contains("npm ERR")); // 防样本漂移：样本确含关键字文本
        var detected = lines
            .Where(l => !ShellLogic.BootGuard.IsShellAuthoredLogEntry(l))
            .Any(l => ShellLogic.ServiceReadiness.LogShowsStartupError(l));
        Assert.False(detected);
    }
}
