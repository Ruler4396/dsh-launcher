using System.Drawing;
using System.Drawing.Imaging;
using System.Reflection;
using System.Windows.Forms;
using DshWeb;
using DshWeb.Windows;
using Xunit;
using Xunit.Abstractions;

namespace DshShell.Tests;

/// <summary>
/// 【真机反馈 2026-09-20】通知卡片：直角、左侧强调条撑满全高且压住边框。
///
/// 用户实拍（安全模式那条粘滞卡）指出两件事：
/// ① 圆角难看；② 红/蓝装饰条"没对齐"，左缘有时有一条白线。
/// 两条同一个根因链：卡片用 <c>Region = new Region(GraphicsPath)</c> 裁圆角，
/// 于是贴在左边缘的强调条上下两头被裁掉；而 <c>OnPaint</c> 先画强调条、**后**画 1px 边框，
/// 边框正好压在色条那一列上（#D1D5DB 压在 #D81E06 上）——那条"白线"就是它。
///
/// 断言取的是**物理像素**，不是"代码里没写 GraphicsPath"：把生产入口
/// <see cref="NoticeCard.Present"/> 真的呈现一次，用 DrawToBitmap 取回客户区，然后要求
/// 第 0 列的每一行都严格等于强调色。旧实现下这条必红（第 0 行是边框灰 + 圆角裁掉的行不是强调色）。
/// 零 Mock：真实窗口、真实 GDI 绘制、真实位图采样。
/// </summary>
[Trait("Category", "RealOS")]
public class Regression_NoticeCardSquareAccent
{
    private readonly ITestOutputHelper _out;
    public Regression_NoticeCardSquareAccent(ITestOutputHelper out_) => _out = out_;

