using System.Text.Json;

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
    /// 探测全局 npm 包的 JS 入口绝对路径。
    /// 读取 %APPDATA%\npm\node_modules\{packageName}\package.json 的 bin 字段，
    /// 解析出相对路径，拼接成绝对物理路径。
    /// </summary>
    public static string? ResolvePackageEntry(string packageName)
    {
        try
        {
            var appDataNpm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
            var pkgDir = Path.Combine(appDataNpm, "node_modules", packageName);
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
}
