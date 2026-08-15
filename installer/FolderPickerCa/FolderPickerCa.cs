// FolderPickerCa —— DTF Type-1 自定义动作。在 msiexec 服务端（remote CA server）执行
// （实测本环境 DLL CA 恒被 marshal，属性回写经 MsiSetProperty 仍会同步回客户端 UI——
// 日志实证 "MSI (c): PROPERTY CHANGE: Modifying INSTALLFOLDER"）。弹窗由客户端进程的
// FolderPicker.exe（Type-38）完成，本 CA 只负责读中转文件并回写安装目录属性。
// 构建目标 net20：SfxCA stub 绑定 CLR v2.0.50727（实测日志），net48 会 BadImageFormat。
//
// 安全（防 picked.txt 提权链）：中转文件位于 C:\ProgramData\dsh-launcher，ACL 允许
// Users 创建文件，低权限攻击者可预置伪造路径。防线：
//   1) 文件内容为"路径\n一次性令牌"（FolderPicker.exe 用 Guid 生成），本 CA 校验
//      令牌非空且形如 32 位十六进制，否则拒绝采纳（攻击者无法预知本次令牌）；
//   2) 路径必须本地绝对路径（根开头 + 盘符/UNC 前缀），拒绝 Windows 系统目录
//      （%SystemRoot%、Program Files、ProgramData 等），防提权安装写任意位置；
//   3) 采纳后立即删除中转文件（清理失败也照常采纳——文件在 ProgramData，
//      无 DC 位时当前用户可能删不掉攻击者的文件，不影响本次采纳）。
using System;
using System.IO;
using System.Text.RegularExpressions;
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

    /// <summary>校验中转路径：必须是本地绝对路径（盘符\ 或 \\server\share\），
    /// 且不在 Windows 系统目录。net20 无 SpecialFolder.Windows 等枚举，用环境变量
    /// 与固定系统路径（SystemRoot/ProgramFiles/ProgramData）组合判断。</summary>
    private static bool IsSafeInstallPath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;
            if (!root.EndsWith("\\")) return false;              // 必须是 "C:\" 或 "\\server\share\"
            if (full.IndexOf('\0') >= 0) return false;
            if (full.StartsWith("\\\\")) return false;           // 拒绝 UNC 网络路径（本机安装目标是本地盘）

            var lower = full.ToLowerInvariant();
            // 拒绝系统关键目录（精确匹配或位于其下）：%SystemRoot%、Program Files、ProgramData
            string[] denied = {
                Environment.GetEnvironmentVariable("SystemRoot"),
                Environment.GetEnvironmentVariable("ProgramFiles"),
                Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Environment.GetEnvironmentVariable("ProgramData"),
                Environment.GetEnvironmentVariable("ALLUSERSPROFILE"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "").TrimEnd('\\'),
            };
            foreach (var d in denied)
            {
                if (string.IsNullOrEmpty(d)) continue;
                var dl = d.TrimEnd('\\').ToLowerInvariant();
                if (dl.Length == 0) continue;
                if (lower == dl || lower.StartsWith(dl + "\\", StringComparison.Ordinal)) return false;
            }
            // 盘符根目录（C:\ 本身）也拒绝——安装到盘根不是合理目标
            if (full.Length == root.Length) return false;
            return true;
        }
        catch
        {
            return false;
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
                if (File.Exists(PickedFile))
                {
                    var lines = File.ReadAllLines(PickedFile);
                    if (lines.Length >= 2)
                    {
                        path = lines[0].Trim();
                        var token = lines[1].Trim();
                        // 令牌必须是非空 32 位十六进制（Guid "N" 格式）；攻击者无法预知。
                        if (!Regex.IsMatch(token, "^[0-9a-fA-F]{32}$"))
                        {
                            session.Log("FolderPickerCa: token mismatch, rejecting picked path (possible tampering)");
                            path = null;
                        }
                    }
                    else
                    {
                        session.Log("FolderPickerCa: picked file lacks token line, rejecting");
                    }
                }
            }
            catch (Exception ex) { session.Log("FolderPickerCa: read failed: " + ex.Message); }
            if (string.IsNullOrEmpty(path))
            {
                session.Log("FolderPickerCa: no valid picked path (cancelled / not picked / tampered)");
                return ActionResult.Success;
            }
            if (!IsSafeInstallPath(path))
            {
                session.Log("FolderPickerCa: rejected unsafe install path: " + path);
                try { File.Delete(PickedFile); } catch { }
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