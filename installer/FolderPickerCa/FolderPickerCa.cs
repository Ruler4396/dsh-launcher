// FolderPickerCa —— DTF Type-1 自定义动作。在 msiexec 服务端（remote CA server）执行
// （实测本环境 DLL CA 恒被 marshal，属性回写经 MsiSetProperty 仍会同步回客户端 UI——
// 日志实证 "MSI (c): PROPERTY CHANGE: Modifying INSTALLFOLDER"）。弹窗由客户端进程的
// FolderPicker.exe（Type-38）完成，本 CA 只负责读中转文件并回写安装目录属性。
// 构建目标 net20：SfxCA stub 绑定 CLR v2.0.50727（实测日志），net48 会 BadImageFormat。
using System;
using System.IO;
using WixToolset.Dtf.WindowsInstaller;

public static class FolderPickerCa
{
    private static string PickedFile
    {
        get
        {
            return Path.Combine(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "dsh-launcher"),
                "picked.txt");
        }
    }

    [CustomAction]
    public static ActionResult LaunchFolderPicker(Session session)
    {
        try
        {
            session.Log("FolderPickerCa: entry");
            string path = null;
            try
            {
                if (File.Exists(PickedFile)) path = File.ReadAllText(PickedFile).Trim();
            }
            catch (Exception ex) { session.Log("FolderPickerCa: read failed: " + ex.Message); }
            if (string.IsNullOrEmpty(path))
            {
                session.Log("FolderPickerCa: no picked file (cancelled / not picked)");
                return ActionResult.Success;
            }
            try { File.Delete(PickedFile); } catch { }

            // 目标属性名：_BrowseProperty 的值是 "INSTALLFOLDER"（间接控件语义），
            // 不能把路径写进 _BrowseProperty / WIXUI_INSTALLDIR 本身（会破坏间接绑定、
            // 让后续 SetTargetPath 拿到字面路径 → MSI 错误 2872）。防御：确认拿到的是属性名。
            string target = session["_BrowseProperty"];
            if (string.IsNullOrEmpty(target)) target = session["WIXUI_INSTALLDIR"];
            if (!string.IsNullOrEmpty(target) && target.IndexOf('\\') < 0)
            {
                session[target] = path.TrimEnd('\\') + "\\";
                session.Log("FolderPickerCa: wrote " + path + " to " + target);
            }
        }
        catch (Exception ex)
        {
            try { session.Log("FolderPickerCa: CA error: " + ex); } catch { }
        }
        return ActionResult.Success;
    }
}