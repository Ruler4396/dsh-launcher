using System.Drawing;
using System.Threading;
using DshWeb;
using Xunit;

namespace DshShell.Tests;

/// <summary>
/// [issue #25 收口] 通知卡片版式契约（纯函数 ShellLogic.NoticeCardLayout）。
/// 与 SplashLayoutContractTests / TrayMenuLayoutContractTests 同一纪律：设计基准 → 物理像素
/// 的折算、段间**恰好一个 Gap**（0 重叠、0 空隙）、越界钳制，全部在纯函数层钉死，
/// 绘制侧（Windows/NoticeCard.cs）只消费不计算。
/// </summary>
public class NoticeCardLayoutContractTests
{
    private static ShellLogic.NoticeCardLayout.Geometry G(int dpi)
        => ShellLogic.NoticeCardLayout.ComputeGeometry(dpi);

    [Theory]
    [InlineData(96, 1.0f)]
    [InlineData(120, 1.25f)]
    [InlineData(144, 1.5f)]
    [InlineData(192, 2.0f)]
    public void Geometry_ScalesLinearlyWithDpi(int dpi, float s)
    {
        var g = G(dpi);
        var baseG = G(96);
        Assert.Equal((int)Math.Round(baseG.TextWidth * s), g.TextWidth);
        Assert.Equal((int)Math.Round(baseG.Padding * s), g.Padding);
        Assert.Equal((int)Math.Round(baseG.CloseSize * s), g.CloseSize);
        Assert.Equal((int)Math.Round(baseG.AccentWidth * s), g.AccentWidth);
        // 窗口宽 = 文字宽 + 左右内边距 + 色条 + 色条与文字的一个间距
        //（不是再乘一次系数——issue #28-3 的 s² 放大根因）
        Assert.Equal(g.TextWidth + 2 * g.Padding + g.AccentWidth + g.Gap, g.Width);
    }

