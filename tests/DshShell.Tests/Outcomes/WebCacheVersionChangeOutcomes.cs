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
///       辅以目录证据：Cache 目录较清理前显著缩水（引擎删除缓存条目）。
///   I2  用户数据四类证据全部完好：localStorage、Cookie、IndexedDB、CacheStorage(Service
///       Worker 缓存)——执行器只传 CoreWebView2BrowsingDataKinds.DiskCache 一种种类；
///   I3  Code Cache（编译产物，枚举无对应种类）不被触碰（尺寸不缩水）；
///   I4  清理成功后账本更新为当前版本（下次版本相同 → 决策 false），且 double clear 幂等；
///   I5  空档案库（从未导航）上清理是安全 no-op：不抛、不破坏后续使用；
///   I6  执行走生产代码：WebViewManager.ClearDiskCacheAsync（内部捕获异常 → Warn 降级）。
///
/// 注：实测本 SDK 的 ExecuteScriptAsync 不等待 Promise（Promise 序列化为 "{}"），
/// 故异步 JS（IndexedDB/CacheStorage）统一用"发起 + window.__dshAsync 标志 + C# 轮询"驱动。
/// </summary>
public class WebCacheVersionChangeOutcomes
{
    private const string KeeperKey = "dsh-cache-test-user-data-must-survive";
    private const string KeeperValue = "1";
    private const string CookieName = "dsh_cache_keep";
    private const string IdbValue = "keepme";
    private const string CacheBody = "kept-body";
    private const int PayloadBytes = 2 * 1024 * 1024; // 2MB 缓存实体，让 DiskCache 明显增长

    [Fact]
    [Trait("Category", "RealOS")]
    public void RealOs_VersionChange_ClearsOnlyDiskCache_AllUserDataUntouched()
    {
        var userData = TempDir("dsh-wv2cache");
        var ledgerDir = TempDir("dsh-wv2ledger");
        Directory.CreateDirectory(ledgerDir);
        WebCacheVersionLedger.Init(ledgerDir);
        WebCacheVersionLedger.Write("1.0.0"); // 基线：上次运行是 v1.0.0

        try
        {
            var failure = RunStaScenario(userData, RunVersionChangeScenarioAsync);
            if (failure is not null)
                throw new Xunit.Sdk.XunitException("场景失败: " + failure);
        }
        finally
        {
            TryDeleteDir(userData);
            TryDeleteDir(ledgerDir);
        }
    }

    [Fact]
    [Trait("Category", "RealOS")]
    public void RealOs_FreshProfile_ClearIsSafeNoOp_AndWebStillUsable()
    {
        // I5：空档案库上清理（首启即清边界）→ 不抛、不破坏后续使用；双清幂等。
        var userData = TempDir("dsh-wv2fresh");
        var ledgerDir = TempDir("dsh-wv2fresh-ledger");
        Directory.CreateDirectory(ledgerDir);
        WebCacheVersionLedger.Init(ledgerDir);

        try
        {
            var failure = RunStaScenario(userData, RunFreshProfileScenarioAsync);
            if (failure is not null)
                throw new Xunit.Sdk.XunitException("场景失败: " + failure);
        }
        finally
        {
            TryDeleteDir(userData);
            TryDeleteDir(ledgerDir);
        }
    }

    private static async Task RunVersionChangeScenarioAsync(WebView2 web, string userData)
    {
        var profileDir = Path.Combine(userData, "EBWebView", "Default");
        var cacheDir = Path.Combine(profileDir, "Cache");
        var codeCacheDir = Path.Combine(profileDir, "Code Cache");
        var lsDir = Path.Combine(profileDir, "Local Storage");

        var port = FreeLoopbackPort();
        using var server = new CacheTestServer(port, PayloadBytes, cookieName: CookieName);
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, userData, new CoreWebView2EnvironmentOptions());
            await web.EnsureCoreWebView2Async(env);

            var page = $"http://127.0.0.1:{port}/app.html";

            // ---- 首次导航 + 写入四类"用户数据"证据 ----
            await NavigateAndWaitAsync(web, page);
            Assert.True((await JsAsync(web, $"localStorage.setItem('{KeeperKey}','{KeeperValue}'); localStorage.getItem('{KeeperKey}')")).Contains(KeeperValue),
                "前置：localStorage 可写");
            Assert.True((await JsAsync(web, "document.cookie")).Contains(CookieName),
                "前置：Cookie 已由服务端写入");
            var idbPut = await JsPollAsync(web, IdbPutStart, TimeSpan.FromSeconds(15));
            Assert.True(idbPut == "ok", "前置：IndexedDB 可写，实际=" + idbPut);
            var cachePut = await JsPollAsync(web, CachePutStart, TimeSpan.FromSeconds(15));
            Assert.True(cachePut == "ok", "前置：CacheStorage 可写，实际=" + cachePut);

