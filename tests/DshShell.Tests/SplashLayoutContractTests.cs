using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// 【契约测试】Splash 启动窗布局纯函数（issue #28-2 高 DPI 根治）。
///
/// 事故：SplashForm 是全仓唯一没有任何 DPI 处理的窗口——380×180 的窗体、60×22 的"取消"按钮
/// 全是硬编码**物理像素**，而字体是 point（随系统 DPI 变大）。报告人 200% 屏上按钮被自己的
/// 文字撑破（截图："启动窗口 UI 异常，按钮基本看不到"）。
///
/// 本契约锁定两件事：① 一切几何随 dpi **线性**放大（与文字同源同速，框才装得下字）；
/// ② 结构不变量（控件互不越界、边距一致）。真实"文字宽度 ≤ 控件宽度"的像素级断言在
/// <c>--ui-selftest</c> 第二遍里做（需要 GDI 测量，不是纯函数能表达的）。
/// </summary>
public class SplashLayoutContractTests
{
    [Theory]
    [InlineData(96, 100)]
    [InlineData(120, 125)]
    [InlineData(144, 150)]
    [InlineData(192, 200)]     // 报告人截图所在量级
    [InlineData(240, 250)]
    public void Contract_AllGeometry_ScalesLinearlyWithDpi(int dpi, int expectedPercent)
    {
        var g = ShellLogic.SplashLayout.Compute(dpi);
        var s = dpi / 96f;

        Assert.Equal(expectedPercent, g.ScalePercent);
        Assert.Equal((int)Math.Round(ShellLogic.SplashLayout.DesignClientWidth * s), g.ClientSize.Width);
        Assert.Equal((int)Math.Round(ShellLogic.SplashLayout.DesignClientHeight * s), g.ClientSize.Height);
        Assert.Equal((int)Math.Round(60 * s), g.Cancel.Width);
        Assert.Equal((int)Math.Round(22 * s), g.Cancel.Height);
        // 字号与框同速放大——旧实现框不放大，这正是"按钮看不到"的根因
        Assert.Equal((int)Math.Round(ShellLogic.SplashLayout.DesignEmPt * 96.0 / 72.0 * s), g.EmPx);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    [InlineData(240)]
    public void Contract_ControlsStayInsideClientAndPanel(int dpi)
    {
        var g = ShellLogic.SplashLayout.Compute(dpi);
        var client = new System.Drawing.Rectangle(System.Drawing.Point.Empty, g.ClientSize);

        Assert.True(client.Contains(g.Status) && client.Contains(g.Bar), $"状态/进度条越出客户区（dpi={dpi}）");
        // 按钮右下角允许贴边（Anchor=Bottom|Right），但不得越出
        Assert.True(g.Cancel.Right <= client.Width && g.Cancel.Bottom <= client.Height,
            $"取消按钮越出客户区：{g.Cancel} vs {client.Size}（dpi={dpi}）");
        Assert.True(client.Contains(g.ConfirmPanel), $"确认面板越出客户区（dpi={dpi}）");

        // 面板内子控件坐标是**面板相对**值（与 WinForms 控件父子关系一致），比对前先平移
        foreach (var child in new[] { g.ConfirmTitle, g.ConfirmText, g.ConfirmYes, g.ConfirmNo })
        {
            var abs = new System.Drawing.Rectangle(
                g.ConfirmPanel.X + child.X, g.ConfirmPanel.Y + child.Y, child.Width, child.Height);
            Assert.True(g.ConfirmPanel.Contains(abs),
                $"确认面板子控件越界：{child}(rel) → {abs} not in {g.ConfirmPanel}（dpi={dpi}）");
        }
        Assert.True(g.ConfirmYes.Right <= g.ConfirmNo.Left, "是/否两个按钮不得重叠");
    }

    [Fact]
    public void Contract_MarginsAreUniformAndContentWidthMatches()
    {
        var g = ShellLogic.SplashLayout.Compute(192);
        var margin = (int)Math.Round(ShellLogic.SplashLayout.DesignMargin * 2f);
        Assert.Equal(margin, g.Status.X);
        Assert.Equal(g.ClientSize.Width - margin * 2, g.Status.Width);
        Assert.Equal(g.Status.Width, g.Bar.Width);
        Assert.Equal(g.Status.Width, g.ConfirmPanel.Width);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-96)]
    public void Contract_UnknownDpi_FallsBackTo100Percent(int dpi)
        => Assert.Equal(ShellLogic.SplashLayout.Compute(96), ShellLogic.SplashLayout.Compute(dpi));

    [Fact]
    public void Contract_ScaleIsClampedBothWays()
    {
        Assert.Equal(50, ShellLogic.SplashLayout.Compute(24).ScalePercent);        // 25% → 夹到 50%
        Assert.Equal(800, ShellLogic.SplashLayout.Compute(96 * 40).ScalePercent);  // 4000% → 夹到 800%
    }
}
