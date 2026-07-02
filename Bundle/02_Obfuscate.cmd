@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem ============================================================
rem  02_Obfuscate.cmd  (BIManageRevit)
rem  Run Obfuscar against each per-version config under
rem  BIManageRevit.org\ObfuscarR<NN>.xml. Each config performs
rem  STRING HIDING ONLY (HideStrings=true, every Rename* toggle
rem  false) on BIManageRevit.dll + BIManage.Addons.dll and
rem  writes output to BIManageRevit.org\Confused\<YYYY>\.
rem
rem  Pivoted from ConfuserEx to Obfuscar on 2026-06-03 because
rem  ConfuserEx last shipped in 2020 (unmaintained). Obfuscar 3.0
rem  betas (1, 16) crashed on this codebase - 2.2.50 stable
rem  (Mono.Cecil-based) handles it.
rem  The previous ConfuserEx-driven setup is preserved at
rem  Bundle\_backup_confuserex_2026-06-03\ for rollback.
rem
rem  Prerequisite:
rem    dotnet tool install --global Obfuscar.GlobalTool --version 2.2.50
rem  After install, obfuscar.console resolves from PATH via
rem  %USERPROFILE%\.dotnet\tools\.
rem
rem  Usage:
rem    02_Obfuscate.cmd            -> all versions (21 22 23 24 25 26 27)
rem    02_Obfuscate.cmd 21 22 23 24 -> only the listed versions
rem  The net8/net10 family (R25-R27) needs Revit 2025-2027 + the .NET 6/7
rem  runtimes installed to resolve its reference closure. On a machine that
rem  only has the net48 toolchain (Revit 2024), run the net48 subset:
rem    02_Obfuscate.cmd 21 22 23 24
rem ============================================================

set "SCRIPT_DIR=%~dp0"
set "ORG_DIR=%SCRIPT_DIR%BIManageRevit.org"
set "LOG_DIR=%SCRIPT_DIR%Logs"

rem Version list: take it from the command-line args, else default to all 7.
set "VERSIONS=%*"
if not defined VERSIONS set "VERSIONS=21 22 23 24 25 26 27"

rem Anchor the working directory to this script's folder so the relative
rem InPath / OutPath / Module paths in ObfuscarR<NN>.xml resolve correctly
rem regardless of where the script is launched from.
cd /d "%SCRIPT_DIR%"

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"

where obfuscar.console >nul 2>&1
if errorlevel 1 (
    echo obfuscar.console not found on PATH.
    echo Install with:
    echo   dotnet tool install --global Obfuscar.GlobalTool --version 2.2.50
    echo Then re-open the shell so %%USERPROFILE%%\.dotnet\tools is on PATH.
    exit /b 1
)

for %%V in (%VERSIONS%) do (
    set "YEAR=20%%V"
    set "CFG=%ORG_DIR%\ObfuscarR%%V.xml"

    if not exist "!CFG!" (
        echo Missing config: !CFG!
        exit /b 1
    )

    echo.
    echo === Obfuscating Revit !YEAR! ===
    obfuscar.console "!CFG!"
    if errorlevel 1 (
        echo OBFUSCATION FAILED for R%%V
        exit /b 1
    )
)

echo.
echo Obfuscate complete (versions: %VERSIONS%): !date! !time! >> "%LOG_DIR%\BundleUpdateLog.txt"
echo.
echo === Obfuscated versions: %VERSIONS% ===
echo Output: %ORG_DIR%\Confused\^<year^>\
echo Next: run 03_BundleUpdate.cmd
endlocal
pause
