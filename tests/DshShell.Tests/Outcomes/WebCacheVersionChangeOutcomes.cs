using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using DshWeb;
using DshWeb.Managers;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【L3 Outcome — 版本变更一次性磁盘缓存失效】真实 WebView2 + 真实 HttpListener，零 Mock。
///
/// 用户任务级不变量（= 本功能对用户的承诺，任何一条被破坏即功能失败）：
///   I1  检测到 dsh 版本变化 → WebView2 磁盘 HTTP 缓存被真正失效。判定用**行为级证据**：
///       同一 URL（Cache-Control: max-age）在清理后再次导航**必然回源**（服务器请求计数 +1）；
///       辅以目录证据：Cache 目录较清理前显著缩水（缓存条目被删除）。清理后新导航重新
///       填充缓存属 WebView2 正常语义，不视为失败。
///   I2  用户数据完好：localStorage 键值仍可读（页面层证据）＋ Local Storage LevelDB 文件
///       仍存在（文件层证据）——执行器只传 CoreWebView2BrowsingDataKinds.DiskCache 一种种类；
///   I3  Code Cache（编译产物，枚举无对应种类）不被触碰（尺寸不缩水）；
///   I4  清理成功后账本更新为当前版本（下次版本相同 → 决策 false，不重复清理）；
///   I5  执行走生产代码：WebViewManager.ClearDiskCacheAsync（内部捕获异常 → Warn 降级）。
///
/// 编排链（Read → ShouldInvalidate → ClearDiskCacheAsync → Write）在组合根 Program 内是
/// 三步薄编排；本测试用生产 API 复现同链，断言只看磁盘/页面/服务端最终物理状态（Outcome 契约）。
/// </summary>
public class WebCacheVersionChangeOutcomes
{
    private const string KeeperKey = "dsh-cache-test-user-data-must-survive";
    private const string KeeperValue = "1";
    private const int PayloadBytes = 2 * 1024 * 1024; // 2MB 缓存实体，让 DiskCache 明显增长

    [Fact]
    [Trait("Category", "RealOS")]
    public void RealOs_VersionChange_ClearsOnlyDiskCache_LocalStorageAndCodeCacheUntouched()
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim();

