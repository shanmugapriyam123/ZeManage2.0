@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem ============================================================
rem  00_BuildAll.cmd  (BIManageRevit)
rem  Runs the full sign-ready pipeline for R21..R27 in sequence:
rem    01_OrgUpdate    -> build + stage every Revit version
rem    02_Obfuscate    -> Dotfuscator (needs dotfuscator.exe on PATH;
rem                       run this script from the Dotfuscator
rem                       command-line shortcut, NOT Explorer)
rem    03_BundleUpdate -> copy obfuscated DLLs back into the per-
rem                       version publish folder so Inno picks them up
rem    04_SignTool     -> code-sign each per-year DLL
rem    06_Installer    -> compile + sign Tools\Installer\Output\
rem                       ZeManageSetup.exe (consumes the signed
rem                       publish output from step 04)
rem
rem  Stops on the first failing step. Each sub-script's "pause" is
rem  bypassed via "< nul" so the chain runs unattended.
rem
rem  05_Deploy is intentionally NOT included -- it overwrites
rem  %AppData%\Autodesk\Revit\Addins\<year>\BIManageRevit\ (per-user)
rem  or %ProgramData%\...\<year>\BIManageRevit\ (all-users mode), which
rem  is a local-test action, not a build action. Run it manually after
rem  a successful 00_BuildAll when you want to smoke-test:
rem    05_Deploy.cmd            (per-user, no admin)
rem    05_Deploy.cmd allusers   (machine-wide, admin)
rem    05_Deploy.cmd both       (both locations, admin)
rem ============================================================

set "SCRIPT_DIR=%~dp0"
set "LOG_DIR=%SCRIPT_DIR%Logs"
if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"

rem ---- Single confirmation gate for the whole signed pipeline ----
rem  Step 04 (DLLs) and step 06 (installer) both invoke signtool with the
rem  publisher cert. Confirm once here, then pass "auto" downstream so the
rem  per-step prompts don't re-ask. Run individual scripts directly if you
rem  want the per-step gate (each one prompts on its own when no auto arg).
echo.
echo This pipeline will code-sign:
echo   - BIManageRevit.dll and BIManage.Addons.dll for R21..R27 (step 04)
echo   - ZeManageSetup.exe                                       (step 06)
echo with the publisher cert. signtool.exe will be invoked twice.
set "ANSWER="
set /p "ANSWER=Proceed? [y/N]: "
if /i not "!ANSWER!"=="y" (
    echo Aborted by user.
    endlocal
    exit /b 1
)

set "T_START=%TIME%"
echo.
echo ============================================================
echo  BIManageRevit end-to-end build started: %DATE% %T_START%
echo ============================================================

call :run "01/05  Org update (build R21..R27 + stage)"      "01_OrgUpdate.cmd"   ""     || goto :failed
call :run "02/05  Dotfuscator (R21..R27)"                   "02_Obfuscate.cmd"   ""     || goto :failed
call :run "03/05  Bundle update (overlay obfuscated DLLs)"  "03_BundleUpdate.cmd" ""    || goto :failed
call :run "04/05  Code-sign (R21..R27)"                     "04_SignTool.cmd"    "auto" || goto :failed
call :run "05/05  Installer (compile + sign setup.exe)"     "06_Installer.cmd"   "auto" || goto :failed

echo.
echo ============================================================
echo  SUCCESS - signed publish output ready in bin\Release R*\publish\
echo            signed installer in Tools\Installer\Output\
echo  Started : %T_START%
echo  Ended   : %TIME%
echo ============================================================
echo BuildAll OK: %DATE% %TIME% >> "%LOG_DIR%\BundleUpdateLog.txt"
endlocal
pause
exit /b 0

:run
set "STEP_NAME=%~1"
set "STEP_SCRIPT=%~2"
set "STEP_ARG=%~3"
echo.
echo ------------------------------------------------------------
echo  Step %STEP_NAME%
echo ------------------------------------------------------------
if "%STEP_ARG%"=="" (
    call "%SCRIPT_DIR%%STEP_SCRIPT%" < nul
) else (
    call "%SCRIPT_DIR%%STEP_SCRIPT%" "%STEP_ARG%" < nul
)
exit /b %errorlevel%

:failed
echo.
echo ============================================================
echo  ABORT - pipeline stopped at step: %STEP_NAME%
echo  Started : %T_START%
echo  Failed  : %TIME%
echo ============================================================
echo BuildAll FAILED at %STEP_NAME%: %DATE% %TIME% >> "%LOG_DIR%\BundleUpdateLog.txt"
endlocal
pause
exit /b 1
