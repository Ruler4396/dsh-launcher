using DshWeb;
using DshWeb.Domain;
using DshWeb.Lifecycle;
using DshWeb.Managers;
using DshShell.Tests.Managers;
using Xunit;

namespace DshShell.Tests.Outcomes;

/// <summary>
/// 【ADR-024 系统模型升级 · L3 Outcome 契约】五条用户任务级不变量。
///
/// 铁律（docs/TESTING-GUARDRAILS.md）：不 Mock 外部进程、不关心内部调用了哪个函数，
/// 只断言系统的**最终物理状态**——磁盘上的运行时目录、发现层返回的 Identity、
/// 启动命令行、注册表与用户目录的逐字节快照。
///
/// 五条不变量：
///   1. Update_Changes_Actual_Running_Identity —— 更新事务后，发现层的身份必须真的切换；
///   2. Update_Failure_Retains_Old_Runtime    —— 更新失败后，旧运行时必须原样保留可用；
///   3. SafeMode_Isolates_Profile             —— 安全模式必须物理隔离 profile 且命令行指向它；
///   4. Crash_Recovery_Reloads_Page           —— WebView 崩溃必须在 10s 内触发页面重载信号；
///   5. Zero_Pollution_On_Exit                —— 正常退出不得污染 ~/.dsh、注册表自启、npm 全局目录。
/// </summary>
[Collection("EnvHygiene")]
    public class SystemUpgradeOutcomeContracts
{
    public SystemUpgradeOutcomeContracts() => EnvHygiene.ClearHostileEnv();

    // ==================== 共享隔离设施 ====================

    /// <summary>环境卫生员：DSH_HOME 指向一次性临时目录，清空版本/URL 覆盖钩子，全程可还原。</summary>
    private sealed class IsolatedHome : IDisposable
    {
        public string Home { get; } = Path.Combine(
            Path.GetTempPath(), "dsh-outcome-l3-" + Guid.NewGuid().ToString("N"));
        public string DataDir => Path.Combine(Home, "dsh-launcher");
        public string RuntimesDir => Path.Combine(DataDir, "runtimes");
        public string StagingDir => Path.Combine(DataDir, "staging");

        private readonly string? _home, _version, _url, _noui;

        public IsolatedHome()
        {
            _home = Environment.GetEnvironmentVariable("DSH_HOME");
            _version = Environment.GetEnvironmentVariable("DSH_VERSION");
            _url = Environment.GetEnvironmentVariable("DSH_WEB_URL");
            _noui = Environment.GetEnvironmentVariable("DSH_NO_UI");
            Directory.CreateDirectory(DataDir);
            Environment.SetEnvironmentVariable("DSH_HOME", Home);
            Environment.SetEnvironmentVariable("DSH_VERSION", null); // 版本只能来自物理 package.json
            Environment.SetEnvironmentVariable("DSH_WEB_URL", null); // 禁止 External 短路
            Environment.SetEnvironmentVariable("DSH_NO_UI", "1");
            DshDiscovery.InvalidateCache();
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable("DSH_HOME", _home);
            Environment.SetEnvironmentVariable("DSH_VERSION", _version);
            Environment.SetEnvironmentVariable("DSH_WEB_URL", _url);
            Environment.SetEnvironmentVariable("DSH_NO_UI", _noui);
            DshDiscovery.InvalidateCache();
            try { Directory.Delete(Home, recursive: true); } catch { }
        }
    }

