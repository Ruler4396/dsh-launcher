using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DshWeb;

/// <summary>
/// 运行环境解析与免污染补齐（v0.3.0）：
/// - 解析唯一 "Node/npm 本位"：PATH → 注册表 → 便携目录 %LOCALAPPDATA%\dsh-launcher\env\node；
/// - 便携 Node：用户确认后下载 LTS zip（固定版本表 + 镜像回退链）→ SHASUMS256 校验 → 解压；
/// - 绝不打包 Node 进安装包；不静默下载、不常驻重试，失败即一次性报错。
/// 原则：系统 Node ≥18 优先（尊重用户环境）；否则便携。
/// </summary>
public static class RuntimeResolver
{
    /// <summary>便携 Node 目录（用户指定 %LOCALAPPDATA%，不随 MSI 卸载误删）。</summary>
    public static string PortableNodeDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "dsh-launcher", "env", "node");

    /// <summary>Node LTS 固定版本表（实现时按当时 LTS 核对；可用 DSH_NODE_VERSION 覆盖）。
    /// 克制：不做启发式选版，只维护一个已知可用版本，随发版手动更新。
    /// v0.3.1：核对于 2026-08——Node 24.x 为 Active LTS（支持至 2028-04），
    /// 当前已知最新 v24.15.0（2026-04 发布）；v22 线仍在维护期（至 2027-04），
    /// 选用 v24 以最大化支持窗口。</summary>
    public static string NodeLtsVersion =>
        Environment.GetEnvironmentVariable("DSH_NODE_VERSION") ?? "v24.15.0";

    public sealed record NodeEnvironment(string? NodeExe, bool IsPortable, string? RootDir);

    /// <summary>解析当前可用的 Node 环境（不安装、不下载）。主版本 ≥18 才算可用。
    /// 质量治理：找不到/不可用的原因记日志（此前静默回退，用户困惑"为何要下载便携版"）。</summary>
    public static NodeEnvironment ResolveExisting()
    {
        try
        {
            var onPath = FindOnPath("node.exe");
            if (onPath is not null)
            {
                if (IsUsableNode(onPath))
                    return new NodeEnvironment(onPath, false, Path.GetDirectoryName(onPath));
                Logger.Warn($"node.exe on PATH is unusable (version <18 or broken): {onPath}");
            }
            var viaRegistry = FindViaRegistry();
            if (viaRegistry is not null)
            {
                if (IsUsableNode(viaRegistry))
                    return new NodeEnvironment(viaRegistry, false, Path.GetDirectoryName(viaRegistry));
                Logger.Warn($"node.exe via registry is unusable (version <18 or broken): {viaRegistry}");
            }
            var portable = Path.Combine(PortableNodeDir, "node.exe");
            if (File.Exists(portable))
            {
                if (IsUsableNode(portable))
                    return new NodeEnvironment(portable, true, PortableNodeDir);
                Logger.Warn($"portable node.exe is unusable (version <18 or broken): {portable}");
            }
            Logger.Info("no usable Node.js found (PATH/registry/portable); portable download will be offered");
        }
        catch { /* 解析失败按缺失处理 */ }
        return new NodeEnvironment(null, false, null);
    }

    /// <summary>Node 缺失原因（供确认框文案区分）：返回 "not-found"（完全未安装）、
    /// "too-old"（存在但版本 &lt;18 或损坏）、null（有可用 Node）。</summary>
    internal static string? NodeMissingReason()
    {
        try
        {
            var onPath = FindOnPath("node.exe");
            if (onPath is not null) return IsUsableNode(onPath) ? null : "too-old";
            var viaRegistry = FindViaRegistry();
            if (viaRegistry is not null) return IsUsableNode(viaRegistry) ? null : "too-old";
            var portable = Path.Combine(PortableNodeDir, "node.exe");
            if (File.Exists(portable)) return IsUsableNode(portable) ? null : "too-old";
            return "not-found";
        }
        catch { return "not-found"; }
    }

    /// <summary>把便携 Node 目录前插到进程级 PATH（wscript → cmd → dsh/npm 自动继承）。</summary>
    public static void PrependToPath(string nodeRoot)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        Environment.SetEnvironmentVariable("PATH", nodeRoot + Path.PathSeparator + path);
    }

    /// <summary>下载并安装便携 Node（版本表 + 镜像回退 + SHA256 校验）。成功返回 true。</summary>
    public static async Task<(bool Ok, string? Code, string? Detail)> EnsurePortableNodeAsync(CancellationToken ct = default)
    {
        var version = NodeLtsVersion;
        var (baseUrl, zipPath) = await DownloadWithFallbackAsync(version, ct);
        if (ct.IsCancellationRequested)
        {
            if (zipPath is not null) TryDelete(zipPath);
            return (false, ErrorCodes.E1002, "已取消（未安装 Node.js）。可稍后重试，或手动安装 Node.js 18+。");
        }
        if (zipPath is null)
        {
            Logger.Error($"portable node download failed (all mirrors), version={version}", ErrorCodes.E1003);
            return (false, ErrorCodes.E1003, $"所有镜像均下载失败（版本 {version}）。可稍后重试，或手动安装 Node.js 18+。");
        }
        Logger.Info($"portable node downloaded from {baseUrl}", ctx: new { version });
        if (baseUrl is not null) RecordLastMirror(baseUrl);
        try
        {
            if (!await VerifySha256Async(zipPath, baseUrl, version))
            {
                TryDelete(zipPath);
                Logger.Error($"portable node checksum mismatch, version={version}", ErrorCodes.E1004);
                return (false, ErrorCodes.E1004, "校验和不匹配，已拒绝使用（可能源被篡改或下载损坏）。");
            }
            var ok = ExtractPortableNode(zipPath, version);
            TryDelete(zipPath);
            if (!ok)
            {
                Logger.Error($"portable node extract failed, version={version}", ErrorCodes.E1005);
                return (false, ErrorCodes.E1005, "解压失败（磁盘空间不足或目录被占用？）。");
            }
            Logger.Info("portable node ready", ctx: new { dir = PortableNodeDir });
            return (true, null, null);
        }
        catch (OperationCanceledException)
        {
            TryDelete(zipPath);
            return (false, ErrorCodes.E1002, "已取消（未安装 Node.js）。");
        }
        catch (Exception ex)
        {
            Logger.Error("portable node install failed: " + ex.Message, ErrorCodes.E1005);
            return (false, ErrorCodes.E1005, ex.Message);
        }
    }

    // ---------- 内部实现 ----------

    private static bool IsUsableNode(string nodeExe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(nodeExe, "--version")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            var readTask = p.StandardOutput.ReadToEndAsync(); // 后台排空管道，防止子进程挂死时阻塞
            if (!p.WaitForExit(3000))
            {
                // 超时：杀进程防泄漏（损坏的安装包弹窗/卡 IO 会让 node --version 挂死）
                try { p.Kill(); p.WaitForExit(); } catch { }
                return false;
            }
            return IsUsableNodeVersion(readTask.Result.Trim());
        }
        catch { return false; }
    }

    /// <summary>
    /// Node 可用门槛契约（C10，P1-6）：主版本 ≥18 才算可用（当前 dsh 运行要求）。
    /// 纯函数（stub 可测），与 dsh 上游要求脱钩时只需改这一处。
    /// </summary>
    internal static bool IsUsableNodeVersion(string? versionOutput)
    {
        if (string.IsNullOrWhiteSpace(versionOutput)) return false;
        var v = versionOutput.Trim().TrimStart('v');
        var major = v.Split('.')[0];
        return int.TryParse(major, out var m) && m >= 18;
    }

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
            catch { /* 跳过不可访问项 */ }
        }
        return null;
    }

    private static string? FindViaRegistry()
    {
        try
        {
            foreach (var hive in new[] { @"HKLM\SOFTWARE\Node.js", @"HKLM\SOFTWARE\WOW6432Node\Node.js" })
            {
                var ip = Microsoft.Win32.Registry.GetValue(hive, "InstallPath", null) as string;
                if (!string.IsNullOrWhiteSpace(ip) && File.Exists(Path.Combine(ip, "node.exe")))
                    return Path.Combine(ip, "node.exe");
            }
        }
        catch { }
        return null;
    }

    /// <summary>镜像回退链（纯函数，可单测）：自定义镜像（DSH_NODE_MIRROR）→ 上次成功源
    /// （runtime-state.json 记忆）→ 官方 nodejs.org → npmmirror。无测速、无并发（克制）。</summary>
    internal static IEnumerable<string> BaseUrls(string version, string? customMirror, string? lastMirror)
    {
        if (!string.IsNullOrWhiteSpace(customMirror))
            yield return customMirror.TrimEnd('/');
        if (!string.IsNullOrWhiteSpace(lastMirror))
            yield return lastMirror;
        yield return $"https://nodejs.org/dist/{version}";
        yield return $"https://registry.npmmirror.com/-/binary/node/{version}";
    }

    private static IEnumerable<string> BaseUrls(string version) =>
        BaseUrls(version, Environment.GetEnvironmentVariable("DSH_NODE_MIRROR"), ReadLastMirror());

    private static async Task<(string? Base, string? ZipPath)> DownloadWithFallbackAsync(string version, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dsh-node-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmp);
        var zipPath = Path.Combine(tmp, $"node-{version}-win-x64.zip");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-launcher");
        foreach (var baseUrl in BaseUrls(version).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Logger.Info($"downloading portable node from {baseUrl}");
                using var resp = await http.GetAsync(baseUrl + $"/node-{version}-win-x64.zip", ct);
                if (!resp.IsSuccessStatusCode) continue;
                await using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write);
                await resp.Content.CopyToAsync(fs, ct);
                return (baseUrl, zipPath);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* 网络错误/HttpClient 超时 → 换下一个镜像（超时不是用户取消，必须继续回退） */ }
        }
        return (null, null);
    }

    private static async Task<bool> VerifySha256Async(string zipPath, string baseUrl, string version)
    {
        try
        {
            string? sums = null;
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-launcher");
                // 校验和优先从官方 nodejs.org 拉取，与 zip 下载源（可能是第三方镜像）解耦——
                // 避免"镜像被投毒则 zip 与 SHASUMS256 一起被换"的供应链防护失效（E1004）。
                // 官方拉取失败再回退到镜像（保证可用性，但默认走官方保证可信）。
                foreach (var sumsUrl in new[]
                {
                    $"https://nodejs.org/dist/{version}/SHASUMS256.txt",
                    baseUrl + "/SHASUMS256.txt",
                })
                {
                    try
                    {
                        var s = await http.GetStringAsync(sumsUrl);
                        if (!string.IsNullOrWhiteSpace(s)) { sums = s; break; }
                    }
                    catch { /* 尝试下一个源 */ }
                }
            }
            if (string.IsNullOrWhiteSpace(sums)) return false;
            var expected = sums.Split('\n')
                .FirstOrDefault(l => l.TrimEnd().EndsWith($"node-{version}-win-x64.zip", StringComparison.OrdinalIgnoreCase))
                ?.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            if (string.IsNullOrWhiteSpace(expected)) return false;
            await using var fs = File.OpenRead(zipPath);
            var actual = Convert.ToHexString(SHA256.HashData(fs));
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static bool ExtractPortableNode(string zipPath, string version)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"dsh-node-x-{Guid.NewGuid():N}");
        try
        {
            ZipFile.ExtractToDirectory(zipPath, tmp);
            var inner = Path.Combine(tmp, $"node-{version}-win-x64");
            if (!Directory.Exists(inner) || !File.Exists(Path.Combine(inner, "node.exe"))) return false;
            Directory.CreateDirectory(PortableNodeDir);
            foreach (var entry in Directory.GetFileSystemEntries(inner))
            {
                var dest = Path.Combine(PortableNodeDir, Path.GetFileName(entry));
                if (Directory.Exists(entry)) CopyDirectory(entry, dest);
                else File.Copy(entry, dest, overwrite: true);
            }
            return File.Exists(Path.Combine(PortableNodeDir, "node.exe"));
        }
        catch { return false; }
        finally
        {
            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var entry in Directory.GetFileSystemEntries(source))
        {
            var target = Path.Combine(dest, Path.GetFileName(entry));
            if (Directory.Exists(entry)) CopyDirectory(entry, target);
            else File.Copy(entry, target, overwrite: true);
        }
    }

    private static string RuntimeStatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "dsh-launcher", "runtime-state.json");

    private static string? ReadLastMirror()
    {
        try
        {
            if (!File.Exists(RuntimeStatePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(RuntimeStatePath));
            return doc.RootElement.TryGetProperty("lastNodeMirror", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() : null;
        }
        catch { return null; }
    }

    private static void RecordLastMirror(string baseUrl)
    {
        try
        {
            var dir = Path.GetDirectoryName(RuntimeStatePath)!;
            Directory.CreateDirectory(dir);
            var existing = ReadLastMirror() is null ? "{}" : File.ReadAllText(RuntimeStatePath);
            using var doc = JsonDocument.Parse(existing);
            var root = doc.RootElement.Clone();
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var p in root.EnumerateObject())
                    if (!p.NameEquals("lastNodeMirror")) p.WriteTo(writer);
                writer.WriteString("lastNodeMirror", baseUrl);
                writer.WriteEndObject();
            }
            ShellLogic.FileSystemPolicy.AtomicWrite(RuntimeStatePath, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch { /* 记录失败忽略 */ }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}