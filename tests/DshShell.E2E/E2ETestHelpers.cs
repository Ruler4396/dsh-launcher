using System.Diagnostics;

namespace DshShell.E2E;

/// <summary>E2E 公共辅助：定位被测 exe、构造隔离的启动环境。</summary>
public static class E2ETestHelpers
{
    /// <summary>定位 DshWeb.exe（优先测试运行目录旁的构建产物）。</summary>
    public static string LocateDshWebExe()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "DshWeb.exe"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "DshShell", "bin", "Debug", "net10.0-windows", "DshWeb.exe"),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        throw new FileNotFoundException("未找到 DshWeb.exe，请先编译 src/DshShell/DshShell.csproj。");
    }

    /// <summary>创建隔离的 DSH_HOME 临时目录（避免测试污染真实用户数据）。</summary>
    public static string CreateIsolatedHome()
        => Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "dsh-e2e-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    /// <summary>构造带测试环境变量的启动参数（UseShellExecute=false 才能注入环境变量）。</summary>
    public static ProcessStartInfo NewStartInfo(string exe, string home, params (string Key, string Value)[] env)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        psi.Environment["DSH_HOME"] = home;
        foreach (var (key, value) in env)
            psi.Environment[key] = value;
        return psi;
    }

    /// <summary>按标题找顶层窗口句柄（FindWindowW：lpClassName=null → 匹配任意类）。</summary>
    public static IntPtr FindTopLevelWindow(string title) => NativeFindWindow(null, title);

    /// <summary>等待指定标题的顶层窗口出现（FindWindow 按标题匹配）。</summary>
    public static async Task<IntPtr> WaitForWindowByTitleAsync(string title, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var hwnd = FindTopLevelWindow(title);
            if (hwnd != IntPtr.Zero) return hwnd;
            await Task.Delay(20);
        }
        return IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr NativeFindWindow(string? lpClassName, string? lpWindowName);
}
