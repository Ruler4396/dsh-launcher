using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Media;
using System.Windows.Forms;
using DshWeb.Win32;

namespace DshWeb.Windows;

/// <summary>
/// 壳的**唯一**通知呈现对象（issue #25 收口）：自绘、非模态、置顶、不抢焦点、到时自动收起。
///
/// 【为什么存在】通知通道在本仓库里换过三代，每一代都留了一套"看得见"的实现和它自己的失效
/// 模式：托盘气泡（依赖托盘存在，默认 FollowWindow 模式下 TrayIcon 为 null → 静默丢弃）→
/// v0.4.1 系统 Toast（手写 WinRT，被 wpnapps.dll 的 native AV 击穿宿主，issue #25）→
/// 护栏 + 气泡 + 标题驻留三档回退链。现在只留这一条：不经托盘、不经 WPN（wpnapps.dll 在本
/// 进程**永不加载**）、不阻塞消息泵（模态 MessageBox 会让来一条 bot 消息就锁一次界面）。
///
/// 【单实例】全局只维护一个窗口对象：正在显示时新通知进队列（上限 QueueCap，溢出丢最旧并
/// Warn），当前条收起后自动放下一条。队列是数据，不是第二个窗口。
///
/// 【尽力而为】Present() 绝不抛出：通知是旁路，不能让"弹不出通知"变成业务失败。所有异常
/// 一律 Warn 留痕（空 catch 违反铁律三）。几何/定位全部来自纯函数
/// <see cref="ShellLogic.NoticeCardLayout"/>，本类只消费物理像素、**绝不自己乘 DPI 系数**
/// （issue #28-3 的教训：点×系数 与绘制 DC 自带的 DPI 相乘 → 高 DPI 下按 s² 放大）。
/// </summary>
internal sealed class NoticeCard : Form
{
    private const int QueueCap = 8;
    private static NoticeCard? _shared;
    private static readonly Queue<Item> _pending = new();

    private readonly record struct Item(
        string Title, string Body, int ExpiresMs, Action? OnAction, string ActionText, string Key,
        ShellLogic.NoticeKind Kind);

    /// <summary>窗口标题（不可见）：外部测试定位卡片的唯一稳定凭据。</summary>
    public const string WindowTitle = "DshNoticeCard";

    /// <summary>最近**受理**的内容键与时刻：同一条内容在冷却窗内只受理一次（正在显示的
    /// 与已排队的都算，见 PresentOnUi）。静态——它是"唯一对象"的去重状态。</summary>
    private static string? _lastKey;
    private static DateTimeOffset _lastAcceptedUtc;

    private ShellLogic.NoticeCardLayout.Geometry _g;
    private ShellLogic.NoticeCardLayout.Placement _p;
    private readonly System.Windows.Forms.Timer _dismissTimer;
    private Font _titleFont;
    private Font _bodyFont;
    private Font _actionFont;
    private Item? _current;
    private Form? _anchor;
    private bool _hoverClose;
    /// <summary>悬停暂停：鼠标在卡片上时不倒计时（"来不及读/来不及点"是自动收起的固有矛盾），
    /// 离开后按剩余时间继续；剩余为 0 则立即收起。</summary>
    private DateTime _expiresAtUtc;
    private bool _hovering;

    private static readonly Color TextBlack = Color.FromArgb(31, 41, 55);      // #1F2937 14.68:1
    private static readonly Color TextBody = Color.FromArgb(55, 65, 81);       // #374151 ≈10.9:1（原 #6B7280 仅 4.83:1）
    private static readonly Color AccentInfo = Color.FromArgb(37, 99, 235);    // #2563EB
    private static readonly Color AccentUrgent = Color.FromArgb(216, 30, 6);   // #D81E06（与托盘退出图标同色）
    private static readonly Color BorderColor = Color.FromArgb(209, 213, 219); // #D1D5DB（原 #E5E7EB 对白底仅 1.24:1）
    private static readonly Color CardBack = Color.FromArgb(252, 252, 253);    // 与纯白页面拉开一层

