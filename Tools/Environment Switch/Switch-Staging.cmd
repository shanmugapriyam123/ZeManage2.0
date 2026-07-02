@echo off
REM ============================================================
REM  Switch BIManageRevit configs to the STAGING API endpoint.
REM  Thin wrapper around Tools\SwitchEnvironment.ps1.
REM ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0SwitchEnvironment.ps1" staging
exit /b %ERRORLEVEL%
