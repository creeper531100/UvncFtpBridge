@echo off
"%~dp0UvncFtpBridge.exe" --remote %*
if errorlevel 1 pause
