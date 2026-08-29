using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// BootGuard 契约测试（ADR-023 Task 1：BootSignature 单点配置）。
/// 锁定纯函数语义：签名档默认值/环境覆盖/降级容错、页面探针四分类求值、
/// 日志层签名表、命令行拆分（DSH_SERVICE_CMD 去 cmd.exe 的支撑函数）。
/// </summary>
public class BootGuardContractTests
{
    // ---------------- 签名档默认值 ----------------

    [Fact]
    public void DefaultProfile_HasSaneFalsePositiveGuards()
    {
        var p = ShellLogic.BootGuard.ResolveProfile(null);
        Assert.True(p.GraceMs > 0, "grace 必须为正（慢启动不误报的第一道闸）");
        Assert.True(p.ProbeIntervalMs > 0);
        Assert.True(p.AbsentThreshold >= 2, "缺席阈值必须 ≥2（单次缺席绝不判死）");
        Assert.True(p.RenderedMinTextChars > 0, "渲染豁免阈值必须 >0（0 会让空白页也被豁免，等于废掉缺席计票）");
        Assert.NotEmpty(p.BadSignatures);
        // 双版本兼容好符号（2026-08 用户实测回归）：
        // - dsh ≤ 0.1.0-rc.7 在页面注入 window.__DSH_BOOT__ = { version: ... }；
        // - dsh ≥ 0.1.1-rc.2 不再写 __DSH_BOOT__，改为内联脚本注入 window.__ModuleLoader__
        //   队列门面，client-modules boot 完成时将其 mode 置 "live"。
        // 析取式必须同时覆盖两代引导链——缺任一支都会让对应版本的页面被判
        // "好符号持续缺席"（E2008 无插件误报的根因）。
        Assert.Contains("__DSH_BOOT__", p.GoodSymbol);
        Assert.Contains("__ModuleLoader__", p.GoodSymbol);
    }

    [Fact]
    public void DefaultGoodSymbol_CoversLegacyAndModernBootChains()
    {
        // 探针脚本必须携带两支好符号表达式（rc.6 与 rc.2+ 都能命中 Healthy）
        var script = ShellLogic.BootGuard.ResolveProfile(null).BuildProbeScript();
        Assert.Contains("__DSH_BOOT__.version", script);
        Assert.Contains("__ModuleLoader__", script);
        Assert.Contains("live", script);
    }

    [Fact]
    public void ResolveProfile_NullOrWhitespaceEnv_ReturnsDefault()
    {
        Assert.Equal(ShellLogic.BootGuard.ResolveProfile(null), ShellLogic.BootGuard.ResolveProfile(""));
        Assert.Equal(ShellLogic.BootGuard.ResolveProfile(null), ShellLogic.BootGuard.ResolveProfile("   "));
    }

    [Fact]
    public void ResolveProfile_ValidJson_OverridesAllFields()
    {
        const string json = """
            {
              "good_symbol": "window.__MY_BOOT__.ok",
              "bad_signatures": ["fake-bad-marker"],
              "grace_ms": 1500,
              "probe_interval_ms": 250,
              "absent_threshold": 4,
              "rendered_min_text_chars": 120,
              "log_error_signatures": ["FAKE-LOG-SIG"]
            }
            """;
        var p = ShellLogic.BootGuard.ResolveProfile(json);
        Assert.Equal("window.__MY_BOOT__.ok", p.GoodSymbol);
        Assert.Equal(new[] { "fake-bad-marker" }, p.BadSignatures);
        Assert.Equal(1500, p.GraceMs);
        Assert.Equal(250, p.ProbeIntervalMs);
        Assert.Equal(4, p.AbsentThreshold);
        Assert.Equal(120, p.RenderedMinTextChars);
        Assert.Equal(new[] { "FAKE-LOG-SIG" }, p.ExtraLogSignatures);
        // 沙盒注入假签名的核心诉求：探针脚本必须携带覆盖后的表达式与坏签名求值路径
        Assert.Contains("__MY_BOOT__", p.BuildProbeScript());
    }

