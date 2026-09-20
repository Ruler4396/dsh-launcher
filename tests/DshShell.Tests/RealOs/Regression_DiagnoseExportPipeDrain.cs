using DshWeb;
using Xunit;

namespace DshShell.Tests.RealOs;

/// <summary>
/// [臃肿审计 B1] DiagnoseExport.RunCaptureLines 管道排空缺失——RealOS 零 Mock 复现。
///
/// 该方法把「三必须」里的两条漏掉了：<c>StandardOutput.ReadToEnd()</c> 是**同步**读、
/// 且**完全不读 stderr**，而 <c>WaitForExit(4000)</c> 的返回值被丢弃、超时也不 Kill。
/// 故障链：子进程往 stderr 写超过管道缓冲（约 40–64KB）→ 无人排空 → 子进程阻塞在写上 →
/// 父进程的 stdout ReadToEnd 等不到 EOF → **无限挂死**（ReadToEnd 没有超时）。
/// 同文件 <see cref="DiagnoseExport"/> 的姊妹方法 RunCapture 是对的（异步排空 + 超时 Kill 进程树），
/// 也就是仓库自己写下的那条铁律在第二份拷贝里被漏掉了——这正是 D1 消重族要收口的形状。
///
/// 后果面：--diagnose 导出会在用户机器上卡住（诊断包恰恰是出问题时才需要的东西）。
/// 本用例用 15s 上限包裹，红时表现为"未完成"而不是把整个测试宿主挂死。
///
/// Trait 必须逐字写 `("Category", "RealOS")`：xUnit 的 trait 匹配区分大小写，本文件原先写成
/// `("category", "real-os")`，于是 RealOS 层的 filter 两头都筛不到它（既进不了 realos 专用
/// workflow，也不会被快线的 `Category!=RealOS` 排除）——真起 powershell 的用例悄悄躲在快线里。
/// </summary>
public class Regression_DiagnoseExportPipeDrain
{
    private static readonly string Powershell = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>子进程同时把 stdout 与 stderr 各写 200KB——足以填满两个管道缓冲。</summary>
    private const string FloodArgs =
        "-NoProfile -NonInteractive -Command \"" +
        "[Console]::Out.Write('a'*200000);[Console]::Error.Write('e'*200000);[Console]::Out.Flush()\"";

    private static bool TryCapture(TimeSpan budget, out string[] lines)
    {
        lines = Array.Empty<string>();
        var t = Task.Run(() => DiagnoseExport.RunCaptureLines(Powershell, FloodArgs));
        if (!t.Wait(budget)) return false;
        lines = t.Result;
        return true;
    }

    [Fact]
    [Trait("Category", "RealOS")]
    public void FloodedStderr_DoesNotHang_AndReturnsBounded()
    {
        Assert.True(File.Exists(Powershell), $"前置：找不到宿主机 PowerShell：{Powershell}");

        var ok = TryCapture(TimeSpan.FromSeconds(15), out var lines);

        Assert.True(ok, "子进程 stderr 洪水下读取未在 15s 内返回——管道未排空导致挂死（B1）");
        Assert.NotEmpty(lines);
    }

    /// <summary>超时路径必须留下有界结果而不是悬挂进程：小输出正常返回。</summary>
    [Fact]
    [Trait("Category", "RealOS")]
    public void SmallOutput_StillCaptured()
    {
        var args = "-NoProfile -NonInteractive -Command \"'l1';'l2'\"";
        var ok = TryCaptureArgs(args, TimeSpan.FromSeconds(20), out var lines);
        Assert.True(ok, "小输出读取应在 20s 内返回");
        Assert.Contains(lines, l => l.Trim() == "l1");
        Assert.Contains(lines, l => l.Trim() == "l2");
    }

    private static bool TryCaptureArgs(string args, TimeSpan budget, out string[] lines)
    {
        lines = Array.Empty<string>();
        var t = Task.Run(() => DiagnoseExport.RunCaptureLines(Powershell, args));
        if (!t.Wait(budget)) return false;
        lines = t.Result;
        return true;
    }
}
