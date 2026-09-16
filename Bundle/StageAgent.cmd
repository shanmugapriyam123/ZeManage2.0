@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem ============================================================
rem  StageAgent.cmd  (BIManageRevit)
rem
rem  Publishes the ZeManage 2.0 DesktopAgent (which lives in a
rem  separate repo) and stages it into the installer payload at
rem    Bundle\BIManageRevit.bundle\Contents\Agent\
rem  so 04_SignTool can code-sign ZeManage.Agent.exe and
rem  06_Installer can ship it in the same ZeManageSetup.exe as the
rem  Revit add-in.
rem
rem  The DesktopAgent repo defaults to C:\Users\Admin\Documents\GitHub\ZeManage2.0
rem  (the actively-developed/deployed copy — D:\ZeManage 2.0\DesktopAgent is a
rem  stale older snapshot, confirmed 2026-08-07).
rem  Override by setting AGENT_REPO before calling this script.
rem ============================================================

set "SCRIPT_DIR=%~dp0"
set "STAGE_DIR=%SCRIPT_DIR%BIManageRevit.bundle\Contents\Agent"

if not defined AGENT_REPO set "AGENT_REPO=C:\Users\Admin\Documents\GitHub\ZeManage2.0"

if not exist "%AGENT_REPO%\deploy\publish.cmd" (
    echo Agent repo not found at "%AGENT_REPO%".
    echo Set AGENT_REPO to the DesktopAgent repo root and retry.
    exit /b 1
)

echo Publishing DesktopAgent from "%AGENT_REPO%"...
call "%AGENT_REPO%\deploy\publish.cmd"
if errorlevel 1 (
    echo *** Agent publish failed.
    exit /b 1
)

set "AGENT_DIST=%AGENT_REPO%\deploy\dist"
if not exist "%AGENT_DIST%\ZeManage.Agent.exe" (
    echo Published agent exe not found at "%AGENT_DIST%\ZeManage.Agent.exe".
    exit /b 1
)

echo Staging agent into "%STAGE_DIR%"...
if exist "%STAGE_DIR%" rmdir /s /q "%STAGE_DIR%"
mkdir "%STAGE_DIR%"

rem Copy the published output (single-file exe plus any WPF runtime files),
rem excluding debug symbols. robocopy exit codes 0-7 are success; >=8 is failure.
robocopy "%AGENT_DIST%" "%STAGE_DIR%" /E /XF *.pdb >nul
if %errorlevel% geq 8 (
    echo *** Staging copy failed (robocopy %errorlevel%).
    exit /b 1
)

echo Agent staged: "%STAGE_DIR%\ZeManage.Agent.exe"
endlocal
exit /b 0
