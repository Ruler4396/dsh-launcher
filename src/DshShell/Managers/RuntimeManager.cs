namespace DshWeb.Managers;

/// <summary>
/// 运行时(Node)管理实现：委托现有 RuntimeResolver 静态逻辑，收敛为可注入实例。
/// 解析/下载/校验/PATH 前插的原有规则不变；【ADR-024】成功时产出统一
/// <see cref="DshWeb.Domain.DshRuntimeIdentity"/>（发现/启动/更新共用同一身份）。
/// </summary>
public sealed class RuntimeManager : IRuntimeManager
{
    private readonly Func<Task<bool>>? _confirmDownload;

    /// <summary>confirmDownload：便携 Node 下载前的用户确认（组合根注入 Splash 内联面板；
    /// null = 不确认直接下载）。v0.4.2 从"调用方先探测再确认"收敛到 Manager 内部，
    /// 保持"先确认后下载"的用户交互契约（E1002=拒绝 / E1003=下载失败）。</summary>
    public RuntimeManager(Func<Task<bool>>? confirmDownload = null) => _confirmDownload = confirmDownload;

    public async Task<RuntimeResolution> EnsureRuntimeAsync(CancellationToken ct = default)
    {
        var env = RuntimeResolver.ResolveExisting();
        if (env.NodeExe is null)
        {
            // 无可用 Node：先确认再下载（用户拒绝 → E1002，与 v0.3.x TryEnsureNodeAsync 语义一致）
            if (_confirmDownload is not null && !await _confirmDownload())
                return RuntimeResolution.Failed(ErrorCodes.E1002, "已取消自动安装便携 Node.js。");
            var (ok, code, detail) = await RuntimeResolver.EnsurePortableNodeAsync(ct);
            if (!ok) return RuntimeResolution.Failed(code ?? ErrorCodes.E1003, detail ?? "便携 Node 安装失败。");
            env = RuntimeResolver.ResolveExisting();
            if (env.NodeExe is null)
                return RuntimeResolution.Failed("E1005", "便携 Node 安装后仍未解析到 node.exe");
        }
        if (env.IsPortable) PrependToPath(env.RootDir!);

        // 【ADR-024】Node 就绪 → 立即产出统一身份（发现层读 PATH/注册表/DSH_HOME）。
        // 便携 Node 刚前插进进程 PATH，发现层 FindNodeExe 必能命中；万一环境异常导致
        // 发现层拿不到 node 路径，则以已解析的物理路径补齐——身份的 NodeExePath 绝不为空地失败。
        var identity = DshWeb.Domain.DshDiscovery.DiscoverCurrentRuntime();
        if (identity.NodeExePath is null && File.Exists(env.NodeExe))
            identity = identity with { NodeExePath = env.NodeExe };
        return RuntimeResolution.Ready(identity);
    }

    public void PrependToPath(string nodeRoot) => RuntimeResolver.PrependToPath(nodeRoot);
}
