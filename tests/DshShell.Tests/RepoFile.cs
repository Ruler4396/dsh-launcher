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
    /// <summary>定位仓库内某相对路径的绝对路径；找不到仓库根就抛（同 Read 的不静默纪律）。</summary>
    public static string FullPath(string repoRelativePath)
    {
        var sep = repoRelativePath.Replace('/', Path.DirectorySeparatorChar);
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(dir.FullName) && File.Exists(Path.Combine(dir.FullName, ".gitignore")))
                return Path.Combine(dir.FullName, sep);
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"未从 {AppContext.BaseDirectory} 向上找到仓库根（以 .gitignore 为锚）。");
    }

    /// <summary>存在性判定（供"不得回流"类负断言用）。找不到仓库根同样抛——不存在的是答案，不是绿灯。</summary>
    public static bool Exists(string repoRelativePath) => File.Exists(FullPath(repoRelativePath));

    public static string Read(string repoRelativePath)
    {
        var path = FullPath(repoRelativePath);
        if (File.Exists(path)) return File.ReadAllText(path);
        throw new FileNotFoundException(
            $"{path} 不存在。" +
            "本 helper 刻意不返回 null、不静默跳过：断言没跑就该红，而不是绿。", repoRelativePath);
    }
}
