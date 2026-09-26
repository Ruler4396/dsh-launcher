using DshWeb;
using DshWeb.Managers;
using Xunit;

namespace DshShell.Tests.RealOs;

/// <summary>
/// [臃肿审计 B2] 启动健康监控的日志层在日志被截断/轮转后永久致盲——RealOS 零 Mock 复现。
///
/// 仓库里有两份"从偏移增量读日志"：
///   · <c>ServiceManager.ReadLogIncrementShared</c>：截断时 <c>start = 0</c> 从头读（正确），
///     并回传 NextOffset。
///   · <c>BootHealthMonitor.ReadLogIncrementAsync</c>（私有）：截断时 <c>return string.Empty</c>
///     且偏移不回绕——文件被轮转/截断后，监控再也不会读到任何新行，直到文件重新长过旧偏移。
/// 更糟的是它自己的文档注释写着「文件被截断/轮转时回退从头读」：**注释与实现相反**。
/// 守启动的那一份是错的那一份：日志层判死（E2003）在轮转后静默失效，用户看到的是傻等超时。
///
/// 修复形态：删掉私有实现，日志层改为注入 <c>ServiceManager.ReadLogIncrementShared</c>，
/// 一处实现、偏移由回传值统一推进。本文件同时锁住这个"只许有一份"的契约。
/// </summary>
[Trait("Category", "RealOS")] // 归属必须显式：本类原先没有 trait，快线和 realos 层的 filter 各跑不到它
public class Regression_BootMonitorLogRotation : IDisposable
{
    private readonly string _dir;
    private readonly string _log;

    public Regression_BootMonitorLogRotation()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dsh-logrotate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _log = Path.Combine(_dir, "dsh.log");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this); // CA1816: Dispose 模式要求，勿跳过派生类终结器
        try { Directory.Delete(_dir, recursive: true); } catch { /* 临时目录清理失败忽略 */ }
    }

    private void Write(string content) => File.WriteAllText(_log, content);

    /// <summary>共享读取器：截断后必须回绕从头读，并给出正确的新偏移。</summary>
    [Fact]
    public void SharedReader_AfterTruncation_ResurfacesNewLines_AndRewindsOffset()
    {
        Write("[dsh] first-line-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n");
        var (first, offset) = ServiceManager.ReadLogIncrementShared(_log, 0);
        Assert.NotNull(first);
        Assert.True(first!.Contains("first-line"), "首读应拿到 first-line");
        Assert.True(offset > 0, "首次读取后偏移应前移");

        // 轮转：文件被截断重写，比旧偏移短
        Write("[dsh] second-line-after-rotation\n");
        Assert.True(new FileInfo(_log).Length < offset, "前置：截断后文件必须短于旧偏移");

        var (second, nextOffset) = ServiceManager.ReadLogIncrementShared(_log, offset);
        Assert.NotNull(second);
        Assert.True(second!.Contains("second-line-after-rotation"),
            "截断后必须从头重读，否则日志层永久致盲");
        Assert.True(nextOffset > 0, "回绕后偏移必须重新前移，不能停在旧值");
    }

    /// <summary>
    /// 启动健康监控**不得**再自带第二份增量读取实现（缺陷的根因是分叉，不是某一处写错）。
    /// 本用例在修复前必红（BootHealthMonitor.cs 里存在私有 ReadLogIncrementAsync）。
    /// </summary>
    [Fact]
    public void BootHealthMonitor_HasNoPrivateDivergentLogReader()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DshShell", "Lifecycle", "BootHealthMonitor.cs"));

        Assert.DoesNotContain("ReadLogIncrementAsync", src);
        Assert.True(src.Contains("ReadLogIncrementShared"),
            "日志层必须复用唯一实现 ServiceManager.ReadLogIncrementShared");
    }

    /// <summary>
    /// 分叉的私有实现不得用 `fs.Length &lt;= fromOffset → 返回空` 的形态复活。
    /// </summary>
    [Fact]
    public void BootHealthMonitor_MustNotReintroduceTheBlindingBranch()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "DshShell", "Lifecycle", "BootHealthMonitor.cs"));
        Assert.DoesNotContain("fs.Length <= fromOffset", src.Replace(" ", "").Replace("\t", ""));
        Assert.DoesNotMatch(@"Math\.Min\(fromOffset", src);
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "DshShell.sln")))
        {
            if (Directory.Exists(Path.Combine(d.FullName, "src", "DshShell"))) return d.FullName;
            d = d.Parent;
        }
        return d?.FullName ?? throw new InvalidOperationException("找不到仓库根");
    }
}
