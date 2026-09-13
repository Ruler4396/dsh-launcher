@echo off
rem Show current lab state (processes, ports, runtimes, pending, stash).
setlocal
set "LAB=%~dp0"
where pwsh >nul 2>nul && (set "PS=pwsh") || (set "PS=powershell")
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%LAB%lab.ps1" status
echo.
pause
