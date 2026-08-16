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
  rem v0.3.0 unified log: DSH_HOME\dsh-launcher\dsh.log (default ~/.dsh\dsh-launcher\dsh.log)
  if defined DSH_HOME (echo Log: %DSH_HOME%\dsh-launcher\dsh.log) else (echo Log: %USERPROFILE%\.dsh\dsh-launcher\dsh.log)
  pause
)
