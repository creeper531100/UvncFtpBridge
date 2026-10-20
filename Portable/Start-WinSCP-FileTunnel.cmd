@echo off
"%~dp0UvncFtpBridge.exe" winscp --file-tunnel
if errorlevel 1 pause
