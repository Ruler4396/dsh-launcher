using System.Diagnostics;
using Xunit;

namespace DshShell.E2E;

/// <summary>
/// 方案 A：启动耗时基准——从进程启动到 Splash 窗口句柄可见必须 &lt;500ms。
/// 若 Program.Main 在创建窗口前仍有同步阻塞（数据迁移/HttpReady 探测/Node 解析），
/// 本测试必然失败（超过 500ms）。测试通过 DSH_TEST_SPLASH_DELAY_MS 让后台流水线
/// 挂起 2.5s，证明"窗口可见"不依赖后台启动完成（消息泵先行）。
/// </summary>
public class StartupLatencyTests
{
    private const string SplashTitle = "dsh-launcher 启动中";

    [Fact]
    public async Task Splash_visible_within_500ms_of_process_start()
    {
        var exe = E2ETestHelpers.LocateDshWebExe();
        var home = E2ETestHelpers.CreateIsolatedHome();
        // 模拟"首次下载 dsh/服务拉起"耗时 2.5s：窗口必须在后台任务完成前就可见
        using var proc = Process.Start(E2ETestHelpers.NewStartInfo(exe, home,
            ("DSH_TEST_SPLASH_DELAY_MS", "2500")));
        Assert.NotNull(proc);

        try
        {
            var sw = Stopwatch.StartNew();
            var hwnd = await E2ETestHelpers.WaitForWindowByTitleAsync(SplashTitle, TimeSpan.FromSeconds(5));
            sw.Stop();

            Assert.NotEqual(IntPtr.Zero, hwnd); // 窗口确实出现（而非直接崩溃）
            Assert.True(sw.ElapsedMilliseconds < 500,
                $"Splash 窗口句柄耗时 {sw.ElapsedMilliseconds}ms（阈值 <500ms）——Main 线程仍存在同步阻塞");
        }
        finally
        {
            if (proc.HasExited == false) proc.Kill(entireProcessTree: true);
            try { Directory.Delete(home, recursive: true); } catch { /* 清理失败不影响结果 */ }
        }
    }
}
