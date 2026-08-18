' DeepSeek Harness silent launcher (no window). Used by autostart / one-click entry.
' Starts `dsh web` and writes log to the unified log file.
' Prefers a globally installed `dsh`; falls back to `npx -y @deepseek-ai/dsh` so the
' service still starts for users who never ran `npm install -g @deepseek-ai/dsh`.
'
' NOTE: keep this file pure ASCII. VBScript reads .vbs as ANSI; non-ASCII
' comment bytes can swallow the following newline and comment out real code.
'
' Port passthrough: the dsh-launcher shell sets DSH_PORT (process env var)
' before spawning us; children inherit it. Unset -> default 3080.
'
' v0.3.0 unified log: the shell sets DSH_LOG to DSH_HOME\dsh-launcher\dsh.log
' (single log file shared with the shell's JSON Lines; append mode 8 - the shell
' owns rotation, this script never truncates or rolls over).
' If the log is locked by a running service or missing, fall back to %TEMP% so the
' service still starts: a dead cold start is worse than a missing log.
Set sh = CreateObject("WScript.Shell")
port = "3080"
Set env = sh.Environment("PROCESS")
If env("DSH_PORT") <> "" Then port = env("DSH_PORT")

Set fso = CreateObject("Scripting.FileSystemObject")
logfile = env("DSH_LOG")
If logfile = "" Then
    If env("DSH_HOME") <> "" Then dshhome = env("DSH_HOME") Else dshhome = sh.ExpandEnvironmentStrings("%USERPROFILE%") & "\.dsh"
    logfile = dshhome & "\dsh-launcher\dsh.log"
End If
' 确保父目录存在（创建目录 + 防御性打开文件）。OpenTextFile 的第三个参数 True
' 只创建文件*不创建目录*，目录不存在时直接失败。此前无此处理导致：
' 1) 旧版 autostart 直接拉起本脚本时 %USERPROFILE%\.dsh\dsh-launcher\ 尚未创建，
'    失败后 f 为 Empty（非 Nothing），f Is Nothing 引发"缺少对象"(800A01A8) 弹窗；
' 2) On Error Resume Next 静默吞掉该错误使回退到 %TEMP% 的分支不执行。
' 先创建目录，再显式初始化 f 为 Nothing，确保无论哪种失败路径结果都正确。
On Error Resume Next
fso.CreateFolder fso.GetParentFolderName(logfile)
On Error GoTo 0
Set f = Nothing
On Error Resume Next
Set f = fso.OpenTextFile(logfile, 8, True)
If f Is Nothing Then
    logfile = sh.ExpandEnvironmentStrings("%TEMP%") & "\dsh.log"
    Set f = fso.OpenTextFile(logfile, 8, True)
End If
On Error GoTo 0
If Not (f Is Nothing) Then
    f.WriteLine "[start-dsh] using port " & port
    f.Close
End If

If sh.Run("cmd /c where dsh >nul 2>&1", 0, True) = 0 Then
    cmdline = "dsh web --host 127.0.0.1 --port " & port
    note = "using global dsh"
Else
    cmdline = "npx -y @deepseek-ai/dsh web --host 127.0.0.1 --port " & port
    note = "dsh not on PATH - falling back to npx -y @deepseek-ai/dsh"
End If
' 日志重定向目标必须加引号：USERPROFILE 含空格（如 C:\Users\John Smith）时，
' 不带引号会把路径截断成命令参数/碎片文件名（实测复现）；含 & 等 cmd 元字符时可注入命令。
q = Chr(34)
sh.Run "cmd /c ""echo [start-dsh] " & note & " >> " & q & logfile & q & " && " & cmdline & " >> " & q & logfile & q & " 2>&1""", 0, False
