@echo off
rem Manual foreground start of dsh service (debug use; normally use dsh-web.cmd)
title DeepSeek Harness Server
where dsh >nul 2>&1
if not errorlevel 1 (
  dsh web --host 127.0.0.1 --port 3080
) else (
  echo [INFO] 'dsh' not found on PATH - trying npx -y @deepseek-ai/dsh ...
  npx -y @deepseek-ai/dsh web --host 127.0.0.1 --port 3080
)
if errorlevel 1 (
  echo.
  echo [ERROR] dsh failed to start. Install globally with: npm install -g @deepseek-ai/dsh
  echo Log: %USERPROFILE%\.dsh-web.log
  pause
)
