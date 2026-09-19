using System.Drawing;
using DshWeb.Chrome;
using DshWeb.Managers;

namespace DshWeb.Windows;

/// <summary>
/// 版本信息窗（2026-09：标题栏 dsh 版本徽标点击弹出）。
/// 两次迭代后的最终形态——**dsh 风格**：
/// - 无边框 + 复用 <see cref="CustomTitleBar"/> 自绘标题栏（鲸鱼图标 + 仅关闭按钮）；
/// - 深/浅主题跟随壳（#202020 / #F0F0F0），与主窗口同一套视觉语言；
/// - 内容用**绝对定位 Label**（与 SplashForm 同款成熟模式，不用 TableLayoutPanel——
///   2026-09 反馈：TLP 单元格文字未渲染、且网格占用过多高度）；每产品一行
///   "名称 | 当前 vX | 最新 vX | 状态"，紧凑不设表头。
/// - 展示：
///   · dsh 组件：当前版本（统一发现层）+ 最新版本（npm registry 回退链）+ 更新状态；
///   · dsh-launcher：当前版本（程序集信息，开发构建回退 git tag）+ 最新版本（GitHub Releases）+ 状态；
///   · 启动器下载地址（LinkLabel，点击用系统默认浏览器打开）。
/// 最新版本在 OnShown 异步拉取，失败降级为"获取失败"（网络/限流不打扰）；本窗体绝不抛出。
///
/// 【DPI 纪律（本轮收口）】全窗几何来自纯函数 <c>ShellLogic.VersionDialogLayout.Compute(dpi)</c>，
/// 渲染侧零算术；字号用 <c>GraphicsUnit.Pixel</c>（物理像素由纯函数算好）——本窗曾是全仓唯一
/// "硬编码 96dpi 像素列位 + Point 字体 + 无 OnDpiChanged"的窗口，缩放屏上文字变宽而列位不动
/// 就会叠列。跨显示器/改倍率经 <see cref="OnDpiChanged"/> 整体重排（列不互叠、URL 不与按钮
/// 相交、内容不出客户端由 VersionDialogLayoutContractTests 钉死）。
/// </summary>
internal sealed class VersionInfoDialog : Form
{
    // 与 CustomTitleBar 同源的 dsh 主题色板（自绘标题栏/正文共用视觉）
    private static readonly Color DarkBg = Color.FromArgb(32, 32, 32);
    private static readonly Color LightBg = Color.FromArgb(240, 240, 240);
    private static readonly Color DarkText = Color.White;
    private static readonly Color LightText = Color.FromArgb(30, 30, 30);
    private static readonly Color DarkBorder = Color.FromArgb(48, 48, 48);
    private static readonly Color LightBorder = Color.FromArgb(225, 225, 225);
    private static readonly Color AccentBlue = Color.FromArgb(77, 107, 254);    // DeepSeek 蓝 #4D6BFE
    private static readonly Color AccentBlueDark = Color.FromArgb(124, 144, 255);

    private readonly string? _dshCurrent;
    private readonly string? _launcherCurrent;
    private readonly bool _dark;

    private readonly CustomTitleBar _titleBar;
    private readonly Label _dshNameLabel = new();
    private readonly Label _dshCurrentLabel = new();
    private readonly Label _dshLatestLabel = new();
    private readonly Label _dshStatusLabel = new();
    private readonly Label _launcherNameLabel = new();
    private readonly Label _launcherCurrentLabel = new();
    private readonly Label _launcherLatestLabel = new();
    private readonly Label _launcherStatusLabel = new();
    private readonly Panel _separator = new();
    private readonly Label _downloadTitleLabel = new();
    private readonly LinkLabel _downloadLink = new();
    private readonly Button _openButton = new();

    public VersionInfoDialog(string? dshCurrent, string? launcherCurrent, bool dark)
    {
        _dshCurrent = dshCurrent;
        _launcherCurrent = launcherCurrent;
        _dark = dark;

        // [2026-09 崩溃根治 issue #28-2] 本窗体是 0xc0000005（ImmSetOpenStatus）崩溃的现场：
        // 窗口激活时 WinForms 把焦点给到首个可聚焦控件（LinkLabel → Label.DefaultImeMode=Disable），
        // UpdateImeContextMode 随即调用 ImeContext.Disable → ImmSetOpenStatus → 第三方 IME 上访问违规，
        // 进程瞬间消失（用户可见"点版本号卡死→闪退"）。护栏把本窗体及全部子控件的 IME 上下文解绑，
        // 使 WinForms 侧 ImeContext.GetImeMode 恒为 Disable、IsOpen 恒 false → 该调用永不发生。
        DshWeb.Win32.ImeContextGuard.Harden(this);

        var bg = _dark ? DarkBg : LightBg;
        Text = "版本信息";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = bg;
        // 手工布局 + OnDpiChanged 整体重排：禁掉 WinForms 自动缩放，否则它乘一次、
        // 纯函数再乘一次（issue #28-3 的 s² 根因就是这个）。
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);

