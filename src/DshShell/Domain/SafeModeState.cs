using System.Text.Json;

namespace DshWeb.Domain;

/// <summary>
/// 安全模式持久化状态（ADR-022）：safe-mode.json 的内存映像 + 原子落盘。
/// - active/tier：当前是否处于安全模式及降级梯级（重启后仍可识别）；
/// - lastFailure：最近一次启动失败的融合证据视图（BootHealthMonitor 裁决 + 吸收态追加）。
/// 读写全部原子（.tmp + File.Move），损坏时按未激活处理并重建文件——状态恢复绝不阻断启动。
/// 组合根持有单例：<c>Program.SafeMode</c>（DefaultStorePath(DshHomeDir)）。
/// </summary>
public sealed class SafeModeState
{
    private readonly string _storePath;

    /// <summary>当前是否处于安全模式。</summary>
    public bool IsActive { get; private set; }

    /// <summary>生效的安全模式梯级（默认一级：保留 @deepseek-ai 核心）。</summary>
    public SafeProfileTier Tier { get; private set; } = SafeProfileTier.Tier1KeepDeepSeekCore;

    /// <summary>最近一次启动失败证据（safe-mode.json lastFailure 原文；无失败为 null）。</summary>
    public JsonElement? LastFailure { get; private set; }

    /// <summary>
    /// 连续启动失败计数（跨会话持久化；2026-08-25 事故回归新增）。
    /// 每次进入 Failed 裁决时 +1（<see cref="RegisterBootFailure"/>，由组合根在
    /// HandleBootHealthFailed 恰好调用一次——吸收态证据追加的 VerdictUpdated 重写不计数）；
    /// 好符号确认健康时清零（<see cref="ResetFailureStreak"/>）。达到
    /// ShellLogic.BootRecoveryPolicy.AnonymousFailureSafeModeThreshold 后，匿名失败也升级为
    /// 安全模式询问，打破"问重启→再崩→再问重启"的跨会话死循环。
    /// </summary>
    public int ConsecutiveBootFailures { get; private set; }

    /// <summary>默认存储路径：DSH_HOME\dsh-launcher\safe-mode.json。</summary>
    public static string DefaultStorePath(string dshHome)
        => Path.Combine(dshHome, "dsh-launcher", "safe-mode.json");

    public SafeModeState(string storePath)
    {
        _storePath = storePath;
        Load();
    }

    /// <summary>进入安全模式（记录梯级并落盘，崩溃/重启后仍可识别）。</summary>
    public void Activate(SafeProfileTier tier)
    {
        IsActive = true;
        Tier = tier;
        Save();
    }

    /// <summary>退出安全模式（落盘；两级阶梯均失败或用户恢复正常模式时调用）。</summary>
    public void Deactivate()
    {
        IsActive = false;
        Save();
    }

    /// <summary>记录启动失败证据（融合视图原文；VerdictUpdated 追加证据时整体重写）。
    /// 注意：本方法**不**推进连续失败计数——吸收态证据追加也会走这里，重复调用属常态；
    /// 计数由 <see cref="RegisterBootFailure"/> 在 Failed 恰好一次的路径上显式推进。</summary>
    public void RecordFailure(JsonElement failure)
    {
        LastFailure = failure.Clone();
        Save();
    }

    /// <summary>推进连续启动失败计数并落盘（组合根在 Failed 裁决处理路径恰好调用一次）。</summary>
    public void RegisterBootFailure()
    {
        ConsecutiveBootFailures++;
        Save();
    }

    /// <summary>好符号确认健康 → 清零连续失败计数并落盘（幂等：已为 0 时不产生 IO）。</summary>
    public void ResetFailureStreak()
    {
        if (ConsecutiveBootFailures == 0) return;
        ConsecutiveBootFailures = 0;
        Save();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_storePath)) return;
            using var doc = JsonDocument.Parse(File.ReadAllText(_storePath));
            if (doc.RootElement.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.True)
                IsActive = true;
            if (doc.RootElement.TryGetProperty("tier", out var tier))
                Tier = tier.GetInt32() != 2 ? SafeProfileTier.Tier1KeepDeepSeekCore : SafeProfileTier.Tier2Minimal;
            if (doc.RootElement.TryGetProperty("lastFailure", out var lf) && lf.ValueKind == JsonValueKind.Object)
                LastFailure = lf.Clone();
            if (doc.RootElement.TryGetProperty("consecutiveBootFailures", out var streak)
                && streak.ValueKind == JsonValueKind.Number
                && streak.TryGetInt32(out var n) && n > 0)
                ConsecutiveBootFailures = n;
        }
        catch
        {
            // 状态文件损坏 → 按未激活处理（下次 Save 重建）；绝不因状态恢复失败而阻断启动
            IsActive = false;
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = _storePath + ".tmp";
            using (var ms = new MemoryStream())
            {
                using (var writer = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean("active", IsActive);
                    writer.WriteNumber("tier", (int)Tier);
                    writer.WriteNumber("consecutiveBootFailures", ConsecutiveBootFailures);
                    if (LastFailure is { } failure)
                    {
                        writer.WritePropertyName("lastFailure");
                        failure.WriteTo(writer);
                    }
                    writer.WriteEndObject();
                }
                File.WriteAllBytes(tmp, ms.ToArray());
            }
            File.Move(tmp, _storePath, overwrite: true);
        }
        catch
        {
            // 状态落盘失败不影响主流程（best-effort）
        }
    }
}
