namespace DshWeb.Managers;

/// <summary>
/// 运行时(Node)管理实现：委托现有 RuntimeResolver 静态逻辑，收敛为可注入实例。
/// 零行为变更（解析/下载/校验/PATH 前插的原有规则不变）。
/// </summary>
public sealed class RuntimeManager : IRuntimeManager
{
    public async Task<RuntimeResult> EnsureRuntimeAsync(CancellationToken ct = default)
    {
        var env = RuntimeResolver.ResolveExisting();
        if (env.NodeExe is not null)
        {
            if (env.IsPortable) PrependToPath(env.RootDir!);
            return RuntimeResult.Portable(env); // Ready 与否由调用方按版本判定；此处仅保证"有可用 Node"
        }
        // 无可用 Node：按用户确认触发便携下载（沿用原 TryEnsureNodeAsync 的确认语义由调用方承担）
        var (ok, code, detail) = await RuntimeResolver.EnsurePortableNodeAsync(ct);
        if (!ok) return RuntimeResult.Failed(code, detail);
        var after = RuntimeResolver.ResolveExisting();
        if (after.NodeExe is null) return RuntimeResult.Failed("E1005", "便携 Node 安装后仍未解析到 node.exe");
        if (after.IsPortable) PrependToPath(after.RootDir!);
        return RuntimeResult.Portable(after);
    }

    public void PrependToPath(string nodeRoot) => RuntimeResolver.PrependToPath(nodeRoot);
}
