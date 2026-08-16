@echo off
rem dsh-launcher prereq check. Pure cmd, no .NET dependency.
rem Checks .NET Desktop Runtime 10 / WebView2 Runtime / Node.js 18+.
rem Missing items print guidance + download links. All ok -> run DshWeb.exe.
rem Entry point when DshWeb.exe double-click seems to do nothing.

setlocal EnableDelayedExpansion
set "FAILED="

echo [dsh-launcher] environment check ...

rem 1) .NET Desktop Runtime 10
rem    dir|findstr matches 10.x subdirs (if exist on trailing dot gets normalized by Windows)
set "DOTNET_OK="
if defined DOTNET_ROOT dir /b "%DOTNET_ROOT%\shared\Microsoft.WindowsDesktop.App" 2>nul | findstr /r "^10\." >nul && set "DOTNET_OK=1"
if not defined DOTNET_OK dir /b "%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App" 2>nul | findstr /r "^10\." >nul && set "DOTNET_OK=1"
if not defined DOTNET_OK (
  echo [MISSING] .NET Desktop Runtime 10
  echo           DshWeb.exe cannot start without it - double-click does nothing or shows an error dialog.
  echo           Install: winget install Microsoft.DotNet.DesktopRuntime.10
  echo           Or download: https://dotnet.microsoft.com/download/dotnet/10.0
  set "FAILED=1"
) else (
  echo [OK]   .NET Desktop Runtime 10
)

rem 2) WebView2 Runtime - Evergreen registry pv value
reg query "HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" /v pv >nul 2>&1
if errorlevel 1 reg query "HKLM\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" /v pv >nul 2>&1
if errorlevel 1 (
  echo [MISSING] WebView2 Runtime - Edge WebView2
  echo           Usually preinstalled on Win10/11; DshWeb.exe tries auto-install when missing.
  echo           Manual: https://developer.microsoft.com/microsoft-edge/webview2/
  set "FAILED=1"
) else (
  echo [OK]   WebView2 Runtime
)

rem 3) Node.js 18+ - executable on PATH
set "NODE_OK="
for /f "tokens=1 delims=." %%M in ('node --version 2^>nul') do (
  set "VER=%%M"
  set "VER=!VER:v=!"
  if !VER! GEQ 18 set "NODE_OK=1"
)
if not defined NODE_OK (
  echo [MISSING] Node.js 18+
  echo           Required by the dsh service; DshWeb.exe will offer a portable Node download.
  echo           Manual: https://nodejs.org/
  set "FAILED=1"
) else (
  echo [OK]   Node.js 18+
)

echo.
if defined FAILED (
  echo Some prerequisites are missing. Install them per the guidance above, then run DshWeb.exe again.
  echo Still stuck after installing? Run  DshWeb.exe --diagnose  to export a diagnostic package.
  exit /b 1
)
echo All prerequisites satisfied. Run DshWeb.exe now.
exit /b 0