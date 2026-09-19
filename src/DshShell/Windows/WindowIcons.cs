using System.Drawing;
using System.Reflection;
using DshWeb.Win32;

namespace DshWeb.Windows;

/// <summary>
/// 鲸鱼图标缓存（臃肿审计 Phase 4 · T8：从 Program.cs 组合根迁出）。
///
/// 【生命周期 = 进程级，且刻意不 Dispose】映射表 G 组的规定：<c>Icon.FromHandle(GetHicon())</c>
/// 拿的是 GDI 句柄，托盘驻留/主题切换会反复复用同一份图标，若在窗口关闭路径 Dispose 会留下
/// 悬挂句柄；因此句柄随进程退出由系统回收。此前这个缓存是 <c>Program</c> 的
/// <c>internal static Icon?</c> 字段，而 Chrome 层在自绘回调里 <c>??=</c> **跨类改写组合根静态**
/// ——既是隐式全局状态，又是依赖方向倒置（棘轮 G3 拦的就是这类）。现在写入收进本类，
/// 外部只读属性。
///
/// 已知残留缺陷（不在本次范围，记入债务台账）：<c>DestroyIcon</c> 在全仓从未被调用
/// （原 extern 已随 T8 删除），故每次 <c>GetHicon()</c> 的句柄不会显式释放。
/// </summary>
internal static class WindowIcons
{
    private static Icon? _dark;
    private static Icon? _light;
    private static Icon? _blue;

    /// <summary>深色鲸鱼（浅色主题标题栏用）。</summary>
    internal static Icon? DarkWhaleIcon => _dark ??= Load("favicon.png");

    /// <summary>白色鲸鱼（深色主题标题栏用）。</summary>
    internal static Icon? LightWhaleIcon => _light ??= Load("favicon-white.png");

    /// <summary>蓝色鲸鱼（DeepSeek 蓝 #4D6BFE；托盘与任务栏按钮固定用，深浅背景都清晰）。</summary>
    internal static Icon? BlueWhaleIcon => _blue ??= Load("favicon-blue.png");

    /// <summary>从嵌入资源按资源名后缀加载图标。</summary>
    private static Icon? Load(string resourceSuffix)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith(resourceSuffix, StringComparison.OrdinalIgnoreCase));
            if (name is null) return null;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return null;
            using var bmp = new Bitmap(stream);
            return Icon.FromHandle(bmp.GetHicon());
        }
        catch (Exception ex)
        {
            // 图标缺失只影响观感，但失败原因必须可见（异常透明性；原实现是静默 return null）
            Logger.Warn($"图标资源加载失败（{resourceSuffix}）: {ex.Message}");
            return null;
        }
    }
}
