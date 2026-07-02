@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem ============================================================
rem  04_SignTool.cmd  (BIManageRevit)
rem
rem  Source of truth for signed payload is Bundle\BIManageRevit.bundle\
rem  (assembled by 03_BundleUpdate.cmd). Per year, this script:
rem
rem    1. signs BIManageRevit.dll, BIManageRevit.Shim.dll, and
rem       BIManage.Addons.dll (when present) inside
rem         Bundle\BIManageRevit.bundle\Contents\<year>\BIManageRevit\
rem    2. copies the now-signed DLLs back into the corresponding
rem         bin\Release R<NN>\publish\Revit <YYYY> Release R<NN> addin\
rem           BIManageRevit\
rem       so the Inno Setup installer (06_Installer) ships the same
rem       signed bytes the Application Plugin Manager bundle does.
rem
rem  Why all three DLLs?
rem    - BIManageRevit.dll      : the obfuscated Core
rem    - BIManage.Addons.dll    : the obfuscated Addons (conditional)
rem    - BIManageRevit.Shim.dll : the manifest-scanner entry point on
rem                               Revit 2025+. Revit's loader inspects
rem                               the Shim signature first; if it's
rem                               unsigned the whole addin can be
rem                               blocked depending on Revit's
rem                               trust-store policy.
rem
rem  Prereqs: 03_BundleUpdate.cmd has run. The cert must be installed
rem  in CurrentUser\My (or LocalMachine\My) with its private key.
rem ============================================================

set "SCRIPT_DIR=%~dp0"
set "PROJECT_ROOT=%SCRIPT_DIR%.."
set "BUNDLE_CONTENTS=%SCRIPT_DIR%BIManageRevit.bundle\Contents"
set "THUMBPRINT=f032806d46fdd36b5152ffbfdbb7cd31b89c97db"
set "TIMESTAMP_URL=http://timestamp.digicert.com"

rem ---- Confirmation gate (skipped when invoked from 00_BuildAll with "auto") ----
set "AUTO_CONFIRM=%~1"
if /i "%AUTO_CONFIRM%"=="auto" goto :skip_confirm
if /i "%AUTO_CONFIRM%"=="-y"   goto :skip_confirm
if /i "%AUTO_CONFIRM%"=="--yes" goto :skip_confirm

echo.
echo You are about to code-sign R21..R27 DLLs with thumbprint:
echo   %THUMBPRINT%
echo Signs the bundle copies first, then mirrors them to publish.
set "ANSWER="
set /p "ANSWER=Proceed with signing? [y/N]: "
if /i not "!ANSWER!"=="y" (
    echo Aborted by user.
    exit /b 1
)
:skip_confirm

rem Resolve signtool.exe (pick the x64 build from the highest installed
rem Windows 10/11 SDK). where /r returns matches in arbitrary order, so
rem we filter to \x64\ and keep the lexicographically last one (newest
rem SDK version).
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

for %%V in (21 22 23 24 25 26 27) do (
    set "YEAR=20%%V"
    set "BUNDLE_ADDIN_DIR=%BUNDLE_CONTENTS%\!YEAR!\BIManageRevit"
    set "PUB_DIR=%PROJECT_ROOT%\bin\Release R%%V\publish\Revit !YEAR! Release R%%V addin\BIManageRevit"

    set "BUNDLE_CORE=!BUNDLE_ADDIN_DIR!\BIManageRevit.dll"
    set "BUNDLE_SHIM=!BUNDLE_ADDIN_DIR!\BIManageRevit.Shim.dll"
    set "BUNDLE_ADDONS=!BUNDLE_ADDIN_DIR!\BIManage.Addons.dll"

    set "PUB_CORE=!PUB_DIR!\BIManageRevit.dll"
    set "PUB_SHIM=!PUB_DIR!\BIManageRevit.Shim.dll"
    set "PUB_ADDONS=!PUB_DIR!\BIManage.Addons.dll"

    rem ---- Pre-flight: bundle is the source of truth, must exist. ----
    if not exist "!BUNDLE_CORE!" (
        echo Missing bundle DLL: !BUNDLE_CORE!
        echo Run 03_BundleUpdate.cmd first.
        exit /b 1
    )
    if not exist "!BUNDLE_SHIM!" (
        echo Missing bundle DLL: !BUNDLE_SHIM!
        echo Run 01_OrgUpdate.cmd and 03_BundleUpdate.cmd first.
        exit /b 1
    )
    if not exist "!PUB_DIR!" (
        echo Missing publish folder: !PUB_DIR!
        echo Run 01_OrgUpdate.cmd first.
        exit /b 1
    )

    rem ---- 1. Sign the bundle copies (source of truth).
    rem        Parens INSIDE an echo inside a for-block are not safe in cmd:
    rem        the parser treats the first unescaped `)` as the end of the
    rem        for body. Using square brackets [bundle] sidesteps that
    rem        entirely (brackets aren't parser-special inside parens groups). ----
    echo.
    echo === Signing Revit !YEAR! [bundle] ===
    "%SIGNTOOL%" sign /sha1 %THUMBPRINT% /tr %TIMESTAMP_URL% /td sha256 /fd sha256 "!BUNDLE_CORE!"
    if errorlevel 1 (
        echo SIGNING FAILED for R%%V [bundle BIManageRevit.dll]
        exit /b 1
    )

    "%SIGNTOOL%" sign /sha1 %THUMBPRINT% /tr %TIMESTAMP_URL% /td sha256 /fd sha256 "!BUNDLE_SHIM!"
    if errorlevel 1 (
        echo SIGNING FAILED for R%%V [bundle BIManageRevit.Shim.dll]
        exit /b 1
    )

    if exist "!BUNDLE_ADDONS!" (
        "%SIGNTOOL%" sign /sha1 %THUMBPRINT% /tr %TIMESTAMP_URL% /td sha256 /fd sha256 "!BUNDLE_ADDONS!"
        if errorlevel 1 (
            echo SIGNING FAILED for R%%V [bundle BIManage.Addons.dll]
            exit /b 1
        )
    )

    rem ---- 2. Mirror signed DLLs into the publish folder so Inno Setup
    rem        installer ships the same signed bytes as the .bundle. ----
    echo === Mirroring signed DLLs into Revit !YEAR! publish ===
    copy /Y "!BUNDLE_CORE!" "!PUB_CORE!" >nul
    if errorlevel 1 (echo COPY FAILED: signed BIManageRevit.dll to publish & exit /b 1)

    copy /Y "!BUNDLE_SHIM!" "!PUB_SHIM!" >nul
    if errorlevel 1 (echo COPY FAILED: signed BIManageRevit.Shim.dll to publish & exit /b 1)

    if exist "!BUNDLE_ADDONS!" (
        copy /Y "!BUNDLE_ADDONS!" "!PUB_ADDONS!" >nul
        if errorlevel 1 (echo COPY FAILED: signed BIManage.Addons.dll to publish & exit /b 1)
    )
)

echo.
echo === All R21..R27 DLLs signed in bundle and mirrored to publish ===
endlocal
pause
