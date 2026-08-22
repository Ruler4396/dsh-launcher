using DshWeb;
using DshWeb.Windows;

namespace DshWeb.Chrome;

/// <summary>
/// 自绘标题栏（无边框窗口用）：背景/文字/按钮颜色完全自绘，主题切换即时生效，
/// 不依赖 DWM 标题栏重绘（实测本机 DWM 属性切换后标题栏画面不刷新，只有焦点变化才重绘）。
/// 提供：标题 + 主题鲸鱼图标 + 最小化/最大化/关闭按钮 + 拖拽移动 + 双击最大化 + 右键系统菜单。
/// 由 Program（组合根/DshShellForm 宿主）经 WindowManager 装配使用；共享图标/主题资源与
/// user32 P/Invoke 暂由 Program 持有（内部委托），后续随 WindowManager 物理迁入收敛。
/// </summary>
internal sealed class CustomTitleBar : Panel
{
    private readonly DshShellForm _owner;
    private float _scale;
    private int _btnWidth;
    private bool _dark;
    private bool _hoverMin, _hoverMax, _hoverClose;

    // ---- 任务五：后台更新构建状态（UI 反馈） ----
    /// <summary>构建状态枚举（组合根写入，OnPaint 读取渲染）。
    /// [2026-08 回归修复] 新增 Failed：此前 Ready/Failed 终态从不渲染（只画 Building），
    /// 成功 100% 与失败结论用户均不可见。</summary>
    internal enum BuildStatus { Idle, Building, Ready, Failed }
    /// <summary>当前构建状态（volatile 保证跨线程可见性）。</summary>
    internal volatile BuildStatus _buildStatus = BuildStatus.Idle;
    /// <summary>构建进度文本（如 "已构建更新 50%（v0.1.0-rc.7）"）。</summary>
    internal volatile string _buildProgressText = "";
    /// <summary>窗口标题（安全模式时为 "DeepSeek Harness（安全模式）"，ADR-022 Task 4 横幅）。</summary>
    internal volatile string _titleText = "DeepSeek Harness";
    /// <summary>构建进度百分比（0.0 - 1.0），用于绘制整体进度条。</summary>
    internal volatile float _buildProgressPercent = 0f;
    /// <summary>脉冲动画定时器（替代 BeginInvoke(Invalidate) 避免无限闪烁）。</summary>
    private System.Windows.Forms.Timer? _marqueeTimer;

    private static readonly Font TitleFont = new("Microsoft YaHei UI", 9F);
    private static readonly Color DarkBg = Color.FromArgb(32, 32, 32);
    private static readonly Color LightBg = Color.FromArgb(240, 240, 240);
    private static readonly Color DarkText = Color.White;
    private static readonly Color LightText = Color.FromArgb(30, 30, 30);
    private static readonly Color DarkHover = Color.FromArgb(58, 58, 58);
    private static readonly Color LightHover = Color.FromArgb(229, 229, 229);
    private static readonly Color CloseHover = Color.FromArgb(232, 17, 35);

