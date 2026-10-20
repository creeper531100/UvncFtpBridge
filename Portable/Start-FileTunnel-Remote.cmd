@echo off
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-FileTunnel-Remote.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-FileTunnel-Remote.ps1" "%~1"
)
if errorlevel 1 pause
