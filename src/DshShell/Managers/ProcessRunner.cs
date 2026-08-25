using System.ComponentModel;

namespace DshWeb.Managers;

/// <summary>
/// 进程执行原语（ADR-024 双轨制收敛：从 Program.cs 整体迁出）——
/// npm/pnpm 的唯一执行点与底层捕获执行器。
///
/// 铁律边界：
/// - [ADR-021] node.exe 直接执行 .js 入口，严禁 cmd.exe/.cmd shim 中间层；
/// - 三必须：stdout/stderr 重定向 + 异步排空（逐行转发 progress）、限时等待、
///   超时/取消 <c>Kill(entireProcessTree)</c> 清理僵尸树；
/// - 本类零 UI 依赖（进度经回调上抛，弹窗/标题栏由调用方驱动）。
/// </summary>
internal static class ProcessRunner
{
    /// <summary>
    /// npm 源序列（快源优先）：DSH_NPM_MIRROR（若设）→ npmmirror → 官方默认。
    /// 策略见 ShellLogic.NpmRegistryPolicy（契约测试锁定）。pack/build/apply 共用同一序列
    /// （防跨 registry cache miss）。</summary>
    internal static string[] GetNpmRegistrySources()
        => ShellLogic.NpmRegistryPolicy.RegistrySources(
            Environment.GetEnvironmentVariable("DSH_NPM_MIRROR"));

    /// <summary>
    /// 按 npm 源序列依次尝试（优先最快可达的源，失败才降级下一个）。
    /// run(sourceIndex)：用 sources[sourceIndex] 的 --registry 参数执行一次，返回是否成功。
    /// 成功来源记入日志；winningIndex 返回首个成功源下标（-1=全部失败），供同流程
    /// 后续步骤粘住同一源（pack 与 build 同源，保证依赖解析与缓存命中一致）。
    /// [2026-08 用户回归：npmjs 直连不稳且慢 → npmmirror 优先]
    /// </summary>
    internal static bool TryNpmOverRegistries(string[] sources, Func<int, bool> run, string opName, out int winningIndex)
    {
        for (var i = 0; i < sources.Length; i++)
        {
            if (run(i))
            {
                winningIndex = i;
                if (i > 0)
                    Logger.Info($"npm op '{opName}' succeeded via registry source #{i} (fallback)");
                return true;
            }
        }
        winningIndex = -1;
        Logger.Warn($"npm op '{opName}' failed on all {sources.Length} registries");
        return false;
    }

    /// <summary>运行 npm 命令（唯一 npm 执行点）。**直接调用 node.exe 执行 npm-cli.js**——
    /// 彻底抛弃 npm.cmd/npm.bat 依赖与 cmd.exe /c 包装，根除 .cmd 编码冲突
    /// （chcp 65001 无效）与 cmd /c 引号剥离（ERROR_INVALID_NAME）两类陷阱。
    /// 链路：RuntimeResolver.ResolveExisting() → node.exe 绝对路径 → JsEntryResolver.ResolveNpmCliJs →
    /// node.exe "npm-cli.js" args（UseShellExecute=false + 双编码 UTF-8）。
    /// <paramref name="ct"/> 取消时**立即 Kill 进程树**返回 false（Splash 取消立即生效）。
    /// <paramref name="timeoutMs"/> 默认 120s；预热放宽 180s，超时强制 kill 保留 tarball。
    /// <paramref name="progress"/> 逐行转发 npm 实时日志到 Splash（滚动消除卡死焦虑）。
    /// <paramref name="workingDirectory"/> 供预热（./&lt;tarball&gt;、--prefix ./deps 相对路径）。</summary>
    internal static bool RunNpmCommand(string args, out string errorTail, CancellationToken ct = default,
        Action<string>? progress = null, int timeoutMs = 120000, string? workingDirectory = null)
    {
        errorTail = "";
        try
        {
            // 1) node.exe：RuntimeResolver 三源解析（PATH/注册表/便携），找不到直接明确报错
            var nodeEnv = RuntimeResolver.ResolveExisting();
            if (nodeEnv?.NodeExe is null || !File.Exists(nodeEnv.NodeExe))
            {
                errorTail = "未检测到可用的 Node.js 环境。请安装 Node.js 18+ 后重试。";
                return false;
            }
            // 2) npm-cli.js：两优先级探测，找不到明确报错
            var npmCliJs = Domain.JsEntryResolver.ResolveNpmCliJs(nodeEnv.NodeExe);
            if (npmCliJs is null || !File.Exists(npmCliJs))
            {
                Logger.Error($"node.exe found at {nodeEnv.NodeExe} but npm-cli.js not found", ErrorCodes.E4001);
                errorTail = "已找到 Node.js 但未找到 npm-cli.js，请重新安装 Node.js。";
                return false;
            }
            // 3) 降维打击：node.exe 直接执行 npm-cli.js，绕过 .cmd/.bat/cmd.exe 全部陷阱。
            //    node 输出统一 UTF-8（npm ≥7 内部即 UTF-8），双编码显式设置保证任何代码页可读。
            // 统一走底层执行器（RunProcessCaptured）：UTF-8 捕获 + 超时 kill 僵尸树 + 取消。
            return RunProcessCaptured(nodeEnv.NodeExe, $"\"{npmCliJs}\" {args}",
                out errorTail, ct, progress, timeoutMs, workingDirectory);
        }
        catch (Win32Exception ex)
        {
            // node.exe 启动失败（CreateProcess 异常）：转明确 Node 环境提示而非裸异常
            errorTail = "无法启动 Node.js（" + ex.Message + "）。请确保已安装 Node.js 18+。";
            return false;
        }
        catch (Exception ex)
        {
            errorTail = "系统级执行异常: " + ex.Message;
            Logger.Error("RunNpmCommand fatal: " + ex);
            return false;
        }
    }

