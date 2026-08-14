using DshWeb;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace DshShell.Tests;

/// <summary>ShellLogic 纯逻辑单元测试。</summary>
public class ShellLogicTests
{
    // ---------- 目标地址解析（DSH_WEB_URL） ----------

    [Theory]
    [InlineData(null, "http://127.0.0.1:3080", 3080)]
    [InlineData("", "http://127.0.0.1:3080", 3080)]
    [InlineData("   ", "http://127.0.0.1:3080", 3080)]
    [InlineData("http://127.0.0.1:3090", "http://127.0.0.1:3090", 3090)]
    [InlineData("http://127.0.0.1:3090/", "http://127.0.0.1:3090", 3090)]
    [InlineData("https://example.com:8443", "https://example.com:8443", 8443)]
    [InlineData("not a url", "http://127.0.0.1:3080", 3080)]   // 非法输入回退默认
    [InlineData("ftp://x:21", "http://127.0.0.1:3080", 3080)]  // 非 http(s) 回退默认
    public void ResolveTarget_Works(string? env, string expectedUrl, int expectedPort)
    {
        var (url, port) = ShellLogic.ResolveTarget(env);
        Assert.Equal(expectedUrl, url);
        Assert.Equal(expectedPort, port);
    }

    // ---------- 弹窗分类 ----------

    [Theory]
    [InlineData("http://127.0.0.1:3080/foo", ShellLogic.PopupTarget.Internal)]
    [InlineData("http://localhost:3080/foo", ShellLogic.PopupTarget.Internal)]
    [InlineData("https://127.0.0.1:3080/foo", ShellLogic.PopupTarget.Internal)]
    [InlineData("https://localhost/foo", ShellLogic.PopupTarget.Internal)]
    [InlineData("https://github.com/omdsh-dev/dsh-notification", ShellLogic.PopupTarget.External)]
    [InlineData("http://example.com/a?b=1", ShellLogic.PopupTarget.External)]
    [InlineData("blob:http://127.0.0.1:3080/uuid-123", ShellLogic.PopupTarget.Default)]
    [InlineData("data:text/plain,hello", ShellLogic.PopupTarget.Default)]
    [InlineData("about:blank", ShellLogic.PopupTarget.Default)]
    [InlineData("file:///C:/x.html", ShellLogic.PopupTarget.Default)]
    [InlineData("not a uri", ShellLogic.PopupTarget.Default)]
    [InlineData("", ShellLogic.PopupTarget.Default)]
    [InlineData(null, ShellLogic.PopupTarget.Default)]
    public void ClassifyPopup_ReturnsExpected(string? raw, ShellLogic.PopupTarget expected) =>
        Assert.Equal(expected, ShellLogic.ClassifyPopup(raw));

    // ---------- 权限策略 ----------

    [Theory]
    [InlineData(CoreWebView2PermissionKind.Notifications, true)]
    [InlineData(CoreWebView2PermissionKind.ClipboardRead, true)]
    [InlineData(CoreWebView2PermissionKind.Autoplay, true)]
    [InlineData(CoreWebView2PermissionKind.MultipleAutomaticDownloads, true)]
    [InlineData(CoreWebView2PermissionKind.PersistentStorage, true)]
    [InlineData(CoreWebView2PermissionKind.Microphone, false)]
    [InlineData(CoreWebView2PermissionKind.Camera, false)]
    [InlineData(CoreWebView2PermissionKind.Geolocation, false)]
    [InlineData(CoreWebView2PermissionKind.FileReadWrite, false)]
    [InlineData(CoreWebView2PermissionKind.LocalFonts, false)]
    [InlineData(CoreWebView2PermissionKind.WindowManagement, false)]
    [InlineData(CoreWebView2PermissionKind.UnknownPermission, false)]
    public void IsAutoGrantedPermission_MatchesPolicy(CoreWebView2PermissionKind kind, bool expected) =>
        Assert.Equal(expected, ShellLogic.IsAutoGrantedPermission(kind));

    // ---------- 下载文件名推导 ----------

