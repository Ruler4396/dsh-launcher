' DeepSeek Harness silent launcher (no window). Used by autostart / one-click entry.
' Starts `dsh web` and writes log to %USERPROFILE%\.dsh-web.log
Set sh = CreateObject("WScript.Shell")
sh.Run "cmd /c ""dsh web --host 127.0.0.1 --port 3080 > %USERPROFILE%\.dsh-web.log 2>&1""", 0, False