            // ---- 二次导航 + 落盘等待：证明 max-age 命中缓存（/big.js 不回源）----
            await NavigateAndWaitAsync(web, page);
            Assert.Equal(1, server.BigJsRequests);
            await WaitUntilAsync(() => DirBytes(cacheDir) > 128 * 1024, TimeSpan.FromSeconds(15));
            long cacheBefore = DirBytes(cacheDir);
            Assert.True(cacheBefore > 128 * 1024, $"前置条件：磁盘缓存应有存量（实际 {cacheBefore} B）");
            long codeBefore = DirBytes(codeCacheDir);
            long csDirBefore = DirBytes(Path.Combine(profileDir, "Service Worker"));

            // ---- 生产编排链（Read → ShouldInvalidate → ClearDiskCacheAsync → Write）----
            var current = "1.0.1";
            Assert.Equal("1.0.0", WebCacheVersionLedger.Read());
            Assert.True(ShellLogic.CacheInvalidationPolicy.ShouldInvalidate("1.0.0", current));
            await WebViewManager.ClearDiskCacheAsync(web);
            WebCacheVersionLedger.Write(current);

            // I1a：Cache 目录缩水（引擎删除条目）
            await WaitUntilAsync(() => DirBytes(cacheDir) < cacheBefore, TimeSpan.FromSeconds(20));
            Assert.True(DirBytes(cacheDir) < cacheBefore, "缓存条目未删除（目录未缩水）");

            // I2：四类用户数据全部完好（页面层证据 + 文件层证据）
            Assert.True((await JsAsync(web, $"localStorage.getItem('{KeeperKey}')")).Contains(KeeperValue),
                "I2a localStorage 必须完好");
            Assert.True((await JsAsync(web, "document.cookie")).Contains(CookieName),
                "I2b Cookie 必须完好");
            var idbGet = await JsPollAsync(web, IdbGetStart, TimeSpan.FromSeconds(15));
            Assert.True(idbGet == IdbValue, $"I2c IndexedDB 必须完好，实际={idbGet}");
            var cacheGet = await JsPollAsync(web, CacheGetStart, TimeSpan.FromSeconds(15));
            Assert.True(cacheGet == CacheBody, $"I2d CacheStorage(Service Worker 缓存)必须完好，实际={cacheGet}");
            Assert.True(DirBytes(lsDir) > 0, "I2e Local Storage LevelDB 目录不得被清空");
            Assert.True(DirBytes(Path.Combine(profileDir, "Service Worker")) >= csDirBefore,
                "I2f Service Worker 目录不得缩水");

            // I3: Code Cache 不缩水
            Assert.True(DirBytes(codeCacheDir) >= codeBefore,
                $"I3 Code Cache 不应被清理触碰: before={codeBefore} after={DirBytes(codeCacheDir)}");

            // I1b：行为级铁证——同 URL 再次导航必然回源
            await NavigateAndWaitAsync(web, page);
            Assert.Equal(2, server.BigJsRequests);

            // I4a：账本更新 + 同版本不再清
            Assert.Equal("1.0.1", WebCacheVersionLedger.Read());
            Assert.False(ShellLogic.CacheInvalidationPolicy.ShouldInvalidate("1.0.1", "1.0.1"));

