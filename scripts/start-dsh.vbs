' DeepSeek Harness silent launcher (no window). Used by autostart / one-click entry.
' Starts `dsh web` and writes log to %USERPROFILE%\.dsh-web.log
' Prefers a globally installed `dsh`; falls back to `npx -y @deepseek-ai/dsh` so the
' service still starts for users who never ran `npm install -g @deepseek-ai/dsh`.
'
' NOTE: keep this file pure ASCII. VBScript reads .vbs as ANSI; non-ASCII
' comment bytes can swallow the following newline and comment out real code.
'
' Port passthrough: the dsh-launcher shell sets DSH_PORT (process env var)
' before spawning us; children inherit it. Unset -> default 3080.
'
' Log file: per-port (3080 -> .dsh-web.log, others -> .dsh-web.<port>.log) so
' parallel instances (e.g. shell-managed 9335 next to a manual 3080) do not
' fight over one file. If the log is locked by a running service (orphaned or
' still starting), fall back to %TEMP% so the service still starts: a dead
' cold start is worse than a missing log.
Set sh = CreateObject("WScript.Shell")
port = "3080"
Set env = sh.Environment("PROCESS")
If env("DSH_PORT") <> "" Then port = env("DSH_PORT")

Set fso = CreateObject("Scripting.FileSystemObject")
If port = "3080" Then
    logname = ".dsh-web.log"
Else
    logname = ".dsh-web." & port & ".log"
End If
logfile = sh.ExpandEnvironmentStrings("%USERPROFILE%") & "\" & logname
On Error Resume Next
Set f = fso.OpenTextFile(logfile, 2, True)
If f Is Nothing Then
    logfile = sh.ExpandEnvironmentStrings("%TEMP%") & "\" & logname
    Set f = fso.OpenTextFile(logfile, 2, True)
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
