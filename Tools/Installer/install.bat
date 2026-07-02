@echo off
setlocal

:: ==========================================================================
:: ZeManage Silent Installer
:: Usage: install.bat LICENSE_KEY [VERSIONS] [ADDON]
::   LICENSE_KEY  - Required. Your license key.
::   VERSIONS     - Optional. Comma-separated: R24,R25 (default: auto-detect)
::   ADDON        - Optional. Set to 1 to include addons (default: 0)
::
:: Examples:
::   install.bat a3d8d34b-f297-4736-b7f9-0d218b7d5285
::   install.bat a3d8d34b-f297-4736-b7f9-0d218b7d5285 R24,R25
::   install.bat a3d8d34b-f297-4736-b7f9-0d218b7d5285 R24,R25 1
:: ==========================================================================

set LICENSE=%~1
set VERSIONS=%~2
set ADDON=%~3

if "%LICENSE%"=="" (
    echo [ERROR] License key is required.
    echo Usage: install.bat LICENSE_KEY [VERSIONS] [ADDON]
    exit /b 1
)

set "INSTALLER=\\172.16.10.100\RND Central\02-Plugins\01-Revit\All Version\Optional\BIM\ZeManageSetup.exe"
:: Log filename with date-time stamp: zemanage_install_PC01_User_20260318_160035.log
for /f %%I in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set TIMESTAMP=%%I
set "LOGFILE=\\172.16.10.100\RND Central\07-Archives\01-Logs\04-InstallLog\zemanage_install_%COMPUTERNAME%_%USERNAME%_%TIMESTAMP%.log"

if not exist "%INSTALLER%" (
    echo [ERROR] ZeManageSetup.exe not found in %~dp0
    exit /b 1
)

:: Build command
set CMD="%INSTALLER%" /VERYSILENT /SUPPRESSMSGBOXES /LOG="%LOGFILE%" /LICENSE=%LICENSE%
if not "%VERSIONS%"=="" set CMD=%CMD% /VERSIONS=%VERSIONS%
if "%ADDON%"=="1" set CMD=%CMD% /ADDON=1

echo ============================================
echo   ZeManage Installer
echo ============================================
echo.
echo   License:  %LICENSE%
if not "%VERSIONS%"=="" (echo   Versions: %VERSIONS%) else (echo   Versions: auto-detect)
if "%ADDON%"=="1" (echo   Addons:   Yes) else (echo   Addons:   No)
echo.
echo [INFO] Installing ZeManage...

%CMD%
set EXITCODE=%ERRORLEVEL%

echo.
if %EXITCODE%==0 (
    echo [SUCCESS] ZeManage installed successfully.
) else if %EXITCODE%==1 (
    echo [ERROR] Setup failed to initialize.
) else if %EXITCODE%==2 (
    echo [ERROR] Installation was cancelled.
) else if %EXITCODE%==3 (
    echo [ERROR] Fatal error during preparation.
) else if %EXITCODE%==4 (
    echo [ERROR] Fatal error during installation.
) else if %EXITCODE%==5 (
    echo [ERROR] Installation was cancelled during file copy.
) else if %EXITCODE%==6 (
    echo [ERROR] Setup aborted - check license key or version selection.
) else (
    echo [ERROR] Installation failed with exit code %EXITCODE%.
)

echo.
echo   Log file: %LOGFILE%
exit /b %EXITCODE%
