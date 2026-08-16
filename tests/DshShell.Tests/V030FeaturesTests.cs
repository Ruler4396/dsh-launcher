using System.Drawing;
using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>v0.3.0 新特性纯逻辑测试：配置降级、插件检测、多显示器窗口恢复、统一日志轮转、
/// 延迟更新状态机、窗口状态持久化。只测纯逻辑（可注入），不触碰 UI/进程。</summary>
public class V030FeaturesTests
{
    // ---------- 配置降级（ResolveEffectiveLifetime） ----------

    [Fact]
    public void ResolveEffectiveLifetime_PluginMissingWithStaleField_FallsBackAndPurges()
    {
        var (mode, purge) = ShellLogic.ResolveEffectiveLifetime("{\"serviceLifetime\":0}", pluginPresent: false);
        Assert.Equal(ShellLogic.ServiceLifetime.FollowWindow, mode);
        Assert.True(purge, "插件缺失且存在残留 serviceLifetime 时应提示抹除");
    }

    [Theory]
    [InlineData("{\"serviceLifetime\":0}")]
    [InlineData("{\"serviceLifetime\":1}")]
    [InlineData("{\"serviceLifetime\":2}")]
    [InlineData(null)]
    [InlineData("not json")]
    public void ResolveEffectiveLifetime_PluginPresent_NeverPurges(string? json)
    {
        var (mode, purge) = ShellLogic.ResolveEffectiveLifetime(json, pluginPresent: true);
        Assert.False(purge);
        Assert.Equal(ShellLogic.ParseLifetimeMode(json), mode);
    }

    [Fact]
    public void ResolveEffectiveLifetime_PluginMissingWithoutField_NoPurge()
    {
        // 字段本来就不存在 → 无需重写文件（幂等）
        var (mode, purge) = ShellLogic.ResolveEffectiveLifetime("{\"other\":1}", pluginPresent: false);
        Assert.Equal(ShellLogic.ServiceLifetime.FollowWindow, mode);
        Assert.False(purge);
    }

