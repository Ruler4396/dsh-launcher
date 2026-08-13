@echo off
rem Manual foreground start of dsh service (debug use; normally use dsh-web.cmd)
title DeepSeek Harness Server
dsh web --host 127.0.0.1 --port 3080
if errorlevel 1 (
  echo.
  echo [ERROR] dsh failed to start. If 'dsh' is not found, run: npm install -g @deepseek-ai/dsh
  echo Log: %USERPROFILE%\.dsh-web.log
  pause
)
