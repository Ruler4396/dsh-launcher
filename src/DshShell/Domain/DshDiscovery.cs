using System.Diagnostics;
using System.Text.Json;

namespace DshWeb.Domain;

/// <summary>
/// dsh 运行时统一发现机制。
///
/// 这是系统中唯一合法的"dsh 在哪里、什么版本"探查点。
/// 回退链：SelfContained → DSH_VERSION → DSH_WEB_URL → where dsh → npm shim → npx。
///
/// 【铁律】：UpdateChecker、Program.ReadGlobalDshVersion、start-dsh.vbs 的调用方
/// 必须通过本类获取 DshRuntimeIdentity，严禁各自独立探测。
/// </summary>
public static class DshDiscovery
{
    /// <summary>标准 npm 包名。</summary>
    public const string PackageName = "@deepseek-ai/dsh";

    // ---------------- 昂贵探测的记忆化 ----------------
    // DiscoverCurrentRuntime 的各步骤里唯一昂贵的是全局/shim 身份的版本探测
    // （spawn node --version，数百 ms～3s，且旧实现可无限阻塞）；其余（PATH 扫描、
    // npm shim 存在性、runtimes 目录扫描）都是廉价文件系统/注册表读取。
    // 因此只对版本探测做会话级记忆：DSH_VERSION 等环境钩子保持即时生效（每次重读），
    // 已有测试语义不变。InvalidateCache() 同时清除探测记忆与调用侧缓存。
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> ProbeMemo = new();

    /// <summary>失效发现层记忆（写侧动作后调用：首装安装成功、更新应用/回滚等）。</summary>
    public static void InvalidateCache()
    {
        ProbeMemo.Clear();
    }

    /// <summary>
    /// 发现当前 dsh 运行时身份（ADR-024：全系统唯一合法的 Identity 产出点）。
    /// 优先级：SelfContained → DSH_VERSION → DSH_WEB_URL → where dsh/npm shim（合并 GlobalNpm）→ npx。
    /// 返回的身份携带 NodeExePath × DshEntryJsPath 物理要件——服务启动命令只能由它拼装。
    /// </summary>
    public static DshRuntimeIdentity DiscoverCurrentRuntime()
    {
        // 0. DSH_VERSION 环境变量（测试钩子/显式覆盖）：覆盖所有 Source 的版本
        var envVersion = Environment.GetEnvironmentVariable("DSH_VERSION");

        // 1. DSH_WEB_URL 设置时视为外部托管（壳不管理生命周期）
        var externalUrl = Environment.GetEnvironmentVariable("DSH_WEB_URL");
        if (!string.IsNullOrWhiteSpace(externalUrl))
        {
            return new DshRuntimeIdentity(
                DshSource.External, null, null, envVersion);
        }

        // 2. SelfContained 运行时（launcher 自管，后台构建，原子切换）— 最高优先级
        var selfContained = DiscoverSelfContainedRuntime();
        if (selfContained is not null)
        {
            return selfContained with { Version = envVersion ?? selfContained.Version };
        }

        // 3. 全局 npm 安装：where dsh 命中，或 %APPDATA%\npm\dsh.cmd 存在但 PATH 未包含。
        //    [ADR-024] 旧 DshSource.NpmShim 并入 GlobalNpm——两者物理形态相同（全局安装），
        //    分裂建模曾导致"检测的 dsh"与"启动的 dsh"身份割裂。
        var globalDsh = FindOnPath("dsh.cmd") ?? FindOnPath("dsh.exe") ?? FindOnPath("dsh");
        var npmShim = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "dsh.cmd");
        if (globalDsh is not null || File.Exists(npmShim))
        {
            var version = envVersion ?? ReadVersionFromExecutable(globalDsh ?? npmShim);
            // 【issue #24】入口经统一全局解析器定位（就近 node_modules / shim 内嵌路径 /
            // PATH 全候选 / 遗留 %APPDATA%\npm 四级策略，不再硬编码 %APPDATA%\npm——
            // 自定义 npm prefix 与 pnpm 全局布局同样可解析）。缺失时 CanLaunchDirectly=false，
            // 启动层响亮报 E2001 且携带探针路径（EntryProbeFailures），而非静默落入 cmd.exe 中间层。
            var entryJs = JsEntryResolver.ResolveGlobalPackageEntry(globalDsh ?? npmShim, PackageName, out var probed);
            if (entryJs is null && probed.Count > 0)
                Logger.Warn($"global dsh entry resolution failed; probed: {string.Join(" | ", probed)}");
            return new DshRuntimeIdentity(
                DshSource.GlobalNpm, FindNodeExe(), entryJs, version,
                EntryProbeFailures: entryJs is null && probed.Count > 0 ? probed : null);
        }

