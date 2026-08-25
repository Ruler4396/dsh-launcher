using Microsoft.Win32;

namespace DshWeb.Managers;

/// <summary>
/// 应用环境维护（ADR-024 双轨制收敛：自 Program.cs 整体迁出）——
/// 旧版数据迁移、ProgramData 残留清理、自启落地、旧版本/孤儿快捷方式清理、
/// settings.json 生命周期模式解析。全部机器级副作用由调用方以
/// <see cref="ShellLogic.RuntimeConfig.IsSandboxMode"/>（或等价门控）把关后进入。
/// 本类零 UI 依赖（弹窗决策经回调上抛）。
/// </summary>
internal static class AppEnvironment
{
    /// <summary>dsh 主目录：DSH_HOME 环境变量，未设置时 ~/.dsh。</summary>
    internal static string DshHomeDir =>
        Environment.GetEnvironmentVariable("DSH_HOME") is { Length: > 0 } env
            ? env
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");

    /// <summary>壳的数据目录（settings.json / 统一日志 / service-pid 等）：DSH_HOME\dsh-launcher。</summary>
    internal static string DataDir => Path.Combine(DshHomeDir, "dsh-launcher");

    internal static string SettingsPath => Path.Combine(DataDir, "settings.json");

    /// <summary>读取文件文本（容错，失败返回 null）。</summary>
    internal static string? SafeReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 检测系统应用深色模式（注册表 AppsUseLightTheme=0）。
    /// [ADR-024] 自 Program.cs 迁出：注册表读取属环境探查，组合根只保留薄转发。
    /// </summary>
    internal static bool IsSystemDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 启动时迁移旧版数据（%LOCALAPPDATA%\dsh-launcher → DSH_HOME\dsh-launcher）：
    /// settings.json 保留用户的选择；旧文件迁移后删除，避免卸载后残留。
    /// </summary>
    internal static void MigrateLegacyData()
    {
        try
        {
            var legacyDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dsh-launcher");
            var newDir = DataDir;
            if (!Directory.Exists(legacyDir) || string.Equals(legacyDir, newDir, StringComparison.OrdinalIgnoreCase))
                return;
            Directory.CreateDirectory(newDir);

            var legacySettings = Path.Combine(legacyDir, "settings.json");
            var newSettings = Path.Combine(newDir, "settings.json");
            if (File.Exists(legacySettings) && !File.Exists(newSettings))
            {
                try { File.Copy(legacySettings, newSettings); } catch { /* 复制失败保留旧文件 */ }
            }

            // 清理旧目录（shell.log / service-pid 等历史文件一并删除，无残留）
            foreach (var file in Directory.GetFiles(legacyDir))
            {
                try { File.Delete(file); } catch { /* 被占用则跳过 */ }
            }
            try { if (Directory.GetFiles(legacyDir).Length == 0) Directory.Delete(legacyDir); } catch { }
        }
        catch
        {
            // 迁移失败不影响启动
        }
    }