            // I4b：double clear 幂等——再清一次，用户数据依旧完好
            await WebViewManager.ClearDiskCacheAsync(web);
            Assert.True((await JsAsync(web, $"localStorage.getItem('{KeeperKey}')")).Contains(KeeperValue),
                "I4b 二次清理后 localStorage 仍必须完好");
            var idbGet2 = await JsPollAsync(web, IdbGetStart, TimeSpan.FromSeconds(15));
            Assert.True(idbGet2 == IdbValue, $"I4b 二次清理后 IndexedDB 仍须完好，实际={idbGet2}");
        }
        finally
        {
            TryDeleteDir(userData);
        }
    }

    private static async Task RunFreshProfileScenarioAsync(WebView2 web, string userData)
    {
        var port = FreeLoopbackPort();
        using var server = new CacheTestServer(port, PayloadBytes, cookieName: null);
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, userData, new CoreWebView2EnvironmentOptions());
            await web.EnsureCoreWebView2Async(env);

            // I5a：从未导航的空档案库上直接清 → 必须是无副作用 no-op
            await WebViewManager.ClearDiskCacheAsync(web);
            await WebViewManager.ClearDiskCacheAsync(web); // 双清

            var page = $"http://127.0.0.1:{port}/app.html";
            await NavigateAndWaitAsync(web, page);
            var r = await JsAsync(web, $"localStorage.setItem('{KeeperKey}','{KeeperValue}'); localStorage.getItem('{KeeperKey}')");
            Assert.True(r.Contains(KeeperValue), "I5b 空库清理后 WebView 仍可正常使用");

            // I5c：有内容后再清一次，依旧不伤用户数据
            await WebViewManager.ClearDiskCacheAsync(web);
            var r2 = await JsAsync(web, $"localStorage.getItem('{KeeperKey}')");
            Assert.True(r2.Contains(KeeperValue), "I5c 清理后 localStorage 完好");
        }
        finally
        {
            TryDeleteDir(userData);
        }
    }

    // ---- 异步 JS 证据：发起后写 window.__dshAsync 标志，C# 轮询读取（ExecuteScriptAsync 不等 Promise）----

    private const string IdbPutStart = """
        window.__dshAsync='pending'; (()=>{ const r=indexedDB.open('dsh_keep_db',1);
          r.onupgradeneeded=()=>{ r.result.createObjectStore('k'); };
          r.onsuccess=()=>{ const db=r.result; const tx=db.transaction('k','readwrite');
            tx.objectStore('k').put('keepme','k1');
            tx.oncomplete=()=>{ window.__dshAsync='ok'; };
            tx.onerror=()=>{ window.__dshAsync='ERR'; }; };
          r.onerror=()=>{ window.__dshAsync='ERR'; }; })();
        """;

    private const string IdbGetStart = """
        window.__dshAsync='pending'; (()=>{ const r=indexedDB.open('dsh_keep_db',1);
          r.onsuccess=()=>{ const g=r.result.transaction('k').objectStore('k').get('k1');
            g.onsuccess=()=>{ window.__dshAsync=String(g.result ?? '<missing>'); };
            g.onerror=()=>{ window.__dshAsync='ERR'; }; };
          r.onerror=()=>{ window.__dshAsync='ERR'; }; })();
        """;

    private const string CachePutStart = """
        window.__dshAsync='pending'; caches.open('dsh_keep_cache')
          .then(c=>c.put('/kept-asset', new Response('kept-body')))
          .then(()=>{ window.__dshAsync='ok'; })
          .catch(()=>{ window.__dshAsync='ERR'; });
        """;

    private const string CacheGetStart = """
        window.__dshAsync='pending'; caches.open('dsh_keep_cache')
          .then(c=>c.match('/kept-asset')).then(r=>r?r.text():'<missing>')
          .then(v=>{ window.__dshAsync=v; })
          .catch(()=>{ window.__dshAsync='ERR'; });
        """;

    // ---- 基础设施 ----

    private static string TempDir(string prefix)
        => Path.Combine(Path.GetTempPath(), prefix + "-" + Guid.NewGuid().ToString("N"));

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    private static Exception? RunStaScenario(string userData, Func<WebView2, string, Task> body)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        var sta = new Thread(() =>
        {
            try
            {
                using var form = new Form { Width = 900, Height = 640, ShowInTaskbar = false };
                var web = new WebView2 { Dock = DockStyle.Fill };
                form.Controls.Add(web);
                form.Shown += async (_, _) =>
                {
                    try { await body(web, userData); }
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
        Assert.True(done.Wait(TimeSpan.FromSeconds(150)), "WebView2 场景超时（WebView2 Runtime 缺失或环境异常）");
        return failure;
    }

    private static Task<string> JsAsync(WebView2 web, string script)
        => web.CoreWebView2.ExecuteScriptAsync(script);

    /// <summary>发起异步 JS 操作，轮询 window.__dshAsync 直到离开 pending（成功值或 ERR）。</summary>
    private static async Task<string> JsPollAsync(WebView2 web, string startScript, TimeSpan timeout)
    {
        await web.CoreWebView2.ExecuteScriptAsync(startScript);
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("JS 异步标志轮询超时");
            await Task.Delay(150);
            var flag = (await web.CoreWebView2.ExecuteScriptAsync("window.__dshAsync ?? 'pending'")).Trim('"');
            if (flag != "pending") return flag;
        }
    }

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
            try { total += new FileInfo(file).Length; } catch { }
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

    /// <summary>本地缓存测试源：/app.html（引 /big.js），全部 Cache-Control: max-age=3600；
    /// cookieName 非空时在 /app.html 响应上种 Cookie。BigJsRequests：/big.js 真实回源次数。</summary>
    private sealed class CacheTestServer : IDisposable
    {
        private readonly HttpListener _listener;
        private readonly byte[] _payload;
        private readonly string? _cookieName;
        private int _bigJsRequests;

        public int BigJsRequests => Volatile.Read(ref _bigJsRequests);

        public CacheTestServer(int port, int payloadBytes, string? cookieName)
        {
            _payload = new byte[payloadBytes];
            new Random(42).NextBytes(_payload);
            _cookieName = cookieName;
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
                        if (_cookieName is not null)
                            ctx.Response.AddHeader("Set-Cookie", $"{_cookieName}=1; Path=/; Max-Age=3600");
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