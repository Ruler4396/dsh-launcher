@echo off
setlocal
set "DIR=%~dp0"

rem If dsh is not listening yet, start it silently (DshWeb.exe also does this;
rem doing it here avoids the extra startup wait inside the shell app).
powershell -NoProfile -Command "try{$c=New-Object Net.Sockets.TcpClient;$c.Connect('127.0.0.1',3080);$c.Close();exit 0}catch{exit 1}"
if errorlevel 1 (
  if exist "%DIR%start-dsh.vbs" (
    wscript "%DIR%start-dsh.vbs"
  ) else (
    echo [ERROR] start-dsh.vbs not found next to this script.
    echo Run this from the dsh-launcher deploy folder.
    pause
    exit /b 1
  )
)

start "" "%DIR%DshWeb.exe"
