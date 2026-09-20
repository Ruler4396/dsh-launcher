using DshWeb;
using DshWeb.Windows;
using DshWeb.Win32; // T8：P/Invoke 与 Win32 常量的唯一归属层

namespace DshWeb.Chrome;

/// <summary>
/// 自绘标题栏（无边框窗口用）：背景/文字/按钮颜色完全自绘，主题切换即时生效，
/// 不依赖 DWM 标题栏重绘（实测本机 DWM 属性切换后标题栏画面不刷新，只有焦点变化才重绘）。
/// 提供：标题 + 主题鲸鱼图标 + 最小化/最大化/关闭按钮 + 拖拽移动 + 双击最大化 + 右键系统菜单。
/// 由组合根/DshShellForm 宿主经 WindowManager 装配使用；共享图标/主题资源与
/// user32 P/Invoke 与 Win32 常量一律取自 DshWeb.Win32（T8：不再回调组合根静态）。
/// </summary>
internal sealed class CustomTitleBar : Panel
{
    private readonly Form _owner;
    /// <summary>仅保留关闭按钮（dsh 风格版本信息窗等模态小窗用：无最小化/最大化）。</summary>
    private readonly bool _closeOnly;
    private float _scale;
    private int _btnWidth;
    private bool _dark;
    private bool _hoverMin, _hoverMax, _hoverClose;

    /// <summary>标题栏左键按下的记账点（null = 无候选）。只有指针越过拖拽阈值才交给系统
    /// HTCAPTION 拖拽循环——见 OnMouseDown/OnMouseMove 的 T12 修复注释。</summary>
    private Point? _captionDownAt;

    // ---- 2026-09：dsh 版本徽标（"DeepSeek Harness v0.1.0-rc.7"，点击弹版本信息窗） ----
    /// <summary>dsh 当前版本（原始版本号，如 "0.1.0-rc.7"；空 = 不渲染徽标）。Program 启动时写入。
    /// 徽标展示文本由 ShellLogic.VersionInfoPolicy 纯函数合成（UI 层零拼字符串）。</summary>
    internal volatile string _dshVersion = "";
    /// <summary>版本徽标点击回调（Program 注入：弹出版本信息窗）；null=不响应点击。</summary>
    internal Action? VersionClick;
    /// <summary>版本徽标命中矩形（OnPaint 计算，点击/悬停命中测试用）。</summary>
    private Rectangle _versionRect = Rectangle.Empty;
    /// <summary>版本徽标是否悬停（手型光标 + 下划线）。</summary>
    private bool _hoverVersion;

    // ---- 2026-09-20：安全模式标记可点（用户 × 关掉通知卡之后唯一的退出入口） ----
    /// <summary>点击"（安全模式）"标记的回调（Program 注入：重新呈现带退出动作的卡片）。</summary>
    internal Action? SafeModeMarkerClick;
    /// <summary>安全模式标记的命中矩形（OnPaint 计算；空 = 当前没有可点的标记）。</summary>
    private Rectangle _safeModeRect = Rectangle.Empty;
    private bool _hoverSafeMode;
    /// <summary>标记用色与通知卡 Urgent 强调条同色（#D81E06）：两处"你正在降级运行"的线索必须看起来是一回事。</summary>
    private static readonly Color SafeModeMarkerColor = Color.FromArgb(216, 30, 6);

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

    /// <summary>标题字号：**实例级**像素字体（几何来自 WindowGeometry.TitleEmPx(dpi)）。
    /// 旧实现是 <c>static readonly Font(..., 9F)</c>——全进程共享一份 Point 字体，
    /// <c>Rescale</c> 改不了它，混屏（主窗 200% / 弹窗 100%）下两块标题栏只能共用同一档字号；
    /// 且 Point 单位在自带 DPI 的绘制 DC 上会被再折算一次（#28-3 的 s² 教训）。</summary>
    private Font _titleFont = TitleFontAt(96);

    /// <summary>按 DPI 建字体：字号一律**像素单位**（折算只在 WindowGeometry.EmPx 发生一次），
    /// 家族缺失（精简系统/容器）时回退系统无衬线。</summary>
    private static Font FontAt(string family, double designPoint, FontStyle style, int deviceDpi)
    {
        var em = DshWeb.Win32.WindowGeometry.EmPx(designPoint, deviceDpi);
        try
        {
            return new Font(family, em, style, GraphicsUnit.Pixel);
        }
        catch (ArgumentException)
        {
            return new Font(FontFamily.GenericSansSerif, em, style, GraphicsUnit.Pixel);
        }
    }

    private const string UiFontFamily = "Microsoft YaHei UI";

    private static Font TitleFontAt(int deviceDpi)
        => FontAt(UiFontFamily, 9.0, FontStyle.Regular, deviceDpi);

