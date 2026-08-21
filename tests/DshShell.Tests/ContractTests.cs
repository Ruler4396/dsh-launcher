using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 上游契约测试（P1-6）：锁定 Launcher 与 dsh 之间的判定契约，防上游行为变更无声破坏。
/// - C3 ready 判定：HTTP 有应答即就绪（任何响应码）；网络异常/超时/拒绝连接 → 未就绪。
/// - C9 PID 身份：只杀 node 进程（负向分支：非 node / 不存在的 PID 一律拒绝）。
/// - C10 Node 可用门槛：主版本 ≥18（纯函数解析，stub 可测，不 spawn 真实 node）。
/// 全部本地注入（FakeHttpMessageHandler / 真实 TcpListener 环回端口），秒级、确定性、不进网络。
/// </summary>
public class ContractTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;
        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_respond(request));
    }

    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> respond) => new(new FakeHandler(respond));

    // ---------- C3 ready 判定：任何 HTTP 响应 = 就绪 ----------

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.NotFound)]      // 404 也是"有服务在应答"（dsh 前端存在）
    [InlineData(HttpStatusCode.InternalServerError)] // 5xx 同理：服务进程活着
    public void IsHttpReady_AnyHttpResponse_Ready(HttpStatusCode status)
    {
        var http = Client(_ => new HttpResponseMessage(status));
        Assert.True(ShellLogic.ServiceReadiness.IsHttpReady("http://127.0.0.1:3080/", http));
    }

    [Fact]
    public void IsHttpReady_ConnectionRefused_NotReady()
    {
        var http = Client(_ => throw new HttpRequestException("connection refused"));
        Assert.False(ShellLogic.ServiceReadiness.IsHttpReady("http://127.0.0.1:1/", http));
    }

    [Fact]
    public void IsHttpReady_Timeout_NotReady()
    {
        var http = Client(_ => throw new TaskCanceledException("timeout"));
        Assert.False(ShellLogic.ServiceReadiness.IsHttpReady("http://127.0.0.1:3080/", http));
    }

    // ---------- 服务就绪轮询预算（网络下载兜底放宽超时，首次 npx 冷下载不被误判超时） ----------

    [Theory]
    [InlineData(false, 180)]
    [InlineData(true, 360)]
    public void GetPollBudgetSeconds_NetworkFallback_ExtendsBudget(bool networkDownloadFallback, int expected)
    {
        // 非网络兜底（SelfContained/全局安装）保持 180s；npx 网络下载路径放宽到 360s。
        Assert.Equal(expected, ShellLogic.ServiceReadiness.GetPollBudgetSeconds(networkDownloadFallback));
    }

    [Fact]
    public void GetPollBudgetSeconds_NetworkFallback_IsGenerousWindow()
    {
        // 网络兜底预算必须显著大于本地直启，确保慢但能成功的首次下载不被"刚超 3 分钟"误杀。
        Assert.True(
            ShellLogic.ServiceReadiness.GetPollBudgetSeconds(true)
            > ShellLogic.ServiceReadiness.GetPollBudgetSeconds(false));
    }

    // ---------- C3 端口探测：TCP connect 语义 ----------

    [Fact]
    public void PortOpen_ListeningSocket_True_ThenClosed_False()
    {
        // 真实环回 socket（127.0.0.1:0 取空闲端口），确定性、无 Sleep。
        // CI runner（Windows Server）上 TcpListener.Start() 后 connect 偶发瞬时不就绪
        //（PortOpen 300ms 硬超时内 connect 超时）→ 断言前置短重试（≤5 次/200ms）吸收时序抖动。
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var deadline = DateTime.UtcNow.AddSeconds(1);
        var opened = false;
        while (DateTime.UtcNow < deadline)
        {
            if (ShellLogic.ServiceReadiness.PortOpen("127.0.0.1", port)) { opened = true; break; }
            Thread.Sleep(200);
        }
        Assert.True(opened, "监听 socket 应能被 PortOpen 探测到（含 CI 时序抖动容错）");
        listener.Stop();
        Assert.False(ShellLogic.ServiceReadiness.PortOpen("127.0.0.1", port));
    }

    [Fact]
    public void PortOpen_UnusedPort_False()
    {
        // 取一个刚释放的端口（listen→stop）→ 无监听 → connect 拒绝
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.False(ShellLogic.ServiceReadiness.PortOpen("127.0.0.1", port));
    }

    // ---------- C9 PID 身份：负向分支（非 node / 不存在 → 拒绝） ----------

    [Fact]
    public void IsLikelyDshService_CurrentTestProcess_NotNode()
    {
        // testhost 进程名不是 node → 拒绝（防误杀无关进程的关键负向分支）
        Assert.False(ShellLogic.ProcessManagement.IsLikelyDshService(Environment.ProcessId));
    }

    [Fact]
    public void IsLikelyDshService_NonexistentPid_False()
    {
        Assert.False(ShellLogic.ProcessManagement.IsLikelyDshService(999_999_999));
    }

    // ---------- C10 Node 可用门槛：主版本 ≥18 ----------

    [Theory]
    [InlineData("v24.15.0", true)]
    [InlineData("v18.0.0", true)]
    [InlineData("v20", true)]
    [InlineData(" v18.2.1 ", true)]           // 首尾空白容忍
    [InlineData("v17.9.0", false)]            // 低于门槛
    [InlineData("v10.24.1", false)]
    [InlineData("", false)]
    [InlineData("garbage", false)]            // 解析失败 → 不可用
    [InlineData("v", false)]
    [InlineData(null, false)]
    public void IsUsableNodeVersion_Threshold(string? versionOutput, bool expected)
    {
        Assert.Equal(expected, RuntimeResolver.IsUsableNodeVersion(versionOutput));
    }

    // ---------- 系统通知（Toast）纯策略：XML 构造与 AUMID ----------

    [Fact]
    public void BuildToastXml_ContainsTitleAndBody_InTemplateStructure()
    {
        var xml = ShellLogic.ToastPolicy.BuildToastXml("dsh 有新版本", "检测到 0.1.1-rc.1");
        Assert.Contains("template=\"ToastText02\"", xml);
        Assert.Contains("<text id=\"1\">dsh 有新版本</text>", xml);
        Assert.Contains("<text id=\"2\">检测到 0.1.1-rc.1</text>", xml);
        Assert.StartsWith("<toast>", xml);
        Assert.EndsWith("</toast>", xml);
    }

    [Theory]
    [InlineData("<script>", "&lt;script&gt;")]          // 防注入：外部输入不得破坏 XML 结构
    [InlineData("a&b", "a&amp;b")]
    [InlineData("x\"y", "x&quot;y")]
    [InlineData("p'q", "p&apos;q")]
    public void BuildToastXml_EscapesExternalInput(string raw, string escaped)
    {
        var xml = ShellLogic.ToastPolicy.BuildToastXml(raw, "body");
        Assert.Contains($"<text id=\"1\">{escaped}</text>", xml);
    }

    [Fact]
    public void BuildToastXml_NullInputs_ProduceEmptyTextNodes()
    {
        var xml = ShellLogic.ToastPolicy.BuildToastXml(null!, null!);
        Assert.Contains("<text id=\"1\"></text>", xml);
        Assert.Contains("<text id=\"2\"></text>", xml);
    }

    [Fact]
    public void ToastAumid_IsStableNonEmpty()
    {
        // AUMID 是系统聚合通知来源的标识，中途变更会让用户通知设置失效 → 锁定为常量
        Assert.False(string.IsNullOrWhiteSpace(ShellLogic.ToastPolicy.ToastAumid));
        Assert.Equal("dsh-launcher", ShellLogic.ToastPolicy.ToastAumid);
    }
}