    [Theory]
    [InlineData("attachment; filename=report.pdf", null, null, "report.pdf")]
    [InlineData("attachment; filename=\"my file.txt\"", null, null, "my file.txt")]
    [InlineData("attachment; filename=export.zip; filename*=UTF-8''x.zip", null, null, "export.zip")] // 普通 filename 优先
    [InlineData("attachment; filename*=UTF-8''%E6%B5%8B%E8%AF%95.txt", null, null, "测试.txt")]       // RFC 5987 中文解码
    [InlineData(null, "https://example.com/a/b/archive.tar.gz", null, "archive.tar.gz")]
    [InlineData(null, "http://127.0.0.1:3080/api/export?fmt=json", null, "export")]                  // URI 尾段去掉查询串
    [InlineData("attachment; filename=dup.txt", "https://example.com/other.bin", null, "dup.txt")]    // Content-Disposition 优先
    [InlineData(null, "blob:http://127.0.0.1:3080/abc-123", "application/zip", null)]                 // blob + MIME → 补扩展名（断言见下）
    public void SuggestDownloadName_CoreCases(string? disposition, string? uri, string? mime, string? expected)
    {
        var name = ShellLogic.SuggestDownloadName(disposition, uri, mime);
        if (expected is null)
        {
            // blob + MIME：应为 dsh-时间戳 + MIME 扩展名
            Assert.StartsWith("dsh-", name);
            Assert.EndsWith(".zip", name);
        }
        else
        {
            Assert.Equal(expected, name);
        }
    }

    [Theory]
    [InlineData(null, "blob:http://127.0.0.1:3080/abc", "text/markdown", ".md")]
    [InlineData(null, "blob:http://127.0.0.1:3080/abc", "image/png", ".png")]
    [InlineData(null, "blob:http://127.0.0.1:3080/abc", "application/json", ".json")]
    [InlineData(null, "blob:http://127.0.0.1:3080/abc", "application/octet-stream", null)]   // 未知 MIME 不加扩展名
    [InlineData(null, "blob:http://127.0.0.1:3080/abc", "text/plain; charset=utf-8", ".txt")] // 带 charset 的 MIME
    [InlineData(null, "data:text/plain,hello", "text/plain", ".txt")]                        // data: 同样走兜底
    [InlineData(null, null, null, null)]                                                     // 全空 → 时间戳兜底
    public void SuggestDownloadName_MimeFallback(string? disposition, string? uri, string? mime, string? expectedExt)
    {
        var name = ShellLogic.SuggestDownloadName(disposition, uri, mime);
        Assert.StartsWith("dsh-", name);
        if (expectedExt is null)
            Assert.DoesNotContain(".", name[(name.IndexOf('-') + 1)..]); // 时间戳名不含点
        else
            Assert.EndsWith(expectedExt, name);
    }

    // ---------- 文件名清理 ----------

    [Theory]
    [InlineData("a<b>c:d|e?f*g", "a_b_c_d_e_f_g")]
    [InlineData("  spaced  ", "spaced")]
    [InlineData("trailing.", "trailing")]            // 结尾点被 Windows 吞掉，主动去掉
    [InlineData("trailing..", "trailing")]
    [InlineData("CON", "_CON")]                      // 保留设备名
    [InlineData("con.txt", "_con.txt")]              // 保留名带扩展名同样非法
    [InlineData("NUL", "_NUL")]
    [InlineData("COM1", "_COM1")]
    [InlineData("LPT9.json", "_LPT9.json")]
    [InlineData("normal-name.json", "normal-name.json")]
    [InlineData("中文名.txt", "中文名.txt")]
    [InlineData("", null)]                           // 空 → 时间戳兜底（断言见下）
    [InlineData("   ", null)]
    [InlineData("...", null)]
    public void SanitizeFileName_HandlesEdgeCases(string input, string? expected)
    {
        var result = ShellLogic.SanitizeFileName(input);
        if (expected is null)
            Assert.StartsWith("dsh-", result);
        else
            Assert.Equal(expected, result);
    }

    [Fact]
    public void SanitizeFileName_ResultIsUsableAsWindowsFileName()
    {
        var samples = new[] { "CON", "a<b:c", "trailing.", "valid.txt", "中文 文件.md", "NUL.txt" };
        foreach (var s in samples)
        {
            var result = ShellLogic.SanitizeFileName(s);
            Assert.NotEqual(string.Empty, result);
            Assert.All(result, c => Assert.DoesNotContain(c, Path.GetInvalidFileNameChars()));
        }
    }

    [Fact]
    public void PickOldInstalls_EmptyOrSingle_ReturnsEmpty()
    {
        Assert.Empty(ShellLogic.PickOldInstalls(new()));
        Assert.Empty(ShellLogic.PickOldInstalls(new()
        {
            new ShellLogic.InstalledDsh("{A}", new Version(0, 1, 6)),
        }));
    }

    [Fact]
    public void PickOldInstalls_KeepsOnlyNewest()
    {
        var olds = ShellLogic.PickOldInstalls(new()
        {
            new ShellLogic.InstalledDsh("{A}", new Version(0, 1, 6)),
            new ShellLogic.InstalledDsh("{B}", new Version(0, 1, 5)),
        });
        Assert.Equal("{B}", Assert.Single(olds).ProductCode);
    }

