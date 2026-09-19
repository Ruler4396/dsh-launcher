using System.IO;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// [臃肿审计 Phase 4 · T9] 更新提示的宿主窗口不得由组合根静态持有。
///
/// <c>_pendingForm</c> 曾是进程级静态且**从不清除**：主窗关闭后它仍指向那个已 Dispose 的
/// Form，而 3 处把它交给 NoticeCard.Present 当 owner、另 2 处取来做对话框 owner 与标题栏
/// 改写，都没有 IsDisposed 守卫——等于把已释放的窗口句柄传给 Win32（表现为偶发弹窗打死进程 /
/// 弹到看不见的目标上）。现在整个字段删除，宿主一律走 GetMainFormForDialog()：
/// 它实时查 <c>Application.OpenForms</c>，结构上不可能返回已关闭的窗，因此连守卫都不需要。
/// </summary>
public class PendingOwnerGuardTests
{
    private static string ProgramSrc()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "src", "DshShell"))) d = d.Parent;
        return File.ReadAllText(Path.Combine(d!.FullName, "src", "DshShell", "Program.cs"));
    }

    [Fact]
    public void CompositionRootNoLongerHoldsAWindowReference()
    {
        var src = ProgramSrc();
        Assert.DoesNotContain("_pendingForm", src);
        Assert.DoesNotContain("PendingOwner", src);
    }

    /// <summary>宿主一律实时取；取到之后不得跨多次读取（每次取都可能返回不同实例）。</summary>
    [Fact]
    public void NoticeOwnersComeFromTheLiveQuery()
    {
        var src = ProgramSrc();
        Assert.Equal(3, Count(src, "NoticeCard.Present(GetMainFormForDialog()"));
        Assert.Contains("Application.OpenForms.OfType<DshShellForm>()", src);
    }

    private static int Count(string hay, string needle)
    {
        var n = 0;
        for (var i = hay.IndexOf(needle, StringComparison.Ordinal);
             i >= 0; i = hay.IndexOf(needle, i + 1, StringComparison.Ordinal)) n++;
        return n;
    }
}
