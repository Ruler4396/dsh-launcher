using DshWeb;
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

    /// <summary>构造被测对象：tcp/http 探针全 Fake（不经真实网络）。
    /// 退出探针默认注入 -1（未观察到退出），避免静态追踪器被其他真实进程测试污染
    /// （issue #26 快速失败观测的确定性注入）。</summary>
    private static ServiceManager Create(bool tcpOpen = false, bool httpReady = false, int exitCode = -1)
        => new(
            tcpProbe: (_, _) => tcpOpen,
            httpProbe: (_, _) => httpReady,
            pollDelay: TimeSpan.FromMilliseconds(1));

    /// <summary>就绪轮询统一入口：注入延迟虚时钟 + 确定性退出探针。</summary>
    private static string Poll(ServiceManager svc, string logPath, Action<TimeSpan> delay, int exitCode = -1)
        => svc.PollReadiness(CancellationToken.None, 3080, "http://127.0.0.1:3080", logPath,
            e2eMode: true, delay: delay, serviceExitCodeProbe: () => exitCode);

    [Fact]
    public void ReadyShortCircuits_BeforeAnyLogJudgment()
    {
        File.WriteAllText(_logPath, "npm ERR! poisoned stale line\n"); // 历史污染在场
        var svc = Create(tcpOpen: true, httpReady: true);
        // 就绪短路优先于一切判定（含 issue #26 退出观测）：服务健康时进程退出观测不适用
        var result = Poll(svc, _logPath, _ => { }, exitCode: 7);
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
        var result = Poll(svc, _logPath, _ => { });
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
        var result = Poll(svc, _logPath, _ => { });
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
        var result = Poll(svc, _logPath, _ =>
        {
            if (!appended)
            {
                appended = true;
                File.AppendAllText(_logPath, "[12:00:05.000] [dsh] npm ERR! code ENOSPC\n");
            }
        });
        Assert.Equal("logerror", result);
    }

    // ---------------- issue #26：就绪前服务进程已退出 → 快速失败（service-exited） ----------------

    [Fact]
    public void ServiceProcessExitedBeforeReady_FailsFast_ReturnsServiceExited()
    {
        // 复现路径：服务已启动但 HTTP 迟迟不就绪；进程退出观测命中（code=7，退出输出不含
        // 启动错误标志，logerror 不会触发）→ 必须立即返回 service-exited，而不是盲等 20 轮预算。
        var svc = Create(tcpOpen: false, httpReady: false);
        var result = Poll(svc, _logPath, _ => { }, exitCode: 7);
        Assert.Equal(ShellLogic.ServiceReadiness.ServiceExitedVerdict, result);
    }

    [Fact]
    public void ServiceProcessExited_WithExitCodeZero_AlsoFailsFast()
    {
        // 长驻服务就绪前退出（即使 code=0）也判启动失败：dsh web 不会在就绪前正常退出。
        var svc = Create(tcpOpen: false, httpReady: false);
        var result = Poll(svc, _logPath, _ => { }, exitCode: 0);
        Assert.Equal(ShellLogic.ServiceReadiness.ServiceExitedVerdict, result);
    }

    [Fact]
    public void ServiceProcessStillRunning_NoExitObserved_TimesOutAsBefore()
    {
        // 阴性对照：进程仍在启动（退出观测 -1）→ 维持原 timeout 语义，不误判快速失败。
        var svc = Create(tcpOpen: false, httpReady: false);
        var result = Poll(svc, _logPath, _ => { });
        Assert.Equal("timeout", result);
    }

    [Fact]
    public void ServiceExitObserved_ButLogErrorGraceWins_StillFailsFastAsExit()
    {
        // 语义优先级：退出观测与 logerror 宽限不冲突——进程已退出是更强证据（判定顺序上
        // 退出检查在宽限检查之前），二者任一命中都快速失败；此处锁定退出观测先触发。
        File.WriteAllText(_logPath, "[12:00:00.000] [dsh] web starting...\n");
        var svc = Create(tcpOpen: false, httpReady: false);
        var result = Poll(svc, _logPath, _ => { }, exitCode: 3);
        Assert.Equal(ShellLogic.ServiceReadiness.ServiceExitedVerdict, result);
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
            e2eMode: true, delay: _ => { }, serviceExitCodeProbe: () => -1);
        Assert.Equal("canceled", result);
    }
}
