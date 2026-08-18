namespace DshWeb.Managers;

/// <summary>
/// dsh 服务管理：端口/HTTP 就绪探测与启动决策。
/// 探针以委托注入（默认 ShellLogic.PortOpen / IsHttpReady），使超时/就绪逻辑可 Headless 单测。
/// 服务进程拉起/PID/僵尸清理等 UI 耦合部分留在 Main，后续按 DSH_USE_NEW_LIFECYCLE 切换迁移。
/// </summary>
public sealed class ServiceManager : IServiceManager
{
    private readonly Func<string, int, bool> _tcpProbe;
    private readonly Func<string, System.Net.Http.HttpClient, bool> _httpProbe;
    private readonly TimeSpan _pollDelay;

    public ServiceManager(
        Func<string, int, bool>? tcpProbe = null,
        Func<string, System.Net.Http.HttpClient, bool>? httpProbe = null,
        TimeSpan? pollDelay = null)
    {
        _tcpProbe = tcpProbe ?? ShellLogic.PortOpen;
        _httpProbe = httpProbe ?? ShellLogic.IsHttpReady;
        _pollDelay = pollDelay ?? TimeSpan.FromSeconds(1);
    }

    public bool NeedsStart(int port) => !_tcpProbe("127.0.0.1", port);

    /// <summary>按 pollDelay 轮询端口+HTTP，超时返回 false。每轮 HTTP 探测带 3s 超时（契约见 IsHttpReady）。</summary>
    public async Task<bool> WaitReadyAsync(int port, TimeSpan timeout, CancellationToken ct = default)
    {
        var url = $"http://127.0.0.1:{port}";
        var deadline = DateTime.UtcNow + timeout;
        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        while (!ct.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            if (_tcpProbe("127.0.0.1", port) && _httpProbe(url, http))
                return true; // TCP + HTTP 都已就绪（对应 E2002 超时的成功分支）
            await Task.Delay(_pollDelay, ct).ConfigureAwait(false);
        }
        return false; // 超时（组合根映射 ErrorCodes.E2002）
    }
}
