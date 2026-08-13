@echo off
setlocal
set DIR=%~dp0
powershell -NoProfile -Command "$c=New-Object Net.Sockets.TcpClient;try{$c.Connect('127.0.0.1',3080);exit 0}catch{exit 1}"
if %errorlevel% equ 0 goto open
wscript "%DIR%start-dsh.vbs"
powershell -NoProfile -Command "$i=0;while($i -lt 90){$c=New-Object Net.Sockets.TcpClient;try{$c.Connect('127.0.0.1',3080);exit 0}catch{};Start-Sleep -Milliseconds 1000;$i++}"
:open
start "" "%DIR%bin\DshWeb.exe"
