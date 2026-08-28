using System.ComponentModel;

namespace DshWeb.Managers;

/// <summary>
/// 服务进程生命周期操作（ADR-024 双轨制收敛：自 Program.cs 整体迁出）——
/// PID 账本（记录/接管/清扫）、服务停止链、就绪 HTTP 快探。
/// 全部经 <see cref="ShellLogic.ProcessManagement"/> 的防误杀原语执行
/// （身份校验 + 端口归属双重校验），本类零 UI 依赖。
/// </summary>
internal static class ServiceLifecycleOps
{
    /// <summary>壳托管服务的 PID 记录文件（按端口隔离）：崩溃/异常退出后残留的服务可被下次启动接管管理。</summary>
    internal static string PidFilePath(string dataDir, int port) => Path.Combine(dataDir, $"service-pid-{port}.txt");

    /// <summary>记录本次壳拉起的服务 PID（服务就绪后调用），供下次启动接管残留服务。
    /// [F22] 原子写（.tmp + File.Move）——半截 pid 文件会让接管判定误入"损坏清账"分支；
    /// [F24] 失败不再静默：接管链路依赖此账本，失败必须留痕。</summary>
    internal static void RecordServicePid(string dataDir, int port)
    {
        try
        {
            var pid = ShellLogic.ProcessManagement.GetProcessIdByPort(port);
            if (pid > 0)
                ShellLogic.FileSystemPolicy.AtomicWrite(PidFilePath(dataDir, port), pid.ToString());
        }
        catch (Exception ex)
        {
            // [F24] 记录失败不影响启动，但下次启动将无法接管残留服务（服务可能永久无主）——必须留痕。
            Logger.Warn($"record service pid failed (next start cannot adopt orphan service): {ex.Message}",
                ErrorCodes.E2005, new { port });
        }
    }

    /// <summary>
    /// 端口已开但本实例没拉起服务时调用：若监听进程正是壳上次拉起的残留服务
    /// （PID 记录在账本），则校验健康后接管管理，避免崩溃/异常退出后服务永久残留。
    /// v0.3.0 健康校验：HTTP 就绪才算可接管；坏状态/旧版本进程不得带病运行。
    /// [F19/F8g] 无账本兜底：壳可能崩溃于 RecordServicePid 之前（就绪前窗口）——端口
    /// 占用者确为 node 且 HTTP 健康 → 同样认领并补写账本（此后 F4 账本判定把
    /// 它视为"我们自己家"的服务，关停链路与 FollowWindow 语义随之生效）。
    /// 返回被接管的 PID（>0），否则 0。
    /// </summary>
    internal static int TryAdoptOrphanService(string dataDir, int port, string url)
    {
        try
        {
            var pidFile = PidFilePath(dataDir, port);
            var ledgerPid = 0;
            var hasLedger = File.Exists(pidFile)
                            && int.TryParse(File.ReadAllText(pidFile).Trim(), out ledgerPid)
                            && ledgerPid > 0;
            if (!hasLedger)
            {
                var owner = ShellLogic.ProcessManagement.GetProcessIdByPort(port);
                if (owner > 0
                    && ShellLogic.ProcessManagement.IsLikelyDshService(owner)
                    && IsReady(port, url))
                {
                    Logger.Info($"adopted healthy service pid={owner} without ledger (pre-record crash window)");
                    try { ShellLogic.FileSystemPolicy.AtomicWrite(pidFile, owner.ToString()); }
                    catch (Exception ex) { Logger.Warn($"ledger backfill failed for pid={owner}: {ex.Message}"); }
                    return owner;
                }
                return 0;
            }

            var pid = ledgerPid;
            if (ShellLogic.ProcessManagement.GetProcessIdByPort(port) == pid)
            {
                if (IsReady(port, url))
                {
                    Logger.Info($"adopted orphan service pid={pid}");
                    return pid;
                }
                Logger.Warn($"orphan service pid={pid} unhealthy (no HTTP); killing", ErrorCodes.E2005,
                    new { port });
                if (KillProcess(port, pid)) ClearPidFile(dataDir, port); // P2-10：杀不干净则保留 pid 文件
            }
        }
        catch
        {
            // 接管失败不影响启动
        }
        return 0;
    }