    /// <summary>清理卸载后 ProgramData 范围外的空目录残留：安装用 FolderPicker 会在
    /// C:\ProgramData\dsh-launcher 创建中转文件（picked.txt），卸载不删该目录；目录为空
    /// 时顺手清掉，非空则不动。</summary>
    internal static void CleanupProgramDataResidue()
    {
        // 沙盒早退（铁律双保险：调用点已门控，副作用体内再设一道防线）
        if (ShellLogic.RuntimeConfig.IsSandboxMode) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "dsh-launcher");
            if (!Directory.Exists(dir)) return;
            // 只清理中转文件与空目录；不删除任何非本产品文件
            var picked = Path.Combine(dir, "picked.txt");
            if (File.Exists(picked))
            {
                try { File.Delete(picked); } catch { /* 占用则跳过 */ }
            }
            try
            {
                if (Directory.GetFiles(dir).Length == 0 && Directory.GetDirectories(dir).Length == 0)
                    Directory.Delete(dir);
            }
            catch { /* 删除失败（可能有其他用户文件/占用）不动 */ }
        }
        catch
        {
            // 清理失败不影响启动
        }
    }

    /// <summary>自启落地：MSI 勾选自启时只在 HKLM 写机器级意图标志（AutoStartWanted=1），
    /// 本方法在壳启动时读标志、以当前用户身份补写 HKCU Run（指向 DshWeb.exe 拉壳方案）。
    /// 升级/自定义目录导致的路径变化自动更新。<paramref name="trace"/> 组合根轨迹日志。</summary>
    internal static void EnsureAutoStartRequested(Action<string>? trace = null)
    {
        // 沙盒早退（铁律双保险：调用点已门控，副作用体内再设一道防线）
        if (ShellLogic.RuntimeConfig.IsSandboxMode) { trace?.Invoke("autostart skipped (sandbox mode)"); return; }
        try
        {
            var wanted = false;
            try
            {
                using var flagKey = Registry.LocalMachine.OpenSubKey(@"Software\dsh-launcher");
                wanted = flagKey?.GetValue("AutoStartWanted") is int v && v == 1;
            }
            catch { /* 读不到按无标志处理（便携版/未勾选） */ }
            if (!wanted) return;

            // 自启直接拉起壳（登录即见窗口）：壳自行按 Identity 探测/拉起 dsh 服务。
            var exe = Path.Combine(AppContext.BaseDirectory, "DshWeb.exe");
            if (!File.Exists(exe)) return; // 自身路径异常时不写
            var expected = "\"" + exe + "\"";

            using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            var cur = run.GetValue("dsh-launcher") as string;
            if (string.Equals(cur, expected, StringComparison.OrdinalIgnoreCase)) return;
            run.SetValue("dsh-launcher", expected, RegistryValueKind.String);
            trace?.Invoke("autostart: " + (cur is null ? "created" : "updated") + " HKCU Run entry (HKLM AutoStartWanted=1)");
        }
        catch (Exception ex)
        {
            trace?.Invoke("autostart ensure failed: " + ex);
        }
    }

    /// <summary>settings.json 的 serviceLifetime 字段抹除（插件缺失降级；只改字段不动其他内容）。</summary>
    internal static void PurgeServiceLifetime(string path)
    {
        try
        {
            var text = SafeReadText(path);
            if (string.IsNullOrWhiteSpace(text)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return;
            using var stream = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream, new System.Text.Json.JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                    if (!prop.NameEquals("serviceLifetime")) prop.WriteTo(writer);
                writer.WriteEndObject();
            }
            File.WriteAllText(path, System.Text.Encoding.UTF8.GetString(stream.ToArray()));
        }
        catch { /* 抹除失败幂等：下次启动再判 */ }
    }

    /// <summary>
    /// 读取服务停留模式；缺失/非法回退跟随窗口。兼容旧版路径（%LOCALAPPDATA%），
    /// 读到后迁移并清理。v0.3.0 配置降级：lifetime 插件物理缺失时忽略残留值并回退。
    /// </summary>
    internal static ShellLogic.ServiceLifetime ReadLifetimeMode(string dshHomeDir)
    {
        var settingsPath = Path.Combine(dshHomeDir, "dsh-launcher", "settings.json");
        var json = SafeReadText(settingsPath);
        if (string.IsNullOrWhiteSpace(json))
        {
            try
            {
                var legacy = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "dsh-launcher", "settings.json");
                if (File.Exists(legacy))
                {
                    json = SafeReadText(legacy);
                    if (!string.IsNullOrWhiteSpace(json))
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                            File.WriteAllText(settingsPath, json);
                            File.Delete(legacy);
                        }
                        catch { /* 迁移失败按旧值执行 */ }
                    }
                }
            }
            catch { /* 旧路径不可读按默认执行 */ }
        }
        var pluginPresent = ShellLogic.PluginConfig.IsLifetimePluginInstalled(dshHomeDir);
        // 质量治理：settings.json 存在但非法 JSON → 记 Warn（此前静默回退默认模式难排查）
        if (!string.IsNullOrWhiteSpace(json) && !ShellLogic.PluginConfig.HasServiceLifetimeKey(json))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
                    Logger.Warn("settings.json is not a JSON object; lifetime defaults apply",
                        ctx: new { path = settingsPath });
            }
            catch
            {
                Logger.Warn("settings.json is not valid JSON; lifetime defaults apply",
                    ctx: new { path = settingsPath });
            }
        }
        var (mode, shouldPurge) = ShellLogic.PluginConfig.ResolveEffectiveLifetime(json, pluginPresent);
        if (shouldPurge)
        {
            Logger.Warn("settings.json serviceLifetime ignored (lifetime plugin missing); purging stale value",
                ErrorCodes.E2011, new { path = settingsPath, pluginPresent });
            PurgeServiceLifetime(settingsPath);
        }
        return mode;
    }
}
