@echo off
rem ============================================================
rem  ONE-CLICK: snap sandbox back to rc6 and relaunch the
rem  launcher for a fresh manual-update test cycle.
rem  - stops previous lab processes (targeted, host-safe)
rem  - restores rc6 instantly from local snapshot (no download)
rem  - starts local fake registry (latest = rc7)
rem  - launches the sandbox launcher on port 3999
rem ============================================================
setlocal
set "LAB=%~dp0"
where pwsh >nul 2>nul && (set "PS=pwsh") || (set "PS=powershell")
"%PS%" -NoProfile -ExecutionPolicy Bypass -File "%LAB%lab.ps1" start -Reset
echo.
pause
