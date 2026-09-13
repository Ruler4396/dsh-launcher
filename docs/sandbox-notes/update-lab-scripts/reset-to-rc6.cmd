@echo off
rem ============================================================
rem  Instant reset: stop lab processes + restore pristine rc6.
rem  Local file operations only - no network, no re-download.
rem  (does NOT relaunch; use start-rc6-and-launch.cmd for that)
rem ============================================================
setlocal
set "LAB=%~dp0"
where pwsh >nul 2>nul && (set "PS=pwsh") || (set "PS=powershell")
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%LAB%lab.ps1" reset
echo.
pause