        _titleBar = new CustomTitleBar(this, dark, closeOnly: true)
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(_titleBar);

        // 两行版本（紧凑：每产品一行，无表头）
        SetupCell(_dshNameLabel); SetupCell(_dshCurrentLabel);
        SetupCell(_dshLatestLabel); SetupCell(_dshStatusLabel);
        SetupCell(_launcherNameLabel); SetupCell(_launcherCurrentLabel);
        SetupCell(_launcherLatestLabel); SetupCell(_launcherStatusLabel);
        _dshNameLabel.Text = "dsh 组件";
        _launcherNameLabel.Text = "dsh-launcher";
        _dshCurrentLabel.Text = "当前 " + ShellLogic.VersionInfoPolicy.FormatCurrent(_dshCurrent);
        _launcherCurrentLabel.Text = "当前 " + ShellLogic.VersionInfoPolicy.FormatCurrent(_launcherCurrent);
        _dshLatestLabel.Text = "最新 检查中…";
        _launcherLatestLabel.Text = "最新 检查中…";
        _dshStatusLabel.Text = "检查中…";
        _launcherStatusLabel.Text = "检查中…";
        foreach (var c in new Control[]
        {
            _dshNameLabel, _dshCurrentLabel, _dshLatestLabel, _dshStatusLabel,
            _launcherNameLabel, _launcherCurrentLabel, _launcherLatestLabel, _launcherStatusLabel,
        }) Controls.Add(c);

        _separator.BackColor = _dark ? DarkBorder : LightBorder;
        Controls.Add(_separator);

        _downloadTitleLabel.Text = "启动器下载地址";
        Controls.Add(_downloadTitleLabel);

        var linkColor = _dark ? AccentBlueDark : AccentBlue;
        _downloadLink.Text = UpdateChecker.LauncherLatestReleaseUrl;
        _downloadLink.LinkColor = linkColor;
        _downloadLink.ActiveLinkColor = linkColor;
        _downloadLink.AutoSize = false;
        _downloadLink.AutoEllipsis = true;
        _downloadLink.TextAlign = ContentAlignment.MiddleLeft;
        _downloadLink.LinkClicked += (_, _) => WebRuntimeInstaller.OpenExternally(UpdateChecker.LauncherLatestReleaseUrl);
        Controls.Add(_downloadLink);

        // 唯一动作按钮：打开下载页（DeepSeek 蓝主按钮，关闭走标题栏 X / ESC）
        _openButton.Text = "打开下载页";
        _openButton.FlatStyle = FlatStyle.Flat;
        _openButton.BackColor = linkColor;
        _openButton.ForeColor = Color.White;
        _openButton.FlatAppearance.BorderSize = 0;
        _openButton.Click += (_, _) => WebRuntimeInstaller.OpenExternally(UpdateChecker.LauncherLatestReleaseUrl);
        Controls.Add(_openButton);

