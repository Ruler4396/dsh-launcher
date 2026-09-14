using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using DshWeb.Windows;
using Xunit;
using Xunit.Abstractions;

namespace DshShell.Tests;

/// <summary>
/// 【Regression_Issue28_TrayExitRow】托盘右键"退出"条目 UI 异常 的真实渲染回归测试（零 Mock，RealOS）。
///
/// 事故（issue #28-1，用户截图指出）：电源图标与"退 出"两字之间有一道明显空档，整行观感断裂。
/// 根因：<c>TextRenderer.MeasureText/DrawText</c> 默认带内边距（实测 Noto Sans SC 10pt：
/// 每字 23px vs NoPadding 14px = 每侧 4.5px），旧实现又给首字矩形额外 <c>+4*s</c> 宽度，
/// 叠加后"字距 2px"被放大成 ≈11px（1x）/ 22px（2x）。
///
/// 本测试直接调用生产代码 <see cref="TrayMenuForm"/> 的渲染（Draw 经反射，非复制实现），
/// 从真实像素统计两字墨迹之间的空档列数，断言空档不超过半个字宽——旧实现必然超标。
/// </summary>
[Collection("RealOS")]
[Trait("Category", "RealOS")]
public class Regression_Issue28_TrayExitRow_RealOs
{
    private readonly ITestOutputHelper _out;
    public Regression_Issue28_TrayExitRow_RealOs(ITestOutputHelper o) => _out = o;

    [Fact]
    public void RealOs_TrayExitRow_GlyphGap_IsTrackingNotPadding()
    {
        using var form = new TrayMenuForm(() => { });
        var draw = typeof(TrayMenuForm).GetMethod("Draw", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(draw);

        using var bmp = new Bitmap(form.Width, form.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.White);
            draw!.Invoke(form, new object[] { g });
        }

        // 只保留"近黑"像素（退出文字；电源图标是红色、卡片是白色 → 天然分离）
        var isDark = new Func<Color, bool>(c => c.R < 90 && c.G < 90 && c.B < 90);
        var columns = new List<int>();
        for (var x = 0; x < bmp.Width; x++)
        {
            for (var y = 0; y < bmp.Height; y++)
            {
                if (isDark(bmp.GetPixel(x, y))) { columns.Add(x); break; }
            }
        }
        Assert.True(columns.Count > 0, "渲染结果里找不到任何文字像素（字体缺失/渲染失败？）");

        // 按"连续列"聚簇：应为两簇（退、出）
        var clusters = new List<(int Start, int End)>();
        var start = columns[0];
        var prev = columns[0];
        foreach (var x in columns.Skip(1))
        {
            if (x - prev > 1) { clusters.Add((start, prev)); start = x; }
            prev = x;
        }
        clusters.Add((start, prev));

        Assert.Equal(2, clusters.Count);
        var (s1, e1) = clusters[0];
        var (s2, e2) = clusters[1];
        var inkGap = s2 - e1 - 1;
        var charInk = Math.Max(e1 - s1 + 1, e2 - s2 + 1);
        _out.WriteLine($"字簇1=[{s1},{e1}] 字簇2=[{s2},{e2}] 墨迹空档={inkGap}px 单字墨迹={charInk}px");

        // 修复前：空档 ≈ 11px、单字墨迹 ≈ 14px（比值 0.79）；修复后：空档 ≈ 4-5px（比值 ≈0.3）。
        // 以"空档 ≤ 半个字宽"为闸门：任何重新引入测量/绘制内边距叠加的实现都会超标。
        Assert.True(inkGap <= charInk * 0.5,
            $"两字墨迹空档 {inkGap}px 超过半个字宽（{charInk}px 的 50%）——"
            + "测量/绘制内边距又被叠加进字距了（issue #28-1 复发）");
    }
}
