@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem ============================================================
rem  05_Deploy.cmd  (BIManageRevit)
rem  Local-test install. Mirrors the install-mode logic in
rem  Tools\Installer\ZeManageInstaller.iss (GetAddinsDir):
rem
rem    Per-user (default):   %AppData%\Autodesk\Revit\Addins\<year>\
rem    All-users (admin):    %ProgramData%\Autodesk\Revit\Addins\<year>\
rem
rem  Usage:
rem    05_Deploy.cmd                -> per-user install (no admin needed)
rem    05_Deploy.cmd user           -> same as above (explicit)
rem    05_Deploy.cmd allusers       -> machine-wide install (requires admin)
rem    05_Deploy.cmd both           -> install to BOTH locations (requires admin)
rem
rem  The signed installer produced by 06_Installer.cmd is the official
rem  ship vehicle - 05 here is a developer smoke-test only.
rem
rem  Versions missing from bin\Release R<NN>\publish\ are skipped silently
rem  so partial builds still deploy what they have.
rem ============================================================

set "SCRIPT_DIR=%~dp0"
set "PROJECT_ROOT=%SCRIPT_DIR%.."
set "USER_ADDINS_ROOT=%AppData%\Autodesk\Revit\Addins"
set "COMMON_ADDINS_ROOT=%ProgramData%\Autodesk\Revit\Addins"

rem ---- Parse mode argument ----
set "MODE=%~1"
if /i "%MODE%"=="" set "MODE=user"
if /i "%MODE%"=="user"     goto :parsed
if /i "%MODE%"=="allusers" goto :parsed
if /i "%MODE%"=="both"     goto :parsed
echo Unknown mode: %MODE%
echo Usage: 05_Deploy.cmd [user^|allusers^|both]
exit /b 1

:parsed
echo Deployment mode: %MODE%

rem ---- Admin check when targeting ProgramData ----
if /i "%MODE%"=="allusers" goto :needadmin
if /i "%MODE%"=="both"     goto :needadmin
goto :skipadmin

:needadmin
net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo This mode writes to %ProgramData%\Autodesk\Revit\Addins\ which requires admin.
    echo Right-click 05_Deploy.cmd and "Run as administrator", or use:
    echo   05_Deploy.cmd user      ^(per-user only, no admin needed^)
    exit /b 1
)

:skipadmin

set "ANY_DEPLOYED=0"

for %%V in (21 22 23 24 25 26 27) do (
    set "YEAR=20%%V"
    set "PUB_ROOT=%PROJECT_ROOT%\bin\Release R%%V\publish\Revit !YEAR! Release R%%V addin"
    set "PUB_MANIFEST=!PUB_ROOT!\BIManageRevit.addin"
    set "PUB_DLL_DIR=!PUB_ROOT!\BIManageRevit"

    if not exist "!PUB_MANIFEST!" (
        echo --- Revit !YEAR!: no publish output, skipping
    ) else (
        echo.
        echo --- Revit !YEAR! ---
        if /i "%MODE%"=="user"     call :install_to "!PUB_MANIFEST!" "!PUB_DLL_DIR!" "!USER_ADDINS_ROOT!\!YEAR!"   "per-user (AppData)"   || exit /b 1
        if /i "%MODE%"=="allusers" call :install_to "!PUB_MANIFEST!" "!PUB_DLL_DIR!" "!COMMON_ADDINS_ROOT!\!YEAR!" "all-users (ProgramData)" || exit /b 1
        if /i "%MODE%"=="both" (
            call :install_to "!PUB_MANIFEST!" "!PUB_DLL_DIR!" "!USER_ADDINS_ROOT!\!YEAR!"   "per-user (AppData)"      || exit /b 1
            call :install_to "!PUB_MANIFEST!" "!PUB_DLL_DIR!" "!COMMON_ADDINS_ROOT!\!YEAR!" "all-users (ProgramData)" || exit /b 1
        )
        set "ANY_DEPLOYED=1"
    )
)

if "!ANY_DEPLOYED!"=="0" (
    echo.
    echo Nothing to deploy. Run 01_OrgUpdate.cmd (or 00_BuildAll.cmd^) first.
    endlocal
    pause
    exit /b 1
)

echo.
echo === Deployment complete (mode: %MODE%) ===
echo Restart Revit to load the addin.
endlocal
pause
exit /b 0

rem ============================================================
rem  :install_to  <manifestSrc>  <payloadDirSrc>  <destDir>  <label>
rem  Cleans the BIManageRevit subfolder under destDir, copies the
rem  .addin to destDir, and xcopies the payload into destDir\BIManageRevit\.
rem ============================================================
:install_to
set "MANIFEST_SRC=%~1"
set "PAYLOAD_SRC=%~2"
set "DEST=%~3"
set "LABEL=%~4"
set "DEST_DLL_DIR=%DEST%\BIManageRevit"

echo   [%LABEL%] -^> %DEST%
if not exist "%DEST%" mkdir "%DEST%" 2>nul
if errorlevel 1 (echo FAILED to create %DEST% & exit /b 1)

if exist "%DEST_DLL_DIR%" (
    echo   Removing existing install at %DEST_DLL_DIR% ...
    rmdir /S /Q "%DEST_DLL_DIR%"
    if errorlevel 1 (echo FAILED to remove existing %LABEL% install & exit /b 1)
)

echo   Copying BIManageRevit.addin ...
copy /Y "%MANIFEST_SRC%" "%DEST%\" >nul
if errorlevel 1 (echo COPY FAILED: BIManageRevit.addin & exit /b 1)

echo   Copying BIManageRevit\ payload ...
xcopy "%PAYLOAD_SRC%" "%DEST_DLL_DIR%\" /E /I /Y /Q >nul
if errorlevel 1 (echo XCOPY FAILED: payload & exit /b 1)
exit /b 0