    public CustomTitleBar(DshShellForm owner, bool dark)
    {
        _owner = owner;
        _dark = dark;
        // [2026-08 回归修复] 双缓冲：构建进度高频 Invalidate 时旧实现每帧先擦背景再绘制
        // （WM_ERASEBKGND + OnPaint 两段），产生可见闪烁。三样式联用把绘制合并到内存位图。
        SetStyle(ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer, true);
        // DPI 缩放：150% 缩放下 32px 物理高度会显得又矮又挤（按钮/图标/间距全按逻辑缩放）
        _scale = owner.DeviceDpi / 96f;
        _btnWidth = (int)Math.Round(46 * _scale);
        BackColor = _dark ? DarkBg : LightBg;
        MouseDown += OnMouseDown;
        MouseUp += OnMouseUp;
        MouseDoubleClick += OnDoubleClick;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) =>
        {
            if (_hoverMin || _hoverMax || _hoverClose)
            {
                _hoverMin = _hoverMax = _hoverClose = false;
                Invalidate();
            }
        };
    }

    /// <summary>主题切换：自绘颜色立即更新（无 DWM 重绘问题）。</summary>
    public void ApplyTheme(bool dark)
    {
        _dark = dark;
        BackColor = _dark ? DarkBg : LightBg;
        Invalidate();
    }

    /// <summary>DPI 变化时重算缩放比例与按钮宽度。</summary>
    public void Rescale(float scale)
    {
        _scale = scale;
        _btnWidth = (int)Math.Round(46 * _scale);
        Invalidate();
    }

    private Rectangle BtnRect(int i) => new(Width - _btnWidth * (3 - i), 0, _btnWidth, Height);

    private int HitButton(int x)
    {
        for (var i = 0; i < 3; i++)
            if (BtnRect(i).Contains(x, Height / 2)) return i;
        return -1;
    }

    private void OnMouseDown(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            ShowSystemMenu(e.Location);
            return;
        }
        if (e.Button != MouseButtons.Left) return;
        if (HitButton(e.X) >= 0) return; // 按钮点击交给 MouseUp
        // 拖拽移动窗口（系统级 HTCAPTION 拖拽）
        Program.ReleaseCapture();
        Program.SendMessage(_owner.Handle, (uint)Program.WM_NCLBUTTONDOWN, (IntPtr)Program.HTCAPTION, IntPtr.Zero);
    }

    private void OnMouseUp(object? s, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        switch (HitButton(e.X))
        {
            case 0: _owner.WindowState = FormWindowState.Minimized; break;
            case 1:
                _owner.WindowState = _owner.WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal : FormWindowState.Maximized;
                break;
            case 2: _owner.Close(); break;
        }
    }

    private void OnDoubleClick(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left && HitButton(e.X) < 0)
            _owner.WindowState = _owner.WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    private void OnMouseMove(object? s, MouseEventArgs e)
    {
        var btn = HitButton(e.X);
        var h1 = btn == 0;
        var h2 = btn == 1;
        var h3 = btn == 2;
        if (h1 != _hoverMin || h2 != _hoverMax || h3 != _hoverClose)
        {
            _hoverMin = h1;
            _hoverMax = h2;
            _hoverClose = h3;
            Invalidate();
        }
    }

    private void ShowSystemMenu(Point p)
    {
        try
        {
            var hMenu = Program.GetSystemMenu(_owner.Handle, false);
            if (hMenu == IntPtr.Zero) return;
            Program.TrackPopupMenu(hMenu, Program.TPM_RETURNCMD | Program.TPM_RIGHTBUTTON,
                _owner.Left + p.X, _owner.Top + p.Y, 0, _owner.Handle, IntPtr.Zero);
        }
        catch { /* 系统菜单失败忽略 */ }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(_dark ? DarkBg : LightBg);
        var textColor = _dark ? DarkText : LightText;

        // 标题栏图标（主题对应鲸鱼，按 DPI 缩放）
        var icon = _dark
            ? (Program._lightWhaleIcon ??= Program.LoadIconResource("favicon-white.png"))
            : (Program._darkWhaleIcon ??= Program.LoadIconResource("favicon.png"));
        var iconSize = (int)Math.Round(16 * _scale);
        if (icon is not null)
        {
            g.DrawIcon(icon, new Rectangle((int)Math.Round(10 * _scale), (Height - iconSize) / 2, iconSize, iconSize));
        }

        // 标题
        var titleLeft = (int)Math.Round(34 * _scale);
        TextRenderer.DrawText(g, _titleText, TitleFont,
            new Rectangle(titleLeft, 0, Math.Max(0, Width - _btnWidth * 3 - titleLeft - 8), Height),
            textColor, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);

        // 窗口按钮：用 Segoe MDL2 字形（最小化/最大化/还原/关闭），清晰且与系统图标一致
        using (var btnFont = new Font("Segoe MDL2 Assets", (float)Math.Round(11 * _scale), FontStyle.Regular, GraphicsUnit.Pixel))
        {
            for (var i = 0; i < 3; i++)
            {
                var r = BtnRect(i);
                var hover = (i == 0 && _hoverMin) || (i == 1 && _hoverMax) || (i == 2 && _hoverClose);
                if (hover)
                {
                    using var hb = new SolidBrush(i == 2 ? CloseHover : (_dark ? DarkHover : LightHover));
                    g.FillRectangle(hb, r);
                }
                var glyph = i switch
                {
                    0 => '\uE921', // Minimize
                    1 => _owner.WindowState == FormWindowState.Maximized ? '\uE923' : '\uE922', // Restore / Maximize
                    _ => '\uE8BB', // ChromeClose
                };
                var glyphColor = hover && i == 2 && _dark ? Color.White : textColor;
                TextRenderer.DrawText(g, glyph.ToString(), btnFont, r, glyphColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
        }

        // 底部细分隔线
        using var line = new Pen(_dark ? Color.FromArgb(48, 48, 48) : Color.FromArgb(225, 225, 225));
        g.DrawLine(line, 0, Height - 1, Width, Height - 1);

        // ---- 任务五：构建进度指示 ----
        // 当后台正在构建更新时，在标题栏底部绘制 2px 高的蓝色进度条。
        // pnpm 模式：真实百分比进度条 + "已构建更新 x%（版本号）"
        // npm 模式：脉冲动画 + "正在构建更新（版本号）..."
        if (_buildStatus == BuildStatus.Building)
        {
            var progressColor = Color.FromArgb(77, 107, 254); // DeepSeek 蓝 #4D6BFE
            using var progressBrush = new SolidBrush(progressColor);

            if (_buildProgressPercent > 0)
            {
                // pnpm 真实进度：实心进度条
                var progressWidth = (int)(Width * Math.Min(1f, _buildProgressPercent));
                g.FillRectangle(progressBrush, 0, Height - 2, progressWidth, 2);
                // 停止脉冲定时器（如果有的话）
                StopMarqueeTimer();
            }
            else
            {
                // npm 脉冲模式：循环滚动的 15% 宽度光条
                var pulseOffset = (int)((Environment.TickCount64 / 33) % Width);
                var pulseWidth = (int)(Width * 0.15f);
                var x = pulseOffset - pulseWidth;
                g.FillRectangle(progressBrush, Math.Max(0, x), Height - 2, pulseWidth, 2);
                if (x + pulseWidth > Width)
                    g.FillRectangle(progressBrush, 0, Height - 2, (x + pulseWidth) - Width, 2);
                // 启动脉冲定时器（33ms 间隔，~30fps）
                StartMarqueeTimer();
            }

            // 文本显示
            if (!string.IsNullOrEmpty(_buildProgressText))
            {
                var statusFont = new Font("Microsoft YaHei UI", 8F);
                var statusText = " " + _buildProgressText;
                var statusWidth = TextRenderer.MeasureText(statusText, statusFont).Width;
                TextRenderer.DrawText(g, statusText, statusFont,
                    new Rectangle(Width - _btnWidth * 3 - statusWidth - 8, 0, statusWidth, Height),
                    _dark ? Color.FromArgb(150, 255, 255, 255) : Color.FromArgb(150, 0, 0, 0),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
        }
        else if (_buildStatus is BuildStatus.Ready or BuildStatus.Failed)
        {
            // [2026-08 回归修复] 终态驻留渲染：此前 Ready 从未绘制（只画 Building），且组合根
            // 在 finally 里立即清回 Idle → 成功/失败结论一帧都不可见。现在终态由驻留定时器
            // （Program.UpdateBuildStatus）保活约 12s：Ready=蓝色满宽条，Failed=红色满宽条，
            // 文案自含结论与版本号（ShellLogic.UpdateProgress.ComposeTerminalTitleText）。
            StopMarqueeTimer();
            var terminalColor = _buildStatus == BuildStatus.Ready
                ? Color.FromArgb(77, 107, 254)    // DeepSeek 蓝 #4D6BFE
                : Color.FromArgb(229, 72, 77);    // 红 #E5484D
            using var terminalBrush = new SolidBrush(terminalColor);
            g.FillRectangle(terminalBrush, 0, Height - 2, Width, 2);
            if (!string.IsNullOrEmpty(_buildProgressText))
            {
                using var statusFont = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold);
                var statusText = " " + _buildProgressText;
                var statusWidth = TextRenderer.MeasureText(statusText, statusFont).Width;
                TextRenderer.DrawText(g, statusText, statusFont,
                    new Rectangle(Width - _btnWidth * 3 - statusWidth - 8, 0, statusWidth, Height),
                    terminalColor,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
            }
        }
        else
        {
            // 非构建状态：停止脉冲定时器
            StopMarqueeTimer();
        }
    }

    /// <summary>启动脉冲定时器（仅在 npm 模式下使用）。</summary>
    private void StartMarqueeTimer()
    {
        if (_marqueeTimer is not null) return; // 已经在运行
        _marqueeTimer = new System.Windows.Forms.Timer { Interval = 33 }; // ~30fps
        _marqueeTimer.Tick += (_, _) => Invalidate();
        _marqueeTimer.Start();
    }

    /// <summary>停止脉冲定时器。</summary>
    private void StopMarqueeTimer()
    {
        if (_marqueeTimer is null) return;
        _marqueeTimer.Stop();
        _marqueeTimer.Dispose();
        _marqueeTimer = null;
    }
}
