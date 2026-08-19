using System.Drawing;
using DshWeb;

namespace DshWeb.Windows;

/// <summary>
/// 快速启动状态窗（Splash）——启动体验两大问题的根治点：
/// ① 启动延迟：Main 只保留最小初始化后立即 Application.Run(SplashForm)，UI 线程瞬间接管
///    消息泵；所有耗时 IO（数据迁移/服务探测/Node 解析/服务拉起）由 OnShown 启动的后台
///    流水线执行（Task.Run + IProgress&lt;T&gt; 回填进度），双击后 &lt;500ms 出现启动窗。
/// ② 渲染空白：强制双缓冲（OptimizedDoubleBuffer | AllPaintingInWmPaint | UserPaint），
///    且控件在构造时立即设置默认文本/颜色（预渲染），不依赖任何事件先触发再绘制。
///
/// 消息泵健康模型：本窗体是 Application.Run 的根窗体，UI 线程只做消息泵；流水线结果通过
/// await（WindowsFormsSynchronizationContext 回投）传回 UI 线程，全程无 .Result/.Wait()，
/// 无 DoEvents，无 MessageBox 嵌套模态循环（确认交互用窗体内联面板 + TaskCompletionSource）。
/// 取消按钮只撤销后台流水线，不立即关窗——由流水线完成/取消后自行 Close（保持
/// "取消 ≠ 放弃服务" 语义：服务可能仍在后台下载/启动，留待下次启动接管）。
/// </summary>
public sealed class SplashForm : Form
{
    /// <summary>流水线进度消息（Stage 供诊断；Text 直接显示在状态标签上）。</summary>
    public sealed record Message(string Stage, string Text, bool IsError = false);

    /// <summary>启动流水线结果：Main 在 Application.Run 返回后据此接力（建主窗/失败提示/退出）。</summary>
    public sealed record Outcome(
        bool Ready,
        string? WaitResult,
        bool ServiceStartedByShell,
        string LogPath,
        string? ErrorCode,
        string? ErrorDetail);

    /// <summary>后台启动流水线委托：由 Program 实现（访问其私有静态方法与状态）。</summary>
    private readonly Func<IProgress<Message>, Func<string, string, Task<bool>>, CancellationToken, Task<Outcome>> _pipeline;
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _visible;

    private readonly Label _statusLabel = new();
    private readonly ProgressBar _bar = new();
    private readonly Button _cancelButton = new();
    private readonly Panel _confirmPanel = new();
    private readonly Label _confirmTitle = new();
    private readonly Label _confirmText = new();
    private readonly Button _confirmYes = new();
    private readonly Button _confirmNo = new();
    private TaskCompletionSource<bool>? _confirmTcs;

    /// <summary>流水线结果（Application.Run 返回时非 null：OnShown 即启动，同步抛给消息泵）。</summary>
    public Outcome? Result { get; private set; }

    /// <summary>用户点击"取消"（或取消衍生出的确认拒绝）。</summary>
    public bool CancelledByUser { get; private set; }