        var userData = Path.Combine(Path.GetTempPath(), "dsh-wv2cache-" + Guid.NewGuid().ToString("N"));
        var ledgerDir = Path.Combine(Path.GetTempPath(), "dsh-wv2ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(ledgerDir);
        WebCacheVersionLedger.Init(ledgerDir);
        WebCacheVersionLedger.Write("1.0.0"); // 基线：上次运行是 v1.0.0

        var sta = new Thread(() =>
        {
            try
            {
                using var form = new Form { Width = 900, Height = 640, ShowInTaskbar = false };
                var web = new WebView2 { Dock = DockStyle.Fill };
                form.Controls.Add(web);
                form.Shown += async (_, _) =>
                {
                    try { await RunVersionChangeScenarioAsync(web, userData, ledgerDir); }
                    catch (Exception ex) { failure = ex; }
                    finally { form.Close(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        sta.SetApartmentState(ApartmentState.STA);
        sta.Start();

        Assert.True(done.Wait(TimeSpan.FromSeconds(150)), "WebView2 缓存失效场景超时（WebView2 Runtime 缺失或环境异常）");
        if (failure is not null)
            throw new Xunit.Sdk.XunitException("场景失败: " + failure);
    }

    private static async Task RunVersionChangeScenarioAsync(WebView2 web, string userData, string ledgerDir)
    {
        var profileDir = Path.Combine(userData, "EBWebView", "Default");
        var cacheDir = Path.Combine(profileDir, "Cache");
        var codeCacheDir = Path.Combine(profileDir, "Code Cache");
        var lsDir = Path.Combine(profileDir, "Local Storage");

        var port = FreeLoopbackPort();
        using var server = new CacheTestServer(port, PayloadBytes);
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(
                null, userData, new CoreWebView2EnvironmentOptions());
            await web.EnsureCoreWebView2Async(env);

            var page = $"http://127.0.0.1:{port}/app.html";

            // 首次导航：页面 + 2MB 脚本资源带 Cache-Control: max-age 落 HTTP 缓存
            await NavigateAndWaitAsync(web, page);
            // 写入"用户数据"证据（webui 设置类内容，绝对不允许被清）
            var setResult = await web.CoreWebView2.ExecuteScriptAsync(
                $"localStorage.setItem('{KeeperKey}','{KeeperValue}'); localStorage.getItem('{KeeperKey}')");
            Assert.Equal($"\"{KeeperValue}\"", setResult);

            // 二次导航 + 等待：缓存从内存异步落盘；两次导航证明 max-age 让 /big.js 不回源
            await NavigateAndWaitAsync(web, page);
            Assert.Equal(1, server.BigJsRequests); // 缓存生效前置：第二次导航 /big.js 未回源
            await WaitUntilAsync(() => DirBytes(cacheDir) > 128 * 1024, TimeSpan.FromSeconds(15));
            long cacheBefore = DirBytes(cacheDir);
            Assert.True(cacheBefore > 128 * 1024, $"前置条件：磁盘缓存应有存量（实际 {cacheBefore} B）");
            long codeBefore = DirBytes(codeCacheDir);

            // ———— 生产编排链（与 Program.InvalidateWebCacheOnVersionChangeAsync 同构）————
            var current = "1.0.1"; // 模拟 dsh 升级
            var lastSeen = WebCacheVersionLedger.Read();
            Assert.Equal("1.0.0", lastSeen);
            Assert.True(ShellLogic.CacheInvalidationPolicy.ShouldInvalidate(lastSeen, current),
                "决策：有基线且版本不同 → 应清");
            await WebViewManager.ClearDiskCacheAsync(web); // 生产执行器（内部仅 DiskCache 种类）
            WebCacheVersionLedger.Write(current);

            // 清理生效：Cache 目录较清理前显著缩水（条目删除）
            await WaitUntilAsync(() => DirBytes(cacheDir) < cacheBefore, TimeSpan.FromSeconds(20));
            long cacheAfter = DirBytes(cacheDir);
            Assert.True(cacheAfter < cacheBefore,
                $"缓存条目未删除: before={cacheBefore} after={cacheAfter}");

            // I2: 用户数据完好（先页面层后文件层）
            var getResult = await web.CoreWebView2.ExecuteScriptAsync($"localStorage.getItem('{KeeperKey}')");
            Assert.Equal($"\"{KeeperValue}\"", getResult);
            Assert.True(DirBytes(lsDir) > 0, "I2 用户数据目录(Local Storage)不得被清空");

            // I3: Code Cache 不得因清理而缩水（枚举无对应种类；若实现误清，本断言立即红）
            Assert.True(DirBytes(codeCacheDir) >= codeBefore,
                $"I3 Code Cache 不应被清理触碰: before={codeBefore} after={DirBytes(codeCacheDir)}");

            // I1: 行为级铁证——同一 URL 再次导航必须回源（缓存真正失效，而非目录腾挪）
            await NavigateAndWaitAsync(web, page);
            Assert.Equal(2, server.BigJsRequests);

            // I4: 账本已更新 → 同版本不再清（幂等）
            Assert.Equal("1.0.1", WebCacheVersionLedger.Read());
            Assert.False(ShellLogic.CacheInvalidationPolicy.ShouldInvalidate("1.0.1", "1.0.1"),
                "I4 版本相同 → 不再清理");
        }
        finally
        {
            try { if (Directory.Exists(userData)) Directory.Delete(userData, true); } catch { }
            try { if (Directory.Exists(ledgerDir)) Directory.Delete(ledgerDir, true); } catch { }
        }
    }

    // ---- 基础设施 ----

    private static Task NavigateAndWaitAsync(WebView2 web, string url)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            web.CoreWebView2.NavigationCompleted -= OnCompleted;
            if (e.IsSuccess) tcs.TrySetResult(true);
            else tcs.TrySetException(new InvalidOperationException($"导航失败: http={e.HttpStatusCode}"));
        }
        web.CoreWebView2.NavigationCompleted += OnCompleted;
        web.CoreWebView2.Navigate(url);
        return tcs.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException($"等待条件超时（{timeout.TotalSeconds}s）");
            await Task.Delay(250);
        }
    }

    private static long DirBytes(string dir)
    {
        if (!Directory.Exists(dir)) return 0;
        long total = 0;
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; } catch { /* 锁定的文件忽略（不参与证据） */ }
        }
        return total;
    }

    private static int FreeLoopbackPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>本地缓存测试源：/app.html（引 /big.js），全部 Cache-Control: max-age=3600。
    /// BigJsRequests：/big.js 被真实回源的次数（缓存命中则不计——行为级"失效"证据）。</summary>
    private sealed class CacheTestServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly byte[] _payload;
        private int _bigJsRequests;

        public int BigJsRequests => Volatile.Read(ref _bigJsRequests);

        public CacheTestServer(int port, int payloadBytes)
        {
            _payload = new byte[payloadBytes];
            new Random(42).NextBytes(_payload);
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); }
                    catch { break; }
                    ctx.Response.StatusCode = 200;
                    ctx.Response.AddHeader("Cache-Control", "public, max-age=3600");
                    if ((ctx.Request.Url?.AbsolutePath ?? "/") == "/big.js")
                    {
                        Interlocked.Increment(ref _bigJsRequests);
                        ctx.Response.ContentType = "application/javascript";
                        await ctx.Response.OutputStream.WriteAsync(_payload);
                    }
                    else
                    {
                        ctx.Response.ContentType = "text/html";
                        var body = Encoding.UTF8.GetBytes(
                            "<html><body>dsh cache test<script src='/big.js'></script></body></html>");
                        await ctx.Response.OutputStream.WriteAsync(body);
                    }
                    ctx.Response.Close();
                }
            });
        }

        public void Dispose()
        {
            try { _listener.Stop(); _listener.Close(); } catch { }
        }
    }
}