    /// <summary>端口未开时的遗留清扫（拉起服务前调用）：上次崩溃记录过、但已不在
    /// 监听的进程 → 清理（只动我们记录的 PID），确保端口不被占用、不留僵尸进程。</summary>
    internal static void SweepStaleServicePid(string dataDir, int port)
    {
        try
        {
            var pidFile = PidFilePath(dataDir, port);
            if (!File.Exists(pidFile)) return;
            if (!int.TryParse(File.ReadAllText(pidFile).Trim(), out var pid) || pid <= 0)
            {
                ClearPidFile(dataDir, port);
                return;
            }
            if (!IsProcessAlive(pid))
            {
                ClearPidFile(dataDir, port);
                return;
            }
            if (ShellLogic.ProcessManagement.GetProcessIdByPort(port) != pid)
            {
                // P1-3（质量治理）：记录过但未监听目标端口的 node 大概率是 PID 复用（无关进程）——
                // 不杀，只清 pid 文件；进程本身不是我们管理的服务。
                Logger.Warn($"stale service pid={pid} alive but not listening on port {port}; clearing pid file (possible PID reuse)",
                    ErrorCodes.E2005, new { port });
                ClearPidFile(dataDir, port);
                return;
            }
            // 活着且确实监听目标端口：真僵尸 → 认领并清理（启动清扫闭环，2026-08 修复点2）。
            Logger.Info($"SWEEP: adopting stale service pid={pid} on port {port}");
            bool killed = ShellLogic.ProcessManagement.KillServiceProcess(pid, port);
            if (killed && !IsProcessAlive(pid))
                ClearPidFile(dataDir, port);
            else
                Logger.Error($"stale dsh service pid={pid} on port {port} could not be terminated; pid file kept for next-start sweep",
                    ErrorCodes.E2005, new { pid, port });
        }
        catch { /* 清扫失败不影响启动 */ }
    }

    internal static void ClearPidFile(string dataDir, int port)
    {
        try
        {
            var f = PidFilePath(dataDir, port);
            if (File.Exists(f)) File.Delete(f);
        }
        catch { }
    }

    internal static bool IsProcessAlive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    /// <summary>停止指定 PID（优雅终止决策 + 防误杀）：薄委托给
    /// <see cref="ShellLogic.ProcessManagement.KillServiceProcess"/>（等待 taskkill 退出、
    /// 强杀确认、失败重试一次、失败响亮上报 E2005）。返回 false 时调用方保留 pid 文件。</summary>
    internal static bool KillProcess(int port, int pid)
    {
        try
        {
            return ShellLogic.ProcessManagement.KillServiceProcess(pid, port);
        }
        catch { return false; }
    }

    /// <summary>尽力而为的优雅终止通道（历史决策保留）：CTRL_BREAK 广播路径因会误杀共享
    /// 控制台的 shell 自身已被安全禁用——直接返回 false，走温和 taskkill。绝不改变服务链路。</summary>
    internal static bool TryGracefulStop(int pid) => false;

    /// <summary>
    /// 停止"壳管理的"dsh 服务：优先用传入的记忆 PID（就绪时已记录），否则按端口反查兜底。
    /// 杀干净后短等端口释放（上限 1s）；超时则反查占用者认领清理（防 TIME_WAIT 卡关窗）。
    /// 杀不干净则保留 pid 文件，下次启动由 SweepStaleServicePid 认领。
    /// </summary>
    internal static void StopService(string dataDir, int port, int rememberedPid)
    {
        try
        {
            var pid = rememberedPid;
            if (pid <= 0) pid = ShellLogic.ProcessManagement.GetProcessIdByPort(port); // 兜底：内存没有时再查
            if (pid <= 0)
            {
                ClearPidFile(dataDir, port);
                return;
            }
            if (KillProcess(port, pid))
            {
                // 端口释放探测——进程已死但端口未释放（子进程/TIME_WAIT）时同步等待，
                // 确保关窗后 node 不残留、不占端口。等待上限 1s：TIME_WAIT 由 SO_REUSEADDR 收敛，
                // 超过即记日志不阻塞关窗（消除"关窗卡两秒"）。
                var deadline = DateTime.UtcNow.AddSeconds(1);
                while (DateTime.UtcNow < deadline && ShellLogic.ProcessManagement.GetProcessIdByPort(port) > 0)
                    Thread.Sleep(80);
                if (ShellLogic.ProcessManagement.GetProcessIdByPort(port) > 0)
                {
                    // 兜底：端口释放超时 → 反查占用者，确属 dsh 服务则认领清理
                    // （KillServiceProcess 内部再做身份 + 端口归属双重校验，绝无误杀）。
                    int occupant = ShellLogic.ProcessManagement.GetProcessIdByPort(port);
                    if (occupant > 0 && occupant != pid)
                    {
                        Logger.Info($"STOP: port {port} still occupied by pid={occupant}; attempting reclaim");
                        ShellLogic.ProcessManagement.KillServiceProcess(occupant, port);
                    }
                    Logger.Warn($"service pid={pid} killed but port {port} still occupied",
                        ErrorCodes.E2005, new { pid, port });
                }
                else
                    ClearPidFile(dataDir, port);
            }
            // P2-10：杀不干净则保留 pid 文件，下次启动认领
        }
        catch
        {
            // 停服务失败不影响退出
        }
    }

    /// <summary>服务就绪快探：TCP 可连 + HTTP 有响应（dsh 前端在端口监听后可能还需数十秒才提供 HTTP）。</summary>
    internal static bool IsReady(int port, string url)
    {
        if (!ShellLogic.ServiceReadiness.PortOpen("127.0.0.1", port)) return false;
        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            return ShellLogic.ServiceReadiness.IsHttpReady(url, http); // 契约纯函数（P1-6）
        }
        catch
        {
            return false;
        }
    }
}