    public SplashForm(
        Func<IProgress<Message>, Func<string, string, Task<bool>>, CancellationToken, Task<Outcome>> pipeline,
        bool visible = true)
    {
        _pipeline = pipeline;
        _visible = visible;

        // ---- 双缓冲三件套：消除 GDI+ 绘制撕裂与控件短暂空白 ----
        // UserPaint 让本窗体自绘层级完全受控；OptimizedDoubleBuffer 把绘制目标换成后台缓冲，
        // 一次 BitBlt 上屏；AllPaintingInWmPaint 抑制擦背景的白闪（经典闪烁根因）。
        SetStyle(ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.UserPaint, true);
        DoubleBuffered = true;

        // ---- 窗口骨架 ----
        // 标题不用主窗口的"DeepSeek Harness"：单实例逻辑按标题找主窗口，避免第二次点击
        // 把状态窗误当成主窗口聚焦（表现为"点了两次没反应"）。
        Text = "dsh-launcher 启动中";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        // v0.4.1b：原 440x232 字号少、窗口大显空；缩至 380x196 紧凑布局，控件等比收紧。
        ClientSize = new Size(380, 196);
        MinimizeBox = false;
        MaximizeBox = false;
        // 加载窗必须 TopMost——否则用户前台有其他窗口时，加载窗藏在后面看不到（并行开窗 Step5）。
        TopMost = true;
        ShowInTaskbar = true;
        ControlBox = false;

        // ---- 预渲染：构造时立即设置默认文本/颜色/布局，不依赖任何事件先触发再绘制 ----
        _statusLabel.Text = "正在准备启动…";
        _statusLabel.Location = new Point(18, 14);
        _statusLabel.Size = new Size(344, 38);
        _statusLabel.ForeColor = SystemColors.WindowText;
        _statusLabel.AutoEllipsis = true;

        _bar.Style = ProgressBarStyle.Marquee;
        _bar.MarqueeAnimationSpeed = 30; // 后台缓冲下的不确定进度条动画平滑无闪烁
        _bar.Location = new Point(18, 58);
        _bar.Size = new Size(344, 12);

        _cancelButton.Text = "取消";
        _cancelButton.Location = new Point(306, 166);
        _cancelButton.Size = new Size(60, 24);
        _cancelButton.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
        _cancelButton.Click += (_, _) =>
        {
            // 确认面板显示中：点击取消 = 拒绝确认 + 取消启动
            if (_confirmTcs is not null)
            {
                _confirmTcs.TrySetResult(false);
                _confirmPanel.Visible = false;
            }
            // 只撤销后台流水线，不立即关窗——流水线收到取消（ThrowIfCancellationRequested）
            // 后自行 Close。服务若已在后台下载/启动会继续，下次启动可接管。
            _cts.Cancel();
        };

        // ---- 内联确认面板：替代 MessageBox 嵌套模态循环 ----
        // MessageBox.Show 会开启一个嵌套消息循环（模态），与本窗体消息泵叠加造成两层循环；
        // 内联面板与 Splash 共用同一消息泵，确认期间 Splash 照常刷新、取消照常可用。
        _confirmPanel.Visible = false;
        _confirmPanel.Bounds = new Rectangle(18, 78, 344, 86);
        _confirmTitle.Location = new Point(0, 2);
        _confirmTitle.Size = new Size(344, 16);
        _confirmTitle.Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold);
        _confirmText.Location = new Point(0, 22);
        _confirmText.Size = new Size(344, 38);
        _confirmText.AutoEllipsis = true;
        _confirmYes.Text = "是";
        _confirmYes.Location = new Point(212, 60);
        _confirmYes.Size = new Size(62, 22);
        _confirmNo.Text = "否";
        _confirmNo.Location = new Point(278, 60);
        _confirmNo.Size = new Size(62, 22);
        _confirmYes.Click += (_, _) => FinishConfirm(true);
        _confirmNo.Click += (_, _) => FinishConfirm(false);
        _confirmPanel.Controls.Add(_confirmTitle);
        _confirmPanel.Controls.Add(_confirmText);
        _confirmPanel.Controls.Add(_confirmYes);
        _confirmPanel.Controls.Add(_confirmNo);

        Controls.Add(_statusLabel);
        Controls.Add(_bar);
        Controls.Add(_cancelButton);
        Controls.Add(_confirmPanel);

        // 无 UI 测试钩子（DSH_NO_UI=1）：窗口隐藏但消息循环照跑（自动化断言不依赖可见窗口）。
        // 注意：仍走 Application.Run——流水线里的 await/轮询需要消息泵驱动 SynchronizationContext。
        if (!_visible)
        {
            Opacity = 0;
            ShowInTaskbar = false;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        // fire-and-forget：方法内部全部 await、异常全部捕获，不会逃逸到消息泵
        _ = RunPipelineAsync();
    }

    private async Task RunPipelineAsync()
    {
        // Progress<T> 在 UI 线程构造，绑定 Application.Run 安装的 WindowsFormsSynchronizationContext：
        // 后台流水线 Report() 的内容自动 Post 回 UI 线程消息泵执行——线程安全且不阻塞 UI。
        var progress = new Progress<Message>(m =>
        {
            if (IsDisposed) return;
            _statusLabel.Text = m.Text;
            _statusLabel.ForeColor = m.IsError ? Color.Firebrick : SystemColors.WindowText;
        });
        try
        {
            Result = await _pipeline(progress, ConfirmAsync, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            CancelledByUser = true; // 用户点"取消"（流水线侧 ThrowIfCancellationRequested 抛出）
        }
        catch (Exception ex)
        {
            // 注册的全局崩溃钩子只管未处理异常，这里 catch 后必须留痕
            Logger.Error("splash pipeline crashed: " + ex.Message, ErrorCodes.E9001);
            Result = new Outcome(false, null, false, "", ErrorCodes.E9001, "启动流程内部错误：" + ex.Message);
        }
        finally
        {
            // 统一在 UI 线程关闭：Close → Application.Run(splash) 返回 → Main 接力
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke(Close);
        }
    }

    private void FinishConfirm(bool ok)
    {
        _confirmPanel.Visible = false;
        _confirmTcs?.TrySetResult(ok);
    }

    /// <summary>在 UI 线程显示内联确认面板并等待结果。返回 true=用户确认。</summary>
    private Task<bool> ConfirmAsync(string title, string text)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginInvoke(() =>
        {
            if (!_visible)
            {
                // DSH_NO_UI 测试钩子：不弹确认，视为拒绝
                tcs.TrySetResult(false);
                return;
            }
            _confirmTitle.Text = title;
            _confirmText.Text = text;
            _confirmPanel.Visible = true;
            _confirmPanel.BringToFront();
            _confirmTcs = tcs;
        });
        return tcs.Task;
    }
}
