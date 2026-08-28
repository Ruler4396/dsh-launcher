using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// BootGuard 解析器 golden 样本测试（F31 契约哨兵，批次 6 补齐）：
/// - MatchBootErrorSignature：服务崩溃 stderr（管道行）/ 良性运行期告警（管道行）；
/// - EvaluatePageProbe：前端致命错误页 / 渲染豁免配置页（双编码前的原始探针 JSON）；
/// - IsServicePipedLogLine（F6）：签名匹配只认壳管道转发的服务行。
/// 样本失败 = dsh 输出/前端契约变更：改样本须同步 docs/DSH_CONTRACT_INVENTORY.md。
/// </summary>
public class GoldenBootGuardTests
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

    private static readonly ShellLogic.BootGuard.BootProfile Profile = new();

    // ---------- MatchBootErrorSignature ----------

    [Fact]
    public void PluginFatalStderr_PipedLines_MatchPluginInvolvedSignature()
    {
        var lines = LoadGolden("MatchBootErrorSignature_pluginFatalStderr.log")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var hit = lines
            .Select(l => l.TrimEnd('\r'))
            .Where(ShellLogic.BootGuard.IsServicePipedLogLine)
            .Select(l => ShellLogic.BootGuard.MatchBootErrorSignature(l, Profile))
            .FirstOrDefault(m => m is not null);
        Assert.NotNull(hit);
        Assert.Contains(hit, ShellLogic.BootGuard.PluginInvolvedMarkers); // 插件归因路由的证据面
    }

    [Fact]
    public void BenignRuntimeWarn_PipedLines_MatchNothing()
    {
        // 良性运行期输出（含 ECONNRESET——启动错误标志词表成员）在**运行期**签名表
        //（BootErrorMarkers）不得命中：两表分表是 S22 教训的契约。
        var lines = LoadGolden("MatchBootErrorSignature_benignRuntimeWarn.log")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.All(lines.Select(l => l.TrimEnd('\r')), l =>
            Assert.Null(ShellLogic.BootGuard.MatchBootErrorSignature(l, Profile)));
    }

    [Fact]
    public void StartupErrorMarker_InPipedLine_IsDetected_F6()
    {
        // 同一良性 ECONNRESET 管道行在**启动期**词表（StartupErrorMarkers）命中——
        // 这正是 F2/F6 防线设计的前提：增量 + 前缀过滤 + 宽限，而不是删词表。
        var line = LoadGolden("MatchBootErrorSignature_benignRuntimeWarn.log")
            .Split('\n').First(l => l.Contains("ECONNRESET"));
        Assert.True(ShellLogic.ServiceReadiness.LogShowsStartupError(line));
    }

    // ---------- IsServicePipedLogLine（F6） ----------

    [Fact]
    public void PipedLine_Detection_Contract()
    {
        Assert.True(ShellLogic.BootGuard.IsServicePipedLogLine("[12:00:01.000] [dsh] hello"));
        Assert.False(ShellLogic.BootGuard.IsServicePipedLogLine("{\"code\":\"E1012\",\"message\":\"[dsh] x\"}")); // 壳 JSON 行
        Assert.False(ShellLogic.BootGuard.IsServicePipedLogLine("bare npm ERR line"));                            // 无前缀
        Assert.False(ShellLogic.BootGuard.IsServicePipedLogLine(""));
    }

    // ---------- EvaluatePageProbe ----------

    [Fact]
    public void FatalErrorPage_ProbeJson_BadSignature_WithOriginalText()
    {
        var result = ShellLogic.BootGuard.EvaluatePageProbe(
            LoadGolden("EvaluatePageProbe_fatalErrorPage.json"), Profile);
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.BadSignature, result.Kind);
        Assert.NotNull(result.Detail);
        Assert.Contains("bootstrap facade is missing", result.Detail); // 证据携带异常原文（S22 验收）
    }

    [Fact]
    public void RenderedConfigPage_ProbeJson_RenderedExemption()
    {
        var result = ShellLogic.BootGuard.EvaluatePageProbe(
            LoadGolden("EvaluatePageProbe_renderedConfigPage.json"), Profile);
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.Rendered, result.Kind);
    }
}