    /// <summary>构建进度文案字号（设计 8pt）：随 _scale 走，不再写死 Point。</summary>
    private Font StatusFont(bool bold) => FontAt(UiFontFamily, 8.0,
        bold ? FontStyle.Bold : FontStyle.Regular, (int)Math.Round(_scale * 96f));
    private static readonly Color DarkBg = Color.FromArgb(32, 32, 32);
    private static readonly Color LightBg = Color.FromArgb(240, 240, 240);
    private static readonly Color DarkText = Color.White;
    private static readonly Color LightText = Color.FromArgb(30, 30, 30);
    private static readonly Color DarkHover = Color.FromArgb(58, 58, 58);
    private static readonly Color LightHover = Color.FromArgb(229, 229, 229);
    private static readonly Color CloseHover = Color.FromArgb(232, 17, 35);

    public CustomTitleBar(Form owner, bool dark, bool closeOnly = false)
    {
        _owner = owner;
        _dark = dark;
        _closeOnly = closeOnly;
        // [2026-08 回归修复] 双缓冲：构建进度高频 Invalidate 时旧实现每帧先擦背景再绘制
        // （WM_ERASEBKGND + OnPaint 两段），产生可见闪烁。三样式联用把绘制合并到内存位图。
        SetStyle(ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer, true);
        // DPI 缩放：150% 缩放下 32px 物理高度会显得又矮又挤（按钮/图标/间距全按逻辑缩放）
        _scale = DshWeb.ShellLogic.DpiScale.Of(owner.DeviceDpi);
        _btnWidth = (int)Math.Round(46 * _scale);
        _titleFont.Dispose();
        _titleFont = TitleFontAt(owner.DeviceDpi);
        BackColor = _dark ? DarkBg : LightBg;
        MouseDown += OnMouseDown;
        MouseUp += OnMouseUp;
        MouseDoubleClick += OnDoubleClick;
        MouseMove += OnMouseMove;
        MouseLeave += (_, _) =>
        {
            if (_hoverMin || _hoverMax || _hoverClose || _hoverVersion)
            {
                _hoverMin = _hoverMax = _hoverClose = _hoverVersion = false;
                Cursor = Cursors.Default;
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

    /// <summary>DPI 变化时重算缩放比例、按钮宽度与字号（字号必须跟着换，否则跨屏后
    /// 标题按旧档 DPI 渲染——混屏下"窗口变大了字没变大"就是这个）。</summary>
    public void Rescale(float scale)
    {
        _scale = scale;
        _btnWidth = (int)Math.Round(46 * _scale);
        var next = TitleFontAt((int)Math.Round(scale * 96f));
        var old = _titleFont;          // Rescale 与 OnPaint 同在 UI 线程，无需原子交换
        _titleFont = next;
        old.Dispose();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _titleFont.Dispose();
        base.Dispose(disposing);
    }

    /// <summary>窗口按钮个数：closeOnly 仅关闭按钮，否则最小化/最大化/关闭三键。</summary>
    private int BtnCount => _closeOnly ? 1 : 3;

    private Rectangle BtnRect(int i) => new(Width - _btnWidth * (BtnCount - i), 0, _btnWidth, Height);

    private int HitButton(int x)
    {
        for (var i = 0; i < BtnCount; i++)
            if (BtnRect(i).Contains(x, Height / 2)) return i;
        return -1;
    }

    /// <summary>分段绘制标题：安全模式标记着红并记下命中矩形。整条画不下时退回省略号绘制且
    /// **不给命中框**——宁可"此刻不可点"，也不要让用户点到看不见的东西。</summary>
    private void DrawTitleSegments(Graphics g,
        IReadOnlyList<ShellLogic.TitleBarText.Segment> segments, int titleLeft,
        Rectangle titleRect, Color textColor)
    {
        _safeModeRect = Rectangle.Empty;
        var x = titleLeft;
        var total = 0;
        foreach (var s in segments) total += TextRenderer.MeasureText(g, s.Text, _titleFont).Width;
        if (total > titleRect.Width)
        {
            TextRenderer.DrawText(g, _titleText, _titleFont, titleRect, textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            return;
        }
        foreach (var s in segments)
        {
            var w = TextRenderer.MeasureText(g, s.Text, _titleFont).Width;
            var r = new Rectangle(x, 0, w, Height);
            TextRenderer.DrawText(g, s.Text, _titleFont, r,
                s.SafeModeMarker ? SafeModeMarkerColor : textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
            if (s.SafeModeMarker)
                _safeModeRect = _safeModeRect.IsEmpty ? r : Rectangle.Union(_safeModeRect, r);
            x += w;
        }
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
        // [2026-09 版本徽标] 点击徽标：不触发窗口拖拽，转交 VersionClick（版本信息窗）
        if (VersionClick is not null && _dshVersion.Length > 0 && _versionRect.Contains(e.Location))
        {
            try { VersionClick(); }
            catch (Exception ex) { Logger.Info($"version badge click handler failed: {ex.Message}"); }
            return;
        }
        // [2026-09-20] 点击"（安全模式）"标记：重新呈现带退出动作的通知卡（用户 × 关掉卡片之后
        // 只剩这个入口；没有它就得手动重启一次才能再拿到退出通道）。同样不进拖拽循环。
        if (!_safeModeRect.IsEmpty && _safeModeRect.Contains(e.Location))
        {
            try { SafeModeMarkerClick?.Invoke(); }
            catch (Exception ex) { Logger.Info($"safe-mode marker click handler failed: {ex.Message}"); }
            return;
        }
        // [真机 T12 修复] 这里**不再**立刻进系统拖拽循环：旧实现无条件
        // ReleaseCapture + SendMessage(WM_NCLBUTTONDOWN, HTCAPTION)，一进模态循环就把第二次
        // 点击吞掉，WM_LBUTTONDBLCLK 永不产生 → OnDoubleClick 是死代码，用户双击标题栏
        // 无法最大化（单屏 96 DPI 同样复现）。现改为"按下先记账，指针越过拖拽阈值才接管"，
        // 判定在 WindowGeometry.ShouldStartCaptionDrag（有契约测试）。
        _captionDownAt = e.Location;
    }

    private void OnMouseUp(object? s, MouseEventArgs e)
    {
        _captionDownAt = null; // 一次按下只对应一次拖拽候选
        if (e.Button != MouseButtons.Left) return;
        switch (HitButton(e.X))
        {
            case 0 when _closeOnly: _owner.Close(); break; // 模态小窗：仅关闭
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
        // [真机 T12 修复] 按下后指针越过拖拽阈值 → 此刻才交给系统 HTCAPTION 拖拽循环。
        // 阈值内一律不接管，双击因此能走到 OnDoubleClick；真拖动必然越阈值，行为不变。
        if (_captionDownAt is { } down && e.Button == MouseButtons.Left
            && DshWeb.Win32.WindowGeometry.ShouldStartCaptionDrag(
                down, e.Location, SystemInformation.DragSize))
        {
            _captionDownAt = null; // 系统接管后本控件不再参与这次按下
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(_owner.Handle, (uint)Win32Constants.WM_NCLBUTTONDOWN, (IntPtr)Win32Constants.HTCAPTION, IntPtr.Zero);
            return;
        }
        var btn = HitButton(e.X);
        var h1 = btn == 0;
        var h2 = btn == 1;
        var h3 = btn == 2;
        // [2026-09 版本徽标] 悬停 → 手型光标（与按钮悬停同通道去重 Invalidate）
        var overVersion = _dshVersion.Length > 0 && _versionRect.Contains(e.Location);
        // 安全模式标记同样给手型光标：红色是"这里不一样"，手型才是"这里能点"
        var overSafeMode = !_safeModeRect.IsEmpty && _safeModeRect.Contains(e.Location);
        if (overVersion != _hoverVersion || overSafeMode != _hoverSafeMode
            || h1 != _hoverMin || h2 != _hoverMax || h3 != _hoverClose)
        {
            _hoverVersion = overVersion;
            _hoverSafeMode = overSafeMode;
            _hoverMin = h1;
            _hoverMax = h2;
            _hoverClose = h3;
            Cursor = overVersion || overSafeMode ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
    }

    /// <summary>版本徽标命中矩形（OnPaint 计算，客户区坐标；空 = 无徽标）。
    /// 供点击命中测试与 E2E TestHook（真实鼠标点击坐标）读取。</summary>
    internal Rectangle GetVersionBadgeRect() => _versionRect;

    /// <summary>安全模式标记命中矩形（空 = 当前标题里没有可点的标记，或被省略号裁掉不给命中框）。</summary>
    internal Rectangle GetSafeModeMarkerRect() => _safeModeRect;

    private void ShowSystemMenu(Point p)
    {
        try
        {
            var hMenu = NativeMethods.GetSystemMenu(_owner.Handle, false);
            if (hMenu == IntPtr.Zero) return;
            NativeMethods.TrackPopupMenu(hMenu, Win32Constants.TPM_RETURNCMD | Win32Constants.TPM_RIGHTBUTTON,
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
        var icon = _dark ? Windows.WindowIcons.LightWhaleIcon : Windows.WindowIcons.DarkWhaleIcon;
        var iconSize = (int)Math.Round(16 * _scale);
        if (icon is not null)
        {
            g.DrawIcon(icon, new Rectangle((int)Math.Round(10 * _scale), (Height - iconSize) / 2, iconSize, iconSize));
        }

        // 标题 + dsh 版本徽标（2026-09：版本号紧跟标题右侧，正文样式可点击弹版本信息窗）
        var titleLeft = (int)Math.Round(34 * _scale);
        var rightBound = Width - _btnWidth * BtnCount - (int)Math.Round(8 * _scale); // 按钮区左缘（预留 8 设计像素）
        var badgeText = ShellLogic.VersionInfoPolicy.ComposeTitleBarBadge(_dshVersion);
        // 标题按段绘制：主体与"（有更新）"用常规色，"（…安全模式…）"用红并且可点。
        // 测量一律带 g：无 Graphics 的重载按"任意一个 DC"采样 DPI，与下面用 g 绘制的实际宽度
        // 可能不同一档 → 命中框与墨迹错位（混屏移动后尤其明显，版本徽标当年就是这条）。
        var segments = ShellLogic.TitleBarText.Segments(_titleText);
        var titleWidth = 0;
        foreach (var seg in segments) titleWidth += TextRenderer.MeasureText(g, seg.Text, _titleFont).Width;
        Rectangle titleRect;
        if (badgeText.Length > 0)
        {
            // [2026-09 反馈] 徽标与标题同字重同色（正文样式，不加粗不变蓝），悬停仅下划线提示可点；
            // 与标题间隙收窄到 4px，读作 "DeepSeek Harness v0.1.2-rc.1" 的自然文本流。
            using var badgeFont = new Font(_titleFont.FontFamily, _titleFont.Size,
                _hoverVersion ? FontStyle.Underline : FontStyle.Regular, GraphicsUnit.Pixel);
            var badgeWidth = TextRenderer.MeasureText(g, badgeText, badgeFont).Width;
            var gap = (int)Math.Round(4 * _scale);
            // 徽标紧跟标题实测宽度之后；空间不足（窗口过窄/标题过长）时右对齐按钮区，
            // 标题被 EndEllipsis 收窄——徽标位置稳定、始终可点。
            var badgeX = titleLeft + titleWidth + gap;
            if (badgeX + badgeWidth > rightBound) badgeX = Math.Max(titleLeft, rightBound - badgeWidth);
            _versionRect = new Rectangle(badgeX, 0, badgeWidth, Height);
            titleRect = new Rectangle(titleLeft, 0, Math.Max(0, badgeX - titleLeft - gap), Height);
            TextRenderer.DrawText(g, badgeText, badgeFont, _versionRect, textColor,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.NoPadding);
        }
        else
        {
            _versionRect = Rectangle.Empty;
            titleRect = new Rectangle(titleLeft, 0, Math.Max(0, rightBound - titleLeft), Height);
        }
        DrawTitleSegments(g, segments, titleLeft, titleRect, textColor);

        // 窗口按钮：用 Segoe MDL2 字形（最小化/最大化/还原/关闭），清晰且与系统图标一致
        using (var btnFont = new Font("Segoe MDL2 Assets", (float)Math.Round(11 * _scale), FontStyle.Regular, GraphicsUnit.Pixel))
        {
            for (var i = 0; i < BtnCount; i++)
            {
                var r = BtnRect(i);
                var isClose = _closeOnly || i == 2;
                var hover = isClose ? _hoverClose : (i == 0 ? _hoverMin : _hoverMax);
                if (hover)
                {
                    using var hb = new SolidBrush(isClose ? CloseHover : (_dark ? DarkHover : LightHover));
                    g.FillRectangle(hb, r);
                }
                var glyph = _closeOnly ? '\uE8BB' : i switch
                {
                    0 => '\uE921', // Minimize
                    1 => _owner.WindowState == FormWindowState.Maximized ? '\uE923' : '\uE922', // Restore / Maximize
                    _ => '\uE8BB', // ChromeClose
                };
                var glyphColor = hover && isClose && _dark ? Color.White : textColor;
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
                // using：这段在启动脉冲定时器下每帧都跑（~30fps），Font 不释放就是
                // 每秒 ~30 个 GDI 句柄的泄漏（下方 Ready/Failed 分支已经是 using 写法）。
                using var statusFont = StatusFont(bold: false);
                var statusText = " " + _buildProgressText;
                var statusWidth = TextRenderer.MeasureText(g, statusText, statusFont).Width;
                TextRenderer.DrawText(g, statusText, statusFont,
                    new Rectangle(Width - _btnWidth * BtnCount - statusWidth - 8, 0, statusWidth, Height),
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
                using var statusFont = StatusFont(bold: true);
                var statusText = " " + _buildProgressText;
                var statusWidth = TextRenderer.MeasureText(g, statusText, statusFont).Width;
                TextRenderer.DrawText(g, statusText, statusFont,
                    new Rectangle(Width - _btnWidth * BtnCount - statusWidth - 8, 0, statusWidth, Height),
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
