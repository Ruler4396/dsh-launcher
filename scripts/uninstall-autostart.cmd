@echo off
schtasks /Delete /TN "DeepSeekHarness" /F
del "%USERPROFILE%\Desktop\DeepSeek Harness.lnk"
echo Autostart task and desktop shortcut removed.
pause
