namespace DshWeb.Managers;

/// <summary>
/// WebView2 运行时缺失兜底安装器（自 Program.cs 迁出，逻辑保持不变）：
/// 下载 Evergreen Bootstrapper（官方固定链接，约 2MB）静默安装后重测；
/// 任何一步失败返回 false（调用方回退 E1006 弹窗）。不内嵌 runtime。
/// 质量治理 P1-5：各失败分支写入结构化日志（区分 已装/下载失败/安装失败/超时）。
/// </summary>
internal static class WebRuntimeInstaller
{
    /// <summary>统一 HttpClient 工厂（网络调用的唯一出口；UserAgent 标识 dsh-launcher）。</summary>
    internal static System.Net.Http.HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var http = new System.Net.Http.HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("dsh-launcher");
        return http;
    }

    internal static async Task<bool> TryInstallWebView2Async()
    {
        try
        {
            if (ShellLogic.RuntimeConfig.ReadWebView2Version() is not null) return true;
            var boot = Path.Combine(Path.GetTempPath(), "dsh-wv2-bootstrapper.exe");
            try
            {
                using var http = CreateHttpClient(TimeSpan.FromSeconds(120));
                using var resp = await http.GetAsync("https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                if (!resp.IsSuccessStatusCode)
                {
                    Logger.Error($"webview2 bootstrapper download failed: HTTP {(int)resp.StatusCode}",
                        ErrorCodes.E1006, new { stage = "download" });
                    return false;
                }
                await using var fs = new FileStream(boot, FileMode.Create, FileAccess.Write);
                await resp.Content.CopyToAsync(fs);
            }
            catch (Exception ex)
            {
                Logger.Error("webview2 bootstrapper download error: " + ex.Message, ErrorCodes.E1006, new { stage = "download" });
                return false;
            }
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(boot, "/silent /install")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                if (p is null)
                {
                    Logger.Error("webview2 bootstrapper failed to start", ErrorCodes.E1006, new { stage = "install" });
                    return false;
                }
                if (!p.WaitForExit(120000))
                {
                    try { p.Kill(entireProcessTree: true); } catch { }
                    Logger.Error("webview2 bootstrapper install timed out", ErrorCodes.E1006, new { stage = "install", timeout = 120000 });
                    return false;
                }
                var ok = p.ExitCode == 0 && ShellLogic.RuntimeConfig.ReadWebView2Version() is not null;
                if (!ok)
                    Logger.Error($"webview2 bootstrapper install failed: exit={p.ExitCode}",
                        ErrorCodes.E1006, new { stage = "install", exitCode = p.ExitCode });
                return ok;
            }
            catch (Exception ex)
            {
                Logger.Error("webview2 bootstrapper install error: " + ex.Message, ErrorCodes.E1006, new { stage = "install" });
                return false;
            }
            finally
            {
                try { if (File.Exists(boot)) File.Delete(boot); } catch { }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("webview2 fallback install error: " + ex.Message, ErrorCodes.E1006, new { stage = "unknown" });
            return false;
        }
    }

    /// <summary>用系统默认程序打开 URL/路径（UseShellExecute 直启；打开失败静默——
    /// 纯增值动作，绝不反噬主流程）。集中于此以免散落的 Process.Start 调用点。</summary>
    internal static void OpenExternally(string target)
    {
        try
        {
            _ = System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch { /* 打开失败忽略 */ }
    }
}
