using System.Drawing;
using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>v0.3.0 新特性纯逻辑测试：配置降级、插件检测、多显示器窗口恢复、统一日志轮转、
/// 延迟更新状态机、窗口状态持久化。只测纯逻辑（可注入），不触碰 UI/进程。
/// 注意：本类含写 Logger 的用例（P1-4 损坏告警），已加入 LoggerState 串行集合。</summary>
[Collection("LoggerState")]
public class V030FeaturesTests
{
    // ---------- 配置降级（ResolveEffectiveLifetime） ----------

    [Fact]
    public void ResolveEffectiveLifetime_PluginMissingWithStaleField_FallsBackAndPurges()
    {
        var (mode, purge) = ShellLogic.PluginConfig.ResolveEffectiveLifetime("{\"serviceLifetime\":0}", pluginPresent: false);
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
        var (mode, purge) = ShellLogic.PluginConfig.ResolveEffectiveLifetime(json, pluginPresent: true);
        Assert.False(purge);
        Assert.Equal(ShellLogic.RuntimeConfig.ParseLifetimeMode(json), mode);
    }

    [Fact]
    public void ResolveEffectiveLifetime_PluginMissingWithoutField_NoPurge()
    {
        // 字段本来就不存在 → 无需重写文件（幂等）
        var (mode, purge) = ShellLogic.PluginConfig.ResolveEffectiveLifetime("{\"other\":1}", pluginPresent: false);
        Assert.Equal(ShellLogic.ServiceLifetime.FollowWindow, mode);
        Assert.False(purge);
    }

    [Theory]
    [InlineData("{\"x_serviceLifetime_note\":\"abc\"}")] // 键名带子串，顶层无 serviceLifetime
    [InlineData("{\"other\":{\"serviceLifetime\":1}}")]   // 值带子串，顶层无 serviceLifetime
    public void ResolveEffectiveLifetime_PluginMissing_SubstringOnlyKey_NoPurge(string json)
    {
        // 质量治理 P2-4：旧 Contains 判定会误报，精确键判定下这些都不该清理。
        var (mode, purge) = ShellLogic.PluginConfig.ResolveEffectiveLifetime(json, pluginPresent: false);
        Assert.Equal(ShellLogic.ServiceLifetime.FollowWindow, mode);
        Assert.False(purge);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(-1)]
    public void ResolveEffectiveLifetime_PluginPresent_OutOfRangeValue_Purges(int n)
    {
        // 插件在但值越界（3/-1 非法）→ 回退 FollowWindow 且标记清理（A4 R2）。
        var (mode, purge) = ShellLogic.PluginConfig.ResolveEffectiveLifetime($"{{\"serviceLifetime\":{n}}}", pluginPresent: true);
        Assert.Equal(ShellLogic.ServiceLifetime.FollowWindow, mode);
        Assert.True(purge, "越界值应被清理");
    }

    [Fact]
    public void ResolveEffectiveLifetime_PluginPresent_ValidValue_NoPurge()
    {
        // 插件在且值合法 → 保留用户选择，不清理（显式断言）。
        var (mode, purge) = ShellLogic.PluginConfig.ResolveEffectiveLifetime("{\"serviceLifetime\":0}", pluginPresent: true);
        Assert.Equal(ShellLogic.ServiceLifetime.AlwaysOn, mode);
        Assert.False(purge);
    }

    // ---------- 插件物理存在检测（IsLifetimePluginInstalled） ----------