        // 4. npx 兜底（无任何物理安装；首装链负责 npm -g 后 InvalidateCache 再发现）
        return new DshRuntimeIdentity(
            DshSource.NpxCache, null, null, envVersion);
    }

    /// <summary>检查当前 dsh 是否已物理安装（SelfContained / GlobalNpm）。</summary>
    public static bool IsGloballyInstalled()
    {
        var identity = DiscoverCurrentRuntime();
        return identity.Source is DshSource.SelfContained or DshSource.GlobalNpm;
    }

    // ---------- SelfContained 运行时发现 ----------

    /// <summary>
    /// 发现 launcher 自管的 SelfContained 运行时。
    /// 扫描 DataDir/runtimes/ 目录，找最新版本的完整构建。
    /// </summary>
    private static DshRuntimeIdentity? DiscoverSelfContainedRuntime()
    {
        try
        {
            var dataDir = GetDataDir();
            var runtimesDir = Path.Combine(dataDir, "runtimes");
            if (!Directory.Exists(runtimesDir)) return null;

            string? bestDir = null;
            string? bestVersion = null;
            string? bestBinEntry = null;

            foreach (var dir in Directory.GetDirectories(runtimesDir))
            {
                var dshPkg = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "package.json");
                if (!File.Exists(dshPkg)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(dshPkg));
                    var version = doc.RootElement.TryGetProperty("version", out var v) ? v.GetString() : null;
                    var binEntry = ResolveBinEntry(dir, doc.RootElement);
                    if (version is null || binEntry is null) continue;

                    if (bestVersion is null || CompareVersions(version, bestVersion) > 0)
                    {
                        bestDir = dir;
                        bestVersion = version;
                        bestBinEntry = binEntry;
                    }
                }
                catch { /* 单个目录损坏跳过 */ }
            }

            if (bestDir is not null && bestVersion is not null && bestBinEntry is not null)
            {
                var binPath = Path.Combine(bestDir, "node_modules", "@deepseek-ai", "dsh", bestBinEntry);
                return new DshRuntimeIdentity(
                    DshSource.SelfContained, FindNodeExe(), binPath, bestVersion);
            }
        }
        catch { /* 发现失败按无 SelfContained 处理 */ }
        return null;
    }

    /// <summary>从 package.json 的 bin 字段解析 JS 入口的相对路径。</summary>
    internal static string? ResolveBinEntry(string runtimeDir, JsonElement pkgRoot)
    {
        try
        {
            if (pkgRoot.TryGetProperty("bin", out var bin))
            {
                string? binPath = null;
                if (bin.ValueKind == JsonValueKind.String)
                    binPath = bin.GetString();
                else if (bin.ValueKind == JsonValueKind.Object)
                {
                    if (bin.TryGetProperty("dsh", out var dshBin) && dshBin.ValueKind == JsonValueKind.String)
                        binPath = dshBin.GetString();
                    else
                    {
                        foreach (var prop in bin.EnumerateObject())
                            if (prop.Value.ValueKind == JsonValueKind.String)
                            { binPath = prop.Value.GetString(); break; }
                    }
                }
                if (binPath is not null)
                {
                    var sep = Path.DirectorySeparatorChar;
                    var normalized = binPath.Replace('/', sep);
                    var fullPath = Path.Combine(runtimeDir, "node_modules", "@deepseek-ai", "dsh", normalized);
                    if (File.Exists(fullPath)) return normalized;
                    if (!Path.HasExtension(normalized) && File.Exists(fullPath + ".js"))
                        return normalized + ".js";
                }
            }
        }
        catch (Exception ex)
        {
            // [F24] bin 入口解析失败（package.json 损坏/schema 变更）留痕：否则下游只见
            // 笼统 E2001，"为什么解析不到入口"无从归因。
            Logger.Warn($"bin entry resolve failed for {runtimeDir}: {ex.Message}");
        }
        return null;
    }

    // ---------- 辅助 ----------

    internal static string GetDataDir()
    {
        var env = Environment.GetEnvironmentVariable("DSH_HOME");
        var dshHome = !string.IsNullOrWhiteSpace(env)
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        return Path.Combine(dshHome, "dsh-launcher");
    }

    /// <summary>
    /// 版本比较（F1 修复）：委托 ShellLogic.VersionPolicy 全系统唯一实现。
    /// 历史教训：本方法曾用 string.CompareOrdinal 比较 prerelease——'1' &lt; '9' 使
    /// 0.1.0-rc.10 被判小于 0.1.0-rc.9，runtimes\ 多版本共存时（apply 不删旧目录，
    /// 共存是常态）发现层永远选中旧版，"更新进度 100%、重启后版本没变"。
    /// 与 UpdateChecker（更新检测）必须同源，严禁再出现两套比较器。
    /// </summary>
    internal static int CompareVersions(string? a, string? b)
        => ShellLogic.VersionPolicy.CompareVersions(a, b);

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) return null;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var exe = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(exe)) return exe;
            }
            catch { }
        }
        return null;
    }

    /// <summary>
    /// 读取 dsh 版本号。[ADR-021] 使用 node.exe 直接执行 dsh 的 JS 入口，彻底绕过 cmd.exe。
    /// 失败返回 null（不抛异常）。探测进程遵循三必须：输出异步排空 + 限时等待 +
    /// 超时 Kill(entireProcessTree)（修复旧实现同步 ReadToEnd 可无限阻塞、超时不杀树）。
    /// </summary>
    private static string? ReadVersionFromExecutable(string exePath)
    {
        try
        {
            // 尝试找到 node.exe 和 dsh 的 JS 入口（全局布局经统一解析器，issue #24）
            var nodeExe = FindNodeExe();
            var dshEntryJs = JsEntryResolver.ResolveGlobalPackageEntry(exePath, PackageName, out _);

            string fileName;
            string arguments;

            if (nodeExe is not null && dshEntryJs is not null)
            {
                // [ADR-021] 优先使用 node.exe 直接执行 dsh 的 JS 入口
                fileName = nodeExe;
                arguments = $"\"{dshEntryJs}\" --version";
            }
            else
            {
                // Fallback：直接执行可执行文件（非 .cmd 情况）
                fileName = exePath;
                arguments = "--version";
            }

            return ProbeVersionOutput(fileName, arguments, timeoutMs: 3000);
        }
        catch { return null; }
    }

    /// <summary>
    /// 有界版本探测（内部可测）：启动子进程采集 stdout 中的版本号。
    /// 三必须合规：stdout/stderr 异步排空（防管道缓冲满死锁）、WaitForExit(timeout)、
    /// 超时 Kill(entireProcessTree) 并回收。超时/启动失败返回 null。
    /// 【F3】版本提取委托 <see cref="ExtractVersionLine"/>（旧行为把整段 stdout 当版本号，
    /// dsh 输出任何 banner/提示行即产生脏版本）。
    /// </summary>
    internal static string? ProbeVersionOutput(string fileName, string arguments, int timeoutMs)
    {
        var memoKey = fileName + "|" + arguments;
        if (ProbeMemo.TryGetValue(memoKey, out var memoed)) return memoed;
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            // 异步排空两个管道（旧实现在 UI 线程同步 ReadToEnd：子进程若不关 stdout 则无限阻塞）
            var outputTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); p.WaitForExit(2000); } catch { /* 尽力回收 */ }
                Logger.Warn($"version probe timed out ({timeoutMs}ms); process tree killed: {fileName}");
                return ProbeMemo[memoKey] = null; // 失败同样记忆：防会话内反复 3s 空转
            }
            var output = outputTask.Result; // 进程已退出（WaitForExit=true）→ 管道已关闭，任务必已完成
            return ProbeMemo[memoKey] = ExtractVersionLine(output);
        }
        catch (Exception ex)
        {
            Logger.Warn($"version probe failed for {fileName}: {ex.Message}");
            return ProbeMemo[memoKey] = null;
        }
    }

    /// <summary>
    /// 【F3】从版本探测 stdout 提取版本号：按行扫描，返回首个"看起来像版本号"的行
    /// （v 前缀可选、2-4 段数字、可带 -prerelease/+metadata）。旧行为把整段 stdout
    /// 当版本号——dsh 输出任何 banner/升级提示行即产生脏版本，更新比较退化为
    /// 0.0.0 误报循环。找不到匹配行返回 null（fail-open：版本未知不阻断启动，
    /// 更新检测按"本地未知"处理）。刻意不做松散 token 搜索：避免把 "requires
    /// node >= 18.0.0" 之类的提示行误认成 dsh 版本。
    /// </summary>
    internal static readonly System.Text.RegularExpressions.Regex VersionLineRegex = new(
        @"^v?\d+(\.\d+){1,3}([-+].*)?$",
        System.Text.RegularExpressions.RegexOptions.Compiled
        | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static string? ExtractVersionLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 0 && VersionLineRegex.IsMatch(line)) return line;
        }
        return null;
    }

    /// <summary>查找 node.exe 绝对路径。</summary>
    private static string? FindNodeExe()
    {
        // 优先从 PATH 查找
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var exe = Path.Combine(dir.Trim(), "node.exe");
                    if (File.Exists(exe)) return exe;
                }
                catch { }
            }
        }
        // 注册表
        try
        {
            foreach (var hive in new[] { @"HKLM\SOFTWARE\Node.js", @"HKLM\SOFTWARE\WOW6432Node\Node.js" })
            {
                var ip = Microsoft.Win32.Registry.GetValue(hive, "InstallPath", null) as string;
                if (!string.IsNullOrWhiteSpace(ip))
                {
                    var exe = Path.Combine(ip, "node.exe");
                    if (File.Exists(exe)) return exe;
                }
            }
        }
        catch { }
        return null;
    }
}
