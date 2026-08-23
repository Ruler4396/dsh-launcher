using System.IO.Compression;
using System.Text.Json;
using DshWeb;
using DshWeb.Domain;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// Outcome Contract：启动崩溃的证据链物理状态（ADR-023）。
/// 不关心内部调用了哪个函数——只验证最终物理状态：
/// ① 失败证据落盘 safe-mode.json 且重载后仍在；② 诊断包含 log/state/errors 三件套。
/// 真实文件系统交互（铁律：禁 Mock OS 边界）。
/// </summary>
public class BootCrashOutcomes : IDisposable
{
    private readonly string _home;

    public BootCrashOutcomes()
    {
        _home = Path.Combine(Path.GetTempPath(), $"bootcrash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
    }

    [Fact]
    public void Failure_Evidence_PersistedToSafeModeState_AndSurvivesReload()
    {
        var storePath = SafeModeState.DefaultStorePath(_home);
        var state = new SafeModeState(storePath);
        var json = JsonSerializer.Serialize(new
        {
            utc = DateTime.UtcNow.ToString("o"),
            code = "E2007",
            summary = "dsh 服务进程异常退出（exit code=1）",
            layers = new object[]
            {
                new { layer = "Process", summary = "dsh 服务进程异常退出", detail = "pid exit code=1", code = "E2007", utc = DateTime.UtcNow.ToString("o") },
                new { layer = "Http", summary = "HTTP 探测回死", detail = "consecutive misses=2", code = "E2004", utc = DateTime.UtcNow.ToString("o") },
            },
        });
        using (var doc = JsonDocument.Parse(json))
        {
            state.RecordFailure(doc.RootElement);
        }
        Assert.True(File.Exists(storePath), "safe-mode-state.json must exist on disk");
        var json2 = File.ReadAllText(storePath);
        using var doc2 = JsonDocument.Parse(json2);
        Assert.True(doc2.RootElement.TryGetProperty("lastFailure", out var lf));
        Assert.Equal("E2007", lf.GetProperty("code").GetString());
        Assert.Equal(2, lf.GetProperty("layers").GetArrayLength());
        Assert.Contains("exit code=1", lf.GetProperty("summary").GetString());

        // 重载（新实例读同一份盘上状态）：证据不丢
        var reloaded = new SafeModeState(storePath);
        Assert.NotNull(reloaded.LastFailure);
        Assert.Equal("E2007", reloaded.LastFailure!.Value.GetProperty("code").GetString());
    }

    [Fact]
    public void Failure_DiagnosticPackage_WrittenWithLogAndState()
    {
        var launcherDir = Path.Combine(_home, "dsh-launcher");
        Directory.CreateDirectory(launcherDir);
        var logPath = Path.Combine(launcherDir, "dsh.log");
        File.WriteAllLines(logPath, new[]
        {
            "{\"ts\":\"2025-01-01 00:00:00\",\"level\":\"INFO\",\"msg\":\"[boot-monitor] started\"}",
            "{\"ts\":\"2025-01-01 00:00:05\",\"level\":\"ERROR\",\"code\":\"E2007\",\"msg\":\"[boot-monitor] FAILED layer=process: dsh 服务进程异常退出\"}",
        });
        File.WriteAllText(Path.Combine(launcherDir, "safe-mode.json"),
            "{\"active\":false,\"tier\":1,\"lastFailure\":{\"code\":\"E2007\",\"summary\":\"process died\"}}");
        var zipPath = Path.Combine(launcherDir, "diagnostics", "boot-failure-test.zip");

        var zip = DiagnoseExport.ExportTo(zipPath, _home, logPath, Logger.Level.Warn, includeVersions: false);
        Assert.NotNull(zip);
        Assert.True(File.Exists(zipPath));
        using var archive = ZipFile.OpenRead(zipPath);
        var entries = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("log-warn.txt", entries);
        Assert.Contains("state.txt", entries);
        Assert.Contains("errors.txt", entries);
        using (var s = archive.GetEntry("log-warn.txt")!.Open())
        using (var r = new StreamReader(s))
        {
            var content = r.ReadToEnd();
            Assert.Contains("E2007", content);
        }
        using (var s = archive.GetEntry("state.txt")!.Open())
        using (var r = new StreamReader(s))
        {
            var content = r.ReadToEnd();
            Assert.Contains("safe-mode.json", content);
            Assert.Contains("E2007", content);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
    }
}
