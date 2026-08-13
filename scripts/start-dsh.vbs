' DeepSeek Harness silent launcher (no window). Used by autostart / one-click entry.
' Starts `dsh web` and writes log to %USERPROFILE%\.dsh-web.log
' Prefers a globally installed `dsh`; falls back to `npx -y @deepseek-ai/dsh` so the
' service still starts for users who never ran `npm install -g @deepseek-ai/dsh`.
Set sh = CreateObject("WScript.Shell")
If sh.Run("cmd /c where dsh >nul 2>&1", 0, True) = 0 Then
    cmdline = "dsh web --host 127.0.0.1 --port 3080"
    note = "using global dsh"
Else
    cmdline = "npx -y @deepseek-ai/dsh web --host 127.0.0.1 --port 3080"
    note = "dsh not on PATH - falling back to npx -y @deepseek-ai/dsh"
End If
sh.Run "cmd /c ""echo [start-dsh] " & note & " > %USERPROFILE%\.dsh-web.log && " & cmdline & " >> %USERPROFILE%\.dsh-web.log 2>&1""", 0, False
