using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshWeb.Domain;

/// <summary>
/// JS 入口解析器：彻底绕过 .cmd/.bat shim，直接定位 Node.js 可执行的 .js 入口文件。
///
/// 【铁律】：本类是项目中唯一合法的"npm/pnpm/dsh 的 JS 入口在哪里"探查点。
/// 所有需要执行 npm/pnpm/dsh 的代码必须通过本类获取 JS 入口路径，
/// 然后用 node.exe 直接执行，严禁使用 cmd.exe /c 包装。
///
/// 为什么不使用 .cmd shim：
/// - cmd.exe /c 引号剥离导致 ERROR_INVALID_NAME
/// - cmd.exe 的 GBK 编码导致中文乱码
/// - cmd.exe 中间层导致进程 Kill 不干净
/// - GUI 进程的 PATH 继承问题
/// </summary>
public static class JsEntryResolver
{
    /// <summary>探测 npm-cli.js 的绝对路径。
    /// 优先级：node.exe 同级 → %APPDATA%\npm 全局目录。</summary>
    public static string? ResolveNpmCliJs(string nodeExePath)
    {
        try
        {
            var nodeDir = Path.GetDirectoryName(nodeExePath);
            if (!string.IsNullOrWhiteSpace(nodeDir))
            {
                var std = Path.Combine(nodeDir, "node_modules", "npm", "bin", "npm-cli.js");
                if (File.Exists(std)) return std;
            }
            var appDataNpm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
            var global = Path.Combine(appDataNpm, "node_modules", "npm", "bin", "npm-cli.js");
            if (File.Exists(global)) return global;
        }
        catch { }
        return null;
    }

