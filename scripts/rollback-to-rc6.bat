@echo off
echo.
echo ========================================
echo   dsh Update Test Environment Setup
echo   Rollback to rc.6 + Cleanup
echo ========================================
echo.

echo [1/4] Closing launcher...
taskkill /IM DshWeb.exe /F >nul 2>&1
if %errorlevel%==0 (
    echo    [OK] Closed
) else (
    echo    [SKIP] Not running
)
timeout /t 2 /nobreak >nul

echo.
echo [2/4] Cleaning update state...
set "DATADIR=%USERPROFILE%\.dsh\dsh-launcher"
set "CLEANUP_OK=1"

if exist "%DATADIR%\pending-update.json" (
    del "%DATADIR%\pending-update.json" 2>nul
    if exist "%DATADIR%\pending-update.json" (
        echo    [FAIL] Cannot delete pending-update.json
        set "CLEANUP_OK=0"
    ) else (
        echo    [OK] Deleted pending-update.json
    )
) else (
    echo    [SKIP] pending-update.json not found
)

if exist "%DATADIR%\staging" (
    rmdir /s /q "%DATADIR%\staging" 2>nul
    if exist "%DATADIR%\staging" (
        echo    [FAIL] Cannot delete staging
        set "CLEANUP_OK=0"
    ) else (
        echo    [OK] Deleted staging
    )
) else (
    echo    [SKIP] staging not found
)

if exist "%DATADIR%\runtimes" (
    rmdir /s /q "%DATADIR%\runtimes" 2>nul
    if exist "%DATADIR%\runtimes" (
        echo    [FAIL] Cannot delete runtimes
        set "CLEANUP_OK=0"
    ) else (
        echo    [OK] Deleted runtimes
    )
) else (
    echo    [SKIP] runtimes not found
)

if exist "%DATADIR%\skipped-update.json" (
    del "%DATADIR%\skipped-update.json" 2>nul
    if exist "%DATADIR%\skipped-update.json" (
        echo    [FAIL] Cannot delete skipped-update.json
        set "CLEANUP_OK=0"
    ) else (
        echo    [OK] Deleted skipped-update.json
    )
) else (
    echo    [SKIP] skipped-update.json not found
)

echo.
echo [3/4] Downgrading to dsh@0.1.0-rc.6...
for /f "tokens=*" %%i in ('cmd /c dsh --version 2^>nul') do set CURRENT_VER=%%i
echo    Current: %CURRENT_VER%

if "%CURRENT_VER%"=="0.1.0-rc.6" (
    echo    [OK] Already rc.6, skipping install
    goto :verify
)

echo    Installing rc.6 (1-2 min, please wait)...
call npm install -g @deepseek-ai/dsh@0.1.0-rc.6 --no-audit --no-fund --registry=https://registry.npmmirror.com >nul 2>&1
if %errorlevel% neq 0 (
    echo    [FAIL] npm install error %errorlevel%
    echo    Run manually: npm install -g @deepseek-ai/dsh@0.1.0-rc.6 --registry=https://registry.npmmirror.com
    goto :done
)

for /f "tokens=*" %%i in ('cmd /c dsh --version 2^>nul') do set NEW_VER=%%i
if "%NEW_VER%"=="0.1.0-rc.6" (
    echo    [OK] Downgraded to %NEW_VER%
) else (
    echo    [FAIL] Version mismatch: expected 0.1.0-rc.6, got %NEW_VER%
    goto :done
)

:verify
echo.
echo [4/4] Verifying...
for /f "tokens=*" %%i in ('cmd /c dsh --version 2^>nul') do echo    dsh version: %%i

if exist "%DATADIR%\pending-update.json" (
    echo    pending: [EXISTS] needs cleanup
    set "CLEANUP_OK=0"
) else (
    echo    pending: [NONE] OK
)

if exist "%DATADIR%\staging" (
    echo    staging: [EXISTS] needs cleanup
    set "CLEANUP_OK=0"
) else (
    echo    staging: [NONE] OK
)

if exist "%DATADIR%\runtimes" (
    echo    runtimes: [EXISTS] needs cleanup
    set "CLEANUP_OK=0"
) else (
    echo    runtimes: [NONE] OK
)

echo.
if "%CLEANUP_OK%"=="1" (
    echo ========================================
    echo   [SUCCESS] Test environment ready!
    echo ========================================
) else (
    echo ========================================
    echo   [WARNING] Some cleanup failed, check above
    echo ========================================
)

echo.
echo   Next: double-click start-launcher.bat
echo   NOTE: Do NOT launch DshWeb.exe from inside dsh
echo.

:done
pause