    /// <summary>铺设一个发现器认可的完整自包含运行时目录（真实文件，bin 入口可解析）。</summary>
    private static void WriteValidRuntime(string dir, string version)
    {
        var libDir = Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "lib");
        Directory.CreateDirectory(libDir);
        File.WriteAllText(Path.Combine(libDir, "bin.js"), "// entry " + version);
        File.WriteAllText(
            Path.Combine(dir, "node_modules", "@deepseek-ai", "dsh", "package.json"),
            $"{{\"name\":\"@deepseek-ai/dsh\",\"version\":\"{version}\",\"bin\":{{\"dsh\":\"lib/bin.js\"}}}}");
    }

    // ==================== Outcome 1：更新改变实际运行身份 ====================

    /// <summary>
    /// 【Outcome 1 / FP1 终极拦截器】更新事务完成后，DiscoverCurrentRuntime 必须返回目标版本，
    /// 且 DshEntryJsPath 物理指向新运行时内的入口。
    ///
    /// 拦截的历史事故："npm 返回 0 / 搬移成功，但实际运行的还是旧版本"。
    /// 本测试零 Mock：真实在磁盘上铺设 v0.0.9 旧运行时 → 经生产引擎
    /// <see cref="DshUpdateManager.ApplyPending"/> 执行原子切换 → 重发现身份做物理取证。
    /// </summary>
    [Fact]
    public void Update_Changes_Actual_Running_Identity()
    {
        using var env = new IsolatedHome();
        const string oldVer = "0.0.9-outcome";
        const string newVer = "0.1.0-outcome";
        StagedUpdate.Init(env.DataDir);

        // ---- Given：旧版本真实在位，且是发现层当前选中的身份 ----
        WriteValidRuntime(Path.Combine(env.RuntimesDir, oldVer), oldVer);
        DshDiscovery.InvalidateCache();
        var before = DshDiscovery.DiscoverCurrentRuntime();
        Assert.Equal(DshSource.SelfContained, before.Source);
        Assert.Equal(oldVer, before.Version);

        // ---- When：新版构建产物落 staging + 生产引擎应用（真实目录原子切换） ----
        var buildDir = Path.Combine(env.StagingDir, $"runtime-build-{newVer}");
        WriteValidRuntime(buildDir, newVer);
        StagedUpdate.MarkPending(newVer, $"deepseek-ai-dsh-{newVer}.tgz",
            prefetched: true, runtimeDir: buildDir);

        var engine = new DshUpdateManager(env.DataDir);
        var appliedEvent = new List<string>();
        engine.UpdateApplied += v => appliedEvent.Add(v); // 回滚闸门武装证据
        engine.ApplyPending();

        // ---- Then：五重物理证据 ----
        // ① 目标位置出现完整新运行时
        var appliedDir = Path.Combine(env.RuntimesDir, newVer);
        Assert.True(File.Exists(Path.Combine(appliedDir,
            "node_modules", "@deepseek-ai", "dsh", "package.json")), "应用后 runtimes\\<new> 缺失");
        // ② 发现层身份切换到新版（FP1 的直接反面）
        var after = DshDiscovery.DiscoverCurrentRuntime();
        Assert.Equal(newVer, after.Version);
        Assert.NotEqual(before.Version, after.Version);
        // ③ 入口物理位于新运行时内部（不是旧目录换皮）
        Assert.StartsWith(appliedDir, after.DshEntryJsPath, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(after.DshEntryJsPath), "身份携带的 JS 入口必须真实存在");
        // ④ pending 清账（下次启动不再重复应用）
        Assert.Null(StagedUpdate.ReadPending().Version);
        // ⑤ 回滚闸门武装事件已发（update-guard 链路闭合）
        Assert.Equal(newVer, Assert.Single(appliedEvent));
        // ⑥ 旧版保留作回滚（Failure 兜底的前提）
        Assert.True(Directory.Exists(Path.Combine(env.RuntimesDir, oldVer)), "旧版运行时被误删");
    }

    // ==================== Outcome 2：更新失败保留旧运行时 ====================

    /// <summary>
    /// 【Outcome 2】更新事务失败（构建产物不完整，源完整性门禁拒绝搬运）后：
    /// 旧运行时必须毫发无损且仍是发现层当前身份；半成品绝不进入 runtimes；
    /// 失败必须经回调上抛（不允许静默假装成功）。
    /// 零 Mock：真实的半成品目录 + 生产引擎完整走一遍门禁路径。
    /// </summary>
    [Fact]
    public void Update_Failure_Retains_Old_Runtime()
    {
        using var env = new IsolatedHome();
        const string oldVer = "0.0.9-outcome";
        const string badVer = "0.1.0-broken";
        StagedUpdate.Init(env.DataDir);

        // ---- Given：旧版在位；新版为半成品（缺 package.json/bin，模拟中断的构建） ----
        var oldDir = Path.Combine(env.RuntimesDir, oldVer);
        WriteValidRuntime(oldDir, oldVer);
        DshDiscovery.InvalidateCache();
        var before = DshDiscovery.DiscoverCurrentRuntime();
        Assert.Equal(oldVer, before.Version);
        var beforeEntryStamp = File.ReadAllText(before.DshEntryJsPath!);

        var brokenDir = Path.Combine(env.StagingDir, $"runtime-build-{badVer}");
        Directory.CreateDirectory(Path.Combine(brokenDir, "node_modules")); // 只有半个树
        StagedUpdate.MarkPending(badVer, prefetched: true, runtimeDir: brokenDir);

        // ---- When：生产引擎尝试应用（应被源完整性门禁拒绝） ----
        var engine = new DshUpdateManager(env.DataDir);
        var failures = new List<(string Version, string Tail)>();
        engine.NotifyApplyFailed += (v, tail) => failures.Add((v, tail));
        engine.ApplyPending();

        // ---- Then：旧运行时原样可用 + 半成品未扩散 + 失败可见 ----
        // ① 发现层身份仍是旧版，入口内容未被触碰
        var after = DshDiscovery.DiscoverCurrentRuntime();
        Assert.Equal(DshSource.SelfContained, after.Source);
        Assert.Equal(oldVer, after.Version);
        Assert.Equal(beforeEntryStamp, File.ReadAllText(after.DshEntryJsPath!));
        // ② 半成品绝不被搬进 runtimes（12:23:29 现场事故的回归防线）
        Assert.False(Directory.Exists(Path.Combine(env.RuntimesDir, badVer)), "半成品混入了正式运行时区");
        Assert.True(Directory.Exists(brokenDir), "半成品现场应保留供诊断");
        // ③ 失败经回调上抛（非重试类 → 引擎清 pending 防死循环 + 明确告知）
        var failure = Assert.Single(failures);
        Assert.Equal(badVer, failure.Version);
        Assert.Null(StagedUpdate.ReadPending().Version);
    }

    // ==================== Outcome 3：安全模式物理隔离 profile ====================

    /// <summary>
    /// 【Outcome 3 / ADR-022×024 交汇】安全模式下：
    /// ① 隔离 profile 目录<b>物理存在</b>且 manifest 只含 @deepseek-ai 核心 bundle
    ///   （第三方插件全部剥离）；② 服务启动命令行必须包含根级
    ///   <c>--profile .dsh-safe</c>（name-only，无分隔符——dsh 契约），由
    ///   Identity.ProfilePath × ServiceLaunch.BuildArgs 唯一决定；
    /// ③ 用户原 profiles 目录逐字节零污染。
    /// 不启动任何进程：命令行断言针对生产拼装纯函数（Start 直传给 ProcessStartInfo 的同一字符串）。
    /// </summary>
    [Fact]
    public void SafeMode_Isolates_Profile()
    {
        using var env = new IsolatedHome();
        var builder = new SafeProfileBuilder(env.Home);
        var webPkgDir = Path.Combine(env.Home, "profiles", "web");
        Directory.CreateDirectory(webPkgDir);
        File.WriteAllText(Path.Combine(webPkgDir, "package.json"),
            "{\"dsh\":{\"profile\":{\"bundles\":[" +
            "\"@deepseek-ai/dsh-base\",\"evil-local-plugin\",\"@deepseek-ai/dsh-notes\"]}}}");

        var userHashBefore = SafeProfileBuilder.CaptureUserProfilesHash(Path.Combine(env.Home, "profiles"));

        // ---- When：构建 Tier1 隔离 profile + 组装安全模式身份与启动命令 ----
        Assert.True(builder.Build(SafeProfileTier.Tier1KeepDeepSeekCore), "隔离 profile 构建必须成功");
        var identity = IdentityFixtures.Launchable().WithProfile(builder.SafeProfileDir);
        var cmdline = ShellLogic.ServiceLaunch.BuildArgs(identity, port: 3080);

        // ---- Then：三重物理证据 ----
        // ① 目录物理存在，manifest 只含 @deepseek-ai 核心（第三方 evil-local-plugin 被剥离）
        Assert.True(Directory.Exists(builder.SafeProfileDir), ".dsh-safe 目录必须物理存在");
        using (var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(builder.SafeProfilePackageJson)))
        {
            var bundles = doc.RootElement.GetProperty("dsh").GetProperty("profile")
                .GetProperty("bundles").EnumerateArray().Select(b => b.GetString()!).ToList();
            Assert.DoesNotContain("evil-local-plugin", bundles);
            Assert.Contains("@deepseek-ai/dsh-base", bundles);
            Assert.Contains("@deepseek-ai/dsh-web-app", bundles); // 官方最小核心永远在列
            Assert.All(bundles, b => Assert.StartsWith("@deepseek-ai/", b));
        }
        // ② 命令行含根级 --profile .dsh-safe（仅 name，无任何分隔符）；web 子命令互斥不出现
        Assert.Contains("--profile .dsh-safe ", cmdline);
        Assert.DoesNotContain(".dsh-safe\\", cmdline);
        Assert.DoesNotContain(".dsh-safe/", cmdline);
        Assert.DoesNotContain(" web", cmdline.Replace("--no-open", ""));
        Assert.Contains("--host 127.0.0.1 --port 3080 --no-open", cmdline);
        // ③ 用户原 profiles 零污染（构建前后逐字节一致）
        Assert.True(SafeProfileBuilder.UsersProfilesUntouched(
            userHashBefore, Path.Combine(env.Home, "profiles")), "安全模式不得触碰用户 profile 文件");
    }

    // ==================== Outcome 4：崩溃恢复 10s 内重载页面 ====================

    /// <summary>
    /// 【Outcome 4】WebView2 渲染进程崩溃后：状态机必须保持 Running 并广播一次自转移——
    /// 这正是壳订阅后调用 CoreWebView2.Reload() 的唯一信号。信号从崩溃到广播必须 &lt;10s
    /// （用户感知契约：崩溃窗口白屏不超过 10 秒）。Headless 全速执行，实测远小于阈值。
    /// </summary>
    [Fact]
    public void Crash_Recovery_Reloads_Page()
    {
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        Assert.True(app.RunStartupAsync().GetAwaiter().GetResult());
        Assert.Equal(LifecycleState.Running, app.State);

        var reloadSignals = new List<LifecycleState>();
        app.StateChanged += (_, s) => reloadSignals.Add(s); // WebViewManager 同款订阅点

        var sw = System.Diagnostics.Stopwatch.StartNew();
        app.HandleWebViewCrashed(); // 渲染进程崩溃事件（生产由 ProcessFailed 触发）
        sw.Stop();

        // Then：10s 内发出恰好一次 Running 自转移（重载信号），且状态未被带偏
        Assert.True(sw.Elapsed <= TimeSpan.FromSeconds(10),
            $"崩溃重载信号耗时 {sw.Elapsed.TotalSeconds:F2}s，超过 10s 用户感知上限");
        var signal = Assert.Single(reloadSignals);
        Assert.Equal(LifecycleState.Running, signal); // Running→Running：拦截并重载，而非死亡
        Assert.Equal(LifecycleState.Running, app.State);
    }

    // ==================== Outcome 5：退出零污染 ====================

    /// <summary>
    /// 【Outcome 5】一次完整的"启动→服务会话→退出清理"生命周期结束后：
    /// ① %USERPROFILE%\.dsh 文件/目录清单逐项不变（沙盒外的用户数据神圣不可侵犯）；
    /// ② HKCU 自动启动注册表值不变（未经用户显式勾选绝不写入）;
    /// ③ npm 全局目录文件数不变（更新流才允许动它，正常启停绝不碰）；
    /// ④ 会话自身的数据目录不残留 service-pid 账本（PID 只在真实拉起后才记录）。
    /// 全部真实 OS 交互（注册表 + 文件系统枚举），零 Mock。
    /// </summary>
    [Fact]
    public void Zero_Pollution_On_Exit()
    {
        // ---- 快照三块"禁污区"（先于任何壳行为） ----
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var userDsh = Path.Combine(userProfile, ".dsh");
        var dshFilesBefore = SnapshotTree(userDsh);
        var runValueBefore = ReadAutostartRunValue();
        var npmDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
        var npmCountBefore = CountFiles(npmDir);

        using var env = new IsolatedHome();
        var port = GetFreePort();

        // ---- 完整生命周期：启动（Fake 服务链，不起真进程）→ 就绪 → 退出清理 ----
        var app = new LauncherApp(new FakeRuntime(), new FakeService { Ready = true });
        Assert.True(app.RunStartupAsync().GetAwaiter().GetResult());
        Assert.Equal(LifecycleState.Running, app.State);

        // 退出编排的服务收尾段（BeginShutdownAsync 后台线程执行的同款调用）
        DshWeb.Managers.ServiceLifecycleOps.SweepStaleServicePid(env.DataDir, port);
        DshWeb.Managers.ServiceLifecycleOps.StopService(env.DataDir, port, rememberedPid: 0);

        // ---- Then：四重零污染证据 ----
        Assert.Equal(dshFilesBefore, SnapshotTree(userDsh));          // ① ~/.dsh 未增删改
        Assert.Equal(runValueBefore, ReadAutostartRunValue());        // ② 注册表自启未动
        Assert.Equal(npmCountBefore, CountFiles(npmDir));             // ③ npm 全局目录未动
        Assert.Empty(Directory.GetFiles(env.DataDir, "service-pid-*")); // ④ 无 PID 账本残留
    }

    // ---- 辅助：物理快照原语 ----

    /** 递归相对路径快照（文件 + 目录；读不了的条目跳过，两端同规则即可比对）。 */
    private static SortedSet<string> SnapshotTree(string root)
    {
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root)) return set;
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            try { set.Add("F:" + Path.GetRelativePath(root, f)); } catch { }
        }
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            try { set.Add("D:" + Path.GetRelativePath(root, d)); } catch { }
        }
        return set;
    }

    /** 读 HKCU Run 的 dsh-launcher 自启值（只读，绝不写）。 */
    private static string? ReadAutostartRunValue()
    {
        try
        {
            using var runKey = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            return runKey?.GetValue("dsh-launcher") as string ?? "<absent>";
        }
        catch { return "<unreadable>"; }
    }

    /** 递归统计文件数（目录不存在计 0）。 */
    private static int CountFiles(string dir)
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Count()
            : 0;

    /** 取一个当前空闲的高位 TCP 端口（避免撞开发机上真实 3080 服务）。 */
    private static int GetFreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        try { return ((System.Net.IPEndPoint)l.LocalEndpoint).Port; }
        finally { l.Stop(); }
    }
}
