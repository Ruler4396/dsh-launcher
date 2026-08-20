using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DshWeb;

/// <summary>
/// 一键诊断导出（v0.3.0）：DshWeb.exe --diagnose [--min-level warn|error]
/// 把统一日志（按级别过滤可选）、环境信息、版本、错误码汇总打包成脱敏 zip 放到"下载"文件夹。
/// 绝不含 .credentials.yaml / 会话 / 存储 / 插件内容；产物由用户自主上传（延续"无遥测"承诺）。
/// </summary>
public static class DiagnoseExport
{
    /// <summary>入口（在 Main 最早期调用，不初始化 UI）。返回写入的 zip 路径；失败返回 null。</summary>
    public static string? Run(string[] args, string dshHomeDir, string logPath)
    {
        try
        {
            var downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!Directory.Exists(downloads)) downloads = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var minLevel = ParseMinLevel(args); // null = 全量
            var zipPath = Path.Combine(downloads, $"dsh-launcher-diagnose-{DateTime.Now:yyyyMMdd-HHmmss}.zip");

            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                if (minLevel is null)
                    AddTextEntry(zip, "log-full.txt", TailLines(logPath, 3000));
                else
                    AddTextEntry(zip, "log-warn.txt", FilterByLevel(logPath, minLevel.Value));

                AddTextEntry(zip, "env.txt", CollectEnv(dshHomeDir));
                AddTextEntry(zip, "versions.txt", CollectVersions());
                AddTextEntry(zip, "settings.txt", ReadSafe(Path.Combine(dshHomeDir, "dsh-launcher", "settings.json")));
                AddTextEntry(zip, "state.txt", CollectState(dshHomeDir));
                AddTextEntry(zip, "errors.txt", SummarizeErrors(logPath));
            }
            return zipPath;
        }
        catch (Exception ex)
        {
            Logger.Error("diagnostic export failed: " + ex.Message, ErrorCodes.E5001);
            return null;
        }
    }

    internal static Logger.Level? ParseMinLevel(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--min-level", StringComparison.OrdinalIgnoreCase)) continue;
            var v = args[i + 1].Trim().ToLowerInvariant();
            if (v is "warn" or "warning") return Logger.Level.Warn;
            if (v == "error") return Logger.Level.Error;
            return null;
        }
        return null;
    }

    /// <summary>按级别过滤统一日志：JSON 行按 level 字段；原始服务输出命中启动错误标志按告警计。
    /// 质量治理修复：输出行统一过 Sanitize（日志主体此前漏脱敏）。</summary>
    internal static string FilterByLevel(string logPath, Logger.Level minLevel)
    {
        if (!File.Exists(logPath)) return "（统一日志不存在：" + logPath + "）";
        var sb = new StringBuilder();
        foreach (var raw in ShellLogic.ReadLinesShared(logPath))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            var level = TryGetJsonLevel(line);
            if (level is not null)
            {
                if (level >= minLevel) sb.AppendLine(Sanitize(line));
            }
            else if (minLevel <= Logger.Level.Warn && ShellLogic.ServiceReadiness.LogShowsStartupError(line))
            {
                sb.AppendLine(Sanitize(line));
            }
        }
        return sb.Length == 0 ? "（无告警/错误记录）" : sb.ToString();
    }

    internal static Logger.Level? TryGetJsonLevel(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("level", out var l))
            {
                var s = l.GetString()?.ToUpperInvariant();
                if (s == "ERROR") return Logger.Level.Error;
                if (s == "WARN") return Logger.Level.Warn;
                if (s == "INFO") return Logger.Level.Info;
            }
        }
        catch { /* 非 JSON 行 */ }
        return null;
    }

    /// <summary>日志尾部若干行（大文件不整读）。质量治理修复：输出行统一过 Sanitize（脱敏）。
    /// v0.3.1 修复：必须用共享读打开——运行中的 dsh 服务（cmd >> 重定向）以独占共享模式持有
    /// dsh.log，File.ReadLines 默认 FileShare.Read 会被拒（IOException），导致服务运行期间
    /// --diagnose 必然失败（22 字节空 zip + E5001 写不进被锁日志）。
    /// P1-1（质量治理）：读取实现与 ShellLogic.ReadLogTail 合一（共享读 + 流式尾部），消除双实现。</summary>
    internal static string TailLines(string logPath, int maxLines)
    {
        if (!File.Exists(logPath)) return "（统一日志不存在：" + logPath + "）";
        var sb = new StringBuilder();
        foreach (var l in ShellLogic.ReadLogTail(logPath, maxLines))
            sb.AppendLine(Sanitize(l));
        return sb.ToString();
    }

    private static string CollectEnv(string dshHomeDir)
    {
        var sb = new StringBuilder();
        sb.AppendLine("dsh-home=" + dshHomeDir);
        foreach (var key in new[] { "DSH_WEB_URL", "DSH_WEB_PORT", "DSH_VERSION", "DSH_LOG_LEVEL", "DSH_NODE_VERSION", "DSH_NODE_MIRROR", "DSH_HOME" })
        {
            var v = Environment.GetEnvironmentVariable(key);
            sb.AppendLine((string.IsNullOrWhiteSpace(v) ? key + "=" : key + "=" + Sanitize(v)));
        }
        return sb.ToString();
    }

    private static string CollectVersions()
    {
        var sb = new StringBuilder();
        sb.AppendLine("node: " + RunCapture("node", "--version"));
        sb.AppendLine("npm: " + RunCapture("npm", "--version"));
        sb.AppendLine("dotnet runtimes:");
        foreach (var l in RunCaptureLines("dotnet", "--list-runtimes"))
        {
            if (l.Contains("WindowsDesktop", StringComparison.OrdinalIgnoreCase)) sb.AppendLine("  " + l);
        }
        sb.AppendLine("webview2: " + (ShellLogic.RuntimeConfig.ReadWebView2Version() ?? "（未检测到 Evergreen WebView2 版本注册表项）"));
        return sb.ToString();
    }

    private static string CollectState(string dshHomeDir)
    {
        var dir = Path.Combine(dshHomeDir, "dsh-launcher");
        var sb = new StringBuilder();
        foreach (var name in new[] { "window-state.json", "pending-update.json", "runtime-state.json", "theme.json" })
        {
            var p = Path.Combine(dir, name);
            if (File.Exists(p))
            {
                sb.AppendLine("--- " + name + " ---");
                sb.AppendLine(Sanitize(File.ReadAllText(p)));
            }
        }
        return sb.ToString();
    }

    /// <summary>按错误码汇总错误目录中出现的错误：每个码出现次数 + 首条消息（诊断排序依据）。</summary>
    internal static string SummarizeErrors(string logPath)
    {
        if (!File.Exists(logPath)) return "（无日志）";
        var counts = new Dictionary<string, (int Count, string FirstMsg)>(StringComparer.Ordinal);
        foreach (var raw in ShellLogic.ReadLinesShared(logPath))
        {
            if (!raw.TrimStart().StartsWith('{')) continue;
            try
            {
                using var doc = JsonDocument.Parse(raw);
                if (!doc.RootElement.TryGetProperty("code", out var code)
                    || code.ValueKind != JsonValueKind.String) continue;
                var c = code.GetString() ?? "?";
                var msg = doc.RootElement.TryGetProperty("msg", out var m) && m.ValueKind == JsonValueKind.String
                    ? m.GetString() ?? "" : "";
                if (counts.TryGetValue(c, out var cur)) counts[c] = (cur.Count + 1, cur.FirstMsg);
                else counts[c] = (1, msg);
            }
            catch { /* 跳过非 JSON 行 */ }
        }
        if (counts.Count == 0) return "（无错误码记录）";
        var sb = new StringBuilder();
        foreach (var kv in counts.OrderByDescending(k => k.Value.Count))
        {
            sb.AppendLine($"[{kv.Key}] x{kv.Value.Count}  {ErrorCodes.Describe(kv.Key)}");
            if (!string.IsNullOrWhiteSpace(kv.Value.FirstMsg)) sb.AppendLine("    例: " + Sanitize(kv.Value.FirstMsg));
        }
        return sb.ToString();
    }

    private static void AddTextEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content ?? "");
    }

    private static string ReadSafe(string path)
    {
        try { return File.Exists(path) ? Sanitize(File.ReadAllText(path)) : "（文件不存在）"; }
        catch (Exception ex) { return "（读取失败: " + ex.Message + "）"; }
    }

    /// <summary>脱敏：把用户目录及可推导的用户相关信息替换为占位符，避免日志/路径泄漏用户名。
    /// 顺序（均 OrdinalIgnoreCase）：
    ///   1) 当前用户目录全路径 → %USER%（保留原文覆盖）；
    ///   2) %USERPROFILE% 字面量 → %USER%（环境变量形式出现的路径）；
    ///   3) 波浪号缩写 "~\" → "%USER%\"（仅替换反斜杠后缀形式，避免误伤普通波浪号文本；
    ///      独立 "~" token 不替换，因其可能是正常文本中与路径无关的波浪号）；
    ///   4) 反斜杠分隔的 "…\用户名\" 路径片段 → "…\USERNAME\"（正则只在反斜杠之后才匹
    ///      配用户名，且要求其后紧跟反斜杠，故只命中路径上下文，防过度替换普通文本中的
    ///      用户名单词）。</summary>
    internal static string Sanitize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? "";
        try
        {
            var up = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(up))
            {
                // 1) 全路径 — 覆盖默认形式 C:\Users\xxx
                s = s.Replace(up, "%USER%", StringComparison.OrdinalIgnoreCase);
                // 2) 环境变量字面量形式
                s = s.Replace("%USERPROFILE%", "%USER%", StringComparison.OrdinalIgnoreCase);
                // 3) 波浪号缩写形式（仅 "~\"，保守安全）
                s = s.Replace("~\\", "%USER%\\", StringComparison.OrdinalIgnoreCase);
                // 4) 用户名路径片段：仅 \用户名\ 上下文（用户名可能出现在 profile 名/短路径
                //    等未被全路径替换覆盖到的位置）。用户名取用户目录最后一段。
                var userName = Path.GetFileName(up.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                if (!string.IsNullOrEmpty(userName))
                {
                    // 模式：反斜杠 + 用户名 +（后随反斜杠，用前瞻保留它）；OrdinalIgnoreCase。
                    s = Regex.Replace(s, "\\\\" + Regex.Escape(userName) + "(?=\\\\)", "\\\\USERNAME",
                        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                }
            }
        }
        catch { }
        return s;
    }

    private static string RunCapture(string file, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return "（无法启动）";
            var readTask = p.StandardOutput.ReadToEndAsync(); // 后台排空管道，防止挂死阻塞
            if (!p.WaitForExit(4000))
            {
                try { p.Kill(); p.WaitForExit(); } catch { } // 超时杀进程防泄漏
                return "（执行超时）";
            }
            var outText = readTask.Result.Trim();
            return string.IsNullOrWhiteSpace(outText) ? "（无输出）" : outText;
        }
        catch (Exception ex) { return "（" + ex.Message + "）"; }
    }

    private static string[] RunCaptureLines(string file, string args)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return Array.Empty<string>();
            var lines = p.StandardOutput.ReadToEnd().Split('\n');
            p.WaitForExit(4000);
            return lines;
        }
        catch { return Array.Empty<string>(); }
    }
}