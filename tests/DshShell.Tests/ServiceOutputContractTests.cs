using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// ServiceOutput.TryExtractTokenUrl 契约测试（2026-08-29 token 栅栏回归）。
///
/// 背景：dsh ≥0.1.2 的 web-startup 给根路径加了 token 信任栅栏（0.1.1 根路径免鉴权），
/// 启动横幅变为 `dsh web: http://127.0.0.1:3080/?token=...`。壳若不跟随该 URL，
/// WebView 停在 401 错误页（E2004）且页面探针永久挂死（实测：探针派发后无任何后续日志）。
///
/// 锁定契约：
/// 1. 宽进——容忍时间戳/[dsh] 渲染前缀、行尾附加文本；
/// 2. 严出——http(s) + 回环主机 + 端口匹配 + token 非空，四者缺一即拒；
/// 3. 0.1.1 形态（无 token）必须拒绝（维持既有 Target.Url 行为，不误导航）。
/// </summary>
public class ServiceOutputContractTests
{
    private const int Port = 3080;
    private const string RealBanner =
        "dsh web: http://127.0.0.1:3080/?token=De9b9mWQs9_I7WMIJY0b8_xo7CEG8JZvtTcpJSXnAjQ";

    [Fact]
    public void RealV012Banner_ExtractsExactAbsoluteUrl()
    {
        var ok = ShellLogic.ServiceOutput.TryExtractTokenUrl(RealBanner, Port, out var url);
        Assert.True(ok);
        Assert.Equal("http://127.0.0.1:3080/?token=De9b9mWQs9_I7WMIJY0b8_xo7CEG8JZvtTcpJSXnAjQ", url);
    }

    [Fact]
    public void RenderedPrefixAndTrailingText_StillExtracted()
    {
        var line = "[10:05:53.738] [dsh] " + RealBanner + " (pid 32260)";
        var ok = ShellLogic.ServiceOutput.TryExtractTokenUrl(line, Port, out var url);
        Assert.True(ok);
        Assert.StartsWith("http://127.0.0.1:3080/?token=De9b9mWQs9", url);
        Assert.DoesNotContain("(pid", url);
    }

    [Fact]
    public void ExtraQueryParams_Preserved()
    {
        var ok = ShellLogic.ServiceOutput.TryExtractTokenUrl(
            "dsh web: http://127.0.0.1:3080/?token=abc&foo=bar", Port, out var url);
        Assert.True(ok);
        Assert.Contains("token=abc", url);
        Assert.Contains("foo=bar", url);
    }

    [Fact]
    public void Https_AndLocalhost_Accepted()
    {
        var ok = ShellLogic.ServiceOutput.TryExtractTokenUrl(
            "dsh web: https://localhost:8443/?token=t1", 8443, out var url);
        Assert.True(ok);
        Assert.Equal("https://localhost:8443/?token=t1", url);
    }

    [Fact]
    public void V011BannerWithoutToken_Rejected()
    {
        // 0.1.1 形态：无 token 查询参数——拒绝（保持 Target.Url 原行为）
        var ok = ShellLogic.ServiceOutput.TryExtractTokenUrl(
            "dsh web: http://127.0.0.1:3080", Port, out _);
        Assert.False(ok);
    }

    [Fact]
    public void EmptyTokenValue_Rejected()
    {
        Assert.False(ShellLogic.ServiceOutput.TryExtractTokenUrl(
            "dsh web: http://127.0.0.1:3080/?token=", Port, out _));
    }

    [Fact]
    public void PortMismatch_Rejected()
    {
        // 防陈旧行/外部服务行劫持导航目标
        Assert.False(ShellLogic.ServiceOutput.TryExtractTokenUrl(
            "dsh web: http://127.0.0.1:3999/?token=abc", Port, out _));
    }

    [Fact]
    public void NonLoopbackHost_Rejected()
    {
        // 防恶意插件伪造 stdout 行把壳 WebView 引向外站
        Assert.False(ShellLogic.ServiceOutput.TryExtractTokenUrl(
            "dsh web: http://evil.example.com:3080/?token=abc", Port, out _));
        Assert.False(ShellLogic.ServiceOutput.TryExtractTokenUrl(
            "dsh web: http://192.168.1.10:3080/?token=abc", Port, out _));
    }

    [Fact]
    public void NonHttpScheme_Rejected()
    {
        Assert.False(ShellLogic.ServiceOutput.TryExtractTokenUrl(
            "dsh web: ftp://127.0.0.1:3080/?token=abc", Port, out _));
        Assert.False(ShellLogic.ServiceOutput.TryExtractTokenUrl(
            "dsh web: file:///C:/windows/notepad.exe", Port, out _));
    }

    [Fact]
    public void GarbageAndNull_Rejected()
    {
        Assert.False(ShellLogic.ServiceOutput.TryExtractTokenUrl(null, Port, out _));
        Assert.False(ShellLogic.ServiceOutput.TryExtractTokenUrl("", Port, out _));
        Assert.False(ShellLogic.ServiceOutput.TryExtractTokenUrl("ready on port", Port, out _));
        Assert.False(ShellLogic.ServiceOutput.TryExtractTokenUrl(RealBanner, 0, out _)); // 非法端口守卫
    }
}