    /// <summary>探测 pnpm.cjs 的绝对路径。
    /// 路径推导：%APPDATA%\npm\node_modules\pnpm\bin\pnpm.cjs。</summary>
    public static string? ResolvePnpmEntry()
    {
        try
        {
            var appDataNpm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
            // 标准 npm 全局安装布局
            var pnpmCjs = Path.Combine(appDataNpm, "node_modules", "pnpm", "bin", "pnpm.cjs");
            if (File.Exists(pnpmCjs)) return pnpmCjs;
            // pnpm 自有全局目录
            var localPnpm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "pnpm", "pnpm.cjs");
            if (File.Exists(localPnpm)) return localPnpm;
        }
        catch { }
        return null;
    }

    /// <summary>
    /// [兼容入口] 探测全局 npm 包的 JS 入口绝对路径（默认前缀布局）。
    /// 读取 %APPDATA%\npm\node_modules\{packageName}\package.json 的 bin 字段，
    /// 解析出相对路径，拼接成绝对物理路径。
    /// 【issue #24】不再作为全局发现的唯一路径——自定义 npm prefix / pnpm 全局
    /// 布局下包不在 %APPDATA%\npm，请改用 <see cref="ResolveGlobalPackageEntry"/>。
    /// 本方法保留为兼容/回退（含既有调用方语义），内部即第四策略。
    /// </summary>
    public static string? ResolvePackageEntry(string packageName)
    {
        var appDataNpm = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        var pkgDir = Path.Combine(appDataNpm, "node_modules", packageName);
        return ResolveEntryFromPkgDir(pkgDir, packageName);
    }

    /// <summary>
    /// 从指定包目录解析 JS 入口：读取 {pkgDir}\package.json 的 bin 字段
    /// （三态：字符串 / 对象 {短名} 键 / 对象首个字符串键），拼接相对路径，
    /// 校验文件真实存在；bin 无扩展名时兜底补 .js。失败返回 null。
    /// </summary>
    internal static string? ResolveEntryFromPkgDir(string pkgDir, string packageName)
    {
        try
        {
            var pkgJson = Path.Combine(pkgDir, "package.json");
            if (!File.Exists(pkgJson)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(pkgJson));
            if (!doc.RootElement.TryGetProperty("bin", out var bin)) return null;

            string? binPath = null;
            if (bin.ValueKind == JsonValueKind.String)
                binPath = bin.GetString();
            else if (bin.ValueKind == JsonValueKind.Object)
            {
                // 优先用包名对应的键（如 "dsh"），其次第一个
                var shortName = packageName.Contains('/') ? packageName.Split('/')[^1] : packageName;
                if (bin.TryGetProperty(shortName, out var named) && named.ValueKind == JsonValueKind.String)
                    binPath = named.GetString();
                else
                {
                    foreach (var prop in bin.EnumerateObject())
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        { binPath = prop.Value.GetString(); break; }
                }
            }

            if (binPath is null) return null;
            var normalized = binPath.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.Combine(pkgDir, normalized);
            if (File.Exists(fullPath)) return fullPath;
            if (!Path.HasExtension(normalized) && File.Exists(fullPath + ".js"))
                return fullPath + ".js";
        }
        catch { }
        return null;
    }

    /// <summary>
    /// 全局安装 dsh 的 JS 入口自动定位（issue #24 根因修复：不再硬编码 %APPDATA%\npm）。
    ///
    /// 策略（失败逐级记录到 <paramref name="probed"/>，供 E2001 弹窗/日志归因）：
    /// 1. 就近：shim 所在目录的 node_modules\&lt;packageName&gt;（覆盖 npm 默认/自定义前缀、
    ///    pnpm 全局虚拟目录 %LOCALAPPDATA%\pnpm —— 两种布局都是"shim 与 node_modules 同父"）；
    /// 2. shim 文本：npm/pnpm/yarn/nvm 生成的 .cmd/.bat/.ps1 均内嵌真实 JS 入口路径
    ///    （"…node_modules\@deepseek-ai\dsh\lib\bin.js"），提取并校验存在性；
    /// 3. PATH 全候选：对 PATH 上全部 dsh.cmd/dsh.exe/dsh 重复策略 1+2（当前只取第一个）；
    /// 4. 遗留回退：%APPDATA%\npm\node_modules\&lt;packageName&gt;（旧布局与既有语义兜底）。
    /// 全部失败返回 null（上游 E2001 响亮，不再静默落 npx 冷路径）。
    /// </summary>
    /// <param name="shimPathOrNull">调用方已定位的 shim（可为 null，仅作首选）。</param>
    /// <param name="packageName">包名（如 @deepseek-ai/dsh）。</param>
    /// <param name="probed">每级探查到的候选位置（含原因后缀），失败归因用；非空即应展示。</param>
    public static string? ResolveGlobalPackageEntry(string? shimPathOrNull, string packageName, out List<string> probed)
    {
        probed = new List<string>();

        // 候选 shim：调用方首选 + PATH 上全部存在形态（去重，保持顺序）
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(shimPathOrNull)) candidates.Add(shimPathOrNull);
        foreach (var name in new[] { "dsh.cmd", "dsh.exe", "dsh" })
        {
            foreach (var dir in PathDirs())
            {
                try
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) candidates.Add(p);
                }
                catch { /* 不可访问目录跳过 */ }
            }
        }
        candidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var shim in candidates)
        {
            var shimDir = Path.GetDirectoryName(shim);
            if (!string.IsNullOrWhiteSpace(shimDir))
            {
                // 包名分隔符规范化（Path.Combine 不转换单段内 '/'）：保证产出路径全反斜杠，
                // 与 SelfContained 布局一致，E2001 弹窗/日志不出现 @deepseek-ai/dsh 混杂形式。
                var pkgRel = packageName.Replace('/', Path.DirectorySeparatorChar);
                // ---- 策略 1：就近 node_modules ----
                var near = Path.Combine(shimDir, "node_modules", pkgRel);
                if (Directory.Exists(near))
                {
                    var hit = ResolveEntryFromPkgDir(near, packageName);
                    if (hit is not null) return hit;
                    probed.Add($"near-shim(bin-unresolvable):{near}");
                }
                else
                {
                    probed.Add($"near-shim(missing):{near}");
                }

                // ---- 策略 2：shim 文本内嵌入口 ----
                var embedded = TryExtractEmbeddedEntry(shim, shimDir, packageName);
                if (embedded is not null) return embedded;
                probed.Add($"shim-content(no-{packageName.Split('/')[^1]}-entry):{shim}");
            }
        }

        // ---- 策略 4：遗留默认前缀 ----
        var legacyDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "npm", "node_modules",
            packageName.Replace('/', Path.DirectorySeparatorChar));
        probed.Add($"legacy:{legacyDir}");
        var legacyHit = ResolveEntryFromPkgDir(legacyDir, packageName);
        if (legacyHit is not null) return legacyHit;

        return null;
    }

    /// <summary>
    /// 从 shim 文本提取内嵌的 JS 入口绝对路径（策略 2）。
    /// npm/pnpm 的 .cmd/.bat 形如 <c>"%_prog%" "&lt;dir&gt;\..\@deepseek-ai\dsh\lib\bin.js" %*</c>，
    /// yarn/.ps1 同类内嵌真实路径。取首个"含 node_modules\&lt;packageName&gt; 且以 .js/.cjs/.mjs
    /// 结尾"的引号段，归一化 ..\ 后校验存在。
    /// </summary>
    private static string? TryExtractEmbeddedEntry(string shimPath, string shimDir, string packageName)
    {
        try
        {
            var text = File.ReadAllText(shimPath);
            // npm/pnpm 生成的 shim 用 %~dp0 / %dp0% 指代 shim 目录；路径分隔符可能是 \ 或 /
            var packageMarker = string.Concat("node_modules", Path.DirectorySeparatorChar,
                packageName.Replace('/', Path.DirectorySeparatorChar));
            var packageMarkerAlt = "node_modules/" + packageName.Replace('\\', '/');
            foreach (Match m in Regex.Matches(text,
                "\"([^\"]+\\.(?:js|cjs|mjs))\"", RegexOptions.IgnoreCase))
            {
                var raw = m.Groups[1].Value.Trim(); // 引号前的位置可能吞入前导空白（"%_prog%" 之后的空格）
                var resolved = raw;
                if (resolved.StartsWith("%~dp0", StringComparison.OrdinalIgnoreCase))
                    resolved = Path.Combine(shimDir, resolved["%~dp0".Length..].TrimStart('\\', '/'));
                else if (resolved.StartsWith("%dp0%", StringComparison.OrdinalIgnoreCase))
                    resolved = Path.Combine(shimDir, resolved["%dp0%".Length..].TrimStart('\\', '/'));
                else
                {
                    try { resolved = Path.GetFullPath(Path.Combine(shimDir, resolved)); }
                    catch { continue; }
                }
                if (!resolved.Contains(packageMarker, StringComparison.OrdinalIgnoreCase)
                    && !resolved.Contains(packageMarkerAlt, StringComparison.OrdinalIgnoreCase)) continue;
                if (File.Exists(resolved)) return resolved;
            }
        }
        catch { /* shim 不可读/无内嵌入口：交由其他策略 */ }
        return null;
    }

    private static IEnumerable<string> PathDirs()
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv)) yield break;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = dir.Trim();
            if (trimmed.Length > 0) yield return trimmed;
        }
    }
}