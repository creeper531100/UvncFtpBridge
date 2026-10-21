@echo off
"%~dp0UvncFtpBridge.exe" --open --file-tunnel
if errorlevel 1 pause