    [Fact]
    public void ResolveProfile_PartialJson_MergesWithDefaults()
    {
        var def = ShellLogic.BootGuard.ResolveProfile(null);
        var p = ShellLogic.BootGuard.ResolveProfile("""{ "grace_ms": 777 }""");
        Assert.Equal(777, p.GraceMs);
        Assert.Equal(def.ProbeIntervalMs, p.ProbeIntervalMs); // 未指定字段保持默认
        Assert.Equal(def.BadSignatures, p.BadSignatures);
    }

    [Fact]
    public void ResolveProfile_InvalidJson_FallsBackToDefaultEntirely()
    {
        var def = ShellLogic.BootGuard.ResolveProfile(null);
        var p = ShellLogic.BootGuard.ResolveProfile("{ not-json !!");
        Assert.Equal(def.GraceMs, p.GraceMs);
        Assert.Equal(def.GoodSymbol, p.GoodSymbol);
        Assert.Equal(def.AbsentThreshold, p.AbsentThreshold);
    }

    [Fact]
    public void ResolveProfile_WrongFieldTypes_FallBackPerField()
    {
        var def = ShellLogic.BootGuard.ResolveProfile(null);
        var p = ShellLogic.BootGuard.ResolveProfile(
            """{ "grace_ms": "not-a-number", "bad_signatures": "also-wrong", "good_symbol": 42 }""");
        Assert.Equal(def.GraceMs, p.GraceMs);
        Assert.Equal(def.BadSignatures, p.BadSignatures);
        Assert.Equal(def.GoodSymbol, p.GoodSymbol);
    }

    [Fact]
    public void BuildProbeScript_IsSelfContained_TryCatchWrapped()
    {
        var script = ShellLogic.BootGuard.ResolveProfile(null).BuildProbeScript();
        Assert.Contains("try", script);
        Assert.Contains("catch", script);
        Assert.Contains("__dshLastError", script); // 异常原文采集通道
        Assert.Contains("JSON.stringify", script);
    }

    // ---------------- 页面探针求值（四分类） ----------------

    private static ShellLogic.BootGuard.PageProbeResult Evaluate(string? json)
        => ShellLogic.BootGuard.EvaluatePageProbe(json, ShellLogic.BootGuard.ResolveProfile(null));

    private static string ProbeJson(bool good, string text, string err)
        => System.Text.Json.JsonSerializer.Serialize(new { good, text, err });

