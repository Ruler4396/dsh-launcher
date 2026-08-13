@echo off
setlocal

rem Remove the autostart entry and desktop shortcuts created by dsh-launcher.

rem 1) Startup-folder entry (the actual autostart mechanism)
set "STARTUP=%APPDATA%\Microsoft\Windows\Start Menu\Programs\Startup"
if exist "%STARTUP%\start-dsh.vbs" (
  del /q "%STARTUP%\start-dsh.vbs"
  echo [OK] Removed autostart entry: "%STARTUP%\start-dsh.vbs"
) else (
  echo [SKIP] No autostart entry in the Startup folder.
)

rem 2) Desktop shortcuts (any name starting with DshWeb / DeepSeek Harness,
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
)

echo.
echo dsh-launcher autostart entry and desktop shortcuts removed.
pause
