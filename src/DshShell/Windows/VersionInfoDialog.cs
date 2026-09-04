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

    private readonly Label _dshLatestLabel = new();
    private readonly Label _launcherLatestLabel = new();
    private readonly Label _dshStatusLabel = new();
    private readonly Label _launcherStatusLabel = new();

    /// <summary>版本信息列 X 坐标（2026-09 反馈：列距太挤 → 窗口加宽到 520，列间留 8-12px）。
    /// 名称/当前/最新/状态。</summary>
    private const int ColNameX = 16;
    private const int ColCurrentX = 132;
    private const int ColLatestX = 258;
    private const int ColStatusX = 400;
    private const int RowW = 488;   // 520 - 左右 16px 边距

    public VersionInfoDialog(string? dshCurrent, string? launcherCurrent, bool dark)
    {
        _dshCurrent = dshCurrent;
        _launcherCurrent = launcherCurrent;
        _dark = dark;

        // ---- 窗口骨架：dsh 风格（无边框 + 自绘标题栏，跟随壳主题） ----
        var bg = _dark ? DarkBg : LightBg;
        var textColor = _dark ? DarkText : LightText;
        Text = "版本信息";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = bg;
        ClientSize = new Size(520, 188);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
        Font = new Font("Microsoft YaHei UI", 9F);

        var titleH = (int)Math.Round(32 * DeviceDpi / 96f);
        var titleBar = new CustomTitleBar(this, dark, closeOnly: true)
        {
            Bounds = new Rectangle(1, 1, ClientSize.Width - 2, titleH),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        Controls.Add(titleBar);

        // ---- 两行版本（紧凑：每产品一行，无表头） ----
        //   dsh 组件    当前 v0.1.2-rc.1  最新 v0.1.1-rc.2  已是最新
        //   dsh-launcher 当前 v0.4.3       最新 v0.4.3       已是最新
        var row1Y = titleH + 10;
        AddVersionRow(row1Y, "dsh 组件", dshCurrent, _dshLatestLabel, _dshStatusLabel);
        var row2Y = row1Y + 20;
        AddVersionRow(row2Y, "dsh-launcher", launcherCurrent, _launcherLatestLabel, _launcherStatusLabel);

        // ---- 细分隔线（与标题栏底部分隔线同色系，dsh 界面语言） ----
        var separator = new Panel
        {
            BackColor = _dark ? DarkBorder : LightBorder,
            Bounds = new Rectangle(16, row2Y + 26, RowW, 1),
        };
        Controls.Add(separator);

        // ---- 启动器下载地址（LinkLabel：显示 + 点击打开；链接色 = DeepSeek 蓝） ----
        var linkColor = _dark ? AccentBlueDark : AccentBlue;
        var downloadTitle = new Label
        {
            Text = "启动器下载地址",
            ForeColor = textColor,
            BackColor = bg,
            AutoSize = true,
            Location = new Point(16, separator.Top + 10),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        Controls.Add(downloadTitle);

        var downloadLink = new LinkLabel
        {
            Text = UpdateChecker.LauncherLatestReleaseUrl,
            LinkColor = linkColor,
            ActiveLinkColor = linkColor,
            ForeColor = textColor,
            BackColor = bg,
            Location = new Point(16, downloadTitle.Bottom + 2),
            Size = new Size(RowW, 20),
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        downloadLink.LinkClicked += (_, _) => WebRuntimeInstaller.OpenExternally(UpdateChecker.LauncherLatestReleaseUrl);
        Controls.Add(downloadLink);

        // ---- 唯一动作按钮：打开下载页（DeepSeek 蓝主按钮，关闭走标题栏 X / ESC） ----
        // 2026-09 反馈：按钮贴近右/下边框 → 右侧留 20px、底部留 14px，与 URL 行保持间隙
        var openButton = new Button
        {
            Text = "打开下载页",
            FlatStyle = FlatStyle.Flat,
            BackColor = linkColor,
            ForeColor = Color.White,
            Location = new Point(ClientSize.Width - 100, ClientSize.Height - 40),
            Size = new Size(80, 26),
        };
        openButton.FlatAppearance.BorderSize = 0;
        openButton.Click += (_, _) => WebRuntimeInstaller.OpenExternally(UpdateChecker.LauncherLatestReleaseUrl);
        Controls.Add(openButton);
    }

    /// <summary>一行版本信息：产品名 | 当前 vX | 最新 vX（检查中…）| 状态（检查中…）。</summary>
    private void AddVersionRow(int y, string product, string? current,
        Label latestLabel, Label statusLabel)
    {
        var bg = _dark ? DarkBg : LightBg;
        var textColor = _dark ? DarkText : LightText;
        var nameLabel = TextCell(product, ColNameX, y);
        var currentLabel = TextCell("当前 " + ShellLogic.VersionInfoPolicy.FormatCurrent(current), ColCurrentX, y);
        nameLabel.Width = 110;
        currentLabel.Width = 118; // 132..250，与最新列留 8px
        latestLabel.Text = "最新 检查中…";
        statusLabel.Text = "检查中…";
        latestLabel.AutoSize = false;
        statusLabel.AutoSize = false;
        latestLabel.TextAlign = ContentAlignment.MiddleLeft;
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        latestLabel.Location = new Point(ColLatestX, y);
        latestLabel.Size = new Size(130, 18); // 258..388，与状态列留 12px
        statusLabel.Location = new Point(ColStatusX, y);
        statusLabel.Size = new Size(RowW - (ColStatusX - ColNameX), 18); // 400..504（右缘留 16px）
        latestLabel.ForeColor = textColor;
        statusLabel.ForeColor = textColor;
        latestLabel.BackColor = bg;
        statusLabel.BackColor = bg;
        latestLabel.AutoEllipsis = true;
        statusLabel.AutoEllipsis = true;
        Controls.Add(nameLabel);
        Controls.Add(currentLabel);
        Controls.Add(latestLabel);
        Controls.Add(statusLabel);
    }

    /// <summary>单元格文本（主题色文字/背景；AutoSize 由调用方按列宽约束）。</summary>
    private Label TextCell(string text, int x, int y) => new()
    {
        Text = text,
        AutoSize = true,
        Location = new Point(x, y),
        ForeColor = _dark ? DarkText : LightText,
        BackColor = _dark ? DarkBg : LightBg,
        TextAlign = ContentAlignment.MiddleLeft,
    };

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