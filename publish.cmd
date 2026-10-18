@echo off
rem 双击运行：用 Bypass 策略执行 publish.ps1，不改系统设置
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish.ps1" %*
echo.
pause
