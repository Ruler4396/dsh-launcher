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
    // 紧凑版（单功能按钮）：约缩小 20%；图标加粗、文字去加粗，视觉平衡。
    // 所有尺寸仍按 tray-preview.html 的比例体系等比缩减，DPI 缩放不变。
    private const int MenuWidth = 116;  // 原 142 等比缩减
    private const int MenuHeight = 40;  // 原 58
    private const int CornerRadius = 12; // 原 16
    private const int ItemInset = 5;     // .menu 的 padding:4 + 1px 边框 → .exit 条目内缩
    private const int ItemRadius = 6;    // 原 8
    private const int Shadow = 10;       // 阴影边距（逻辑，容纳 0 6px 16px 的扩散）

    private static readonly Color TextDanger = Color.FromArgb(216, 30, 6);    // #D81E06 电源.svg 的亮红
    private static readonly Color TextBlack = Color.FromArgb(31, 41, 55);     // #1F2937 退出文字黑
    private static readonly Color BorderColor = Color.FromArgb(229, 231, 235);
    private static readonly Color HoverFill = Color.FromArgb(20, 220, 38, 38); // .exit:hover rgba(220,38,38,.08)

    private readonly Action _onExit;
    private readonly float _s; // DPI 缩放（96 为 1）
    private readonly Font _exitFont;
    private System.Windows.Forms.Timer? _fadeTimer; // 淡入动画，完成后 Dispose（B3）
    private bool _hoverExit;
    private byte _alpha = 255;

    public TrayMenuForm(Action onExit)
    {
        _onExit = onExit;
        using (var g = CreateGraphics()) _s = Math.Max(1f, g.DpiX / 96f);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size((int)((MenuWidth + Shadow * 2) * _s), (int)((MenuHeight + Shadow * 2) * _s));
        BackColor = Color.White;
        _exitFont = CreateExitFont();
    }

    /// <summary>菜单字体回退链：Noto Sans SC（思源黑体）→ DengXian（等线，Win10/11 自带）
    /// → Microsoft YaHei UI → 系统默认，统一 Regular（400）单画——v0.2.3 再降一档：
    /// 前版 Medium(500)/伪粗体双画实测仍偏粗，与图标描边（1.8px）视觉不再平衡。
    /// 其他电脑缺字体时静默降级，不会回退成默认丑字体，也不会抛异常。
    /// 思源/等线为 TrueType 各字重独立 family，按 family 名检测存在性。</summary>
    private Font CreateExitFont()
    {
        try
        {
            var families = FontFamily.Families;
            // 1) 思源黑体：商务现代，Regular 字重清爽
            var noto = Array.Find(families, f => string.Equals(f.Name, "Noto Sans SC", StringComparison.OrdinalIgnoreCase));
            if (noto is not null) return new Font(noto, 10f * _s, FontStyle.Regular, GraphicsUnit.Point);
            // 2) 等线：Win10/11 自带
            var deng = Array.Find(families, f => string.Equals(f.Name, "DengXian", StringComparison.OrdinalIgnoreCase));
            if (deng is not null) return new Font(deng, 10f * _s, FontStyle.Regular, GraphicsUnit.Point);
            // 3) 微软雅黑：最通用兜底
            var yahei = Array.Find(families, f => string.Equals(f.Name, "Microsoft YaHei UI", StringComparison.OrdinalIgnoreCase)
                || string.Equals(f.Name, "Microsoft YaHei", StringComparison.OrdinalIgnoreCase));
            if (yahei is not null) return new Font(yahei, 10f * _s, FontStyle.Regular, GraphicsUnit.Point);
        }
        catch { /* 字体枚举失败走默认 */ }
        return new Font(FontFamily.GenericSansSerif, 10f * _s, FontStyle.Regular, GraphicsUnit.Point);
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
            DshWeb.Program.Trace("tray render failed: " + ex);
        }
    }

    private void Draw(Graphics g)
    {
        float s = _s;
        var content = new Rectangle((int)(Shadow * s), (int)(Shadow * s), (int)(MenuWidth * s), (int)(MenuHeight * s));
        int cr = (int)(CornerRadius * s);

        var item = new Rectangle(content.X + (int)(ItemInset * s), content.Y + (int)(ItemInset * s),
            content.Width - (int)(ItemInset * 2 * s), content.Height - (int)(ItemInset * 2 * s));
    
        // 柔和两级阴影（box-shadow 两级等比缩减；GDI+ 无原生高斯模糊，
        // 用多层扩张圆角矩形模拟衰减）
        DrawShadowLayer(g, content, cr, 5, 8, s);
        DrawShadowLayer(g, content, cr, 2, 3, s);
    
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
            using var hoverPath = RoundedRect(item, (int)(ItemRadius * s));
            g.FillPath(hb, hoverPath);
        }
    
        // 内容：红色电源图标 + 黑色"退出"（13px 常规、字距 2px，紧凑版式）
        int iconSize = (int)(18 * s);
        int gap = (int)(12 * s);
        int letterSpacing = (int)(2 * s);
        var m1 = TextRenderer.MeasureText(g, "退", _exitFont);
        var m2 = TextRenderer.MeasureText(g, "出", _exitFont);
        int totalW = iconSize + gap + m1.Width + letterSpacing + m2.Width;
        int x = item.X + (item.Width - totalW) / 2;
    
        DrawPowerIcon(g, x + iconSize / 2f, item.Y + item.Height / 2f, 5.2f * s, 1.8f * s);
    
        int tx = x + iconSize + gap;
        var r1 = new Rectangle(tx, item.Y, m1.Width + (int)(4 * s), item.Height);
        TextRenderer.DrawText(g, "退", _exitFont, r1, TextBlack, TextFormatFlags.VerticalCenter);
        var r2 = new Rectangle(tx + m1.Width + letterSpacing, item.Y, m2.Width + (int)(4 * s), item.Height);
        TextRenderer.DrawText(g, "出", _exitFont, r2, TextBlack, TextFormatFlags.VerticalCenter);
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

    /// <summary>多层扩张圆角矩形模拟柔和投影（dy 垂直偏移、spread 最大扩散，均为逻辑 px）。</summary>
    private static void DrawShadowLayer(Graphics g, Rectangle content, int cr, int dy, int spread, float s)
    {
        const int steps = 6;
        for (int i = steps; i >= 1; i--)
        {
            int e = (int)(spread * s * i / steps);
            var r = Rectangle.Inflate(content, e, e);
            r.Offset(0, (int)(dy * s));
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

    private bool HitExit(Point p) => new Rectangle((int)((Shadow + ItemInset) * _s), (int)((Shadow + ItemInset) * _s),
        (int)((MenuWidth - ItemInset * 2) * _s), (int)((MenuHeight - ItemInset * 2) * _s)).Contains(p);

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
