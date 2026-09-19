using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshWeb.Windows;

/// <summary>
/// 托盘右键菜单：LayeredWindow 自绘（alpha 平滑圆角无锯齿 + 商务质感）。
/// 内容仅"红色电源图标 + 退出"（黑色、居中、加粗、字距）；实心浅色底 + 轻阴影。
/// 全部尺寸按当前 DPI 缩放（物理 = 逻辑 × scale），150% 屏上与 HTML 预览观感一致。
/// Step 6：从 Program.cs 迁出（纯搬迁，行为逐位不变）。
/// </summary>
internal sealed class TrayMenuForm : Form
{
    // 全部尺寸/字号的设计基准与折算规则沉在纯函数 ShellLogic.TrayMenuLayout（契约测试锁定）：
    // 本类只消费"物理像素"几何，不再自己乘缩放系数——历史上正是这里"点数 × DPI 系数"与
    // 绘制 DC 自带的 DPI 相乘，导致高 DPI 屏上文字按 s² 放大（issue #28-3 用户截图）。
    private ShellLogic.TrayMenuLayout.Geometry _g;
    private int _deviceDpi;

    private static readonly Color TextDanger = Color.FromArgb(216, 30, 6);    // #D81E06 电源.svg 的亮红
    private static readonly Color TextBlack = Color.FromArgb(31, 41, 55);     // #1F2937 退出文字黑
    private static readonly Color BorderColor = Color.FromArgb(229, 231, 235);
    private static readonly Color HoverFill = Color.FromArgb(20, 220, 38, 38); // .exit:hover rgba(220,38,38,.08)

    private readonly Action _onExit;
    private Font _exitFont;
    private System.Windows.Forms.Timer? _fadeTimer; // 淡入动画，完成后 Dispose（B3）
    private bool _hoverExit;
    private byte _alpha = 255;

    /// <param name="deviceDpi">菜单**将要出现的那块显示器**的 DPI（由 WindowManager 按光标位置
    /// 反查）。旧实现用 CreateGraphics() 在构造时采样，那时 Location 还是 (0,0) → 恒取主屏 DPI，
    /// 混屏（主屏 200% + 副屏 100%）下副屏上的菜单尺寸完全错。0 = 未知，按 96 处理。</param>
    public TrayMenuForm(Action onExit, int deviceDpi)
    {
        _onExit = onExit;
        _deviceDpi = DshWeb.ShellLogic.DpiScale.Sanitize(deviceDpi);
        _g = ShellLogic.TrayMenuLayout.ComputeGeometry(_deviceDpi);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(_g.FormWidth, _g.FormHeight);
        BackColor = Color.White;
        _exitFont = CreateExitFont(_g.EmPx);
        // [2026-09 IME 崩溃护栏] 托盘菜单弹出时会 Activate() 抢占激活，焦点变化同样会走 WinForms
        // 的 ImeContext 路径（第三方 IME 上 ImmSetOpenStatus 访问违规），必须解绑 IME 上下文。
        Win32.ImeContextGuard.Harden(this);
    }

