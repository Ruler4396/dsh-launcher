using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.Managers;

/// <summary>
/// F2 回归门禁 + F26 时间注入验证：PollReadiness 的错误标志判定必须**增量**（只看本轮新增），
/// 壳自写行必须被过滤；等待/间隔/宽限经注入 delay 全虚拟时钟驱动（测试毫秒级完成）。
/// 背景：旧实现每 5s 整文件扫描统一日志——dsh 运行期的良性网络告警（ECONNRESET 等）一旦
/// 落入日志便永久驻留，任何 >15s 的慢启动都会被误判 logerror 并被 HandleStartupFailure
/// 强杀刚拉起的（可能健康的）服务。
/// </summary>
public sealed class PollReadinessTests : IDisposable
{
    private readonly string _dir;
    private readonly string _logPath;

    public PollReadinessTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pollreadiness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _logPath = Path.Combine(_dir, "dsh.log");
        // 防止 DiscoverCurrentRuntime 在本机探测版本时 spawn node --version（测试提速与确定性）
        Environment.SetEnvironmentVariable("DSH_VERSION", "9.9.9");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DSH_VERSION", null);
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败忽略 */ }
    }

    /// <summary>构造被测对象：tcp/http 探针全 Fake（不经真实网络）。</summary>
    private static ServiceManager Create(bool tcpOpen = false, bool httpReady = false)
        => new(
            tcpProbe: (_, _) => tcpOpen,
            httpProbe: (_, _) => httpReady,
            pollDelay: TimeSpan.FromMilliseconds(1));

    [Fact]
    public void ReadyShortCircuits_BeforeAnyLogJudgment()
    {
        File.WriteAllText(_logPath, "npm ERR! poisoned stale line\n"); // 历史污染在场
        var svc = Create(tcpOpen: true, httpReady: true);
        var result = svc.PollReadiness(CancellationToken.None, 3080, "http://127.0.0.1:3080", _logPath,
            e2eMode: true, delay: _ => { });
        Assert.Equal("ready", result);
    }

    [Fact]
    public void StaleErrorMarker_InPreExistingLog_DoesNotCauseLogerror_F2()
    {
        // F2 核心回归：入口前已存在的历史错误标志（上一会话的 npm 失败/运行期告警）
        // 不参与判定——服务迟迟未就绪时最终应是 timeout 而非 logerror。
        File.WriteAllText(_logPath, "[12:00:00.000] [dsh] [warn] upstream retry: ECONNRESET\n" +
                                    "[12:00:01.000] [dsh] npm ERR! code EACCES\n");
        var svc = Create(tcpOpen: false, httpReady: false);
        var result = svc.PollReadiness(CancellationToken.None, 3080, "http://127.0.0.1:3080", _logPath,
            e2eMode: true, delay: _ => { });
        Assert.Equal("timeout", result);
    }

    [Fact]
    public void ShellAuthoredLine_WithEmbeddedNpmErr_DoesNotCauseLogerror_F2()
    {
        // 壳的 E1012 文案内嵌 "npm ERR"——壳行过滤后不得误判（F2 的另一污染源）。
        File.WriteAllText(_logPath,
            "{\"ts\":\"2026-08-28T10:00:00Z\",\"level\":\"Error\",\"code\":\"E1012\"," +
            "\"message\":\"npm 全局安装失败。\\n最后错误：\\nnpm ERR! network request failed\"}\n");
        var svc = Create(tcpOpen: false, httpReady: false);
        var result = svc.PollReadiness(CancellationToken.None, 3080, "http://127.0.0.1:3080", _logPath,
            e2eMode: true, delay: _ => { });
        Assert.Equal("timeout", result);
    }

    [Fact]
    public void NewErrorMarker_AppendedDuringWait_CausesLogerror_AfterGrace()
    {
        // 反向：本轮新增的真实错误标志仍须触发 logerror（判定能力未被收窄掉）。
        // 注入的 delay 回调在第 5 次休眠时向日志追加服务错误行；e2e 宽限 2s（虚拟时钟）内持续命中。
        File.WriteAllText(_logPath, "[12:00:00.000] [dsh] web starting...\n");
        var appended = false;
        var svc = Create(tcpOpen: false, httpReady: false);
        var result = svc.PollReadiness(CancellationToken.None, 3080, "http://127.0.0.1:3080", _logPath,
            e2eMode: true,
            delay: _ =>
            {
                if (!appended)
                {
                    appended = true;
                    File.AppendAllText(_logPath, "[12:00:05.000] [dsh] npm ERR! code ENOSPC\n");
                }
            });
        Assert.Equal("logerror", result);
    }

    [Fact]
    public void IncrementalRead_OnlyReturnsBytesAfterOffset()
    {
        // 增量读取原语契约：偏移后的新增内容可见；无新增返回 null；截断回退从头读。
        File.WriteAllText(_logPath, "first");
        var (t1, off1) = ServiceManager.ReadLogIncrementShared(_logPath, 0);
        Assert.Equal("first", t1);
        Assert.Equal(5, off1);
        var (t2, off2) = ServiceManager.ReadLogIncrementShared(_logPath, off1);
        Assert.Null(t2);
        Assert.Equal(off1, off2);
        File.AppendAllText(_logPath, "-second");
        var (t3, off3) = ServiceManager.ReadLogIncrementShared(_logPath, off1);
        Assert.Equal("-second", t3);
        Assert.Equal(12, off3);
        // 截断（轮转）→ 从头读
        File.WriteAllText(_logPath, "x");
        var (t4, _) = ServiceManager.ReadLogIncrementShared(_logPath, off3);
        Assert.Equal("x", t4);
    }

    [Fact]
    public void Cancelled_ReturnsCanceled()
    {
        var svc = Create(tcpOpen: false, httpReady: false);
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var result = svc.PollReadiness(cts.Token, 3080, "http://127.0.0.1:3080", _logPath,
            e2eMode: true, delay: _ => { });
        Assert.Equal("canceled", result);
    }
}