    [Fact]
    public void PickOldInstalls_KeepsOneOfTiedNewest()
    {
        var olds = ShellLogic.PickOldInstalls(new()
        {
            new ShellLogic.InstalledDsh("{A}", new Version(0, 1, 6)),
            new ShellLogic.InstalledDsh("{B}", new Version(0, 1, 6)),
            new ShellLogic.InstalledDsh("{C}", new Version(0, 1, 4)),
        });
        Assert.Equal(2, olds.Count);
        Assert.Contains(olds, o => o.ProductCode == "{C}");
        Assert.DoesNotContain(olds, o => o.ProductCode == "{A}");
    }

    [Fact]
    public void PickOldInstalls_ThreeOldVersions_AllReturned()
    {
        var olds = ShellLogic.PickOldInstalls(new()
        {
            new ShellLogic.InstalledDsh("{A}", new Version(0, 1, 6)),
            new ShellLogic.InstalledDsh("{B}", new Version(0, 1, 3)),
            new ShellLogic.InstalledDsh("{C}", new Version(0, 1, 0)),
        });
        Assert.Equal(2, olds.Count);
        Assert.All(olds, o => Assert.NotEqual("{A}", o.ProductCode));
    }

    [Fact]
    public void PickOldInstalls_NeverPicksCurrentCode()
    {
        var olds = ShellLogic.PickOldInstalls(new()
        {
            new ShellLogic.InstalledDsh("{CUR}", new Version(0, 1, 6)),
            new ShellLogic.InstalledDsh("{OLD}", new Version(0, 1, 6)),
            new ShellLogic.InstalledDsh("{OLD2}", new Version(0, 1, 4)),
        }, "{CUR}");
        Assert.Equal(2, olds.Count);
        Assert.DoesNotContain(olds, o => o.ProductCode == "{CUR}");
    }

    [Fact]
    public void PickOldInstalls_OnlyCurrent_ReturnsEmpty()
    {
        Assert.Empty(ShellLogic.PickOldInstalls(new()
        {
            new ShellLogic.InstalledDsh("{CUR}", new Version(0, 1, 6)),
        }, "{CUR}"));
    }

    [Fact]
    public void PickOldInstalls_CurrentIsOldest_KeepsOtherInstead()
    {
        // 当前运行版本之外只有一个其他版本 → 两者都保留（不清理，无法判断该卸哪个）
        var olds = ShellLogic.PickOldInstalls(new()
        {
            new ShellLogic.InstalledDsh("{NEW}", new Version(0, 1, 7)),
            new ShellLogic.InstalledDsh("{CUR}", new Version(0, 1, 6)),
        }, "{CUR}");
        Assert.Empty(olds);
    }

    [Fact]
    public void FilterByUpgradeCode_KeepsOnlyMatching()
    {
        var candidates = new List<ShellLogic.InstalledDsh>
        {
            new("{A}", new Version(0, 1, 5)),
            new("{B}", new Version(0, 1, 6)),
        };
        // {A} 匹配我们的 UpgradeCode，{B} 是其他软件（不同 UpgradeCode）
        var result = ShellLogic.FilterByUpgradeCode(candidates, code =>
            code == "{A}" ? ShellLogic.DshUpgradeCode : "{11111111-2222-3333-4444-555555555555}");
        Assert.Equal("{A}", Assert.Single(result).ProductCode);
    }

    [Fact]
    public void FilterByUpgradeCode_FailedReadIsExcluded()
    {
        var candidates = new List<ShellLogic.InstalledDsh>
        {
            new("{A}", new Version(0, 1, 5)),
        };
        // 读取 UpgradeCode 失败（返回 null）→ 宁可不清理也不误删
        var result = ShellLogic.FilterByUpgradeCode(candidates, _ => null);
        Assert.Empty(result);
    }

    [Fact]
    public void FilterByUpgradeCode_SameNameOtherSoftware_Excluded()
    {
        var candidates = new List<ShellLogic.InstalledDsh>
        {
            new("{OTHER}", new Version(9, 9, 9)), // 恰好同名但属于其他软件
        };
        var result = ShellLogic.FilterByUpgradeCode(candidates, _ => "{99999999-9999-9999-9999-999999999999}");
        Assert.Empty(result);
    }

    [Fact]
    public void IsOurShortcutTarget_OnlyDshWebExe()
    {
        Assert.True(ShellLogic.IsOurShortcutTarget(@"C:\Program Files\dsh-launcher\DshWeb.exe"));
        Assert.True(ShellLogic.IsOurShortcutTarget(@"E:\custom\DshWeb.exe"));
        Assert.False(ShellLogic.IsOurShortcutTarget(@"C:\Windows\notepad.exe"));
        Assert.False(ShellLogic.IsOurShortcutTarget(@"C:\Windows\System32\msiexec.exe"));
        Assert.False(ShellLogic.IsOurShortcutTarget(null));
        Assert.False(ShellLogic.IsOurShortcutTarget(""));
    }

