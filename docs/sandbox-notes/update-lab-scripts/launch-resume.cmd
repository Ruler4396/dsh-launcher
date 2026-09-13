@echo off
rem ============================================================
rem  Resume: launch sandbox launcher WITHOUT resetting.
rem  - pending staged build present -> startup applies it (rc7)
rem  - otherwise just launches current state as-is
rem ============================================================
setlocal
set "LAB=%~dp0"
where pwsh >nul 2>nul && (set "PS=pwsh") || (set "PS=powershell")
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%LAB%lab.ps1" start
echo.
pause
