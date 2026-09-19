namespace DshWeb.Domain;

/// <summary>
/// 安全模式启动身份装饰策略（issue #28-4 修复；ADR-022 延伸）：
/// 全系统**唯一**判定"这次拉起要不要带 <c>--profile .dsh-safe</c>"的地方。
///
/// 【事故】这条判定此前只写在重启路径（<c>Program.StartDshServiceViaIdentity</c>）里，
/// 初始启动走 <c>LauncherApp → RuntimeManager.EnsureRuntimeAsync</c> 完全不套 profile。
/// 同一份粘滞 <c>safe-mode.json</c> 下产生用户实测到的诡异不对称——
/// 「托盘退出重开插件都在，点 DSH 内置重启插件凭空消失」：重启那次悄悄带上 .dsh-safe，
/// 而它按设计剥离所有非 <c>@deepseek-ai</c> bundle（<see cref="SafeProfileBuilder.ResolveBundles"/>），
/// 且不写标题栏「（安全模式）」横幅（横幅只在 TryStartSafeMode 里设），全程静默。
///
/// 现在启动与重启都经本函数 → 两侧对称；可见性由返回身份的 <see cref="DshRuntimeIdentity.IsSafeProfile"/>
/// 驱动（以真正用于拉起进程的那份身份为凭据，不读全局标志）。
/// </summary>
public static class SafeModeLaunchPolicy
{
    /// <summary>
    /// 安全模式激活时给身份套上隔离 profile；已套/目录未知时原样返回（幂等，重复重启不叠加、
    /// 不把已生效的 profile 改写成别的目录）。除 <c>ProfilePath</c> 外不改写任何身份事实。
    /// </summary>
    public static DshRuntimeIdentity Decorate(
        DshRuntimeIdentity identity, bool safeModeActive, string? safeProfileDir)
    {
        if (!safeModeActive || identity.IsSafeProfile || string.IsNullOrWhiteSpace(safeProfileDir))
            return identity;
        return identity.WithProfile(safeProfileDir);
    }

    /// <summary>
    /// 粘滞标志说"在安全模式"、但隔离 profile 实际不在（被清理、被用户删、升级残留）时，
    /// 必须先重建再带 <c>--profile</c> 拉起。
    ///
    /// 【实测事故】不重建直接拉起，dsh 侧硬失败：
    /// <c>dsh: profile ".dsh-safe" does not exist</c> → 进程 exit 1 → 壳 E2002 service-exited。
    /// 后果比"插件没加载"严重得多：<b>用户连界面都进不去，也就永远点不到"退出安全模式"</b>
    /// ——粘滞状态把自己锁死了。与 issue #25 同一类陷阱：提示/入口不可用即等于被困。
    /// </summary>
    public static bool NeedsRebuild(bool safeModeActive, bool profileExists)
        => safeModeActive && !profileExists;

    /// <summary>
    /// 重建仍失败的兜底判定：<b>退回正常模式启动</b>（并清掉粘滞标志，让状态自洽）。
    /// 取舍是明确的——"插件被禁用但界面能用"远好于"界面起不来且无法退出"。
    /// </summary>
    public static bool ShouldFallBackToNormal(bool rebuildSucceeded) => !rebuildSucceeded;
}
