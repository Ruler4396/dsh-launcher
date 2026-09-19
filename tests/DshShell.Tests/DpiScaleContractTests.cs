using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 契约测试：ShellLogic.DpiScale（DPI → 缩放系数的唯一换算口，臃肿审计 Phase 5 · D8）。
///
/// 【为什么单独钉】B4 修的缺陷是"8 份同类换算里有一份漏了钳制"：坏显卡驱动 / RDP 会话会给出
/// deviceDpi = 0 或几千，未钳制的结果是一条 1px 的通知卡片或整卡跑出屏幕。既然修复方式是"钳在
/// [0.5, 8]"，那这份钳制就只能存在一次——否则第 9 份照抄时照样会漏。本类锁边界，
/// test.ps1 的 G10 锁"只有一份"。
/// </summary>
public class DpiScaleContractTests
{
    [Theory]
    [InlineData(96, 1f)]        // 标准 1x
    [InlineData(120, 1.25f)]    // 常见 125%
    [InlineData(192, 2f)]       // 200%
    [InlineData(0, 1f)]         // 未知 DPI 按 96 处理，绝不按 0 缩放（那会把窗口压成 0px）
    [InlineData(-1, 1f)]
    [InlineData(1, 0.5f)]       // 荒谬小值 → 钳到下限
    [InlineData(24, 0.5f)]
    [InlineData(4000, 8f)]      // 荒谬大值 → 钳到上限
    [InlineData(100000, 8f)]
    public void Of_IsClamped_AndNeverZero(int deviceDpi, float expected)
    {
        var s = ShellLogic.DpiScale.Of(deviceDpi);
        Assert.Equal(expected, s, 3);
        Assert.InRange(s, ShellLogic.DpiScale.Min, ShellLogic.DpiScale.Max);
    }

    /// <summary>缩放后的物理像素至少 1px：0 尺寸窗口在 Win32 里表现为"看不见且点不到"，
    /// 与 B4 那次是同一类用户后果。</summary>
    [Theory]
    [InlineData(0, 0.5f)]
    [InlineData(1, 0.5f)]
    [InlineData(12, 1f)]
    [InlineData(1280, 8f)]
    public void Px_NeverCollapsesToZero(int design, float scale)
        => Assert.True(ShellLogic.DpiScale.Px(design, scale) >= 1);

    [Fact]
    public void Px_RoundsByScale()
    {
        Assert.Equal(24, ShellLogic.DpiScale.Px(12, 2f));
        Assert.Equal(16, ShellLogic.DpiScale.Px(24, 0.6666667f));
    }
}