    /// <summary>字号也**只乘一次** s：#28-3 的真实事故就是字号按 Point 单位给，再被绘制 DC
    /// 自带的 DPI 折算一遍 → 200% 屏上文字按 s² 长大。</summary>
    [Theory]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(192)]
    public void EmSizes_ScaleOnceWithDpi(int dpi)
    {
        var g = G(dpi);
        var baseG = G(96);
        Assert.Equal((int)Math.Round(baseG.TitleEmPx * dpi / 96f), g.TitleEmPx);
        Assert.Equal((int)Math.Round(baseG.BodyEmPx * dpi / 96f), g.BodyEmPx);
    }

    /// <summary>显眼度下限（96dpi 逻辑像素）。用户连续反馈"通知不够显眼"，前三轮修的是对比度/
    /// 字重/声音，尺寸没动——这里把尺寸底线钉成契约：缩回去就等于把"必须看到并要据此行动"的
    /// 提示降级成正文大小。同时约束卡片别无限撑宽（小屏/低倍率也要放得下）。</summary>
    [Fact]
    public void Geometry_ProminenceFloorAt96dpi()
    {
        var g = G(96);
        Assert.True(g.TitleEmPx >= 16, $"标题字号不得低于 16px，当前 {g.TitleEmPx}");
        Assert.True(g.BodyEmPx >= 14, $"正文字号不得低于 14px，当前 {g.BodyEmPx}");
        Assert.True(g.TitleEmPx > g.BodyEmPx, "标题必须比正文大，否则没有层级");
        Assert.True(g.CloseSize >= g.BodyEmPx, $"× 至少和正文一样大，否则点不中（当前 {g.CloseSize}）");
        Assert.True(g.ActionHeight >= g.BodyEmPx * 2, "动作行要给可点区域留出高度");
        Assert.True(g.TextWidth is >= 360 and <= 520, $"文字宽要够读又不能撑爆小屏，当前 {g.TextWidth}");
    }

    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public void Place_AccentBarSpansFullHeight_AndTextYieldsToIt(int dpi)
    {
        var g = G(dpi);
        var p = ShellLogic.NoticeCardLayout.Place(g, 18, 32, hasAction: true);
        Assert.Equal(g.AccentWidth, p.AccentRect.Width);
        Assert.Equal(p.Height, p.AccentRect.Height);   // 撑满全高，才是一条真正的级别标识
        Assert.Equal(0, p.AccentRect.X);
        // 文字一律从色条右侧开始：色条压住正文 = 视觉上"字被切了一半"
        Assert.True(p.TitleRect.X >= g.AccentWidth + g.Padding);
        Assert.True(p.BodyRect.X >= g.AccentWidth + g.Padding);
        Assert.True(p.ActionRect.X >= g.AccentWidth + g.Padding);
    }

    /// <summary>测量宽度与排版宽度必须同源：否则"按 A 宽换行、按 B 宽绘制"会裁字。</summary>
    [Fact]
    public void MeasureWidths_AreTheWidthsPlaceUses()
    {
        var g = G(96);
        var m = ShellLogic.NoticeCardLayout.MeasureWidths(g);
        var p = ShellLogic.NoticeCardLayout.Place(g, m.TitleWidth, m.BodyWidth, hasAction: true);
        Assert.Equal(m.TitleWidth, p.TitleRect.Width);
        Assert.Equal(m.BodyWidth, p.BodyRect.Width);
        Assert.Equal(m.BodyWidth, p.ActionRect.Width);
    }

    /// <summary>级别 → 提示线索：Urgent 才走警示音/红色条（安全更新、构建失败、安全模式）。</summary>
    [Theory]
    [InlineData(ShellLogic.NoticeKind.Info, false)]
    [InlineData(ShellLogic.NoticeKind.Urgent, true)]
    public void NoticePolicy_WarningCueOnlyForUrgent(ShellLogic.NoticeKind kind, bool expected)
        => Assert.Equal(expected, ShellLogic.NoticePolicy.UseWarningCue(kind));

    /// <summary>
    /// 驻留时长折算：0 = sticky（不自动收起，安全模式降级态用）。
    /// 钳位边界必须有名——调用方传 1 秒会被抬到 3 秒，传 10 分钟会被压到 2 分钟，
    /// 而传 Zero / InfiniteTimeSpan 必须**原样**得到 0，不能被钳成 3 秒：
    /// 那正是"降级态提示 3 秒后自己消失、用户失去退出入口"的旧陷阱。
    /// </summary>
    [Theory]
    [InlineData(0, 0)]                       // TimeSpan.Zero → sticky
    [InlineData(-1, 0)]                      // Timeout.InfiniteTimeSpan(-1ms) → sticky
    [InlineData(-1000, 0)]
    [InlineData(1, 3000)]                    // 低于下限 → 抬到 3s
    [InlineData(25, 25000)]                  // 区间内原样
    [InlineData(120, 120000)]                // 上限
    [InlineData(600, 120000)]                // 超上限 → 压到 120s
    public void NoticePolicy_ResolveExpiryMs(double seconds, int expectedMs)
        => Assert.Equal(expectedMs, ShellLogic.NoticePolicy.ResolveExpiryMs(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void NoticePolicy_InfiniteTimeSpan_IsSticky()
        => Assert.Equal(0, ShellLogic.NoticePolicy.ResolveExpiryMs(Timeout.InfiniteTimeSpan));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Geometry_UnknownDpi_FallsBackTo1x(int dpi)
    {
        Assert.Equal(G(96).Width, G(dpi).Width);
        Assert.True(G(dpi).AccentWidth >= 1);     // 任何折算都不产生 0 尺寸（色条会整条消失）
    }

    /// <summary>
    /// 左侧强调条必须**贴着窗口左边缘并撑满全高**（真机反馈 2026-09-20：圆角时代它上下两头被
    /// Region 裁掉，看起来就是"色条没对齐"）。文字一律让开色条，否则第一列字被色条吃掉。
    /// </summary>
    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Place_AccentBar_IsFlushLeftAndSpansFullHeight(int dpi)
    {
        var g = G(dpi);
        var p = ShellLogic.NoticeCardLayout.Place(g, 18, 32, hasAction: true);
        Assert.Equal(0, p.AccentRect.X);
        Assert.Equal(0, p.AccentRect.Y);
        Assert.Equal(p.Height, p.AccentRect.Height);
        Assert.Equal(g.AccentWidth, p.AccentRect.Width);
        Assert.True(p.TitleRect.Left >= p.AccentRect.Right);
        Assert.True(p.BodyRect.Left >= p.AccentRect.Right);
        Assert.True(p.ActionRect.Left >= p.AccentRect.Right);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(192)]
    public void Place_WithoutAction_LeavesNoTrailingGap(int dpi)
    {
        var g = G(dpi);
        var p = ShellLogic.NoticeCardLayout.Place(g, 18, 32, hasAction: false);
        Assert.True(p.ActionRect.IsEmpty);
        // 底部只剩一个 Padding：多算的 Gap/ActionHeight 会变成卡片下方的空白条
        Assert.Equal(g.Padding + 18 + g.Gap + 32 + g.Padding, p.Height);
    }

    [Theory]
    [InlineData(96)]
    [InlineData(144)]
    [InlineData(192)]
    public void Place_SectionsHaveExactlyOneGap_NoOverlapNoSeam(int dpi)
    {
        var g = G(dpi);
        var p = ShellLogic.NoticeCardLayout.Place(g, 18, 32, hasAction: true);
        Assert.Equal(p.TitleRect.Bottom + g.Gap, p.BodyRect.Top);
        Assert.Equal(p.BodyRect.Bottom + g.Gap, p.ActionRect.Top);
        Assert.Equal(18, p.TitleRect.Height);
        Assert.Equal(p.ActionRect.Height, g.ActionHeight);
        // 三段都在窗口内（× 也不例外）——超出就是被 Region 裁掉的"看不见的按钮"
        Assert.True(p.ActionRect.Bottom <= p.Height);
        Assert.True(p.CloseRect.Bottom <= p.Height);
        Assert.True(p.BodyRect.Right <= p.Width - g.Padding);
    }

    [Fact]
    public void Place_TitleYieldsWidthToCloseButton()
    {
        var g = G(96);
        var p = ShellLogic.NoticeCardLayout.Place(g, 18, 32, hasAction: false);
        // 标题不得伸到 × 底下：让出 CloseSize + 一个 Gap
        Assert.True(p.TitleRect.Right + g.Gap <= p.CloseRect.Left);
        Assert.Equal(p.CloseRect.Right, g.Width - g.Padding);
    }

    [Fact]
    public void Place_HeightNeverClipsCloseButton()
    {
        var g = G(96);
        // 极矮的文字（例如空 body）时，× 仍要完整落在窗口内
        var p = ShellLogic.NoticeCardLayout.Place(g, 1, 1, hasAction: false);
        Assert.True(p.Height >= p.CloseRect.Bottom + g.Padding);
    }

    [Fact]
    public void PlaceAtBottomRight_AnchorsToWorkAreaCornerWithMargin()
    {
        var g = G(96);
        var wa = new Rectangle(0, 0, 1920, 1040);   // 1080 高、40 任务栏
        var (x, y) = ShellLogic.NoticeCardLayout.PlaceAtBottomRight(wa, g, 400, 120);
        Assert.Equal(1920 - 400 - g.ScreenMargin, x);
        Assert.Equal(1040 - 120 - g.ScreenMargin, y);
    }

    [Fact]
    public void PlaceAtBottomRight_RespectsNonOriginWorkArea()
    {
        // 副屏在工作区负坐标一侧：定位必须以 workArea 为基准，不能用 (0,0)
        var g = G(96);
        var wa = new Rectangle(-1920, 0, 1920, 1040);
        var (x, y) = ShellLogic.NoticeCardLayout.PlaceAtBottomRight(wa, g, 400, 120);
        Assert.InRange(x, wa.Left + g.ScreenMargin, wa.Right - 400 - g.ScreenMargin);
        Assert.Equal(wa.Bottom - 120 - g.ScreenMargin, y);
    }

    [Fact]
    public void PlaceAtBottomRight_TinyWorkArea_ClampsInside()
    {
        var g = G(96);
        var wa = new Rectangle(0, 0, 300, 100);   // 比卡片（400×120）还小
        var (x, y) = ShellLogic.NoticeCardLayout.PlaceAtBottomRight(wa, g, 400, 120);
        // 卡片塞不进工作区：贴左上内边距，绝不给出负偏移（窗口跑到工作区外就再也关不掉了）
        Assert.Equal(g.ScreenMargin, x);
        Assert.Equal(g.ScreenMargin, y);
    }

    [Fact]
    public void PlaceAtBottomRight_FitsBothDimensions_StillAnchorsBottomRight()
    {
        // 反向保险：只有一维放不下时，另一维仍须贴右下（别把钳制写成"整张卡片挪到左上"）
        var g = G(96);
        var wa = new Rectangle(0, 0, 300, 600);
        var (x, y) = ShellLogic.NoticeCardLayout.PlaceAtBottomRight(wa, g, 400, 120);
        Assert.Equal(g.ScreenMargin, x);
        Assert.Equal(600 - 120 - g.ScreenMargin, y);
    }

    // ---------------- HitTest：只有 × 与"点击此处"那一行可点 ----------------
    //
    // 真机反馈（2026-09-20 用户实拍后原话）："我点击卡片但是没有点到'点击此处'时什么都没发生
    // ——没有重启"。旧实现是整张卡当动作热区：想复制正文/想点别处都会误触发带进程副作用的动作
    // （退出安全模式并重启），而点到 × 时只有关闭、动作不执行且日志零留痕。

    private static ShellLogic.NoticeCardLayout.HitTarget Hit(
        ShellLogic.NoticeCardLayout.Placement p, int x, int y)
        => ShellLogic.NoticeCardLayout.HitTest(p, x, y);

    [Fact]
    public void HitTest_ActionRowOnly_RespondsWithAction()
    {
        var g = G(96);
        var p = ShellLogic.NoticeCardLayout.Place(g, 18, 32, hasAction: true);
        Assert.Equal(ShellLogic.NoticeCardLayout.HitTarget.Action,
            Hit(p, p.ActionRect.X + 2, p.ActionRect.Y + p.ActionRect.Height / 2));
        // 标题、正文、色条、右下边框——一律不响应（这些正是"想复制/想忽略"的落点）
        Assert.Equal(ShellLogic.NoticeCardLayout.HitTarget.None, Hit(p, p.TitleRect.X + 2, p.TitleRect.Y + 2));
        Assert.Equal(ShellLogic.NoticeCardLayout.HitTarget.None, Hit(p, p.BodyRect.X + 2, p.BodyRect.Y + 2));
        Assert.Equal(ShellLogic.NoticeCardLayout.HitTarget.None, Hit(p, 1, p.Height / 2));
        Assert.Equal(ShellLogic.NoticeCardLayout.HitTarget.None, Hit(p, p.Width - 2, p.Height - 2));
    }

    [Fact]
    public void HitTest_CloseButton_Closes()
    {
        var p = ShellLogic.NoticeCardLayout.Place(G(96), 18, 32, hasAction: true);
        Assert.Equal(ShellLogic.NoticeCardLayout.HitTarget.Close,
            Hit(p, p.CloseRect.X + 2, p.CloseRect.Y + 2));
    }

    [Fact]
    public void HitTest_CardWithoutAction_HasNoActionTargetAtAll()
    {
        // 纯提示卡（没有"点击此处"）：整面都不该有动作热区，只剩 × 可点
        var p = ShellLogic.NoticeCardLayout.Place(G(96), 18, 32, hasAction: false);
        Assert.True(p.ActionRect.IsEmpty);
        Assert.Equal(ShellLogic.NoticeCardLayout.HitTarget.None, Hit(p, p.Width / 2, p.Height - 4));
        Assert.Equal(ShellLogic.NoticeCardLayout.HitTarget.Close, Hit(p, p.CloseRect.X + 1, p.CloseRect.Y + 1));
    }

    [Fact]
    public void HitTest_CloseWins_WhenTheTwoRectsOverlap()
    {
        // Place() 保证两者不重叠；这里造一个重叠的排布，确认"关闭"优先——
        // 重叠时若动作优先，用户想关掉一张卡却把服务重启了。
        var p = ShellLogic.NoticeCardLayout.Place(G(96), 18, 32, hasAction: true);
        var overlapped = new ShellLogic.NoticeCardLayout.Placement(p.Width, p.Height, p.AccentRect,
            p.TitleRect, p.BodyRect, p.CloseRect, p.CloseRect);
        Assert.Equal(ShellLogic.NoticeCardLayout.HitTarget.Close,
            Hit(overlapped, p.CloseRect.X + 2, p.CloseRect.Y + 2));
    }
}