    /// <summary>跨显示器弹出时按新 DPI 重算几何、字号与窗体尺寸（PMv2 下 WinForms 不会自动缩放
    /// 一个手工布局的自绘窗口）。</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (e.DeviceDpiOld == e.DeviceDpiNew) return;
        _deviceDpi = e.DeviceDpiNew;
        _g = ShellLogic.TrayMenuLayout.ComputeGeometry(_deviceDpi);
        _exitFont.Dispose();
        _exitFont = CreateExitFont(_g.EmPx);
        Size = new Size(_g.FormWidth, _g.FormHeight);
        Render();
    }

    /// <summary>菜单字体回退链：Noto Sans SC（思源黑体）→ DengXian（等线，Win10/11 自带）
    /// → Microsoft YaHei UI → 系统默认，统一 Regular（400）单画——v0.2.3 再降一档：
    /// 前版 Medium(500)/伪粗体双画实测仍偏粗，与图标描边（1.8px）视觉不再平衡。
    /// 其他电脑缺字体时静默降级，不会回退成默认丑字体，也不会抛异常。
    /// 【字号单位】一律 <c>GraphicsUnit.Pixel</c>：像素字号与绘制 DC 的 DPI 无关，
    /// 缩放折算只在 <see cref="ShellLogic.TrayMenuLayout.ComputeGeometry"/> 里发生一次。</summary>
    private static Font CreateExitFont(int emPx)
    {
        try
        {
            var families = FontFamily.Families;
            // 1) 思源黑体：商务现代，Regular 字重清爽
            var noto = Array.Find(families, f => string.Equals(f.Name, "Noto Sans SC", StringComparison.OrdinalIgnoreCase));
            if (noto is not null) return new Font(noto, emPx, FontStyle.Regular, GraphicsUnit.Pixel);
            // 2) 等线：Win10/11 自带
            var deng = Array.Find(families, f => string.Equals(f.Name, "DengXian", StringComparison.OrdinalIgnoreCase));
            if (deng is not null) return new Font(deng, emPx, FontStyle.Regular, GraphicsUnit.Pixel);
            // 3) 微软雅黑：最通用兜底
            var yahei = Array.Find(families, f => string.Equals(f.Name, "Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.Name, "Microsoft YaHei", StringComparison.OrdinalIgnoreCase));
            if (yahei is not null) return new Font(yahei, emPx, FontStyle.Regular, GraphicsUnit.Pixel);
        }
        catch { /* 字体枚举失败走默认 */ }
        return new Font(FontFamily.GenericSansSerif, emPx, FontStyle.Regular, GraphicsUnit.Pixel);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00080000; // WS_EX_LAYERED
            return cp;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Render();
        // 抢占激活：菜单窗收到焦点后，用户点击其他任意窗口/桌面时才会触发
        // OnDeactivate → 关闭（与系统右键菜单"点外即消"行为一致）。
        Activate();
        // 淡入动画 Timer：字段持有防 GC，完成后 Dispose（每次弹菜单一个，不泄漏，B3）。
        _fadeTimer = new System.Windows.Forms.Timer { Interval = 12 };
        var start = DateTime.UtcNow;
        _fadeTimer.Tick += (_, _) =>
        {
            var p = Math.Min(1.0, (DateTime.UtcNow - start).TotalMilliseconds / 120.0);
            _alpha = (byte)(255 * p);
            Render();
            if (p >= 1.0)
            {
                _fadeTimer.Stop();
                _fadeTimer.Dispose();
                _fadeTimer = null;
            }
        };
        _fadeTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // 关闭时清理：淡入中途关闭的 Timer + 菜单字体（GDI 句柄）
        _fadeTimer?.Stop();
        _fadeTimer?.Dispose();
        _fadeTimer = null;
        _exitFont.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { }

    private void Render()
    {
        try
        {
            using var bmp = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            // [issue #28-3] 显式把画布钉在 96 DPI：绘制 DC 自带 DPI 时，字号会被再折算一次，
            // 与几何里已经乘过的缩放相乘 → 高 DPI 屏上文字按 s² 放大。像素字号 + 96 DPI 画布
            // 让"一次折算"成为结构性保证，也让渲染结果可在 96 DPI 的 CI 上按任意目标 DPI 复现。
            bmp.SetResolution(96f, 96f);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
                Draw(g);
            }
            UpdateLayered(bmp, _alpha);
        }
        catch (Exception ex)
        {
            Logger.Info("tray render failed: " + ex);
        }
    }

    private void Draw(Graphics g)
    {
        var geo = _g;
        var content = geo.Content;
        int cr = geo.CornerRadius;
        var item = geo.Item;

        // 柔和两级阴影（box-shadow 两级等比缩减；GDI+ 无原生高斯模糊，
        // 用多层扩张圆角矩形模拟衰减）
        DrawShadowLayer(g, content, cr, geo.Shadow1Dy, geo.Shadow1Spread);
        DrawShadowLayer(g, content, cr, geo.Shadow2Dy, geo.Shadow2Spread);

        // 白底 + 1px 边框（.menu: #fff + #E5E7EB）
        using (var bgPath = RoundedRect(content, cr))
        {
            using var bg = new SolidBrush(Color.White);
            g.FillPath(bg, bgPath);
            using var pen = new Pen(BorderColor);
            g.DrawPath(pen, bgPath);
        }

        // hover：只铺 .exit 条目区域（内缩 5、圆角 8，与 CSS 一致）
        if (_hoverExit)
        {
            using var hb = new SolidBrush(HoverFill);
            using var hoverPath = RoundedRect(item, geo.ItemRadius);
            g.FillPath(hb, hoverPath);
        }

        // 内容：红色电源图标 + 黑色"退出"（字号 = 10pt@96dpi 折算后的像素、字距 2px，紧凑版式）
        // [issue #28-1 回归修复] 测量/绘制必须带 NoPadding：
        // TextRenderer 默认 flags 会在每侧加 ~4-5px 内边距（实测 Noto Sans SC 10pt：
        // 默认 23px/字 vs NoPadding 14px/字），旧实现还额外给首字矩形 +4*s 宽度——
        // 两者叠加把"字距 2px"放大成 ~11px（1x）/ 22px（2x）的视觉空档（用户截图报的
        // "退出按钮 UI 异常"）。现在矩形边界 = 字形边界，字距严格等于 letterSpacing。
        var m1 = TextRenderer.MeasureText(g, "退", _exitFont, Size.Empty, TextFormatFlags.NoPadding);
        var m2 = TextRenderer.MeasureText(g, "出", _exitFont, Size.Empty, TextFormatFlags.NoPadding);
        var place = ShellLogic.TrayMenuLayout.PlaceExitRow(
            item.X, item.Width, geo.IconSize, geo.Gap, m1.Width, m2.Width, geo.LetterSpacing);

        var (iconRadius, iconStroke) = ShellLogic.TrayMenuLayout.IconGeometry(_deviceDpi);
        DrawPowerIcon(g, place.IconCenterX, item.Y + item.Height / 2f, iconRadius, iconStroke);

        const TextFormatFlags textFlags = TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPadding | TextFormatFlags.Left;
        var r1 = new Rectangle(place.FirstCharX, item.Y, m1.Width, item.Height);
        TextRenderer.DrawText(g, "退", _exitFont, r1, TextBlack, textFlags);
        var r2 = new Rectangle(place.SecondCharX, item.Y, m2.Width, item.Height);
        TextRenderer.DrawText(g, "出", _exitFont, r2, TextBlack, textFlags);
    }

    /// <summary>电源图标，复刻「电源.svg」（#D81E06，顶部开口圆环 + 圆头竖线）。
    /// 几何按 SVG viewBox(1024) 换算到 18px 图标框并加粗：环中线半径 5.2px、线宽 1.8px；
    /// 开口 234°–305°（约 71°，居中正上方）；竖线从环顶上方伸到中心上方（r×1.22 → r×0.23）。
    /// 用 Pen 描边而不是 FillPath 双圆弧拼环体——拼环的起弧角度/填充模式易错
    /// （曾把开口画到正右方渲染成"C"），描边对任意 DPI/缩放都稳定。</summary>
    private static void DrawPowerIcon(Graphics g, float cx, float cy, float r, float stroke)
    {
        using var pen = new Pen(TextDanger, stroke)
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
        };
        // 开口在正上方：从 305° 顺时针扫 289° 到 234°（GDI+ 角度 0°=3 点钟方向、
        // 顺时针为正，270° 即正上方）
        g.DrawArc(pen, cx - r, cy - r, r * 2, r * 2, 305f, 289f);
        // 圆头竖线：上端超出环外顶 0.6px（r×1.22），下端到中心上方 1.3px（r×0.23）
        g.DrawLine(pen, cx, cy - r * 1.22f, cx, cy - r * 0.23f);
    }

    /// <summary>多层扩张圆角矩形模拟柔和投影（dy 垂直偏移、spread 最大扩散，单位=物理像素，
    /// 缩放折算已在 <see cref="ShellLogic.TrayMenuLayout.ComputeGeometry"/> 完成）。</summary>
    private static void DrawShadowLayer(Graphics g, Rectangle content, int cr, int dyPx, int spreadPx)
    {
        const int steps = 6;
        for (int i = steps; i >= 1; i--)
        {
            int e = spreadPx * i / steps;
            var r = Rectangle.Inflate(content, e, e);
            r.Offset(0, dyPx);
            using var b = new SolidBrush(Color.FromArgb(6, 0, 0, 0));
            using var p = RoundedRect(r, cr + e);
            g.FillPath(b, p);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private bool HitExit(Point p) => _g.Item.Contains(p);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var h = HitExit(e.Location);
        if (h != _hoverExit) { _hoverExit = h; Render(); }
        base.OnMouseMove(e);
    }
    protected override void OnMouseLeave(EventArgs e)
    {
        if (_hoverExit) { _hoverExit = false; Render(); }
        base.OnMouseLeave(e);
    }
    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && HitExit(e.Location)) { Close(); _onExit(); return; }
        base.OnMouseClick(e);
    }
    protected override void OnDeactivate(EventArgs e) { base.OnDeactivate(e); Close(); }
    // 失效关闭能生效的前提：OnShown 里 Activate() 抢占激活（菜单窗从未被激活过
    // 则永远不会收到 Deactivate——0.2.3 前"点外不消失"的根因）。
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape) { Close(); return true; }
        return base.ProcessDialogKey(keyData);
    }

    // ---- LayeredWindow ----
    private void UpdateLayered(Bitmap bmp, byte alpha)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            old = SelectObject(memDc, hBitmap);
            var ptDst = new POINT { X = Left, Y = Top };
            var size = new SIZE { Width = Width, Height = Height };
            var ptSrc = new POINT { X = 0, Y = 0 };
            var blend = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            UpdateLayeredWindow(Handle, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, 2);
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, int dwFlags);
}
