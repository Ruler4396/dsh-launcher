using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 壳自绘等待态页面的契约（<see cref="ShellLogic.WaitingPage"/>）。
///
/// 【为什么需要它】真机 2026-09-20：点"退出安全模式"到服务重新可用之间 20+ 秒，主窗一直挂着
/// 已经断连的旧页面，用户两次读成"点了没反应"（原话"没反应，窗口消失了，没有重启启动器"）。
/// 页面文字经 WebView2 <c>NavigateToString</c> 直接进入 HTML 上下文，所以**转义是这条路径唯一的
/// 安全边界**——它必须可测，不能靠"调用方传的都是常量"这种口头保证。
/// </summary>
public class WaitingPageContractTests
{
    [Theory]
    [InlineData("<script>alert(1)</script>", "<script")]
    [InlineData("</title><h1>注入</h1>", "</title><h1")]
    // 断言的是"注入的文字不能变成元素"：转义后 onerror= 作为纯文本留在页面里是无害的，
    // 真正的判据是 `<img` 这个标签起始不再存在（第一版我错把 "onerror=" 当判据，测的是转义过度）。
    [InlineData("<img src=x onerror=alert(1)>", "<img")]
    public void Html_NeverLetsCallerTextEscapeItsContext(string injected, string mustNotAppear)
    {
        var html = ShellLogic.WaitingPage.Html(injected, "正文");
        Assert.DoesNotContain(mustNotAppear, html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(ShellLogic.WaitingPage.Escape(injected), html);
    }

    [Fact]
    public void Html_EscapesAmpersandQuotesAndApostrophes()
    {
        var html = ShellLogic.WaitingPage.Html("A&B \"x\" 'y'", "d&d");
        Assert.Contains("A&amp;B &quot;x&quot; &#39;y&#39;", html);
        Assert.Contains("d&amp;d", html);
    }

    [Fact]
    public void Html_CarriesBothStrings_VisibleToTheUser()
    {
        var html = ShellLogic.WaitingPage.Html("正在退出安全模式…", "正在以正常配置重新拉起");
        Assert.Contains("<h1>正在退出安全模式…</h1>", html);
        Assert.Contains("<p>正在以正常配置重新拉起</p>", html);
        Assert.Contains("<title>正在退出安全模式…</title>", html);
    }

    [Fact]
    public void Html_IsSelfContained_NoRemoteResourceNoScript()
    {
        // 等待页出现在服务已经停掉的时刻：任何外部资源都拉不到，脚本更不该有。
        var html = ShellLogic.WaitingPage.Html("h", "d");
        Assert.DoesNotContain("http://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("src=", html, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("<!DOCTYPE html>", html);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Escape_EmptyOrNull_IsEmptyString(string? input)
        => Assert.Equal("", ShellLogic.WaitingPage.Escape(input));

    [Fact]
    public void Escape_LeavesPlainTextAlone()
        => Assert.Equal("正在进入安全模式…", ShellLogic.WaitingPage.Escape("正在进入安全模式…"));
}
