// FolderPicker.exe —— 现代化文件夹选择器（MSI 向导"浏览"按钮用，Type-38 立即上下文外部 exe）。
// 客户端进程内弹 Windows 11/10 新版文件夹对话框（IFileDialog），把所选路径写到
// C:\ProgramData\dsh-launcher\picked.txt，由 DTF 自定义动作（服务端 remote 执行，
// 属性回写已验证生效）读回并应用到安装目录属性。取消时不留文件（路径不变）。
// 用法: FolderPicker.exe [初始目录]   退出码 0=已写文件；1=取消/出错。
using System;
using System.IO;
using Microsoft.Win32;

internal static class Program
{
    private static string PickedFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "dsh-launcher", "picked.txt");

    [STAThread]
    private static int Main(string[] args)
    {
        try { if (File.Exists(PickedFile)) File.Delete(PickedFile); } catch { /* ignore */ }

        var raw = args.Length > 0 ? args[0] : null;
        var initial = string.IsNullOrEmpty(raw) ? null : raw.TrimEnd('"', '\\');
        if (!string.IsNullOrEmpty(initial) && !Directory.Exists(initial)) initial = null;

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
                    Directory.CreateDirectory(Path.GetDirectoryName(PickedFile));
                    File.WriteAllText(PickedFile, dlg.FolderName.TrimEnd('\\') + "\\");
                    return 0;
                }
                catch { /* 写文件失败按取消处理 */ }
            }
        }
        catch { /* 对话框失败：保持无文件 */ }
        return 1;
    }
}