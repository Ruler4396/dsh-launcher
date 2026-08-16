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

    /// <summary>卸载清理（immediate/发起用户上下文）：删除自启相关两处注册表：
    /// 1) HKCU Run 的 dsh-launcher 值——只删内容包含 start-dsh.vbs 的（防误删）；
    /// 2) HKLM\Software\dsh-launcher\AutoStartWanted 意图标志——组件机制在卸载时
    /// 不可靠：Level 条件（AUTO_START_OPTION <> 1）在卸载时重评，属性不存在 →
    /// Level=0（feature 禁用），MSI 对禁用 feature 的组件不请求移除（实测
    /// Installed: Local 但 Request: Null），值残留；卸载提权上下文可写 HKLM，由
    /// CA 兜底删除。删除失败不阻断卸载（Return="ignore"）。</summary>
    [CustomAction]
    public static ActionResult RemoveAutoRun(Session session)
    {
        try
        {
            using (var run = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                var val = run != null ? run.GetValue("dsh-launcher") as string : null;
                var isOurs = !string.IsNullOrEmpty(val) &&
                    (val.IndexOf("start-dsh.vbs", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     val.IndexOf("DshWeb.exe", StringComparison.OrdinalIgnoreCase) >= 0);
                if (isOurs)
                {
                    run.DeleteValue("dsh-launcher");
                    session.Log("RemoveAutoRun: removed dsh-launcher from HKCU Run");
                }
                else
                {
                    session.Log("RemoveAutoRun: no matching dsh-launcher Run value (nothing to do)");
                }
            }
        }
        catch (Exception ex)
        {
            try { session.Log("RemoveAutoRun: HKCU delete failed: " + ex.Message); } catch { }
        }
        try
        {
            using (var flag = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"Software\dsh-launcher", true))
            {
                if (flag != null && flag.GetValue("AutoStartWanted") != null)
                {
                    flag.DeleteValue("AutoStartWanted");
                    session.Log("RemoveAutoRun: removed AutoStartWanted from HKLM");
                }
                else
                {
                    session.Log("RemoveAutoRun: no AutoStartWanted flag (nothing to do)");
                }
            }
        }
        catch (Exception ex)
        {
            try { session.Log("RemoveAutoRun: HKLM delete failed: " + ex.Message); } catch { }
        }
        return ActionResult.Success;
    }

    /// <summary>卸载清理用户数据（immediate/发起用户上下文，Return="ignore"，v0.3.0）：
    /// 只清理启动器自有数据——DSH_HOME\dsh-launcher\ 整目录（settings.json 残留、统一日志
    /// dsh.log、window-state/pending-update/service-pid 等）与旧版 %USERPROFILE%\.dsh-web*.log。
    /// 硬边界：绝不触碰 DSH_HOME 下其余内容（profiles/、settings.yaml、.credentials.yaml、
    /// sessions、storages、插件等一切 dsh 生态数据）。同时杀死 pid 文件记录、仍存活的服务
    /// 进程（只动我们记录的 PID，绝不按进程名批量杀）；并在 taskkill 前校验该 PID 确为
    /// node 进程，仅当 pid 文件记录、且当前确为 node 进程时才杀（防 PID 复用误杀无辜进程）。
    /// 任何失败跳过，不阻断卸载。</summary>
    [CustomAction]
    public static ActionResult CleanUserData(Session session)
    {
        string userProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        if (string.IsNullOrEmpty(userProfile))
            userProfile = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
        string dshHome = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrEmpty(dshHome))
            dshHome = Path.Combine(userProfile, ".dsh");
        string dataDir = Path.Combine(dshHome, "dsh-launcher");
        try { session.Log("CleanUserData: dataDir=" + dataDir); } catch { }

        // 1) 清理 pid 文件记录的壳托管服务进程（只动我们记录的 PID）
        try
        {
            if (Directory.Exists(dataDir))
            {
                string[] pidFiles = Directory.GetFiles(dataDir, "service-pid-*.txt");
                foreach (string pf in pidFiles)
                {
                    try
                    {
                        int pid;
                        if (int.TryParse(File.ReadAllText(pf).Trim(), out pid) && pid > 0)
                        {
                            // 身份校验：仅当该 PID 当前确为 node 进程才杀，防 PID 被系统复用
                            // 时误杀无辜进程。进程不存在 → 跳过不杀；进程名非 node → 跳过不杀。
                            bool isNode = false;
                            try
                            {
                                using (System.Diagnostics.Process p = System.Diagnostics.Process.GetProcessById(pid))
                                {
                                    string name = p.ProcessName;
                                    if (!string.IsNullOrEmpty(name)
                                        && string.Equals(name, "node", StringComparison.OrdinalIgnoreCase))
                                        isNode = true;
                                }
                            }
                            catch (ArgumentException) { /* 进程已退出/不存在 → isNode 保持 false */ }
                            catch (System.ComponentModel.Win32Exception) { /* 无权限访问 → 保持 false */ }
                            catch (InvalidOperationException) { /* 进程已退出 → 保持 false */ }

                            if (!isNode)
                            {
                                try { session.Log("CleanUserData: pid " + pid + " 非 node 进程或已不存在，跳过"); }
                                catch { }
                                continue;
                            }

                            try
                            {
                                System.Diagnostics.Process.Start("taskkill", "/pid " + pid);
                                System.Threading.Thread.Sleep(500);
                                System.Diagnostics.Process.Start("taskkill", "/f /pid " + pid);
                                session.Log("CleanUserData: asked taskkill for node pid " + pid);
                            }
                            catch (Exception ex) { try { session.Log("CleanUserData: kill failed: " + ex.Message); } catch { } }
                        }
                    }
                    catch { }
                }
            }
        }
        catch (Exception ex) { try { session.Log("CleanUserData: pid sweep failed: " + ex.Message); } catch { } }

        // 2) 删除自有数据目录（先文件后子目录，最后空目录本身）
        try
        {
            if (Directory.Exists(dataDir))
            {
                foreach (string f in Directory.GetFiles(dataDir))
                { try { File.Delete(f); } catch { } }
                foreach (string s in Directory.GetDirectories(dataDir))
                { try { Directory.Delete(s, true); } catch { } }
                try { Directory.Delete(dataDir, false); } catch { }
            }
        }
        catch (Exception ex) { try { session.Log("CleanUserData: dir delete failed: " + ex.Message); } catch { } }

        // 3) 旧版日志残留 %USERPROFILE%\.dsh-web*.log（多次打开的滚动/按端口分文件）
        try
        {
            foreach (string f in Directory.GetFiles(userProfile, ".dsh-web*.log"))
            { try { File.Delete(f); } catch { } }
        }
        catch { }
        return ActionResult.Success;
    }

    /// <summary>安装/修改时根据 AUTO_START_OPTION 属性写自启两级落地：
    /// 1) HKLM 意图标志 AutoStartWanted=1（机器级，其他上下文可读）；
    /// 2) HKCU Run 的 dsh-launcher 值（当前用户登录自启，安装后无需先启动壳）。
    /// 不使用组件条件（Feature Level / Component Condition）：MSI 在修改安装场景下
    /// 对 Absent feature 的 Level 条件和已安装组件的 Component 条件均不重新评估
    /// （0.2.4/0.2.5 实测），导致 checkbox 勾选也不生效。改为 immediate CA 直接操作
    /// 注册表：AUTO_START_OPTION="1" → 写值；其他情况不操作（保留已有值）。
    /// 执行上下文：per-machine UAC 提权下 msiexec 服务进程以发起用户身份运行，
    /// HKCU 即发起用户 hive（0.2.5 实测：卸载 CA 同样上下文可读写真实用户 HKCU）。
    /// 若 HKCU 写入落空（如部署系统代装），壳 EnsureAutoStartRequested 读 HKLM
    /// 标志自愈补写。删除由 RemoveAutoRun CA（卸载时）和 uninstall-autostart.cmd 负责。</summary>
    [CustomAction]
    public static ActionResult SetAutoStartFlag(Session session)
    {
        var opt = session["AUTO_START_OPTION"];
        if (!string.Equals(opt, "1"))
        {
            session.Log("SetAutoStartFlag: AUTO_START_OPTION=[" + (opt ?? "") + "], not '1' — skipping");
            return ActionResult.Success;
        }
        try
        {
            using (var k = Microsoft.Win32.Registry.LocalMachine.CreateSubKey(@"Software\dsh-launcher"))
            {
                if (k != null)
                {
                    k.SetValue("AutoStartWanted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                    session.Log("SetAutoStartFlag: wrote AutoStartWanted=1 (AUTO_START_OPTION=1)");
                }
            }
        }
        catch (Exception ex)
        {
            try { session.Log("SetAutoStartFlag: HKLM write failed: " + ex.Message); } catch { }
        }
        // 直接落地当前用户 Run 项：安装勾选后重启即拉起壳窗口（壳再自行拉起服务），
        // 无需先手动启动壳（0.2.5 实测：仅靠壳首启自愈则该场景永远不落地）。
        // 值格式与壳 EnsureAutoStartRequested 完全一致，壳启动后比对相同则不重写。
        try
        {
            var dir = session["INSTALLFOLDER"] ?? @"C:\Program Files\dsh-launcher";
            if (!dir.EndsWith("\\", StringComparison.Ordinal)) dir += "\\";
            var expected = "\"" + dir + "DshWeb.exe\"";
            using (var run = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                if (run != null)
                {
                    run.SetValue("dsh-launcher", expected, Microsoft.Win32.RegistryValueKind.String);
                    session.Log("SetAutoStartFlag: wrote HKCU Run dsh-launcher = " + expected);
                }
            }
        }
        catch (Exception ex)
        {
            try { session.Log("SetAutoStartFlag: HKCU Run write failed: " + ex.Message + " (shell self-heal on first start will retry)"); } catch { }
        }
        return ActionResult.Success;
    }
}