    private NoticeCard(int deviceDpi)
    {
        _g = ShellLogic.NoticeCardLayout.ComputeGeometry(deviceDpi);
        _titleFont = CardFont(_g.TitleEmPx, bold: true);
        _bodyFont = CardFont(_g.BodyEmPx);
        _actionFont = CardFont(_g.BodyEmPx, bold: true);
        FormBorderStyle = FormBorderStyle.None;
        // 无边框自绘窗没有可视标题，但窗口标题是外部测试（RealOS/E2E 用 EnumWindows 定位卡片）
        // 唯一能稳定区分"这张卡片"与桌面上其它同尺寸窗口的凭据——按类名/矩形都认不出来。
        Text = WindowTitle;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = CardBack;
        AutoScaleMode = AutoScaleMode.None;   // 手工布局 + OnDpiChanged 重算，禁 WinForms 自动缩放
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        // [issue #28 IME 护栏] 任何自绘窗口都可能在激活/焦点切换时踩第三方输入法的
        // ImmSetOpenStatus 访问违规；卡片本身不接收焦点，仍统一解绑 IME 上下文。
        ImeContextGuard.Harden(this);
        _dismissTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _dismissTimer.Tick += (_, _) => Advance();
    }

    /// <summary>非模态的关键：显示时**不抢占焦点**，用户在主窗上打字不该被通知打断。</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x00000200;   // CS_DROPSHADOW：无边框窗口的投影，让卡片浮在内容之上
            return cp;
        }
    }

    // ---- 公共入口 -----------------------------------------------------------

    /// <summary>尽力呈现一条通知卡片；返回是否已受理（呈现或排队）。绝不抛出。</summary>
    public static bool Present(
        Form? owner, string title, string body, TimeSpan expireAfter,
        Action? onAction = null, string? actionText = null,
        ShellLogic.NoticeKind kind = ShellLogic.NoticeKind.Info)
    {
        try
        {
            var title_ = title ?? "";
            var body_ = body ?? "";
            var item = new Item(
                title_, body_,
                ShellLogic.NoticePolicy.ResolveExpiryMs(expireAfter),
                onAction,
                onAction is null ? "" : (string.IsNullOrWhiteSpace(actionText) ? "点击查看" : actionText!),
                ShellLogic.NoticeDedupe.Key(title_, body_), kind);

            if (owner is null || owner.IsDisposed || !owner.IsHandleCreated)
            {
                Logger.Warn("notice card not presented: no owner window handle", ctx: new { item.Title });
                return false;
            }
            if (owner.InvokeRequired)
            {
                owner.BeginInvoke(new Action(() => PresentOnUi(owner, item)));
                return true;
            }
            PresentOnUi(owner, item);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("notice card present failed: " + ex.Message);
            return false;
        }
    }

    private static void PresentOnUi(Form owner, Item item)
    {
        // 去重闸门：同一条内容在冷却窗内只受理一次。正在显示的与已排队的都受这一条约束
        // （受理时刻在下方统一登记），所以调用方重复轮询/重复信号不会刷屏。
        // 抑制必须留痕——静默丢通知比重复通知更难排查。
        var now = DateTimeOffset.UtcNow;
        if (ShellLogic.NoticeDedupe.ShouldSuppress(
                item.Key, _lastKey, _lastAcceptedUtc, now))
        {
            Logger.Info("notice suppressed as duplicate (within cooldown)", ctx: new
            {
                title = item.Title,
                cooldownSeconds = ShellLogic.NoticeDedupe.DefaultCooldownSeconds,
                sinceAcceptedSeconds = (int)(now - _lastAcceptedUtc).TotalSeconds,
            });
            return;
        }

        if (_shared is { IsDisposed: false } shared && shared._current is not null)
        {
            // 已有卡片在显示：只排队，不开第二个窗口（"只维护一个对象"）。
            if (_pending.Count >= QueueCap)
            {
                var dropped = _pending.Dequeue();
                Logger.Warn("notice card queue full; oldest notice dropped", ctx: new { dropped.Title });
            }
            _pending.Enqueue(item);
            Accept(item, now);
            return;
        }
        if (_shared is null || _shared.IsDisposed)
        {
            var work = SafeWorkAreaOf(owner);
            _shared = new NoticeCard(MonitorDpi.ForPoint(
                new Point(work.Left + work.Width / 2, work.Top + work.Height / 2)));
        }
        Accept(item, now);
        _shared.ShowItem(owner, item);
    }

    /// <summary>登记"这条内容已受理"——显示与排队都算，作为去重闸门的时间基准。</summary>
    private static void Accept(Item item, DateTimeOffset at)
    {
        _lastKey = item.Key;
        _lastAcceptedUtc = at;
    }

    /// <summary>owner 已销毁/最小化时退回主屏工作区——通知定位绝不能抛进调用方。</summary>
    private static Rectangle SafeWorkAreaOf(Form owner)
    {
        try { return Screen.FromControl(owner).WorkingArea; }
        catch (Exception ex)
        {
            Logger.Warn("notice card screen lookup failed, using primary: " + ex.Message);
            return Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1040);
        }
    }

    // ---- 单实例的显示/轮转 ---------------------------------------------------

    private void ShowItem(Form owner, Item item)
    {
        _anchor = owner;
        _current = item;
        _hovering = false;
        Relayout();
        var work = SafeWorkAreaOf(owner);
        var (x, y) = ShellLogic.NoticeCardLayout.PlaceAtBottomRight(work, _g, _p.Width, _p.Height);
        Location = new Point(x, y);
        // ExpiresMs == 0 → sticky：不启动倒计时，只能由用户点击动作或 × 关闭。
        // 安全模式这类"必须处理完才能继续"的提示若自动消失，等于把入口收走。
        _expiresAtUtc = item.ExpiresMs == 0
            ? DateTime.MaxValue
            : DateTime.UtcNow.AddMilliseconds(item.ExpiresMs);
        if (item.ExpiresMs == 0) _dismissTimer.Stop();
        else { _dismissTimer.Interval = item.ExpiresMs; _dismissTimer.Start(); }
        Visible = true;
        BringToFront();
        Invalidate();
        // "受理"与"真的显示出来"是两件事（排队中的那条要等前一条收起）。诊断与回归都以后者为准。
        Logger.Info($"notice displayed: {item.Title}", ctx: new
        {
            kind = item.Kind.ToString(),
            ms = item.ExpiresMs,
            queued = _pending.Count,
        });
        // 提示音：卡片没有 OS toast 的滑动动画与声音，静音是"不显眼"的一半原因。
        // Play() 本身非阻塞；失败只留痕（无声不该让通知整体失败）。
        try
        {
            if (ShellLogic.NoticePolicy.UseWarningCue(item.Kind)) SystemSounds.Exclamation.Play();
            else SystemSounds.Asterisk.Play();
        }
        catch (Exception ex) { Logger.Warn("notice sound failed (shown silently): " + ex.Message); }
    }

    /// <summary>收起当前条；队列里还有就立刻放下一条（复用同一窗口对象）。</summary>
    private void Advance()
    {
        _dismissTimer.Stop();
        Visible = false;
        _current = null;
        _hovering = false;
        if (_pending.Count == 0) return;
        var next = _pending.Dequeue();
        var anchor = _anchor;
        if (anchor is null || anchor.IsDisposed)
        {
            _pending.Clear();
            Logger.Warn("notice card queue cleared: owner window gone");
            return;
        }
        ShowItem(anchor, next);
    }

    private void Relayout()
    {
        var item = _current ?? default;
        var (titleW, bodyW) = ShellLogic.NoticeCardLayout.MeasureWidths(_g);
        var titleH = TextRenderer.MeasureText(
            item.Title, _titleFont, new Size(titleW, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak).Height;
        var bodyH = TextRenderer.MeasureText(
            item.Body, _bodyFont, new Size(bodyW, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak).Height;
        // NoPadding 让矩形边界 = 字形边界（issue #28-1 的排版根因），不再额外加任何内边距。
        _p = ShellLogic.NoticeCardLayout.Place(_g, titleH, bodyH, item.OnAction is not null);
        Size = new Size(_p.Width, _p.Height);
        using var path = RoundedRect(new Rectangle(0, 0, _p.Width, _p.Height), _g.CornerRadius);
        Region = new Region(path);
    }

    /// <summary>跨显示器时按新 DPI 重算几何（PMv2 下 WinForms 不会自动缩放手工布局的自绘窗口）。</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (e.DeviceDpiOld == e.DeviceDpiNew) return;
        _g = ShellLogic.NoticeCardLayout.ComputeGeometry(e.DeviceDpiNew);
        SwapFont(ref _titleFont, _g.TitleEmPx, bold: true);
        SwapFont(ref _bodyFont, _g.BodyEmPx);
        SwapFont(ref _actionFont, _g.BodyEmPx, bold: true);
        Relayout();
        if (_anchor is { IsDisposed: false } anchor)
        {
            var work = SafeWorkAreaOf(anchor);
            var (x, y) = ShellLogic.NoticeCardLayout.PlaceAtBottomRight(work, _g, _p.Width, _p.Height);
            Location = new Point(x, y);
        }
        Invalidate();
    }

    private static void SwapFont(ref Font field, int emPx, bool bold = false)
    {
        field.Dispose();
        field = CardFont(emPx, bold);
    }

    /// <summary>字体回退链与托盘菜单一致（Noto Sans SC → 等线 → 微软雅黑 UI → 默认），
    /// 字号一律 GraphicsUnit.Pixel（物理像素由纯函数算好）。</summary>
    private static Font CardFont(int emPx, bool bold = false)
    {
        try
        {
            var families = FontFamily.Families;
            foreach (var name in new[] { "Noto Sans SC", "DengXian", "Microsoft YaHei UI", "Microsoft YaHei" })
            {
                var f = Array.Find(families, x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (f is not null)
                {
                    // 并非每个家族都有 Bold 字重；缺就回退 Regular（GDI 的合成粗体在 ClearType
                    // 下发虚），宁可不粗也不能糊。
                    var actual = bold && f.IsStyleAvailable(FontStyle.Bold) ? FontStyle.Bold : FontStyle.Regular;
                    return new Font(f, emPx, actual, GraphicsUnit.Pixel);
                }
            }
        }
        catch { /* 字体枚举失败走默认 */ }
        return new Font(FontFamily.GenericSansSerif, emPx, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    // ---- 绘制与交互 ---------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(CardBack);
        var item = _current ?? default;
        var urgent = ShellLogic.NoticePolicy.UseWarningCue(item.Kind);
        // 左侧强调色条：撑满全高，是卡片与背景分离的主线索（边框只有 1.24→2.x:1，不够）
        using (var accentBrush = new SolidBrush(urgent ? AccentUrgent : AccentInfo))
            g.FillRectangle(accentBrush, _p.AccentRect);
        using (var border = new Pen(BorderColor))
            g.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

        TextRenderer.DrawText(g, item.Title, _titleFont, _p.TitleRect, TextBlack,
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        TextRenderer.DrawText(g, item.Body, _bodyFont, _p.BodyRect, TextBody,
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        if (item.OnAction is not null)
            TextRenderer.DrawText(g, item.ActionText, _actionFont, _p.ActionRect,
                urgent ? AccentUrgent : AccentInfo,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "×", _titleFont, _p.CloseRect, TextBody,
            TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    /// <summary>悬停暂停倒计时：鼠标停在卡片上就冻结到期时刻，移开后按剩余时间续；
    /// 已经读完并移开的场景不受影响。<b>sticky 卡片没有倒计时，必须整段跳过</b>——
    /// 否则 _expiresAtUtc 的 DateTime.MaxValue 会被算成"还剩很久"，移开鼠标后反而给它
    /// 装上倒计时，把"不自动收起"悄悄降级成 120 秒。</summary>
    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        if (_current is not { ExpiresMs: > 0 } || _hovering) return;
        _hovering = true;
        _dismissTimer.Stop();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (!_hovering || _current is not { ExpiresMs: > 0 }) return;
        _hovering = false;
        var remain = _expiresAtUtc - DateTime.UtcNow;
        if (remain.TotalMilliseconds <= 0) { Advance(); return; }
        _dismissTimer.Interval = (int)Math.Min(remain.TotalMilliseconds, 120_000);
        _dismissTimer.Start();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_current is not { } item) return;
        if (_p.CloseRect.Contains(e.Location)) { Advance(); return; }
        // 卡片整体即动作热区：老通知的"点击此处"语义（OnPendingBalloonClicked / 退出安全模式）。
        Advance();
        try { item.OnAction?.Invoke(); }
        catch (Exception ex) { Logger.Warn("notice card action failed: " + ex.Message); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var over = _p.CloseRect.Contains(e.Location);
        if (over == _hoverClose) return;
        _hoverClose = over;
        Cursor = over ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _dismissTimer.Dispose();
        _titleFont.Dispose();
        _bodyFont.Dispose();
        _actionFont.Dispose();
        Region?.Dispose();
        if (ReferenceEquals(_shared, this)) _shared = null;
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = Math.Max(1, radius * 2);
        var path = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
