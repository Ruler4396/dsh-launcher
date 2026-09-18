using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        string Title, string Body, int ExpiresMs, Action? OnAction, string ActionText);

    private ShellLogic.NoticeCardLayout.Geometry _g;
    private ShellLogic.NoticeCardLayout.Placement _p;
    private readonly System.Windows.Forms.Timer _dismissTimer;
    private Font _titleFont;
    private Font _bodyFont;
    private Font _actionFont;
    private Item? _current;
    private Form? _anchor;
    private bool _hoverClose;

    private static readonly Color TextBlack = Color.FromArgb(31, 41, 55);
    private static readonly Color TextMuted = Color.FromArgb(107, 114, 128);
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color BorderColor = Color.FromArgb(229, 231, 235);

    private NoticeCard(int deviceDpi)
    {
        _g = ShellLogic.NoticeCardLayout.ComputeGeometry(deviceDpi);
        _titleFont = CardFont(_g.TitleEmPx);
        _bodyFont = CardFont(_g.BodyEmPx);
        _actionFont = CardFont(_g.BodyEmPx);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
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
        Action? onAction = null, string? actionText = null)
    {
        try
        {
            var item = new Item(
                title ?? "", body ?? "",
                (int)Math.Clamp(expireAfter.TotalMilliseconds, 3_000, 120_000),
                onAction,
                onAction is null ? "" : (string.IsNullOrWhiteSpace(actionText) ? "点击查看" : actionText!));

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
        if (_shared is { IsDisposed: false } shared && shared._current is not null)
        {
            // 已有卡片在显示：只排队，不开第二个窗口（"只维护一个对象"）。
            if (_pending.Count >= QueueCap)
            {
                var dropped = _pending.Dequeue();
                Logger.Warn("notice card queue full; oldest notice dropped", ctx: new { dropped.Title });
            }
            _pending.Enqueue(item);
            return;
        }
        if (_shared is null || _shared.IsDisposed)
        {
            var work = SafeWorkAreaOf(owner);
            _shared = new NoticeCard(MonitorDpi.ForPoint(
                new Point(work.Left + work.Width / 2, work.Top + work.Height / 2)));
        }
        _shared.ShowItem(owner, item);
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
        Relayout();
        var work = SafeWorkAreaOf(owner);
        var (x, y) = ShellLogic.NoticeCardLayout.PlaceAtBottomRight(work, _g, _p.Width, _p.Height);
        Location = new Point(x, y);
        _dismissTimer.Interval = item.ExpiresMs;
        _dismissTimer.Start();
        Visible = true;
        BringToFront();
        Invalidate();
    }

    /// <summary>收起当前条；队列里还有就立刻放下一条（复用同一窗口对象）。</summary>
    private void Advance()
    {
        _dismissTimer.Stop();
        Visible = false;
        _current = null;
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
        var titleH = TextRenderer.MeasureText(
            item.Title, _titleFont, new Size(_g.TextWidth - _g.CloseSize - _g.Gap, int.MaxValue),
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak).Height;
        var bodyH = TextRenderer.MeasureText(
            item.Body, _bodyFont, new Size(_g.TextWidth, int.MaxValue),
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
        SwapFont(ref _titleFont, _g.TitleEmPx);
        SwapFont(ref _bodyFont, _g.BodyEmPx);
        SwapFont(ref _actionFont, _g.BodyEmPx);
        Relayout();
        if (_anchor is { IsDisposed: false } anchor)
        {
            var work = SafeWorkAreaOf(anchor);
            var (x, y) = ShellLogic.NoticeCardLayout.PlaceAtBottomRight(work, _g, _p.Width, _p.Height);
            Location = new Point(x, y);
        }
        Invalidate();
    }

    private static void SwapFont(ref Font field, int emPx)
    {
        field.Dispose();
        field = CardFont(emPx);
    }

    /// <summary>字体回退链与托盘菜单一致（Noto Sans SC → 等线 → 微软雅黑 UI → 默认），
    /// 字号一律 GraphicsUnit.Pixel（物理像素由纯函数算好）。</summary>
    private static Font CardFont(int emPx)
    {
        try
        {
            var families = FontFamily.Families;
            foreach (var name in new[] { "Noto Sans SC", "DengXian", "Microsoft YaHei UI", "Microsoft YaHei" })
            {
                var f = Array.Find(families, x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
                if (f is not null) return new Font(f, emPx, FontStyle.Regular, GraphicsUnit.Pixel);
            }
        }
        catch { /* 字体枚举失败走默认 */ }
        return new Font(FontFamily.GenericSansSerif, emPx, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    // ---- 绘制与交互 ---------------------------------------------------------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.White);
        using (var border = new Pen(BorderColor) { DashStyle = DashStyle.Solid })
            g.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));

        var item = _current ?? default;
        TextRenderer.DrawText(g, item.Title, _titleFont, _p.TitleRect, TextBlack,
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        TextRenderer.DrawText(g, item.Body, _bodyFont, _p.BodyRect, TextMuted,
            TextFormatFlags.NoPadding | TextFormatFlags.WordBreak);
        if (item.OnAction is not null)
            TextRenderer.DrawText(g, item.ActionText, _actionFont, _p.ActionRect, Accent,
                TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "×", _titleFont, _p.CloseRect, TextMuted,
            TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
