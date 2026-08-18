using System;
using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// F11 全屏低级键盘钩子判定（v0.3.4）：nCode≥0 且主窗口前台且为 F11 按下即切换并吞键。
/// 系统级钩子在 OS 层捕获按键，物理 F11 可靠、跨重启稳定。
/// </summary>
public class F11HookDecisionTests
{
    private static readonly IntPtr KeyDown = (IntPtr)0x0100;
    private static readonly IntPtr SysKeyDown = (IntPtr)0x0104;
    private const uint VK_F11 = 0x7A;

    [Theory]
    [InlineData(0, 0x0100, 0x7A, true)]   // KeyDown, 前台
    [InlineData(0, 0x0104, 0x7A, true)]   // SysKeyDown, 前台
    public void F11InForeground_ShouldHandle(int nCode, int wParam, uint vk, bool fg)
    {
        Assert.True(Program.ShouldHandleF11Hook(nCode, (IntPtr)wParam, vk, fg));
    }

    [Theory]
    [InlineData(0, 0x0100, 0x74, true)]   // F5, 前台 → 不处理
    [InlineData(0, 0x0100, 0x7B, true)]   // F12 → 不处理
    [InlineData(0, 0x0100, 0x7A, false)]  // F11 但非前台 → 不处理（不抢其他程序的 F11）
    [InlineData(-1, 0x0100, 0x7A, true)]  // nCode<0 → 必放行
    public void OtherCases_ShouldNotHandle(int nCode, int wParam, uint vk, bool fg)
    {
        Assert.False(Program.ShouldHandleF11Hook(nCode, (IntPtr)wParam, vk, fg));
    }
}