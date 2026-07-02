@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem ============================================================
rem  06_Installer.cmd  (BIManageRevit)
rem  Compile the Inno Setup installer (ZeManageInstaller.iss),
rem  then code-sign the produced ZeManageSetup.exe using the
rem  same publisher cert that 04_SignTool used for the DLLs.
rem
rem  Inputs : Bundle\BIManageRevit.bundle\Contents\<YYYY>\BIManageRevit\...
rem           (signed, obfuscated payload from 01..04 - the .iss reads
rem           directly from the bundle since 2026-06-04; the bundle is
rem           the canonical signed source-of-truth and 04_SignTool mirrors
rem           it back to publish for any downstream consumer that still
rem           reads publish.)
rem  Output : Tools\Installer\Output\ZeManageSetup.exe (signed)
rem
rem  Requires: Inno Setup 6 (ISCC.exe) installed in the default
rem  location, and the code-signing cert from 04_SignTool present
rem  in CurrentUser\My.
rem ============================================================

set "SCRIPT_DIR=%~dp0"
set "PROJECT_ROOT=%SCRIPT_DIR%.."
set "INSTALLER_DIR=%PROJECT_ROOT%\Tools\Installer"
set "ISS=%INSTALLER_DIR%\ZeManageInstaller.iss"
set "OUTPUT_DIR=%INSTALLER_DIR%\Output"

rem ---- "auto" arg skips the pre-sign confirmation (used by 00_BuildAll) ----
set "AUTO_CONFIRM=%~1"

rem OutputBaseFilename in the .iss is "ZeManageSetup" with no version
rem suffix, so the produced file is just ZeManageSetup.exe regardless
rem of MyAppVersion. If the .iss is changed to include the version,
rem update SETUP_EXE below to match.
set "SETUP_EXE=%OUTPUT_DIR%\ZeManageSetup.exe"

rem Signing parameters (must mirror 04_SignTool.cmd - if they diverge the
rem setup.exe ends up signed with a different cert than the DLLs inside,
rem which trips audits and confuses customers reading the publisher chain).
set "THUMBPRINT=f032806d46fdd36b5152ffbfdbb7cd31b89c97db"
set "TIMESTAMP_URL=http://timestamp.digicert.com"

rem --- Sanity: must have at least one signed BUNDLE payload to wrap.
rem The .iss now sources from Bundle\BIManageRevit.bundle\Contents\<year>\
rem (canonical signed payload assembled by 03 + signed by 04). It loops
rem over R21..R27 and emits files only for years where BIManageRevit.dll
rem exists in the bundle, so a partial build still produces a usable
rem installer covering fewer Revit years.
rem We pick R26 as the canonical existence check since it's the current
rem primary target (.NET 8 codepath used in the Mar 31 ALC fixes), with
rem R25 as a fallback for the same .NET 8 family.
set "SANITY_DLL=%SCRIPT_DIR%BIManageRevit.bundle\Contents\2026\BIManageRevit\BIManageRevit.dll"
if not exist "%SANITY_DLL%" (
    set "SANITY_DLL=%SCRIPT_DIR%BIManageRevit.bundle\Contents\2025\BIManageRevit\BIManageRevit.dll"
)
if not exist "%SANITY_DLL%" (
    echo No signed bundle payload found for R25 or R26.
    echo Run 03_BundleUpdate.cmd then 04_SignTool.cmd first ^(or the full 00_BuildAll pipeline^).
    exit /b 1
)

rem --- Locate ISCC.exe ---
set "ISCC="
for %%P in (
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
    "C:\Program Files\Inno Setup 6\ISCC.exe"
) do (
    if exist %%~P set "ISCC=%%~P"
)
if not defined ISCC (
    echo ISCC.exe not found.
    echo Install Inno Setup 6 from https://jrsoftware.org/isdl.php
    exit /b 1
)
echo Using ISCC : %ISCC%

rem --- Locate signtool.exe (same logic as 04_SignTool.cmd) ---
set "SIGNTOOL="
for /f "delims=" %%S in ('where /r "C:\Program Files (x86)\Windows Kits\10\bin" signtool.exe 2^>nul ^| findstr /i "\\x64\\signtool.exe" ^| sort') do (
    set "SIGNTOOL=%%S"
)
if not defined SIGNTOOL (
    echo signtool.exe not found under "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\".
    echo Install the Windows 10/11 SDK or hard-set SIGNTOOL in this script.
    exit /b 1
)
echo Using signtool: %SIGNTOOL%

echo Compiling  : %ISS%
echo.

"%ISCC%" "%ISS%"
if errorlevel 1 (
    echo INSTALLER COMPILATION FAILED
    exit /b 1
)

if not exist "%SETUP_EXE%" (
    echo Expected output not produced: %SETUP_EXE%
    exit /b 1
)

echo.
echo Compiled installer ready to sign:
echo   %SETUP_EXE%
echo Signing cert thumbprint: %THUMBPRINT%

if /i "%AUTO_CONFIRM%"=="auto" goto :skip_sign_confirm
if /i "%AUTO_CONFIRM%"=="-y"   goto :skip_sign_confirm
if /i "%AUTO_CONFIRM%"=="--yes" goto :skip_sign_confirm

set "ANSWER="
set /p "ANSWER=Proceed with signing the installer? [y/N]: "
if /i not "!ANSWER!"=="y" (
    echo Aborted by user. Installer is compiled but NOT signed: %SETUP_EXE%
    exit /b 1
)
:skip_sign_confirm

echo.
echo === Signing installer ===
"%SIGNTOOL%" sign /sha1 %THUMBPRINT% /tr %TIMESTAMP_URL% /td sha256 /fd sha256 "%SETUP_EXE%"
if errorlevel 1 (
    echo SIGNING FAILED for installer
    exit /b 1
)

echo.
echo === Installer compiled and signed successfully ===
echo Output: %SETUP_EXE%
endlocal
pause
