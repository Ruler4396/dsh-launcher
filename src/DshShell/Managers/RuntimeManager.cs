namespace DshWeb.Managers;

/// <summary>
/// 运行时(Node)管理实现：委托现有 RuntimeResolver 静态逻辑，收敛为可注入实例。
/// 零行为变更（解析/下载/校验/PATH 前插的原有规则不变）。
/// </summary>
public sealed class RuntimeManager : IRuntimeManager
{
    private readonly Func<Task<bool>>? _confirmDownload;

    /// <summary>confirmDownload：便携 Node 下载前的用户确认（组合根注入 Splash 内联面板；
    /// null = 不确认直接下载）。v0.4.2 从"调用方先探测再确认"收敛到 Manager 内部，
    /// 保持"先确认后下载"的用户交互契约（E1002=拒绝 / E1003=下载失败）。</summary>
    public RuntimeManager(Func<Task<bool>>? confirmDownload = null) => _confirmDownload = confirmDownload;

    public async Task<RuntimeResult> EnsureRuntimeAsync(CancellationToken ct = default)
    {
        var env = RuntimeResolver.ResolveExisting();
        if (env.NodeExe is not null)
        {
            if (env.IsPortable) PrependToPath(env.RootDir!);
            return RuntimeResult.Portable(env); // Ready 与否由调用方按版本判定；此处仅保证"有可用 Node"
        }
        // 无可用 Node：先确认再下载（用户拒绝 → E1002，与 v0.3.x TryEnsureNodeAsync 语义一致）
        if (_confirmDownload is not null && !await _confirmDownload())
            return RuntimeResult.Failed(ErrorCodes.E1002, "已取消自动安装便携 Node.js。");
        var (ok, code, detail) = await RuntimeResolver.EnsurePortableNodeAsync(ct);
        if (!ok) return RuntimeResult.Failed(code ?? ErrorCodes.E1003, detail ?? "便携 Node 安装失败。");
        var after = RuntimeResolver.ResolveExisting();
        if (after.NodeExe is null) return RuntimeResult.Failed("E1005", "便携 Node 安装后仍未解析到 node.exe");
        if (after.IsPortable) PrependToPath(after.RootDir!);
        return RuntimeResult.Portable(after);
    }

    public void PrependToPath(string nodeRoot) => RuntimeResolver.PrependToPath(nodeRoot);
}
