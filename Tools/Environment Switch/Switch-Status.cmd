@echo off
REM ============================================================
REM  Show which environment each config file is currently set to.
REM  Read-only — does not modify anything.
REM ============================================================
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0SwitchEnvironment.ps1" -Status
exit /b %ERRORLEVEL%