    [Fact]
    public void HasExecutableOnPath_FindsNode()
    {
        var path = @"C:\Windows\System32" + Path.PathSeparator + @"C:\Program Files\nodejs";
        // System32 必有 cmd.exe → 找到
        Assert.True(ShellLogic.HasExecutableOnPath("cmd.exe", path));
        // node.exe 装在未知位置（如用户目录）时，给定 PATH 不应误报存在
        Assert.False(ShellLogic.HasExecutableOnPath("node.exe", path));
    }

    [Fact]
    public void HasExecutableOnPath_EmptyOrNullPath_ReturnsFalse()
    {
        Assert.False(ShellLogic.HasExecutableOnPath("node.exe", null));
        Assert.False(ShellLogic.HasExecutableOnPath("node.exe", ""));
        Assert.False(ShellLogic.HasExecutableOnPath("", @"C:\Windows"));
    }

    [Fact]
    public void HasExecutableOnPath_IgnoresBadEntries()
    {
        // 包含不可访问/不存在的目录条目不应抛异常
        var path = @"Z:\does-not-exist" + Path.PathSeparator + @"C:\Windows\System32";
        Assert.True(ShellLogic.HasExecutableOnPath("cmd.exe", path));
        Assert.False(ShellLogic.HasExecutableOnPath("node.exe", path));
    }

    [Fact]
    public void LogShowsStartupError_DetectsNpxAndNpmFailures()
    {
        Assert.True(ShellLogic.LogShowsStartupError("npm ERR! code ENOTFOUND\nsomething"));
        Assert.True(ShellLogic.LogShowsStartupError("'npx' 不是内部或外部命令"));
        Assert.True(ShellLogic.LogShowsStartupError("EACCES: permission denied"));
        Assert.True(ShellLogic.LogShowsStartupError("Cannot find module 'x'"));
        Assert.False(ShellLogic.LogShowsStartupError("dsh web listening on 3080"));
        Assert.False(ShellLogic.LogShowsStartupError(""));
        Assert.False(ShellLogic.LogShowsStartupError(null));
    }

    [Fact]
    public void ReadLogTail_ReturnsLastLines()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dsh-test-{Guid.NewGuid():N}.log");
        try
        {
            File.WriteAllLines(path, new[] { "l1", "l2", "l3", "l4", "l5" });
            var tail = ShellLogic.ReadLogTail(path, 3);
            Assert.Equal(new[] { "l3", "l4", "l5" }, tail);
            Assert.Single(ShellLogic.ReadLogTail(path, 1));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReadLogTail_MissingFile_ReturnsEmpty()
    {
        Assert.Empty(ShellLogic.ReadLogTail(Path.Combine(Path.GetTempPath(), "no-such-dsh-log.log"), 10));
    }

    [Fact]
    public void ParseLifetimeMode_ReadsModes()
    {
        Assert.Equal(ShellLogic.ServiceLifetime.AlwaysOn, ShellLogic.ParseLifetimeMode(null));
        Assert.Equal(ShellLogic.ServiceLifetime.AlwaysOn, ShellLogic.ParseLifetimeMode(""));
        Assert.Equal(ShellLogic.ServiceLifetime.Tray, ShellLogic.ParseLifetimeMode("{\"serviceLifetime\":1}"));
        Assert.Equal(ShellLogic.ServiceLifetime.FollowWindow, ShellLogic.ParseLifetimeMode("{\"serviceLifetime\":2}"));
        Assert.Equal(ShellLogic.ServiceLifetime.AlwaysOn, ShellLogic.ParseLifetimeMode("{\"serviceLifetime\":0}"));
    }

    [Fact]
    public void ParseLifetimeMode_InvalidFallsBackToDefault()
    {
        Assert.Equal(ShellLogic.ServiceLifetime.AlwaysOn, ShellLogic.ParseLifetimeMode("not json"));
        Assert.Equal(ShellLogic.ServiceLifetime.AlwaysOn, ShellLogic.ParseLifetimeMode("{\"serviceLifetime\":99}"));
        Assert.Equal(ShellLogic.ServiceLifetime.AlwaysOn, ShellLogic.ParseLifetimeMode("{\"other\":1}"));
        Assert.Equal(ShellLogic.ServiceLifetime.FollowWindow, ShellLogic.ParseLifetimeMode("{\"other\":1}", ShellLogic.ServiceLifetime.FollowWindow));
    }
}
