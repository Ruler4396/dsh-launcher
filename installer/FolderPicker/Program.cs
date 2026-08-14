// 现代化文件夹选择器（MSI 安装向导"浏览"按钮用）。
//
// 为什么有这个小工具：Windows Installer 自带的老式 BrowseDlg（目录树）体验陈旧；
// 而"现代文件夹选择器"（自定义 CA）在 per-machine 提权安装的远程上下文里会崩溃
// （DTF / Type-38 两种方案均复现）。本工具作为**立即上下文**的 Type-38 外部 exe
// 运行：它弹 Windows 11 风格的新版文件夹对话框（IFileDialog / WPF OpenFolderDialog），
// 把用户选择的路径写入 HKCU\Software\dsh-launcher\InstallDir，由 MSI 侧的标准
// AppSearch 动作读回并应用到安装目录属性。用户取消时删除该注册表值（MSI 侧保持
// 原路径不变）。
//
// 用法: FolderPicker.exe [初始目录]
//   退出码 0 = 用户选择并已写注册表；1 = 用户取消/出错（注册表值已删除）。

using System;
using System.IO;
using Microsoft.Win32;

internal static class Program
{
    private const string RegKey = @"Software\dsh-launcher";
    private const string RegValue = "InstallDir";

    [STAThread]
    private static int Main(string[] args)
    {
        // 先删除旧值：确保"取消"时 AppSearch 读不到任何残留
        try { Registry.CurrentUser.OpenSubKey(RegKey, writable: true)?.DeleteValue(RegValue, throwOnMissingValue: false); }
        catch { /* ignore */ }

        var initial = args.Length > 0 && Directory.Exists(args[0]) ? args[0] : null;

        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择 dsh-launcher 安装目录",
                InitialDirectory = initial,
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                try
                {
                    Registry.CurrentUser.CreateSubKey(RegKey)?.SetValue(RegValue, dlg.FolderName.TrimEnd('\\') + "\\");
                    return 0;
                }
                catch
                {
                    // 写注册表失败按取消处理（无值可读）
                }
            }
        }
        catch
        {
            // 对话框失败：保持无值（MSI 侧按"未选择"处理，路径不变）
        }
        return 1;
    }
}
