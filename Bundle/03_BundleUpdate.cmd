@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem ============================================================
rem  03_BundleUpdate.cmd  (BIManageRevit)
rem
rem  Two outputs, produced in one pass:
rem
rem  1) Overlay the obfuscated BIManageRevit.dll + BIManage.Addons.dll
rem     (produced by 02_Obfuscate.cmd / Obfuscar 2.2.50) back into each
rem     per-version publish folder:
rem
rem       bin\Release R<NN>\publish\Revit <YYYY> Release R<NN> addin\
rem         BIManageRevit\BIManageRevit.dll    <-- replaced
rem         BIManageRevit\BIManage.Addons.dll  <-- replaced
rem
rem     This keeps the Inno Setup compiler (06_Installer) reading from
rem     the standard publish path; no .iss changes are needed.
rem
rem  2) Assemble the Autodesk Application Plugin Manager bundle at
rem     Bundle\BIManageRevit.bundle\:
rem
rem       BIManageRevit.bundle\
rem         PackageContents.xml      <-- static template (source-controlled)
rem         Contents\
rem           2021\
rem             BIManageRevit.addin
rem             BIManageRevit\<all DLLs, deps, configs - from publish>
rem           2022\
rem             ...
rem           ...
rem           2027\
rem             ...
rem
rem     This folder is ready to zip / drop into
rem     %PROGRAMDATA%\Autodesk\ApplicationPlugins\ as an alternative install
rem     path to the Inno Setup installer.
rem
rem  Source of truth for the obfuscated DLLs is Bundle\BIManageRevit.org\
rem  Confused\<year>\ (Obfuscar's OutPath; switched from \Obfuscated on
rem  2026-06-03 when 02_Obfuscate pivoted from Dotfuscator to ConfuserEx,
rem  then to Obfuscar 2.2.50 on the same date when ConfuserEx's lack of
rem  maintenance and Obfuscar 3.0 beta's PE-reader / HideStrings bugs
rem  ruled them out for this codebase).
rem
rem  The unobfuscated build copies in publish\ are OVERWRITTEN in place.
rem  Re-run 01_OrgUpdate.cmd whenever you need to reset to a fresh build.
rem ============================================================

set "SCRIPT_DIR=%~dp0"
set "PROJECT_ROOT=%SCRIPT_DIR%.."
set "ORG_OBF=%SCRIPT_DIR%BIManageRevit.org\Confused"
set "BUNDLE_DIR=%SCRIPT_DIR%BIManageRevit.bundle"
set "BUNDLE_CONTENTS=%BUNDLE_DIR%\Contents"
set "PKG_XML=%BUNDLE_DIR%\PackageContents.xml"
set "LOG_DIR=%SCRIPT_DIR%Logs"

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"

if not exist "%PKG_XML%" (
    echo Missing bundle manifest: %PKG_XML%
    echo This template must be source-controlled at that path.
    exit /b 1
)

if not exist "%BUNDLE_CONTENTS%" mkdir "%BUNDLE_CONTENTS%"

for %%V in (21 22 23 24 25 26 27) do (
    set "YEAR=20%%V"
    set "OBF_CORE=%ORG_OBF%\!YEAR!\BIManageRevit.dll"
    set "OBF_ADDONS=%ORG_OBF%\!YEAR!\BIManage.Addons.dll"
    set "PUB_PARENT=%PROJECT_ROOT%\bin\Release R%%V\publish\Revit !YEAR! Release R%%V addin"
    set "PUB_DIR=!PUB_PARENT!\BIManageRevit"
    set "BUNDLE_YEAR=%BUNDLE_CONTENTS%\!YEAR!"
    set "BUNDLE_ADDIN_DIR=!BUNDLE_YEAR!\BIManageRevit"

    rem ---- Pre-flight ----
    if not exist "!OBF_CORE!" (
        echo Missing obfuscated DLL: !OBF_CORE!
        echo Run 02_Obfuscate.cmd first.
        exit /b 1
    )
    if not exist "!OBF_ADDONS!" (
        echo Missing obfuscated DLL: !OBF_ADDONS!
        echo Run 02_Obfuscate.cmd first.
        exit /b 1
    )
    if not exist "!PUB_DIR!\BIManageRevit.dll" (
        echo Missing publish target: !PUB_DIR!\BIManageRevit.dll
        echo Run 01_OrgUpdate.cmd first.
        exit /b 1
    )
    if not exist "!PUB_PARENT!\BIManageRevit.addin" (
        echo Missing .addin manifest: !PUB_PARENT!\BIManageRevit.addin
        echo Run 01_OrgUpdate.cmd first.
        exit /b 1
    )

    rem ---- 1. Overlay obfuscated DLLs into publish folder ----
    echo.
    echo === Overlaying obfuscated DLLs into Revit !YEAR! publish ===
    copy /Y "!OBF_CORE!"   "!PUB_DIR!\BIManageRevit.dll"   >nul
    if errorlevel 1 (echo COPY FAILED: obfuscated BIManageRevit.dll & exit /b 1)
    copy /Y "!OBF_ADDONS!" "!PUB_DIR!\BIManage.Addons.dll" >nul
    if errorlevel 1 (echo COPY FAILED: obfuscated BIManage.Addons.dll & exit /b 1)

    rem ---- 2. Assemble the .bundle's Contents\<year>\ from the
    rem        (now-obfuscated) publish folder. Clean any prior content
    rem        first so removed/renamed files don't linger across runs. ----
    echo === Assembling .bundle Contents\!YEAR! ===
    if exist "!BUNDLE_YEAR!" rmdir /s /q "!BUNDLE_YEAR!"
    mkdir "!BUNDLE_ADDIN_DIR!"
    xcopy "!PUB_DIR!\*" "!BUNDLE_ADDIN_DIR!\" /E /Y /Q /I >nul
    if errorlevel 1 (echo COPY FAILED: bundle Contents\!YEAR!\BIManageRevit\ & exit /b 1)
    copy /Y "!PUB_PARENT!\BIManageRevit.addin" "!BUNDLE_YEAR!\" >nul
    if errorlevel 1 (echo COPY FAILED: bundle Contents\!YEAR!\BIManageRevit.addin & exit /b 1)
)

echo.
echo BundleUpdate complete: !date! !time! >> "%LOG_DIR%\BundleUpdateLog.txt"
echo.
echo === Publish folders updated for R21..R27 ===
echo === Bundle assembled at %BUNDLE_DIR% ===
echo Next: run 04_SignTool.cmd to sign the DLLs.
endlocal
pause
