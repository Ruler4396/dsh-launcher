using System.Text.Json;

namespace DshWeb;

/// <summary>
/// 主窗口位置/大小持久化（DSH_HOME\dsh-launcher\window-state.json，v0.3.0）。
/// 关闭时写回最近一次 RestoreBounds（位置为物理像素，尺寸存 96dpi 逻辑值便于跨 DPI 恢复）；
/// 启动时经 ShellLogic.RestoreWindowPosition 多显示器校验后恢复（越界 → 主屏居中）。
/// 克制：只存最近一次关闭状态，无历史、无热插拔实时响应。
/// </summary>
public static class WindowStateStore
{
    public sealed record WindowState(int X, int Y, int WidthLogical, int HeightLogical);

    private static string _path = "";

    public static void Init(string dataDir) => _path = Path.Combine(dataDir, "window-state.json");

    public static void Save(WindowState state)
    {
        if (_path.Length == 0) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(state));
        }
        catch { /* 保存失败不影响退出 */ }
    }

    public static WindowState? Load()
    {
        if (_path.Length == 0 || !File.Exists(_path)) return null;
        try
        {
            return JsonSerializer.Deserialize<WindowState>(File.ReadAllText(_path));
        }
        catch
        {
            // P1-4（质量治理）：损坏不静默——位置记忆失效要可诊断（对齐 settings.json 治理：
            // 此前损坏静默回退默认位置，用户"窗口怎么又回到中间了"无从查证）。
            Logger.Warn("window-state.json is corrupt or unreadable; window position memory unavailable",
                ctx: new { path = _path });
            return null;
        }
    }
}