    [Fact]
    public void EvaluatePageProbe_DoubleEncodedStringLiteral_ExecuteScriptAsyncRealShape_DecodedOnce()
    {
        // ExecuteScriptAsync 真实返回形状：脚本 return JSON.stringify(...) → SDK 再编码一层字符串字面量
        var inner = ProbeJson(false, "Plugin crash: bootstrap facade is missing", "");
        var doubleEncoded = System.Text.Json.JsonSerializer.Serialize(inner); // → "\"{\\\"good\\\"...}\""
        var r = Evaluate(doubleEncoded);
        // 2026-08 回归契约：DOM 文本坏签名降级为 Absent（不再一票 BadSignature），防 E2008 误判
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.Absent, r.Kind);
        Assert.StartsWith("dom-suspect[bootstrap facade is missing]=", r.Detail);
    }

    [Fact]
    public void EvaluatePageProbe_GoodSymbolAfterBadSignature_FalsePositiveGuard()
    {
        // 好符号存在且页面干净（S23 ok-slow 的健康路径）
        var r = Evaluate(ProbeJson(true, "DeepSeek Harness (Sandbox)", ""));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.GoodSymbol, r.Kind);
    }

    [Fact]
    public void EvaluatePageProbe_GoodSymbolBeatsDomSuspect_RealUiRendered()
    {
        // 2026-08 回归契约：真实 UI 已渲染（good=true），但隐藏节点/错误边界含字面量坏签名
        // （如 "bootstrap facade is missing"）——好符号必须胜出，判 GoodSymbol（不再误判 E2008）。
        var r = Evaluate(ProbeJson(true, "Plugin crash: window.__ModuleLoader__ bootstrap facade is missing", ""));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.GoodSymbol, r.Kind);
    }

    [Fact]
    public void EvaluatePageProbe_ErrBadSignatureBeatsGoodSymbol_Immediate()
    {
        // err 原文坏签名仍一票否决（S22"捕获原文"硬要求，抗误报仅限 DOM 文本层）。
        var r = Evaluate(ProbeJson(true, "all good", "Uncaught: window.__ModuleLoader__ bootstrap facade is missing"));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.BadSignature, r.Kind);
        Assert.StartsWith("err[bootstrap facade is missing]=", r.Detail);
        Assert.Contains("__ModuleLoader__", r.Detail);
    }

    [Fact]
    public void EvaluatePageProbe_GoodTrue_CleanPage_ReturnsGoodSymbol()
    {
        var r = Evaluate(ProbeJson(true, "DeepSeek Harness", ""));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.GoodSymbol, r.Kind);
    }

    [Fact]
    public void EvaluatePageProbe_BadSignatureInDomText_DowngradedToAbsentWithSuspectDetail()
    {
        // 2026-08 回归契约：DOM 文本坏签名降级为 Absent（需连续阈值轮数才判死），证据不丢
        var r = Evaluate(ProbeJson(false, "Error: bootstrap facade is missing — plugin crashed", ""));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.Absent, r.Kind);
        Assert.NotNull(r.Detail);
        Assert.StartsWith("dom-suspect[bootstrap facade is missing]=", r.Detail);
        Assert.Contains("plugin crashed", r.Detail);
    }

    [Fact]
    public void EvaluatePageProbe_BadSignatureInErrField_PreferredOverText()
    {
        var r = Evaluate(ProbeJson(false, "some dom text", "Uncaught TypeError: bootstrap facade is missing (__ModuleLoader__)"));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.BadSignature, r.Kind);
        // 错误原文优先作为证据：detail 携带命中签名 + 完整原文（S22"捕获原文"硬要求）
        Assert.StartsWith("err[bootstrap facade is missing]=", r.Detail);
        Assert.Contains("__ModuleLoader__", r.Detail);
    }

    [Fact]
    public void EvaluatePageProbe_CleanAbsent_ReturnsAbsent()
    {
        var r = Evaluate(ProbeJson(false, "DeepSeek Harness (loading)", ""));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.Absent, r.Kind);
    }

    // ---------------- 渲染豁免（[E2008 误报根治]：dsh 自带配置/欢迎界面不算 failed） ----------------

    [Fact]
    public void EvaluatePageProbe_RenderedRichText_NoBadSignature_ReturnsRendered()
    {
        // 未配置 API key 等场景：页面已渲染出 dsh 自己的界面（good=false，boot 链未完成），
        // 但 innerText 达到渲染阈值且无坏签名 → Rendered（视同健康，不再误判 E2008）。
        var r = Evaluate(ProbeJson(false,
            "欢迎使用 DeepSeek Harness —— 请先配置你的模型提供方 API Key 以开始使用，设置入口在右上角齿轮。", ""));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.Rendered, r.Kind);
        Assert.NotNull(r.Detail);
        Assert.StartsWith("rendered=", r.Detail);
        Assert.Contains("API Key", r.Detail); // 证据携带 text 摘录，便于排障
    }

    [Fact]
    public void EvaluatePageProbe_BelowRenderedThreshold_StillAbsent()
    {
        // 空白/纯加载页（innerText < RenderedMinTextChars=60）仍走缺席计票：慢启动/白屏保护不削弱。
        var r = Evaluate(ProbeJson(false, "Loading...", ""));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.Absent, r.Kind);
        Assert.DoesNotContain("rendered=", r.Detail ?? "");
    }

    [Fact]
    public void EvaluatePageProbe_DomBadSignature_BeatsRendered()
    {
        // 坏签名优先级高于渲染豁免：真崩溃错误 UI 即使文本很长也必须计票（dom-suspect），不能被豁免。
        var r = Evaluate(ProbeJson(false,
            "启动失败 plugin fatal — 这是一段足够长的错误说明文本，模拟渲染界面长度超过渲染豁免阈值的情况。", ""));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.Absent, r.Kind);
        Assert.StartsWith("dom-suspect[plugin fatal]=", r.Detail);
    }

    [Fact]
    public void EvaluatePageProbe_ErrBadSignature_BeatsRendered()
    {
        // err 原文坏签名仍一票否决（S22 硬要求），渲染豁免不得遮蔽。
        var r = Evaluate(ProbeJson(false, "这是一段足够长的已渲染界面文本内容，用于模拟欢迎界面超过渲染豁免阈值的情形……",
            "Uncaught: window.__ModuleLoader__ bootstrap facade is missing"));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.BadSignature, r.Kind);
        Assert.StartsWith("err[bootstrap facade is missing]=", r.Detail);
    }

    [Fact]
    public void EvaluatePageProbe_ProfileRenderedThreshold_Honored()
    {
        // 环境覆盖生效：阈值调高后，中等长度文本不再被判 Rendered。
        var p = ShellLogic.BootGuard.ResolveProfile("""{ "rendered_min_text_chars": 500 }""");
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            good = false,
            text = "欢迎界面：配置你的 API Key（约一百字以内的短文本，未达自定义阈值）",
            err = "",
        });
        var r = ShellLogic.BootGuard.EvaluatePageProbe(json, p);
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.Absent, r.Kind);
    }

    [Fact]
    public void ResolveProfile_RenderedMinTextChars_InvalidType_FallsBackToDefault()
    {
        var def = ShellLogic.BootGuard.ResolveProfile(null);
        Assert.Equal(60, def.RenderedMinTextChars);
        var bad = ShellLogic.BootGuard.ResolveProfile("""{ "rendered_min_text_chars": "not-a-number" }""");
        Assert.Equal(def.RenderedMinTextChars, bad.RenderedMinTextChars);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("undefined")]
    [InlineData("{garbage json")]
    public void EvaluatePageProbe_InvalidInputs_ReturnInvalid_NeverJudge(string? raw)
    {
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.Invalid, Evaluate(raw).Kind);
    }

    // ---------------- 日志层签名表 ----------------

    [Theory]
    [InlineData("[dsh] plugin load failed: Cannot find module 'dsh-notification'", "plugin load failed")]
    [InlineData("npm ERR! code ENOTFOUND registry.npmjs.org", "npm ERR")]
    [InlineData("Error: Cannot find module 'x'", "Cannot find module")]
    [InlineData("ERR_MODULE_NOT_FOUND", "ERR_MODULE_NOT_FOUND")]
    [InlineData("FATAL ERROR: Reached heap limit - Allocation failed", "FATAL ERROR")]
    public void MatchBootErrorSignature_KnownMarkers_ReturnMatchedMarker(string line, string expectedMarker)
    {
        var hit = ShellLogic.BootGuard.MatchBootErrorSignature(line, ShellLogic.BootGuard.ResolveProfile(null));
        Assert.NotNull(hit);
        Assert.Equal(expectedMarker, hit, ignoreCase: true);
    }

    [Theory]
    [InlineData("")]
    [InlineData("dsh web listening on http://127.0.0.1:3080")]
    [InlineData("[fake-dsh] Listening on port 3999 (mode=ok)")]
    public void MatchBootErrorSignature_CleanLines_ReturnNull(string line)
    {
        Assert.Null(ShellLogic.BootGuard.MatchBootErrorSignature(line, ShellLogic.BootGuard.ResolveProfile(null)));
    }

    [Fact]
    public void MatchBootErrorSignature_ProfileExtraSignatures_Honored()
    {
        var p = ShellLogic.BootGuard.ResolveProfile("""{ "log_error_signatures": ["FAKE-PLUGIN-DIED"] }""");
        Assert.Equal("FAKE-PLUGIN-DIED",
            ShellLogic.BootGuard.MatchBootErrorSignature("[plugin] FAKE-PLUGIN-DIED while booting", p));
    }

    [Fact]
    public void IsShellAuthoredLogEntry_ShellJsonWithErrorCode_ExcludedFromLogLayer()
    {
        // 壳自写条目（E#### 错误码契约）：即使内容命中坏签名也不参与日志层判定（S22 实测教训）
        var shellLine = """{"ts":"2025-01-01","level":"ERROR","pid":1,"code":"E1008","msg":"plugin crash detected via webview message: bootstrap facade is missing"}""";
        Assert.True(ShellLogic.BootGuard.IsShellAuthoredLogEntry(shellLine));

        // 服务原始输出 / 无错误码的行：参与判定
        Assert.False(ShellLogic.BootGuard.IsShellAuthoredLogEntry(
            """{"ts":"2026-08-21","level":"INFO","pid":2,"msg":"[fake-dsh] plugin load failed: Cannot find module 'x'"}"""));
        Assert.False(ShellLogic.BootGuard.IsShellAuthoredLogEntry("[dsh] plugin load failed: raw service output"));
        Assert.False(ShellLogic.BootGuard.IsShellAuthoredLogEntry("{not json"));
    }

    // ---------------- SplitCommandLine（DSH_SERVICE_CMD 去 cmd.exe 支撑） ----------------

    [Fact]
    public void SplitCommandLine_QuotedExeWithArgs()
    {
        var r = ShellLogic.ProcessManagement.SplitCommandLine(
            "\"C:\\Program Files\\node\\node.exe\" \"E:\\x\\fake-dsh.js\" --port 3999");
        Assert.NotNull(r);
        Assert.Equal("C:\\Program Files\\node\\node.exe", r.Value.Exe);
        Assert.Equal("\"E:\\x\\fake-dsh.js\" --port 3999", r.Value.Args);
    }

    [Fact]
    public void SplitCommandLine_UnquotedExe()
    {
        var r = ShellLogic.ProcessManagement.SplitCommandLine("C:\\tools\\node.exe app.js");
        Assert.Equal(("C:\\tools\\node.exe", "app.js"), r!.Value);
    }

    [Fact]
    public void SplitCommandLine_ExeOnly_And_RejectsGarbage()
    {
        Assert.Equal(("node.exe", ""), ShellLogic.ProcessManagement.SplitCommandLine("node.exe")!.Value);
        Assert.Null(ShellLogic.ProcessManagement.SplitCommandLine(null));
        Assert.Null(ShellLogic.ProcessManagement.SplitCommandLine("   "));
        Assert.Null(ShellLogic.ProcessManagement.SplitCommandLine("\"unterminated quote"));
    }

    // ---------------- 插件致命面板（2026-08-29 实机回归：dsh 0.1.2 客户端模块失败面板）----------------

    [Fact]
    public void EvaluatePageProbe_PluginFatalPanel_BeatsGoodSymbol_OneVoteWithDomEvidence()
    {
        // 实机形态：dsh 0.1.2 客户端引导遇到加载失败的插件模块 → 渲染失败面板替代整个应用 UI，
        // 但面板页面仍带 ModuleLoader 门面（good=true）——面板必须一票判死（优先于 good），
        // 证据带 dom[ 前缀 → VerdictIndicatesPluginInvolvement 命中 → 安全模式询问。
        var r = Evaluate(ProbeJson(true,
            "HARNESS\n\nFailed to load plugins\n\nfailed to import loader entry dacd7ad5 (dsh-notification): client-modules: require(\"@deepseek-ai/dsh-client-runtime/client\") missed the module table",
            ""));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.BadSignature, r.Kind);
        Assert.StartsWith("dom[failed to import loader entry]=", r.Detail);
        Assert.Contains("dsh-notification", r.Detail);
    }

    [Fact]
    public void EvaluatePageProbe_FatalPanel_CaseInsensitive()
    {
        var r = Evaluate(ProbeJson(true, "FAILED TO IMPORT LOADER ENTRY something broke", ""));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.BadSignature, r.Kind);
    }

    [Fact]
    public void EvaluatePageProbe_FatalPanelSignatures_OverridableViaProfile()
    {
        var profile = ShellLogic.BootGuard.ResolveProfile(
            "{\"fatal_panel_signatures\":[\"custom fatal panel copy\"]}");
        var hit = ShellLogic.BootGuard.EvaluatePageProbe(
            ProbeJson(true, "custom fatal panel copy: detail", ""), profile);
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.BadSignature, hit.Kind);
        // 默认签名被覆盖后不再命中
        var miss = ShellLogic.BootGuard.EvaluatePageProbe(
            ProbeJson(true, "failed to import loader entry x", ""), profile);
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.GoodSymbol, miss.Kind);
    }

    [Fact]
    public void EvaluatePageProbe_NormalRenderedPage_NotCaughtByFatalPanel()
    {
        // 误报防护：正常渲染页（无面板文案）不受致命面板通道影响
        var r = Evaluate(ProbeJson(true, "DeepSeek Harness 主界面正常渲染的长文本内容，用于模拟真实界面超过渲染豁免阈值的情形……", ""));
        Assert.Equal(ShellLogic.BootGuard.PageProbeKind.GoodSymbol, r.Kind);
    }
}
