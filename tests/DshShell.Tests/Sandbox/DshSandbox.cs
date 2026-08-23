using System.Diagnostics;
using System.Text.Json;

namespace DshShell.Tests.Sandbox;

/// <summary>
/// dsh 沙盒测试环境：创建隔离的 DSH_HOME 目录，用于安全模式等破坏性测试。
/// 不影响当前运行的 dsh 实例。
/// </summary>
public sealed class DshSandbox : IDisposable
{
    public string SandboxRoot { get; }
    public string DshHome { get; }
    public string LauncherDataDir { get; }
    public string ProfilesDir { get; }
    public string LogPath { get; }

    public DshSandbox()
    {
        SandboxRoot = Path.Combine(Path.GetTempPath(), $"dsh-sandbox-{Guid.NewGuid():N}");
        DshHome = Path.Combine(SandboxRoot, ".dsh");
        LauncherDataDir = Path.Combine(DshHome, "dsh-launcher");
        ProfilesDir = Path.Combine(DshHome, "profiles");
        LogPath = Path.Combine(LauncherDataDir, "dsh.log");

        Directory.CreateDirectory(DshHome);
        Directory.CreateDirectory(LauncherDataDir);
        Directory.CreateDirectory(ProfilesDir);
    }

    /// <summary>安装一个会导致崩溃的模拟插件到 web profile。</summary>
    public void InstallBrokenPlugin(string pluginName, string crashScript)
    {
        var profileDir = Path.Combine(ProfilesDir, "web");
        var pluginsDir = Path.Combine(profileDir, "node_modules", pluginName);
        Directory.CreateDirectory(pluginsDir);

        // 创建 package.json
        var pkgJson = new
        {
            name = pluginName,
            version = "0.0.1",
            main = "index.js"
        };
        File.WriteAllText(
            Path.Combine(pluginsDir, "package.json"),
            JsonSerializer.Serialize(pkgJson, new JsonSerializerOptions { WriteIndented = true }));

        // 创建会导致崩溃的入口文件
        File.WriteAllText(
            Path.Combine(pluginsDir, "index.js"),
            crashScript);

        // 更新 profile 的 package.json
        var profilePkg = new
        {
            name = "web",
            version = "1.0.0",
            dependencies = new Dictionary<string, string>
            {
                [pluginName] = "file:./node_modules/" + pluginName
            }
        };
        File.WriteAllText(
            Path.Combine(profileDir, "package.json"),
            JsonSerializer.Serialize(profilePkg, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>安装一个正常的插件（不崩溃）。</summary>
    public void InstallWorkingPlugin(string pluginName, string initScript)
    {
        var profileDir = Path.Combine(ProfilesDir, "web");
        var pluginsDir = Path.Combine(profileDir, "node_modules", pluginName);
        Directory.CreateDirectory(pluginsDir);

        var pkgJson = new
        {
            name = pluginName,
            version = "0.0.1",
            main = "index.js"
        };
        File.WriteAllText(
            Path.Combine(pluginsDir, "package.json"),
            JsonSerializer.Serialize(pkgJson, new JsonSerializerOptions { WriteIndented = true }));

        File.WriteAllText(
            Path.Combine(pluginsDir, "index.js"),
            initScript);
    }

    /// <summary>写入 settings.json 配置。</summary>
    public void WriteSettings(object settings)
    {
        File.WriteAllText(
            Path.Combine(LauncherDataDir, "settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>读取环境变量（沙盒进程内）。</summary>
    public string? GetEnvironmentVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name);
    }

    /// <summary>读取日志内容。</summary>
    public string GetLogContent()
    {
        return File.Exists(LogPath) ? File.ReadAllText(LogPath) : "";
    }

    /// <summary>检查日志中是否包含指定内容。</summary>
    public bool LogContains(string text)
    {
        var content = GetLogContent();
        return content.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>清理沙盒目录。</summary>
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(SandboxRoot))
                Directory.Delete(SandboxRoot, recursive: true);
        }
        catch { /* 清理失败忽略 */ }
    }
}
