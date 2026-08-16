using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 更新检查链路单测（用户高频路径：每次启动异步检查 GitHub/npm 版本）。
/// 用 FakeHttpMessageHandler 注入响应，不碰真实网络；验证 JSON 解析、安全更新
/// 判定、失败静默（网络失败/限流不得打扰用户）与版本比较边界。
/// </summary>
public class UpdateCheckerTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public int CallCount;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            return Task.FromResult(_respond(request));
        }
    }

    private static HttpClient Client(FakeHandler h) => new(h);

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    // ---------- launcher 版本拉取（GitHub Releases /latest） ----------

    [Fact]
    public void FetchLatestLauncherVersion_ValidTag_StripsVPrefix()
    {
        var http = Client(new FakeHandler(_ => Json("""{"tag_name":"v0.3.1"}""")));
        Assert.Equal("0.3.1", UpdateChecker.FetchLatestLauncherVersionAsync(http).Result);
    }

    [Fact]
    public void FetchLatestLauncherVersion_TagWithoutV_KeptAsIs()
    {
        var http = Client(new FakeHandler(_ => Json("""{"tag_name":"0.3.1"}""")));
        Assert.Equal("0.3.1", UpdateChecker.FetchLatestLauncherVersionAsync(http).Result);
    }

    [Fact]
    public void FetchLatestLauncherVersion_MissingTag_ReturnsNull()
    {
        var http = Client(new FakeHandler(_ => Json("""{"name":"some release"}""")));
        Assert.Null(UpdateChecker.FetchLatestLauncherVersionAsync(http).Result);
    }

    [Fact]
    public void FetchLatestLauncherVersion_HttpError_ReturnsNull()
    {
        var http = Client(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Assert.Null(UpdateChecker.FetchLatestLauncherVersionAsync(http).Result);
    }

    [Fact]
    public void FetchLatestLauncherVersion_InvalidJson_ReturnsNull()
    {
        var http = Client(new FakeHandler(_ => Json("{broken")));
        Assert.Null(UpdateChecker.FetchLatestLauncherVersionAsync(http).Result);
    }

    [Fact]
    public void FetchLatestLauncherVersion_NetworkException_ReturnsNull()
    {
        var http = Client(new FakeHandler(_ => throw new HttpRequestException("connection refused")));
        Assert.Null(UpdateChecker.FetchLatestLauncherVersionAsync(http).Result);
    }

    // ---------- 安全/重要更新判定（Release body 含 SECURITY 或 tag 含 -sec） ----------

    [Fact]
    public void FetchLatestLauncherRelease_BodySaysSecurity_Flagged()
    {
        var http = Client(new FakeHandler(_ =>
            Json("""{"tag_name":"v0.3.1","body":"Fixes a SECURITY vulnerability"}""")));
        var r = UpdateChecker.FetchLatestLauncherReleaseAsync(http).Result;
        Assert.NotNull(r);
        Assert.True(r!.IsSecurity);
        Assert.Equal("0.3.1", r.Version);
    }

    [Fact]
    public void FetchLatestLauncherRelease_TagHasSecSuffix_Flagged()
    {
        var http = Client(new FakeHandler(_ => Json("""{"tag_name":"v0.3.1-sec","body":"routine"}""")));
        Assert.True(UpdateChecker.FetchLatestLauncherReleaseAsync(http).Result!.IsSecurity);
    }

    [Fact]
    public void FetchLatestLauncherRelease_OrdinaryRelease_NotFlagged()
    {
        var http = Client(new FakeHandler(_ =>
            Json("""{"tag_name":"v0.3.1","body":"New features"}""")));
        Assert.False(UpdateChecker.FetchLatestLauncherReleaseAsync(http).Result!.IsSecurity);
    }

    [Fact]
    public void FetchLatestLauncherRelease_NoBody_NotFlagged()
    {
        var http = Client(new FakeHandler(_ => Json("""{"tag_name":"v0.3.1"}""")));
        Assert.False(UpdateChecker.FetchLatestLauncherReleaseAsync(http).Result!.IsSecurity);
    }

    [Fact]
    public void FetchLatestLauncherRelease_MissingVersion_ReturnsNull()
    {
        var http = Client(new FakeHandler(_ => Json("""{"body":"SECURITY"}""")));
        Assert.Null(UpdateChecker.FetchLatestLauncherReleaseAsync(http).Result);
    }

    // ---------- dsh 版本拉取（npm registry /latest） ----------

    [Fact]
    public void FetchLatestDshVersion_ValidResponse_ReturnsVersion()
    {
        var http = Client(new FakeHandler(_ => Json("""{"version":"1.2.3"}""")));
        Assert.Equal("1.2.3", UpdateChecker.FetchLatestDshVersionAsync(http).Result);
    }

    [Fact]
    public void FetchLatestDshVersion_MissingVersion_ReturnsNull()
    {
        var http = Client(new FakeHandler(_ => Json("""{"name":"@deepseek-ai/dsh"}""")));
        Assert.Null(UpdateChecker.FetchLatestDshVersionAsync(http).Result);
    }

    [Fact]
    public void FetchLatestDshVersion_NotFound_ReturnsNull()
    {
        var http = Client(new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));
        Assert.Null(UpdateChecker.FetchLatestDshVersionAsync(http).Result);
    }

    [Fact]
    public void FetchLatestDshVersion_ScopedPackage_EscapedInUrl()
    {
        string? requested = null;
        var http = Client(new FakeHandler(req =>
        {
            requested = req.RequestUri!.ToString();
            return Json("""{"version":"1.2.3"}""");
        }));
        UpdateChecker.FetchLatestDshVersionAsync(http).GetAwaiter().GetResult();
        // Uri.EscapeDataString 会把 @ 也转义成 %40，npm scoped 包名整段转义
        Assert.Contains("%40deepseek-ai%2Fdsh", requested!);
    }

    // ---------- 版本比较（决定是否提示更新，误报会打扰用户） ----------

    [Theory]
    [InlineData("0.3.1", "0.3.0", 1)]
    [InlineData("0.3.0", "0.3.1", -1)]
    [InlineData("0.3.1", "0.3.1", 0)]
    [InlineData("0.3.10", "0.3.9", 1)]      // 语义化：10 > 9，非字符串序
    [InlineData("1.0.0", "0.9.9", 1)]
    [InlineData("0.3.1", null, 1)]          // 远端缺失 → 视为 0.0.0 → 有更新？不：本地为 0.3.1 > 0
    [InlineData(null, "0.3.1", -1)]
    [InlineData(null, null, 0)]
    [InlineData("abc", "0.3.1", -1)]        // 非法 → 0.0.0，不产生"有新版"误报
    [InlineData("0.3.1", "abc", 1)]
    public void CompareVersions_ReturnsExpected(string? a, string? b, int expected)
    {
        Assert.Equal(expected, Math.Sign(UpdateChecker.CompareVersions(a, b)));
    }

    [Fact]
    public void CurrentLauncherVersion_FromAssembly_NonEmpty()
    {
        Assert.False(string.IsNullOrWhiteSpace(UpdateChecker.CurrentLauncherVersion));
    }

    [Fact]
    public void ResolveLocalDshVersion_EnvVarPreferred()
    {
        var saved = Environment.GetEnvironmentVariable("DSH_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("DSH_VERSION", "9.9.9");
            Assert.Equal("9.9.9", UpdateChecker.ResolveLocalDshVersion());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DSH_VERSION", saved);
        }
    }
}
