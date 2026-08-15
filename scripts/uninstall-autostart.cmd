@echo off
setlocal

rem Remove autostart entries and shortcuts created by dsh-launcher
rem (covers both the portable ZIP layout and the MSI installer).

rem 1) Startup-folder entry (portable layout; also cleans the legacy
rem    dsh-autostart.vbs name used by some older setups)
set "STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup"
for %%V in ("start-dsh.vbs" "dsh-autostart.vbs") do (
  if exist "%STARTUP%\%%~V" (
    del /q "%STARTUP%\%%~V"
    echo [OK] Removed autostart entry: "%STARTUP%\%%~V"
  )
)
if not exist "%STARTUP%\start-dsh.vbs" if not exist "%STARTUP%\dsh-autostart.vbs" (
  echo [SKIP] No autostart entry in the Startup folder.
)

rem 2) HKCU Run entry (created by DshWeb.exe on first start when the MSI
rem    autostart feature is enabled)
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "dsh-launcher" /f >nul 2>&1
if not errorlevel 1 (
  echo [OK] Removed HKCU Run entry: dsh-launcher
) else (
  echo [SKIP] No HKCU Run entry "dsh-launcher".
)

rem 2.5) Machine-level autostart intent flag written by the MSI installer
rem      (HKLM ...AutoStartWanted=1). Clearing it prevents DshWeb.exe from
rem      re-creating the HKCU Run entry on next start (self-healing).
rem      Requires admin; if denied, re-run this script as Administrator.
reg delete "HKLM\Software\dsh-launcher" /v "AutoStartWanted" /f >nul 2>&1
if not errorlevel 1 (
  echo [OK] Removed HKLM autostart intent flag: AutoStartWanted
) else (
  echo [WARN] Could not remove HKLM autostart intent flag ^(admin required^).
  echo         DshWeb.exe may re-create the HKCU Run entry on next start.
)

rem 3) Desktop shortcuts (any name starting with DshWeb / DeepSeek Harness / dsh-launcher,
rem    also checks the OneDrive-redirected Desktop)
for %%D in ("%USERPROFILE%\Desktop" "%USERPROFILE%\OneDrive\Desktop") do (
  if exist "%%~D\DshWeb*.lnk" (
    del /q "%%~D\DshWeb*.lnk"
    echo [OK] Removed desktop shortcuts: "%%~D\DshWeb*.lnk"
  )
  if exist "%%~D\DeepSeek Harness*.lnk" (
    del /q "%%~D\DeepSeek Harness*.lnk"
    echo [OK] Removed desktop shortcuts: "%%~D\DeepSeek Harness*.lnk"
  )
  if exist "%%~D\dsh-launcher.lnk" (
    del /q "%%~D\dsh-launcher.lnk"
    echo [OK] Removed desktop shortcut: "%%~D\dsh-launcher.lnk"
  )
)

rem 4) Start Menu folder created by the MSI installer (removed only when empty)
if exist "%APPDATA%\Microsoft\Windows\Start Menu\Programs\dsh-launcher" (
  rmdir "%APPDATA%\Microsoft\Windows\Start Menu\Programs\dsh-launcher" 2>nul
  echo [OK] Removed Start Menu folder (if empty).
)

echo.
echo dsh-launcher autostart entries and shortcuts removed.
echo (MSI installs can also be uninstalled via Settings - Apps - dsh-launcher)
pause
