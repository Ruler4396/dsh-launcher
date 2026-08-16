using DshWeb;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 高频安全边界单测（S2 下载自动打开 / S3 导航白名单相关纯函数）：
/// 用户每天下载文件、插件请求权限——可执行代码面（.exe/.html/.svg/.hta/.lnk 等）
/// 必须绝不自动打开；权限放行策略必须精确匹配既定清单。
/// </summary>
public class SecurityBoundaryTests
{
    // ---------- 下载自动打开安全（可执行代码面绝不打开） ----------

    [Theory]
    [InlineData("C:\\Users\\x\\Downloads\\evil.exe")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.html")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.htm")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.svg")]        // SVG 可内嵌脚本
    [InlineData("C:\\Users\\x\\Downloads\\evil.hta")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.lnk")]        // 快捷方式可指向任意程序
    [InlineData("C:\\Users\\x\\Downloads\\evil.url")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.bat")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.cmd")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.ps1")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.vbs")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.js")]         // 脚本文件
    [InlineData("C:\\Users\\x\\Downloads\\evil.msi")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.jar")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.dll")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.sys")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.scr")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.com")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.reg")]
    [InlineData("C:\\Users\\x\\Downloads\\evil.pif")]
    [InlineData("C:\\Users\\x\\Downloads\\noext")]            // 无扩展名：保守不打开
    [InlineData("C:\\Users\\x\\Downloads\\evil.exe.exe")]     // 双重扩展名迷惑
    [InlineData("")]                                          // 空路径
    [InlineData(null)]
    public void IsSafeToOpen_ExecutableSurface_NeverOpens(string? path)
    {
        Assert.False(ShellLogic.IsSafeToOpen(path), $"应拒绝自动打开: {path}");
    }

    [Theory]
    [InlineData("C:\\Users\\x\\Downloads\\photo.png")]
    [InlineData("C:\\Users\\x\\Downloads\\doc.pdf")]
    [InlineData("C:\\Users\\x\\Downloads\\notes.txt")]
    [InlineData("C:\\Users\\x\\Downloads\\data.json")]
    [InlineData("C:\\Users\\x\\Downloads\\music.mp3")]
    [InlineData("C:\\Users\\x\\Downloads\\archive.zip")]
    [InlineData("C:\\Users\\x\\Downloads\\CASE.PNG")]         // 扩展名大小写不敏感
    public void IsSafeToOpen_HarmlessExtensions_Opens(string path)
    {
        Assert.True(ShellLogic.IsSafeToOpen(path), $"应允许自动打开: {path}");
    }

    [Fact]
    public void IsSafeToOpen_PathWithQueryOrFragment_TreatedByExtension()
    {
        // 落盘路径是本地文件路径，不携带 URL 查询串；此处验证带点的奇怪本地路径不误放行
        Assert.False(ShellLogic.IsSafeToOpen("C:\\Users\\x\\Downloads\\..\\..\\Windows\\System32\\cmd.exe"));
        Assert.False(ShellLogic.IsSafeToOpen("C:\\Users\\x\\Downloads\\photo.png.exe"));
    }

    // ---------- WebView2 权限放行策略（白名单精确匹配） ----------

    [Fact]
    public void IsAutoGrantedPermission_AllowsExactlyPolicyList()
    {
        // 放行：通知/剪贴板读取/自动播放/多文件自动下载/持久化存储（dsh 插件必需）
        Assert.True(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.Notifications));
        Assert.True(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.ClipboardRead));
        Assert.True(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.Autoplay));
        Assert.True(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.MultipleAutomaticDownloads));
        Assert.True(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.PersistentStorage));
    }

    [Fact]
    public void IsAutoGrantedPermission_DeniesSensitivePermissions()
    {
        // 敏感权限（麦克风/摄像头/地理位置等）默认拒绝（隐私，不得静默放行）
        Assert.False(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.Microphone));
        Assert.False(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.Camera));
        Assert.False(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.Geolocation));
        Assert.False(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.OtherSensors));
        Assert.False(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.FileReadWrite));
        Assert.False(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.MidiSystemExclusiveMessages));
        Assert.False(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.WindowManagement));
        Assert.False(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.LocalFonts));
        Assert.False(ShellLogic.IsAutoGrantedPermission(CoreWebView2PermissionKind.UnknownPermission));
    }

    // ---------- 目标地址解析（DSH_WEB_URL 覆盖） ----------

    [Theory]
    [InlineData(null, "http://127.0.0.1:3080", 3080)]
    [InlineData("", "http://127.0.0.1:3080", 3080)]
    [InlineData("http://127.0.0.1:9999", "http://127.0.0.1:9999", 9999)]
    [InlineData("http://localhost:4321", "http://localhost:4321", 4321)]
    [InlineData("https://example.com:8443", "https://example.com:8443", 8443)]
    [InlineData("not a url", "http://127.0.0.1:3080", 3080)]   // 非法 → 回退默认
    [InlineData("ftp://x", "http://127.0.0.1:3080", 3080)]     // 非 http(s) → 回退默认
    public void ResolveTarget_ValidAndInvalid(string? envUrl, string expectedUrl, int expectedPort)
    {
        var (url, port) = ShellLogic.ResolveTarget(envUrl);
        Assert.Equal(expectedUrl, url);
        Assert.Equal(expectedPort, port);
    }

    // ---------- 下载文件名推导（用户高频：命名错误会导致文件丢失/覆盖） ----------

    [Theory]
    [InlineData("attachment; filename=..\\..\\evil.txt", null, "..\\..\\evil.txt")] // 原始名保留给 SanitizeFileName 处理
    [InlineData("attachment; filename=report.PDF", null, "report.PDF")]             // 大小写保留
    [InlineData(null, "https://example.com/a/b/", null)]                            // 尾斜杠 → 无尾段
    [InlineData(null, "https://example.com/", null)]                                // 根路径 → 无尾段
    [InlineData(null, "https://example.com", null)]                                 // 无路径 → 无尾段
    [InlineData("attachment; filename=", "https://example.com/real.bin", "real.bin")] // 空 filename → 用 URI
    public void SuggestDownloadName_EdgeCases(string? disposition, string? uri, string? expected)
    {
        var name = ShellLogic.SuggestDownloadName(disposition, uri, null);
        if (expected is null)
            Assert.StartsWith("dsh-", name);
        else
            Assert.Equal(expected, name);
    }
}