    [Fact]
    public void RealOs_AccentBar_CoversEveryRowOfLeftEdge_NoBorderLineOnTopOf_It()
        => RunSta(() =>
        {
            using var owner = new Form { Text = "notice-card-square-probe", ShowInTaskbar = false };
            _ = owner.Handle;   // Present 只要求句柄存在，不显示 owner

            Assert.True(NoticeCard.Present(owner, "安全模式卡片版式探针", "正文用于撑开高度",
                TimeSpan.Zero, onAction: static () => { }, actionText: "点击此处退出安全模式并重启",
                kind: ShellLogic.NoticeKind.Urgent), "Present 未受理（owner 句柄/去重闸门？）");

            var card = SharedCard();
            try
            {
                var w = card.Width; var h = card.Height;
                using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                card.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));   // UserPaint 窗：这一步真的走 OnPaint
                var accent = PrivateColor("AccentUrgent");
                var border = PrivateColor("BorderColor");
                _out.WriteLine($"card {w}x{h} dpi={card.DeviceDpi} accent={accent} border={border}");

                // ① 左缘整列都是强调色：既证明色条撑满全高（上下没被裁），也证明没有 1px 边框
                //    压在它上面（那正是用户看到的"一条白线"）。
                var offColumn = Enumerable.Range(0, h)
                    .Where(y => !Same(bmp.GetPixel(0, y), accent)).ToList();
                Assert.True(offColumn.Count == 0,
                    $"第 0 列有 {offColumn.Count} 行不是强调色（行号 {string.Join(',', offColumn.Take(6))}；" +
                    $"首行实测 {bmp.GetPixel(0, offColumn.Count > 0 ? offColumn[0] : 0)}）");

                // ② 色条宽度就是纯函数给的那几像素，不多不少（多出来会盖住正文，少了会露底色）
                var accentWidth = ShellLogic.NoticeCardLayout.ComputeGeometry(card.DeviceDpi).AccentWidth;
                Assert.True(Same(bmp.GetPixel(accentWidth - 1, h / 2), accent),
                    $"色条第 {accentWidth - 1} 列不是强调色（实测 {bmp.GetPixel(accentWidth - 1, h / 2)}）");
                Assert.False(Same(bmp.GetPixel(accentWidth, h / 2), accent),
                    "色条比几何宽度更宽：会压到正文那一列");

                // ③ 边框没被删掉——只是不再压在色条上（右上角是边框，不是底色）
                var topRight = bmp.GetPixel(w - 1, 0);
                Assert.True(Same(topRight, border),
                    $"右上角应为边框色 {border}，实测 {topRight}（边框被删了？）");

                // ④ 直角：窗口 Region 必须仍然覆盖三个角点。圆角实现用 GraphicsPath 裁 Region，
                //    裁掉后 (0,0) 就落在区域外——这是唯一能分辨"有没有被裁角"的物理判据。
                //    （踩过：先用位图角像素当判据，实测 DrawToBitmap **无视 Region**，
                //    把圆角代码整个加回来这条断言照样绿，是空断言。）
                using var probeG = Graphics.FromImage(bmp);
                // Control.Region == null 就是"没有自定义区域"（= 矩形窗口），正是要断言的状态；
                // 非 null 时必须仍然覆盖三个角点，否则说明有人把圆角 Region 裁切加了回来。
                var region = card.Region;
                Assert.True(region is null
                    || (region.IsVisible(0f, 0f, probeG) && region.IsVisible(0f, h - 1f, probeG)
                        && region.IsVisible(w - 1f, 0f, probeG)),
                    "窗口 Region 不再覆盖角点——圆角裁切回来了（色条上下两头会被它切掉）");
            }
            finally { card.Close(); card.Dispose(); }
        }, "通知卡片直角 + 强调条对齐");

    /// <summary>
    /// 【真机 2026-09-20 用户反馈】点卡片的**正文区**不该触发"退出安全模式并重启"这种带进程
    /// 副作用的动作——旧实现整张卡都是热区，用户"想复制内容"就顺手重启了服务；反过来点到 ×
    /// 只有关闭、动作不执行且日志零留痕，体感就是"点了没反应"。
    /// 这里用反射调用**生产的 OnMouseDown**（不新增测试钩子），物理验证两件事：
    /// 正文中央点击 → 动作一次都没跑；动作行点击 → 动作跑了一次。
    /// </summary>
    [Fact]
    public void RealOs_ClickOnBody_FiresNothing_ClickOnActionRow_FiresOnce()
        => RunSta(() =>
        {
            using var owner = new Form { Text = "notice-card-hit-probe", ShowInTaskbar = false };
            _ = owner.Handle;
            var fired = 0;
            Assert.True(NoticeCard.Present(owner, "命中区探针", "这段正文是用来点着复制的",
                TimeSpan.Zero, onAction: () => fired++, actionText: "点击此处退出安全模式并重启",
                kind: ShellLogic.NoticeKind.Urgent));

            var card = SharedCard();
            try
            {
                var onDown = typeof(NoticeCard).GetMethod("OnMouseDown",
                    BindingFlags.NonPublic | BindingFlags.Instance)!;
                void Click(int x, int y) => onDown.Invoke(card,
                    new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });

                var g = ShellLogic.NoticeCardLayout.ComputeGeometry(card.DeviceDpi);
                var h = card.Height;

                // ① 正文中央：旧实现在这里就把服务重启了
                Click(g.Padding + g.AccentWidth + g.Gap + 20, h / 2);
                Assert.Equal(0, fired);
                Assert.False(card.IsDisposed, "正文点击把卡片收掉了（应当完全无响应）");

                // ② 色条那一列：同样不该有动作
                Click(1, h / 2);
                Assert.Equal(0, fired);

                // ③ 动作行（最后一行，上沿 = 底边 - 内边距 - 动作高）：必须恰好触发一次
                Click(g.Padding + g.AccentWidth + g.Gap + 20, h - g.Padding - g.ActionHeight / 2);
                Assert.Equal(1, fired);
            }
            finally
            {
                if (!card.IsDisposed) { card.Close(); card.Dispose(); }
            }
        }, "卡片命中区收窄");

    /// <summary>取回生产代码里那张真实卡片（私有静态单例字段，不为此加测试钩子）。</summary>
    private static Form SharedCard()
    {
        var f = typeof(NoticeCard).GetField("_shared", BindingFlags.NonPublic | BindingFlags.Static);
        var card = f?.GetValue(null) as Form;
        Assert.NotNull(card);
        Assert.True(card!.IsHandleCreated && card.Visible, "卡片没有真的显示出来（断言会失去意义）");
        return card;
    }

    private static Color PrivateColor(string name)
        => (Color)typeof(NoticeCard).GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    private static bool Same(Color a, Color b)
        => Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2;

    /// <summary>在独立 STA 线程上跑真实 WinForms 代码（句柄/绘制需要 STA），带硬超时。</summary>
    private static void RunSta(Action body, string what, int timeoutSeconds = 90)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(timeoutSeconds)), $"{what}：STA 场景超时（{timeoutSeconds}s）");
        if (failure is not null) throw new Xunit.Sdk.XunitException($"{what} 抛异常：{failure}");
    }
}
