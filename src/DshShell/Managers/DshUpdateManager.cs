namespace DshWeb;

/// <summary>
/// staged dsh 更新的无 UI 构建内核（2026-09 自 Program.DownloadDshUpdateStaged 外科手术式抽出，
/// 唯一动机：让 RealOS 测试能以真实 pnpm/npm 驱动"tarball → 完整运行时"管线并校验产物）。
/// 铁律边界：本类绝不触碰 Form / Toast / 标题栏状态——UI 反馈全部由调用方（组合根包装）驱动；
/// 迁移代码块逐行保持原语义（含日志文案、镜像源粘滞策略、失败清场顺序）。
/// </summary>
internal static class DshUpdateManager
{
    /// <summary>
    /// 从已下载 tarball 构建完整运行时（原"步骤 2"全量逻辑）：
    /// pnpm 可用则 ndjson 真实百分比构建（粘住 pack 成功的镜像源），失败或不可用降级 npm
    /// （npm 无真实进度，脉冲动画由调用方维持）。构建失败（npm 亦败）时清理 buildDir。
    /// </summary>
    /// <param name="tarballPath">已就位的 tarball 绝对路径（仅用于日志与 pnpm 安装参数）。</param>
    /// <param name="tarballName">tarball 文件名（npm install 相对引用用）。</param>
    /// <param name="buildDir">全新构建目标目录（调用方保证已清场重建）。</param>
    /// <param name="regSources">registry 源序列（npmmirror 优先等）。</param>
    /// <param name="packSourceIdx">pack 成功所用源下标（构建粘住同源，缓存/解析同源命中）。</param>
    /// <param name="percentProgress">pnpm 真实百分比回调（packageId 自归一化）；npm 路径不回调。</param>
    /// <param name="beforeNpmFallback">pnpm 失败转入 npm 前触发（调用方刷新脉冲态文案——原时序不可省）。</param>
    /// <returns>(Ok=任一路径成功, Tool=实际使用的包管理器)。</returns>
    internal static (bool Ok, string Tool) BuildRuntimeFromTarball(
        string tarballPath, string tarballName, string buildDir,
        string[] regSources, int packSourceIdx,
        Action<int>? percentProgress, Action? beforeNpmFallback)
    {
        var buildOk = false;
        var buildTool = "npm";

        // 检测 pnpm 可用性（绝不安装）
        var nodeEnv = RuntimeResolver.ResolveExisting();
        var nodeExe = nodeEnv?.NodeExe;
        var pnpmEntryJs = nodeExe is not null ? DshWeb.Domain.JsEntryResolver.ResolvePnpmEntry() : null;
        Logger.Info($"pnpm detection: nodeExe={nodeExe ?? "null"}, pnpmEntry={pnpmEntryJs ?? "not found"}");
        var isPnpm = pnpmEntryJs is not null && nodeExe is not null;

        if (isPnpm)
        {
            try
            {
                Logger.Info($"building dsh runtime with pnpm (ndjson progress)");
                // 粘住 pack 成功的源（依赖解析/缓存与下载同源），失败再沿其余源降级
                var pnpmSources = packSourceIdx > 0
                    ? regSources.Skip(packSourceIdx).Concat(regSources.Take(packSourceIdx)).ToArray()
                    : regSources;
                buildOk = Program.TryNpmOverRegistries(pnpmSources, srcIdx => Program.RunPnpmInstall(
                    nodeExe!, pnpmEntryJs!, tarballPath, buildDir, percentProgress,
                    pnpmSources[srcIdx]), "pnpm-build", out _);
                buildTool = "pnpm";
                Logger.Info($"pnpm build result: {buildOk}");
            }
            catch (Exception ex)
            {
                Logger.Warn($"pnpm build failed, falling back to npm: {ex.Message}");
            }
        }

        if (!buildOk)
        {
            Logger.Info("building dsh runtime with npm");
            beforeNpmFallback?.Invoke();
            string buildTail = "";
            var npmSources = packSourceIdx > 0
                ? regSources.Skip(packSourceIdx).Concat(regSources.Take(packSourceIdx)).ToArray()
                : regSources;
            buildOk = Program.TryNpmOverRegistries(npmSources, srcIdx => Program.RunNpmCommand(
                $"install \"./{tarballName}\" --prefix . --prefer-offline --no-audit --no-fund"
                    + npmSources[srcIdx],
                out buildTail, timeoutMs: 1200000, workingDirectory: buildDir), "npm-build", out _);
            if (!buildOk)
            {
                Logger.Warn($"npm build failed: {buildTail}");
                Program.TryDeleteDir(buildDir);
            }
        }

        return (buildOk, buildTool);
    }

    /// <summary>
    /// 构建产物 bin 入口解析（原"步骤 3"的纯读取段）：读 node_modules/@deepseek-ai/dsh/package.json
    /// 并解析 bin 入口。文件缺失由调用方先行区分报错；解析失败/入口缺失返回 null。
    /// </summary>
    internal static string? ResolveBuiltBinEntry(string buildDir)
    {
        var dshPkg = Path.Combine(buildDir, "node_modules", "@deepseek-ai", "dsh", "package.json");
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(dshPkg));
            return DshWeb.Domain.DshDiscovery.ResolveBinEntry(buildDir, doc.RootElement);
        }
        catch { return null; }
    }
}
