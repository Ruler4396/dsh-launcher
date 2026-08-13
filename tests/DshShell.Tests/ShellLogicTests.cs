using DshWeb;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace DshShell.Tests;

/// <summary>ShellLogic 纯逻辑单元测试。</summary>
public class ShellLogicTests
{
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
}
