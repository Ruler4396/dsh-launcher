namespace DshShell.Tests;

/// <summary>
/// 读仓库内的真实文件（scripts/、assets/ 等）用于"最终物理状态"类断言。
///
/// 为什么要有这个文件：Outcomes/ 下曾有 4 条用例这样写
///     var p = Path.Combine(AppContext.BaseDirectory, "start-dsh.vbs");
///     if (!File.Exists(p)) return;
/// 而 start-dsh.vbs **从来不在**测试输出目录里（实测 tests/**/bin 下 0 个，csproj 也没有
/// CopyToOutputDirectory），于是 `return` 永远命中、断言永远不执行——CI 长期全绿，
/// 且其中两条断言的 token（"DSH_SAFE_MODE"/"--safe-mode"）在真实 vbs 里根本不存在，
/// 一旦真跑立刻红。守卫条件就是那个把假绿维持下去的开关。
///
/// 所以这里的规矩是：**找不到就抛**，绝不允许"静默跳过"。找不到仓库根是环境问题，
/// 环境问题应当把红灯亮出来，而不是把断言蒸发掉。
/// </summary>
internal static class RepoFile
{
    public static string Read(string repoRelativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, repoRelativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"未从 {AppContext.BaseDirectory} 向上找到 {repoRelativePath}。" +
            "本 helper 刻意不返回 null、不静默跳过：断言没跑就该红，而不是绿。", repoRelativePath);
    }
}
