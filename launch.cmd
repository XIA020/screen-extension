@echo off
title Tablet Display Launcher
"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0launch.ps1" -Desktop %*
set "result=%errorlevel%"
if not "%result%"=="0" pause
exit /b %result%