    [Fact]
    public void IsLifetimePluginInstalled_NodeModulesEntity_Detected()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(Path.Combine(tmp.Path, "profiles", "web", "node_modules", "dsh-launcher-lifetime"));
        Assert.True(ShellLogic.PluginConfig.IsLifetimePluginInstalled(tmp.Path));
    }

    [Fact]
    public void IsLifetimePluginInstalled_ManifestDependencies_Detected()
    {
        using var tmp = new TempDir();
        var profile = Path.Combine(tmp.Path, "profiles", "web");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "package.json"),
            "{\"dependencies\":{\"dsh-launcher-lifetime\":\"file:../x\"}}");
        Assert.True(ShellLogic.PluginConfig.IsLifetimePluginInstalled(tmp.Path));
    }

    [Fact]
    public void IsLifetimePluginInstalled_ManifestBundles_Detected()
    {
        using var tmp = new TempDir();
        var profile = Path.Combine(tmp.Path, "profiles", "web");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, "package.json"),
            "{\"dsh\":{\"profile\":{\"bundles\":[\"@deepseek-ai/dsh-base\",\"dsh-launcher-lifetime\"]}}}");
        Assert.True(ShellLogic.PluginConfig.IsLifetimePluginInstalled(tmp.Path));
    }

    [Fact]
    public void IsLifetimePluginInstalled_Absent_ReturnsFalse()
    {
        using var tmp = new TempDir();
        Assert.False(ShellLogic.PluginConfig.IsLifetimePluginInstalled(tmp.Path));
        // 有任何读取异常也不得误报已安装
        Directory.CreateDirectory(Path.Combine(tmp.Path, "profiles", "web"));
        File.WriteAllText(Path.Combine(tmp.Path, "profiles", "web", "package.json"), "{broken json");
        Assert.False(ShellLogic.PluginConfig.IsLifetimePluginInstalled(tmp.Path));
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

    // ---------- 统一日志轮转判定（Logger.ShouldRotate，阈值归口 LoggerTests） ----------
    // （P1-1 去重：轮转阈值断言已合并至 LoggerTests.ShouldRotate_Thresholds）

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
        Assert.False(loaded.IsMaximized); // 默认 false
    }

    [Fact]
    public void WindowStateStore_RoundTrip_IsMaximized()
    {
        using var tmp = new TempDir();
        WindowStateStore.Init(tmp.Path);
        // 保存最大化状态
        WindowStateStore.Save(new WindowStateStore.WindowState(100, 200, 1280, 840, IsMaximized: true));
        var loaded = WindowStateStore.Load();
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsMaximized);
        Assert.Equal((100, 200, 1280, 840), (loaded.X, loaded.Y, loaded.WidthLogical, loaded.HeightLogical));
        // 保存非最大化状态
        WindowStateStore.Save(new WindowStateStore.WindowState(200, 300, 1024, 768, IsMaximized: false));
        loaded = WindowStateStore.Load();
        Assert.NotNull(loaded);
        Assert.False(loaded!.IsMaximized);
    }

    [Fact]
    public void WindowStateStore_IsMaximized_BackwardCompat()
    {
        // 旧版 JSON 没有 IsMaximized 字段，应默认 false
        using var tmp = new TempDir();
        WindowStateStore.Init(tmp.Path);
        File.WriteAllText(Path.Combine(tmp.Path, "window-state.json"),
            """{"X":100,"Y":200,"WidthLogical":1280,"HeightLogical":840}""");
        var loaded = WindowStateStore.Load();
        Assert.NotNull(loaded);
        Assert.False(loaded!.IsMaximized); // 旧版无此字段 → false
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
        var (version, failCount, tarball, prefetched, _) = StagedUpdate.ReadPending();
        Assert.Equal("1.2.3", version);
        Assert.Equal(0, failCount);
        Assert.Null(tarball); // 未传 tarball → 旧兼容（回退线上）
        Assert.False(prefetched); // 未传 prefetched → 默认 false（诚实：不承诺秒装）
    }

    [Fact]
    public void StagedUpdate_MarkApplyFailed_IncrementsCount()
    {
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        StagedUpdate.MarkPending("1.2.3");
        StagedUpdate.MarkApplyFailed();
        StagedUpdate.MarkApplyFailed();
        var (version, failCount, _, _, _) = StagedUpdate.ReadPending();
        Assert.Equal("1.2.3", version);
        Assert.Equal(2, failCount);
    }

    [Fact]
    public void StagedUpdate_MarkPending_WithTarball_PreservedThroughMarkApplyFailed()
    {
        // 任务：下载阶段记录本地 tarball，MarkApplyFailed 不得丢弃它（应用失败重试仍需本地安装）
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        StagedUpdate.MarkPending("1.2.3", "deepseek-ai-dsh-1.2.3.tgz", prefetched: true);
        StagedUpdate.MarkApplyFailed();
        var (_, _, tarball, prefetched, _) = StagedUpdate.ReadPending();
        Assert.Equal("deepseek-ai-dsh-1.2.3.tgz", tarball);
        Assert.True(prefetched, "MarkApplyFailed 必须保留 prefetched 标志（应用失败重试仍需诚实文案）");
    }

    [Fact]
    public void StagedUpdate_LocateTarball_PrefersPendingName_ThenRule_ThenGlob()
    {
        // 任务：应用时优先本地 tarball（不现场拉）——按 pending 名 → 命名规则 → staging 模糊匹配三级定位
        using var tmp = new TempDir();
        StagedUpdate.Init(tmp.Path);
        var staging = Path.Combine(tmp.Path, "staging");
        Directory.CreateDirectory(staging);

        // ① 无 staging → null
        Directory.Delete(staging, recursive: true);
        Assert.Null(StagedUpdate.LocateTarball("1.2.3", "deepseek-ai-dsh-1.2.3.tgz"));
        Directory.CreateDirectory(staging);

        // ② pending 名精确匹配
        File.WriteAllText(Path.Combine(staging, "deepseek-ai-dsh-1.2.3.tgz"), "pack");
        Assert.Equal(Path.Combine(staging, "deepseek-ai-dsh-1.2.3.tgz"),
            StagedUpdate.LocateTarball("1.2.3", "deepseek-ai-dsh-1.2.3.tgz"));

        // ③ pending 名缺失（旧记录）→ 命名规则兜底
        File.Delete(Path.Combine(staging, "deepseek-ai-dsh-1.2.3.tgz"));
        File.WriteAllText(Path.Combine(staging, "deepseek-ai-dsh-1.2.3.tgz"), "pack");
        Assert.Equal(Path.Combine(staging, "deepseek-ai-dsh-1.2.3.tgz"),
            StagedUpdate.LocateTarball("1.2.3", null));

        // ④ 命名规则文件名大小写/后缀差异 → 命中实际存在的 tarball
        // （Windows 文件系统大小写不敏感：规则兜底构造的小写路径也能打开大写文件，
        //   断言"返回路径指向存在的文件"而非精确字符串，避免大小写差异的假失败）
        File.Delete(Path.Combine(staging, "deepseek-ai-dsh-1.2.3.tgz"));
        File.WriteAllText(Path.Combine(staging, "DEEPSEEK-AI-DSH-1.2.3.tgz"), "pack");
        var located = StagedUpdate.LocateTarball("1.2.3", null);
        Assert.NotNull(located);                       // 能找到
        Assert.True(File.Exists(located), "返回路径必须指向已存在的 tarball");

        // ⑤ 完全找不到 → null（回退线上拉取）
        Directory.Delete(staging, recursive: true);
        Assert.Null(StagedUpdate.LocateTarball("9.9.9", null));
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
        var (version, failCount, tarball, prefetched, _) = StagedUpdate.ReadPending();
        Assert.Equal("1.2.3", version);
        Assert.Equal(0, failCount); // 旧格式兼容：无 failCount → 0
        Assert.Null(tarball);       // 旧格式兼容：无 tarball → null（回退线上）
        Assert.False(prefetched);   // 旧格式兼容：无 prefetched → false（诚实：不承诺秒装）
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

    // ---------- 状态文件损坏告警（P1-4：不静默回退，补 Warn 可诊断） ----------

    [Fact]
    public void WindowStateStore_CorruptFile_Warns()
    {
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "dsh.log");
        Logger.Init(log);
        WindowStateStore.Init(tmp.Path);
        File.WriteAllText(Path.Combine(tmp.Path, "window-state.json"), "{broken");
        Assert.Null(WindowStateStore.Load()); // 容错回退保持不变
        Assert.Contains("window-state.json is corrupt", File.ReadAllText(log)); // 但必须留痕
    }

    [Fact]
    public void StagedUpdate_CorruptFile_Warns()
    {
        using var tmp = new TempDir();
        var log = Path.Combine(tmp.Path, "dsh.log");
        Logger.Init(log);
        StagedUpdate.Init(tmp.Path);
        File.WriteAllText(Path.Combine(tmp.Path, "pending-update.json"), "{broken");
        var (version, failCount, tarball, prefetched, _) = StagedUpdate.ReadPending();
        Assert.Null(version);          // 容错按无记录处理
        Assert.Equal(0, failCount);
        Assert.Null(tarball);
        Assert.False(prefetched);
        Assert.Contains("pending-update.json is corrupt", File.ReadAllText(log)); // 但必须留痕
    }

    // ---------- Node 缺失原因（v0.3.1：确认框区分"未安装"与"版本过旧"） ----------
    // （P1-1 清洗：NodeMissingReason 依赖真实 PATH/注册表/便携目录，两个"永真假绿灯"测试已删除——
    // 该行为由 negative 套件进程级覆盖，Node 版本门槛契约测试见 ContractTests.IsUsableNodeVersion）
}