    /// <summary>
    /// 底层进程执行器（真实 OS 交互测试的核心目标，SDET 支柱一）：启动任意可执行文件，
    /// **UTF-8 双编码捕获** stdout/stderr + 逐行实时转发 progress + 超时/取消强杀进程树。
    /// <paramref name="fileName"/> = 可执行文件绝对路径；其余语义同 RunNpmCommand。
    /// </summary>
    internal static bool RunProcessCaptured(
        string fileName, string arguments, out string outputTail,
        CancellationToken ct = default, Action<string>? progress = null,
        int timeoutMs = 120000, string? workingDirectory = null)
    {
        outputTail = "";
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = workingDirectory,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            // 逐行读取 stdout/stderr 实时转发到 Splash（异步事件，不阻塞主循环）
            var outLines = new List<string>();
            var errLines = new List<string>();
            var outLock = new object();
            var errLock = new object();
            p.OutputDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                lock (outLock) outLines.Add(e.Data);
                progress?.Invoke(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (string.IsNullOrEmpty(e.Data)) return;
                lock (errLock) errLines.Add(e.Data);
                progress?.Invoke(e.Data);
            };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            // WaitForExit 期间可被外部取消：注册回调 Kill 进程树，避免"点取消无效"
            using var reg = ct.Register(() =>
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* 尽力 */ }
            });
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* 尽力 */ }
                outputTail = "执行超时 (" + (timeoutMs / 1000) + "s)";
                return false;
            }
            if (ct.IsCancellationRequested) return false;
            // 事件回调可能落后于 WaitForExit 返回，短暂同步读一次剩余流（防 outputTail 缺行）
            var combined = "";
            lock (outLock) combined += string.Join("\n", outLines);
            lock (errLock) { if (combined.Length > 0) combined += "\n"; combined += string.Join("\n", errLines); }
            var lines = combined.Split('\n')
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();
            if (lines.Count > 0)
                outputTail = string.Join("\n", lines.Skip(Math.Max(0, lines.Count - 6)));
            return p.ExitCode == 0;
        }
        catch (Win32Exception ex)
        {
            outputTail = "无法启动进程（" + ex.Message + "）。请确保目标可执行文件存在且可访问。";
            return false;
        }
        catch (Exception ex)
        {
            outputTail = "系统级执行异常: " + ex.Message;
            Logger.Error("RunProcessCaptured fatal: " + ex);
            return false;
        }
    }

    /// <summary>pnpm 安装（机会主义加速，绝不安装 pnpm）。超时 10 分钟。
    /// 使用 node.exe 直接执行 pnpm.cjs，彻底绕过 .cmd shim 和 cmd.exe。
    /// [ADR-021] 使用 --reporter=ndjson 获取精确进度（按 packageId 自归一化，见 UpdateProgress）。
    /// 注意：--no-audit --no-fund 是 npm 专用参数，pnpm 不支持，不能传。
    /// ERR_PNPM_IGNORED_BUILDS（exit=1）表示包已安装但 build scripts 被安全策略阻止。</summary>
    internal static bool RunPnpmInstall(string nodeExe, string pnpmEntryJs, string tarballPath, string buildDir,
        Action<int>? progressCallback = null, string? registryArgs = null)
    {
        try
        {
            // [Fix] 在 buildDir 创建干净的 package.json，防止 pnpm 向上查找父目录的
            // stale package.json（测试遗留的 file: 引用会导致 ENOENT）
            var buildPkgJson = Path.Combine(buildDir, "package.json");
            if (!File.Exists(buildPkgJson))
                File.WriteAllText(buildPkgJson, """{"name":"dsh-runtime-build","version":"1.0.0","private":true}""");

            // [ADR-021] 使用 node.exe 直接执行 pnpm.cjs + --reporter=ndjson 获取精确进度；
            // [--ignore-workspace 铁律] pnpm 会从 buildDir 向上查找 pnpm-workspace.yaml——
            // 一旦用户主目录等祖先路径存在游离工作区清单，整个安装会被劫持到那个根。
            // 此参数强制以 buildDir 自身为项目根，与任何外部工作区彻底隔离。
            //
            // [--config.node-linker=hoisted 铁律] pnpm 默认 linker 用【绝对路径】junction
            // 链接顶层包——构建完成后 Apply 的 Directory.Move 会把树搬进 runtimes\<ver>，
            // 所有 junction 立即悬空 → 服务起不来。hoisted 布局全部真实文件，移动安全。
            var arguments = $"\"{pnpmEntryJs}\" install \"{tarballPath}\" --ignore-workspace"
                + " --config.node-linker=hoisted --reporter=ndjson"
                + (registryArgs ?? GetNpmRegistrySources()[0]);

            var psi = new System.Diagnostics.ProcessStartInfo(nodeExe, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = buildDir,
            };
            psi.EnvironmentVariables["COREPACK_ENABLE_DOWNLOAD_PROMPT"] = "0";
            psi.EnvironmentVariables["PATH"] = GetMergedPath();
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;

            // 流式解析 ndjson 进度（不阻塞到进程结束）。
            // [2026-08 回归修复] 按 packageId 自归一化（ShellLogic.UpdateProgress 聚合器）。
            var aggregator = new ShellLogic.UpdateProgress.PnpmAggregator();
            var errorOutput = "";
            // stdout 全文留存：ERR_PNPM_IGNORED_BUILDS 标记在 pnpm v11 走 stdout
            // ndjson error 事件（stderr 为空），分类必须双流参与（见 UpdateProgress 契约）。
            var stdoutBuilder = new System.Text.StringBuilder();

            // 后台读取 stderr
            var stderrTask = System.Threading.Tasks.Task.Run(() =>
            {
                try { errorOutput = p.StandardError.ReadToEnd(); } catch { }
            });

            // 主线程逐行解析 stdout ndjson
            try
            {
                while (!p.StandardOutput.EndOfStream)
                {
                    var line = p.StandardOutput.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    stdoutBuilder.AppendLine(line);
                    aggregator.OnLine(line);
                    if (progressCallback is not null)
                    {
                        var (percent, hasData) = aggregator.Snapshot();
                        if (hasData) progressCallback(percent); // 无数据回退脉冲模式（不显示伪百分比）
                    }
                }
            }
            catch { /* 流读取中断 */ }

            stderrTask.Wait(1000);
            p.WaitForExit(600000); // 10 分钟超时兜底

            // ERR_PNPM_IGNORED_BUILDS（exit=1）：包已安装，只是 build scripts 被安全策略阻止
            if (ShellLogic.UpdateProgress.IsPnpmIgnoredBuildsExit(p.ExitCode, stdoutBuilder.ToString(), errorOutput))
            {
                Logger.Info($"pnpm install: packages installed but build scripts ignored (exit=1)");
                return true; // 视为成功
            }

            if (p.ExitCode != 0)
            {
                Logger.Warn($"pnpm install failed: exit={p.ExitCode}, stderr={errorOutput}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn($"pnpm install error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 合并注册表中的系统 PATH + 用户 PATH + 当前进程 PATH。
    /// GUI 进程（如 Explorer 启动的 DshWeb.exe）的 PATH 可能不包含
    /// %APPDATA%\npm 等用户级路径，导致 where pnpm 失败。
    /// </summary>
    private static string GetMergedPath()
    {
        var current = Environment.GetEnvironmentVariable("PATH") ?? "";
        var systemPath = "";
        var userPath = "";
        try
        {
            using var sysKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            systemPath = sysKey?.GetValue("PATH", "") as string ?? "";
        }
        catch { }
        try
        {
            using var userKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Environment");
            userPath = userKey?.GetValue("PATH", "") as string ?? "";
        }
        catch { }

        // 合并去重：系统 → 用户 → 当前进程
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var merged = new List<string>();
        foreach (var part in (systemPath + ";" + userPath + ";" + current).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0 && seen.Add(trimmed))
                merged.Add(trimmed);
        }
        return string.Join(Path.PathSeparator, merged);
    }

    /// <summary>递归删除目录（幂等）；失败静默（清理临时目录不阻塞主流程）。</summary>
    internal static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { /* 清理失败忽略 */ }
    }
}
