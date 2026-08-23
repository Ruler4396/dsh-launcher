using System.Text.Json;

namespace DshWeb;

/// <summary>回滚动作的物理结果（调用方据此决定是否还需 npm 降级、如何告知用户）。</summary>
/// <param name="DataRestored">至少还原了一个受保护文件。</param>
/// <param name="RestoredFiles">实际还原的相对文件路径列表。</param>
/// <param name="QuarantinedRuntimeDir">被隔离出 runtimes\ 的新版运行时目录（null=无可隔离/隔离失败）。</param>
/// <param name="Warnings">非致命告警（快照缺失、单文件还原失败等）；全部已在日志响亮留痕。</param>
public sealed record UpdateRollbackResult(
    bool DataRestored,
    IReadOnlyList<string> RestoredFiles,
    string? QuarantinedRuntimeDir,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 更新数据守卫（Update Data Guard，[2026-08-23 用户回归]）。
///
/// 根因：dsh 各版本对共享用户数据（$HOME\.dsh 下文件）的格式要求互不兼容——实测
/// 0.1.1-rc.2 首启把 .credentials.yaml 单向迁移为 version+refs 布局；更新失败回退旧版后，
/// 旧解析器抛 "the value for \"version\" must be a string" → 插件树整树加载失败 →
/// 服务 exit(1)，用户体感"每次更新第二天必炸"。
///
/// 守卫契约：
/// - <see cref="SnapshotBeforeApply"/>：apply 前对受保护文件做"版本首拍"——同一目标版本
///   只拍一次（最早的 pre-update 状态是唯一真源，重复 apply 绝不覆盖首拍）；
/// - <see cref="UnconfirmedSnapshotVersion"/> / <see cref="MarkConfirmedHealthy"/>：
///   快照带"健康确认"状态，好符号确认前一直处于回滚观察期（跨会话有效）；
/// - <see cref="RollbackAfterFailedUpdate"/>：启动自检失败后按字节还原受保护文件
///   （被污染的现行文件另存 .rollback-bak-* 供追责），并把 runtimes\&lt;version&gt;
///   隔离出发现链（DshDiscovery 扫描 runtimes\ 子目录，移出即失活）。
///
/// 决策面纯函数在 ShellLogic.UpdateGuardPolicy（契约测试锁定）；本类只做真实 FS 动作。
/// </summary>
public static class UpdateDataGuard
{
    private static string _dataDir = "";   // DSH_HOME\dsh-launcher（壳数据：runtimes 在这里）
    private static string _dshHome = "";   // DSH_HOME（dsh 共享用户数据在这里）
    private static readonly object Sync = new();

    /// <summary>
    /// 受保护文件（相对 DSH_HOME 的路径）。白名单克制原则：只列被证实跨版本不兼容的文件；
    /// 新增条目须附回归证据。".credentials.yaml" = 2026-08-23 事故实证（rc.2 迁移 → rc.8 崩溃）。
    /// </summary>
    public static readonly string[] ProtectedRelativeFiles = { ".credentials.yaml" };

    /// <summary>快照保留上限（跨所有版本合计；超出按目录名时间序删最旧）。</summary>
    private const int MaxSnapshots = 3;

    private static bool IsReady => _dataDir.Length > 0 && _dshHome.Length > 0;

    /// <summary>守卫根目录：DSH_HOME\dsh-launcher\update-guard\。</summary>
    public static string GuardRoot => Path.Combine(_dataDir, "update-guard");
    private static string SnapshotsRoot => Path.Combine(GuardRoot, "snapshots");

    /// <summary>隔离区：新版运行时的 quarantine 去处（与 runtimes\ 同卷保证 Move 原子）。
    /// 必须在 runtimes\ 之外——DshDiscovery 会扫描 runtimes\ 全部子目录，改名不挪窝仍会被发现。</summary>
    public static string QuarantineRoot => Path.Combine(GuardRoot, "quarantine");
    private static string ManifestPath => Path.Combine(GuardRoot, "guard-state.json");
    private static string HistoryPath => Path.Combine(GuardRoot, "rollback-history.jsonl");

    private sealed class SnapshotEntry
    {
        public string Version { get; set; } = "";
        public string Dir { get; set; } = "";
        public string CreatedAtUtc { get; set; } = "";
        public string? ConfirmedHealthyUtc { get; set; }
    }

    /// <summary>组合根在 RunBackgroundMaintenance 阶段调用（与 StagedUpdate.Init 同点）。</summary>
    public static void Init(string dataDir, string dshHome)
    {
        _dataDir = dataDir ?? "";
        _dshHome = dshHome ?? "";
    }

    // ==================== 快照 ====================

    /// <summary>
    /// apply 前的版本首拍。同版本已有快照时不覆盖（first-shot-wins：最早状态即真源），
    /// 并修剪历史快照到 <see cref="MaxSnapshots"/>。返回是否本次新建了快照；
    /// 失败只 Warn 不抛——守卫绝不阻断更新主流程，但日志必须响亮。
    /// </summary>
    public static bool SnapshotBeforeApply(string version)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(version)) return false;
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(SnapshotsRoot);
                var existingNames = Directory.GetDirectories(SnapshotsRoot)
                    .Select(d => Path.GetFileName(d)!)
                    .ToArray();
                if (ShellLogic.UpdateGuardPolicy.PickRollbackSnapshot(existingNames, version) is not null)
                {
                    Logger.Info($"[update-guard] snapshot for v{version} already exists; first-shot wins, not overwritten");
                    return false;
                }

                var dirName = ShellLogic.UpdateGuardPolicy.SnapshotDirName(version, DateTime.UtcNow);
                var snapDir = Path.Combine(SnapshotsRoot, dirName);
                Directory.CreateDirectory(snapDir);

                var captured = new List<string>();
                foreach (var rel in ProtectedRelativeFiles)
                {
                    var src = Path.Combine(_dshHome, rel);
                    if (!File.Exists(src)) continue;
                    File.Copy(src, Path.Combine(snapDir, rel), overwrite: false);
                    captured.Add(rel);
                }

                var entries = LoadManifest();
                entries.Add(new SnapshotEntry
                {
                    Version = version,
                    Dir = dirName,
                    CreatedAtUtc = DateTime.UtcNow.ToString("o"),
                    ConfirmedHealthyUtc = null,
                });
                SaveManifest(entries);
                PruneSnapshotsLocked();

                Logger.Info($"[update-guard] pre-apply snapshot created for v{version}: {dirName} ({captured.Count} file(s) captured)");
                return true;
            }
        }
        catch (Exception ex)
        {
            // 预期内 IO 失败（锁/盘满）：降级为无快照运行 + 响亮留痕；绝不阻断 apply 主流程
            Logger.Error($"[update-guard] pre-apply snapshot FAILED for v{version}: {ex.Message}", ErrorCodes.E4003);
            return false;
        }
    }

    // ==================== 健康确认 ====================

    /// <summary>
    /// 指定身份版本存在"已应用未确认健康"的快照 → 返回该版本（调用方据此武装回滚闸门）；
    /// 无 → null。这是跨会话观察期的查询入口：上次会话应用后未走到好符号，本次启动仍在观察期。
    /// </summary>
    public static string? UnconfirmedSnapshotVersion(string? identityVersion)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(identityVersion)) return null;
        try
        {
            lock (Sync)
            {
                return LoadManifest()
                    .Where(e => e.ConfirmedHealthyUtc is null
                                && string.Equals(e.Version, identityVersion, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(e => e.Dir, StringComparer.OrdinalIgnoreCase)
                    .Select(e => e.Version)
                    .FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"[update-guard] unconfirmed-snapshot query failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>好符号确认：该版本的未确认快照全部标记健康（幂等）。解除回滚武装的唯一正规途径。</summary>
    public static void MarkConfirmedHealthy(string identityVersion)
    {
        if (!IsReady || string.IsNullOrWhiteSpace(identityVersion)) return;
        try
        {
            lock (Sync)
            {
                var entries = LoadManifest();
                var now = DateTime.UtcNow.ToString("o");
                var touched = 0;
                foreach (var e in entries.Where(e =>
                             e.ConfirmedHealthyUtc is null
                             && string.Equals(e.Version, identityVersion, StringComparison.OrdinalIgnoreCase)))
                {
                    e.ConfirmedHealthyUtc = now;
                    touched++;
                }
                if (touched > 0) SaveManifest(entries);
            }
        }
        catch (Exception ex)
        {
            // 确认写盘失败：下次启动会重新进入观察期（多一次误报回滚风险），必须留痕
            Logger.Warn($"[update-guard] healthy-confirm persist failed for v{identityVersion}: {ex.Message}");
        }
    }

    // ==================== 回滚 ====================

    /// <summary>
    /// 启动自检失败后的物理回滚：① 按字节还原受保护文件（现行污染文件另存
    /// ".rollback-bak-&lt;ts&gt;" 供追责，原子写回）；② 把 runtimes\&lt;version&gt; 整体搬进
    /// 隔离区（同卷 rename，DshDiscovery 立即失活，服务重启自动落回旧版本）。
    /// 单项失败不中断其余项（尽力而为 + 全量告警），结果由 <see cref="UpdateRollbackResult"/> 如实上报。
    /// </summary>
    public static UpdateRollbackResult RollbackAfterFailedUpdate(string version, string reason)
    {
        var warnings = new List<string>();
        var restored = new List<string>();
        string? quarantined = null;

        if (!IsReady || string.IsNullOrWhiteSpace(version))
        {
            warnings.Add("guard not initialized or blank version; nothing rolled back");
            return new UpdateRollbackResult(false, restored, quarantined, warnings);
        }

        // ① 还原受保护文件
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(SnapshotsRoot);
                var names = Directory.GetDirectories(SnapshotsRoot)
                    .Select(d => Path.GetFileName(d)!)
                    .ToArray();
                var snapDirName = ShellLogic.UpdateGuardPolicy.PickRollbackSnapshot(names, version);
                if (snapDirName is null)
                {
                    warnings.Add($"no snapshot found for v{version}; data left as-is (runtime quarantine still applies)");
                }
                else
                {
                    var snapDir = Path.Combine(SnapshotsRoot, snapDirName);
                    foreach (var rel in ProtectedRelativeFiles)
                    {
                        var backupFile = Path.Combine(snapDir, rel);
                        if (!File.Exists(backupFile)) continue;
                        var target = Path.Combine(_dshHome, rel);
                        try
                        {
                            var targetDir = Path.GetDirectoryName(target);
                            if (!string.IsNullOrEmpty(targetDir)) Directory.CreateDirectory(targetDir);
                            if (File.Exists(target))
                            {
                                var poisonedCopy = target + ".rollback-bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                                File.Copy(target, poisonedCopy, overwrite: false);
                                Logger.Warn($"[update-guard] poisoned file kept for forensics: {poisonedCopy}");
                            }
                            ShellLogic.FileSystemPolicy.AtomicWrite(target, File.ReadAllText(backupFile));
                            restored.Add(rel);
                        }
                        catch (Exception ex)
                        {
                            warnings.Add($"restore failed for '{rel}': {ex.Message}");
                            Logger.Error($"[update-guard] restore failed for '{rel}': {ex.Message}", ErrorCodes.E4003);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            warnings.Add("snapshot scan failed: " + ex.Message);
            Logger.Error($"[update-guard] snapshot scan failed during rollback of v{version}: {ex.Message}", ErrorCodes.E4003);
        }

        // ② 隔离新运行时（SelfContained 路径）；npm 路径由调用方另行降级
        try
        {
            var runtimeDir = Path.Combine(_dataDir, "runtimes", version);
            if (Directory.Exists(runtimeDir))
            {
                Directory.CreateDirectory(QuarantineRoot);
                var dest = Path.Combine(QuarantineRoot,
                    "runtimes-" + ShellLogic.UpdateGuardPolicy.SanitizeVersionToken(version)
                    + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
                Directory.Move(runtimeDir, dest);
                quarantined = dest;
                Logger.Warn($"[update-guard] runtime v{version} quarantined out of discovery chain: {dest}");
            }
        }
        catch (Exception ex)
        {
            warnings.Add($"runtime quarantine failed: {ex.Message}");
            Logger.Error($"[update-guard] runtime quarantine failed for v{version}: {ex.Message}", ErrorCodes.E4003);
        }

        AppendHistory(version, reason, restored, quarantined, warnings);

        Logger.Error(
            $"[update-guard] rollback finished for v{version}: restored={restored.Count} file(s), " +
            $"quarantined={(quarantined is null ? "no" : "yes")}, warnings={warnings.Count}",
            ErrorCodes.E4003);

        return new UpdateRollbackResult(restored.Count > 0, restored, quarantined, warnings);
    }

    // ==================== 内部：manifest / 历史 / 修剪 ====================

    private static List<SnapshotEntry> LoadManifest()
    {
        if (!File.Exists(ManifestPath)) return new List<SnapshotEntry>();
        using var doc = JsonDocument.Parse(File.ReadAllText(ManifestPath));
        var list = new List<SnapshotEntry>();
        if (doc.RootElement.TryGetProperty("snapshots", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in arr.EnumerateArray())
            {
                list.Add(new SnapshotEntry
                {
                    Version = el.TryGetProperty("version", out var v) ? v.GetString() ?? "" : "",
                    Dir = el.TryGetProperty("dir", out var d) ? d.GetString() ?? "" : "",
                    CreatedAtUtc = el.TryGetProperty("createdAtUtc", out var c) ? c.GetString() ?? "" : "",
                    ConfirmedHealthyUtc = el.TryGetProperty("confirmedHealthyUtc", out var h) && h.ValueKind == JsonValueKind.String
                        ? h.GetString()
                        : null,
                });
            }
        }
        return list;
    }

    private static void SaveManifest(List<SnapshotEntry> entries)
    {
        var payload = new
        {
            snapshots = entries.Select(e => new
            {
                version = e.Version,
                dir = e.Dir,
                createdAtUtc = e.CreatedAtUtc,
                confirmedHealthyUtc = e.ConfirmedHealthyUtc,
            }),
        };
        ShellLogic.FileSystemPolicy.AtomicWrite(ManifestPath, JsonSerializer.Serialize(payload));
    }

    /// <summary>修剪历史快照：保留最近 MaxSnapshots 个（目录名时间序），删除失败的旧目录仅 Warn。</summary>
    private static void PruneSnapshotsLocked()
    {
        var names = Directory.GetDirectories(SnapshotsRoot)
            .Select(d => Path.GetFileName(d)!)
            .ToList();
        foreach (var stale in ShellLogic.UpdateGuardPolicy.PruneSnapshotDirs(names, MaxSnapshots))
        {
            try { Directory.Delete(Path.Combine(SnapshotsRoot, stale), recursive: true); }
            catch (Exception ex) { Logger.Warn($"[update-guard] prune failed for '{stale}': {ex.Message}"); }
        }
    }

    private static void AppendHistory(
        string version, string reason, IReadOnlyList<string> restored,
        string? quarantined, IReadOnlyList<string> warnings)
    {
        try
        {
            var line = JsonSerializer.Serialize(new
            {
                at = DateTime.UtcNow.ToString("o"),
                version,
                reason,
                restored,
                quarantined,
                warnings,
            });
            Directory.CreateDirectory(GuardRoot);
            File.AppendAllText(HistoryPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            Logger.Warn($"[update-guard] history append failed: {ex.Message}");
        }
    }
}