        ApplyLayout(DeviceDpi);
    }

    /// <summary>版本表单元格：固定盒 + 左对齐 + 溢出省略（叠列的根源就是"文字变宽而列不动"）。</summary>
    private void SetupCell(Label label)
    {
        var bg = _dark ? DarkBg : LightBg;
        label.AutoSize = false;
        label.ForeColor = _dark ? DarkText : LightText;
        label.BackColor = bg;
        label.TextAlign = ContentAlignment.MiddleLeft;
        label.AutoEllipsis = true;
    }

    /// <summary>
    /// 一次性把纯函数几何落到每个控件上（构造与 DPI 变化共用同一段代码——
    /// 分叉过一次就修一处漏一处，这里刻意不给它两份输入）。
    /// </summary>
    private void ApplyLayout(int deviceDpi)
    {
        var g = ShellLogic.VersionDialogLayout.Compute(deviceDpi);
        ClientSize = new Size(g.ClientWidth, g.ClientHeight);
        // 像素单位字号：物理像素已由纯函数算好，DC 不再折算（唯一一次乘法在 Compute 里）。
        // 【绝不 Dispose 旧 Font】WinForms 子控件缓存"继承来的"那个 Font 引用（Label 自己不
        // 拥有字体）；换掉后立刻释放旧的，下一次任何控件量高度就会 GDI+ "Parameter is not valid"
        // ——实测直接把测试宿主进程打崩（连 ThreadExceptionDialog 都建不起来）。DPI 变化是
        // 低频事件，一次一个 GDI 字体对象的滞留可以接受，错着省不得。
        Font = new Font("Microsoft YaHei UI", g.EmPx, FontStyle.Regular, GraphicsUnit.Pixel);

        _titleBar.Bounds = new Rectangle(1, 1, g.ClientWidth - 2, g.TitleHeight);
        _titleBar.Rescale(deviceDpi / 96f);

        PlaceRow(g, _dshNameLabel, _dshCurrentLabel, _dshLatestLabel, _dshStatusLabel, g.Row1Y);
        PlaceRow(g, _launcherNameLabel, _launcherCurrentLabel, _launcherLatestLabel,
            _launcherStatusLabel, g.Row2Y);

        _separator.Bounds = new Rectangle(g.Padding, g.SeparatorY, g.RowWidth, 1);
        _downloadTitleLabel.Bounds = new Rectangle(g.ColNameX, g.LinkTitleY, g.RowWidth, g.RowHeight);
        _downloadLink.Bounds = new Rectangle(g.ColNameX, g.LinkY, g.LinkWidth, g.LinkHeight);
        _openButton.Bounds = new Rectangle(g.ButtonX, g.ButtonY, g.ButtonWidth, g.ButtonHeight);
        Invalidate();
    }

    private void PlaceRow(ShellLogic.VersionDialogLayout.Geometry g,
        Label name, Label current, Label latest, Label status, int y)
    {
        name.Bounds = new Rectangle(g.ColNameX, y, g.NameW, g.RowHeight);
        current.Bounds = new Rectangle(g.ColCurrentX, y, g.CurrentW, g.RowHeight);
        latest.Bounds = new Rectangle(g.ColLatestX, y, g.LatestW, g.RowHeight);
        // 状态列吃到右边界（纯函数已保证 ColStatusX + 该宽 = ClientWidth - Padding）
        status.Bounds = new Rectangle(g.ColStatusX, y,
            Math.Max(1, g.RowWidth - (g.ColStatusX - g.ColNameX)), g.RowHeight);
    }

    /// <summary>跨显示器 / 改倍率：整窗按新 DPI 重排（WinForms 不会替手工布局做这件事）。</summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (e.DeviceDpiOld == e.DeviceDpiNew) return;
        ApplyLayout(e.DeviceDpiNew);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // fire-and-forget：方法内部全 await、异常全捕获，不逃逸到消息泵
        _ = FetchLatestVersionsAsync();
    }

    /// <summary>
    /// 异步拉取两路最新版本（并发；UpdateChecker 回退链自带多 registry × 多网络出口，
    /// 单路失败静默返回 null——不打扰用户）。完成后回 UI 线程更新展示；
    /// 获取失败时最新列与状态列停留在"获取失败"/"无法获取最新版本"（伴随 Warn 留痕）。
    /// </summary>
    private async Task FetchLatestVersionsAsync()
    {
        try
        {
            var dshTask = UpdateChecker.FetchLatestDshVersionFallbackAsync();
            var launcherTask = UpdateChecker.FetchLatestLauncherReleaseFallbackAsync();
            await Task.WhenAll(dshTask, launcherTask);
            if (IsDisposed) return;
            var dshLatest = await dshTask;
            var launcherLatest = (await launcherTask)?.Version;
            if (IsDisposed) return;
            _dshLatestLabel.Text = "最新 " + ShellLogic.VersionInfoPolicy.FormatLatest(dshLatest);
            _launcherLatestLabel.Text = "最新 " + ShellLogic.VersionInfoPolicy.FormatLatest(launcherLatest);
            _dshStatusLabel.Text = ShellLogic.VersionInfoPolicy.FormatRelation(
                ShellLogic.VersionInfoPolicy.CompareCurrentToLatest(_dshCurrent, dshLatest), dshLatest);
            _launcherStatusLabel.Text = ShellLogic.VersionInfoPolicy.FormatRelation(
                ShellLogic.VersionInfoPolicy.CompareCurrentToLatest(_launcherCurrent, launcherLatest), launcherLatest);
            Trace($"version dialog: dsh latest={(dshLatest ?? "<null>")} launcher latest={(launcherLatest ?? "<null>")}");
        }
        catch (Exception ex)
        {
            // 拉取异常（理论上回退链已自吞）：留痕，展示保持"检查中…/获取失败"，绝不弹错
            if (!IsDisposed)
                Logger.Warn("version info dialog fetch failed", ctx: new { error = ex.Message });
        }
    }

    /// <summary>ESC 关闭（无边框自绘窗没有系统关闭按钮，键盘可达性补位）。</summary>
    protected override bool ProcessDialogKey(Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessDialogKey(keyData);
    }

    /// <summary>诊断留痕（复用 Program.Trace 语义，便于"版本弹窗显示异常"排查）。</summary>
    private void Trace(string msg)
    {
        try { DshWeb.Program.Trace(msg); } catch { /* 留痕失败不影响弹窗 */ }
    }
}