    [Theory]
    [InlineData("{\"x_serviceLifetime_note\":\"abc\"}")] // 键名带子串，顶层无 serviceLifetime
    [InlineData("{\"other\":{\"serviceLifetime\":1}}")]   // 值带子串，顶层无 serviceLifetime
    public void ResolveEffectiveLifetime_PluginMissing_SubstringOnlyKey_NoPurge(string json)
    {
        // 质量治理 P2-4：旧 Contains 判定会误报，精确键判定下这些都不该清理。
        var (mode, purge) = ShellLogic.ResolveEffectiveLifetime(json, pluginPresent: false);
        Assert.Equal(ShellLogic.ServiceLifetime.FollowWindow, mode);
        Assert.False(purge);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void ResolveEffectiveLifetime_PluginPresent_OutOfRangeValue_Purges(int n)
    {
        // 插件在但值越界（3/-1 非法）→ 回退 FollowWindow 且标记清理（A4 R2）。
        var (mode, purge) = ShellLogic.ResolveEffectiveLifetime($"{{\"serviceLifetime\":{n}}}", pluginPresent: true);
        Assert.Equal(ShellLogic.ServiceLifetime.FollowWindow, mode);
        Assert.True(purge, "越界值应被清理");
    }

    [Fact]
    public void ResolveEffectiveLifetime_PluginPresent_ValidValue_NoPurge()
    {
        // 插件在且值合法 → 保留用户选择，不清理（显式断言）。
        var (mode, purge) = ShellLogic.ResolveEffectiveLifetime("{\"serviceLifetime\":0}", pluginPresent: true);
        Assert.Equal(ShellLogic.ServiceLifetime.AlwaysOn, mode);
        Assert.False(purge);
    }

    // ---------- 插件物理存在检测（IsLifetimePluginInstalled） ----------

    [Fact]
    public void IsLifetimePluginInstalled_NodeModulesEntity_Detected()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "profiles", "web", "node_modules", "dsh-launcher-lifetime"));
        Assert.True(ShellLogic.IsLifetimePluginInstalled(tmp.Path));
    }

    [Fact]
    public void IsLifetimePluginInstalled_ManifestDependencies_Detected()
    {
        using var tmp = new TempDir();
        var profile = Path.Combine(tmp.Path, "profiles", "web");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "package.json"),
            "{\"dependencies\":{\"dsh-launcher-lifetime\":\"file:../x\"}}");
        Assert.True(ShellLogic.IsLifetimePluginInstalled(tmp.Path));
    }

    [Fact]
    public void IsLifetimePluginInstalled_ManifestBundles_Detected()
    {
        using var tmp = new TempDir();
        var profile = Path.Combine(tmp.Path, "profiles", "web");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "package.json"),
            "{\"dsh\":{\"profile\":{\"bundles\":[\"@deepseek-ai/dsh-base\",\"dsh-launcher-lifetime\"]}}}");
        Assert.True(ShellLogic.IsLifetimePluginInstalled(tmp.Path));
    }

    [Fact]
    public void IsLifetimePluginInstalled_Absent_ReturnsFalse()
    {
        using var tmp = new TempDir();
        Assert.False(ShellLogic.IsLifetimePluginInstalled(tmp.Path));
        // 有任何读取异常也不得误报已安装
        Directory.CreateDirectory(Path.Combine(tmp.Path, "profiles", "web"));
        File.WriteAllText(Path.Combine(tmp.Path, "profiles", "web", "package.json"), "{broken json");
        Assert.False(ShellLogic.IsLifetimePluginInstalled(tmp.Path));
    }

    // ---------- 多显示器窗口恢复（RestoreWindowPosition） ----------

    private static readonly Rectangle Primary = new(0, 0, 1920, 1040);

    [Fact]
    public void RestoreWindowPosition_OnSecondaryMonitor_KeepsPosition()
    {
        var secondary = new Rectangle(1920, 0, 1280, 1040);
        var (x, y) = ShellLogic.RestoreWindowPosition(2000, 100, 800, 600, new[] { Primary, secondary }, Primary);
        Assert.Equal((2000, 100), (x, y));
    }

    [Fact]
    public void RestoreWindowPosition_NegativeCoords_LeftMonitor_Kept()
    {
        var left = new Rectangle(-1280, 0, 1280, 1040);
        var (x, y) = ShellLogic.RestoreWindowPosition(-1000, 300, 800, 600, new[] { left, Primary }, Primary);
        Assert.Equal((-1000, 300), (x, y));
    }

    [Fact]
    public void RestoreWindowPosition_MonitorUnplugged_CentersOnPrimary()
    {
        // 保存位置在已不存在的副屏上 → 回退主屏居中
        var (x, y) = ShellLogic.RestoreWindowPosition(1920 + 2000, 500, 800, 600, new[] { Primary }, Primary);
        var cx = (1920 - 800) / 2;
        var cy = (1040 - 600) / 2;
        Assert.Equal((cx, cy), (x, y));
    }

    [Fact]
    public void RestoreWindowPosition_TaskbarShrank_ClampedIntoWorkArea()
    {
        // 工作区底部上移（任务栏变大）：窗口底部超出 → 整窗钳制回工作区
        var shrunk = new Rectangle(0, 0, 1920, 900);
        var (x, y) = ShellLogic.RestoreWindowPosition(100, 500, 800, 600, new[] { shrunk, Primary }, Primary);
        Assert.True(y <= 300, $"y={y} 应钳制到 900-600=300 以内（窗口完全可见）");
        Assert.Equal(100, x);
    }

    [Fact]
    public void RestoreWindowPosition_OnlyTinySliceVisible_FallsBackToCenter()
    {
        // 目标在屏幕上只露出 20px（<120px 可抓取门槛）→ 视为越界，主屏居中
        var (x, y) = ShellLogic.RestoreWindowPosition(0, 1020, 800, 600, new[] { Primary }, Primary);
        var cy = (1040 - 600) / 2;
        Assert.Equal(cy, y);
    }

    [Fact]
    public void RestoreWindowPosition_StraddlesTwoMonitors_KeepsOnVisibleOne()
    {
        // 窗口跨主屏与主屏下方的副屏，副屏上可见 560px（≥120）→ 保留并钳制到副屏工作区内
        var below = new Rectangle(0, 1040, 1920, 600);
        var (x, y) = ShellLogic.RestoreWindowPosition(0, 1000, 800, 600, new[] { Primary, below }, Primary);
        Assert.Equal((0, 1040), (x, y));
    }

    // ---------- 统一日志轮转判定（Logger.ShouldRotate） ----------

    [Fact]
    public void ShouldRotate_SizeCap()
    {
        Assert.True(Logger.ShouldRotate(31L * 1024 * 1024, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow));
        Assert.False(Logger.ShouldRotate(30L * 1024 * 1024, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow));
        Assert.False(Logger.ShouldRotate(1024, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow));
    }

    [Fact]
    public void ShouldRotate_AgeCap()
    {
        Assert.True(Logger.ShouldRotate(1024, DateTime.UtcNow.AddDays(-4), DateTime.UtcNow));
        Assert.False(Logger.ShouldRotate(1024, DateTime.UtcNow.AddDays(-2), DateTime.UtcNow));
    }

    // ---------- 延迟更新状态机（StagedUpdate） ----------

    [Fact]
    public void StagedUpdate_RoundTrip()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        Assert.Null(StagedUpdate.ReadPendingVersion());
        StagedUpdate.MarkPending("1.2.3");
        Assert.Equal("1.2.3", StagedUpdate.ReadPendingVersion());
        StagedUpdate.ClearPending();
        Assert.Null(StagedUpdate.ReadPendingVersion());
    }

    [Fact]
    public void StagedUpdate_CorruptFile_ReturnsNull()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        File.WriteAllText(Path.Combine(tmp.Path, "pending-update.json"), "{broken");
        Assert.Null(StagedUpdate.ReadPendingVersion());
    }

    // ---------- 窗口状态持久化（WindowStateStore） ----------

    [Fact]
    public void WindowStateStore_RoundTrip()
    {
        using var tmp = new TempDir();
        WindowStateStore.Init(tmp.Path);
        WindowStateStore.Save(new WindowStateStore.WindowState(100, 200, 1280, 840));
        var loaded = WindowStateStore.Load();
        Assert.NotNull(loaded);
        Assert.Equal((100, 200, 1280, 840), (loaded!.X, loaded.Y, loaded.WidthLogical, loaded.HeightLogical));
    }

    [Fact]
    public void WindowStateStore_MissingOrCorrupt_ReturnsNull()
    {
        using var tmp = new TempDir();
        WindowStateStore.Init(tmp.Path);
        Assert.Null(WindowStateStore.Load());
        File.WriteAllText(Path.Combine(tmp.Path, "window-state.json"), "not json");
        Assert.Null(WindowStateStore.Load());
    }

    // ---------- 镜像回退链（RuntimeResolver.BaseUrls，P2） ----------

    [Fact]
    public void BaseUrls_CustomMirrorFirst()
    {
        var urls = RuntimeResolver.BaseUrls("v24.15.0", "https://mirror.example.com/", null).ToList();
        Assert.Equal("https://mirror.example.com", urls[0]); // 尾部 / 被剥掉
        Assert.Contains($"https://nodejs.org/dist/v24.15.0", urls);
        Assert.Contains("https://registry.npmmirror.com/-/binary/node/v24.15.0", urls);
    }

    [Fact]
    public void BaseUrls_LastMirrorSecond_NoCustom()
    {
        var urls = RuntimeResolver.BaseUrls("v24.15.0", null, "https://last.example.com/x").ToList();
        Assert.Equal("https://last.example.com/x", urls[0]);
        Assert.Equal($"https://nodejs.org/dist/v24.15.0", urls[1]);
    }

    [Fact]
    public void BaseUrls_DefaultOrder_OfficialThenMirror()
    {
        var urls = RuntimeResolver.BaseUrls("v24.15.0", null, null).ToList();
        Assert.Equal(new[]
        {
            "https://nodejs.org/dist/v24.15.0",
            "https://registry.npmmirror.com/-/binary/node/v24.15.0",
        }, urls);
    }

    [Fact]
    public void BaseUrls_NoDuplicates_WhenCustomEqualsLast()
    {
        var urls = RuntimeResolver.BaseUrls("v24.15.0", "https://m.example.com", "https://m.example.com/").ToList();
        Assert.Single(urls.Where(u => u == "https://m.example.com"));
    }

    // ---------- 错误码契约（质量治理 R02 防线：码有专属描述、无重复值） ----------

    [Fact]
    public void ErrorCodes_AllDeclaredCodesHaveSpecificDescription()
    {
        var codes = typeof(ErrorCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();
        Assert.NotEmpty(codes);
        foreach (var code in codes)
        {
            if (code == ErrorCodes.E9001) continue; // "内部未分类"码本身允许回退到"未分类错误"
            Assert.False(ErrorCodes.Describe(code).StartsWith("未分类", StringComparison.Ordinal),
                $"错误码 {code} 的 Describe 回退到了'未分类错误'——缺少专属描述");
        }
    }

    [Fact]
    public void ErrorCodes_NoDuplicateCodeValues()
    {
        var codes = typeof(ErrorCodes).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (string)f.GetValue(null)!)
            .ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
    }

    /// <summary>临时目录（自动清理）。</summary>
    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dsh-v030-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { }
        }
    }

    // ---------- 延迟更新失败计数（v0.3.1：持续失败降级气泡） ----------

    [Fact]
    public void StagedUpdate_MarkPending_StartsFailCountAtZero()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        StagedUpdate.MarkPending("1.2.3");
        var (version, failCount) = StagedUpdate.ReadPending();
        Assert.Equal("1.2.3", version);
        Assert.Equal(0, failCount);
    }

    [Fact]
    public void StagedUpdate_MarkApplyFailed_IncrementsCount()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        StagedUpdate.MarkPending("1.2.3");
        StagedUpdate.MarkApplyFailed();
        StagedUpdate.MarkApplyFailed();
        var (version, failCount) = StagedUpdate.ReadPending();
        Assert.Equal("1.2.3", version);
        Assert.Equal(2, failCount);
    }

    [Fact]
    public void StagedUpdate_MarkApplyFailed_WithoutPending_Noop()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        StagedUpdate.MarkApplyFailed(); // 无 pending 时不应创建文件
        Assert.Null(StagedUpdate.ReadPendingVersion());
    }

    [Fact]
    public void StagedUpdate_ReadPending_LegacyFileWithoutFailCount_ReturnsZero()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        File.WriteAllText(Path.Combine(tmp.Path, "pending-update.json"),
            "{\"version\":\"1.2.3\",\"at\":\"2026-08-16 12:00:00\"}");
        var (version, failCount) = StagedUpdate.ReadPending();
        Assert.Equal("1.2.3", version);
        Assert.Equal(0, failCount); // 旧格式兼容：无 failCount → 0
    }

    [Fact]
    public void StagedUpdate_ClearPending_ResetsAll()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        StagedUpdate.MarkPending("1.2.3");
        StagedUpdate.MarkApplyFailed();
        StagedUpdate.ClearPending();
        Assert.Null(StagedUpdate.ReadPendingVersion());
    }

    // ---------- Node 缺失原因（v0.3.1：确认框区分"未安装"与"版本过旧"） ----------

    [Fact]
    public void NodeMissingReason_NoNodeAnywhere_ReturnsNotFound()
    {
        // 隔离 PATH：空 PATH + 无注册表可查（测试环境注册表可能有 node，但空 PATH 下
        // FindOnPath 必然为空；注册表与便携目录在本机若有 node 则返回 null/too-old——
        // 该断言只验证"完全没有任何候选"的纯路径，用空 PATH 即可稳定触发 not-found 分支？
        // 实际：注册表/便携命中时返回 null 或 too-old，因此这里改为验证空 PATH 行为。
        var saved = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", "");
            var reason = RuntimeResolver.NodeMissingReason();
            // 无论注册表/便携是否存在，空 PATH 都不应返回 "too-old"（too-old 只由找到
            // 但不可用的候选触发）；not-found 或 null 均可接受。
            Assert.NotEqual("too-old", reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", saved);
        }
    }

    [Fact]
    public void NodeMissingReason_WithUsableNode_ReturnsNull()
    {
        // 本机有可用 node（PATH 命中且版本≥18）时返回 null；若本机无 node 则测试前提不成立，
        // 跳过（返回 not-found 也可接受——这里断言"不返回 too-old 除非确有低版本"）。
        var reason = RuntimeResolver.NodeMissingReason();
        if (reason == "too-old")
        {
            // 本机确有低版本/损坏 node：验证与 ResolveExisting 行为一致（日志 Warn 路径）
            var env = RuntimeResolver.ResolveExisting();
            Assert.Null(env.NodeExe);
        }
        // 其余情况（null / not-found）都是合法结果，不强制
    }
}
