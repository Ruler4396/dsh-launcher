using DshWeb;
using Microsoft.Web.WebView2.Core;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// F11 全屏加速键判定（v0.3.4）：物理 F11 进入 WebView2 浏览器进程，宿主只能经
/// CoreWebView2Controller.AcceleratorKeyPressed 拦截；本测试覆盖判定纯函数
/// Program.IsF11KeyDown——只认 F11 的按下事件，其余按键/弹起一律放行。
/// </summary>
public class F11KeyDownTests
{
    [Fact]
    public void F11KeyDown_ReturnsTrue()
    {
        Assert.True(Program.IsF11KeyDown(0x7A, CoreWebView2KeyEventKind.KeyDown));
    }

    [Theory]
    [InlineData(0x74)] // F5
    [InlineData(0x7B)] // F12
    [InlineData(0x41)] // A
    [InlineData(0x0D)] // Enter
    public void OtherKeys_KeyDown_ReturnsFalse(uint vk)
    {
        Assert.False(Program.IsF11KeyDown(vk, CoreWebView2KeyEventKind.KeyDown));
    }

    [Fact]
    public void F11KeyUp_ReturnsFalse()
    {
        // 只认按下，避免 KeyDown+KeyUp 各触发一次导致"按一下切两次"
        Assert.False(Program.IsF11KeyDown(0x7A, CoreWebView2KeyEventKind.KeyUp));
        Assert.False(Program.IsF11KeyDown(0x7A, CoreWebView2KeyEventKind.SystemKeyUp));
    }

    [Fact]
    public void F11SystemKeyDown_ReturnsFalse()
    {
        Assert.False(Program.IsF11KeyDown(0x7A, CoreWebView2KeyEventKind.SystemKeyDown));
    }
}