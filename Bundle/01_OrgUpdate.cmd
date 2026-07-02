@echo off
setlocal EnableExtensions EnableDelayedExpansion

rem ============================================================
rem  01_OrgUpdate.cmd  (BIManageRevit)
rem  Build Release R21..R27 and stage the main outputs into
rem  BIManageRevit.org\Contents\<year>\ for the Dotfuscator
rem  configs to consume.
rem ============================================================

set "SCRIPT_DIR=%~dp0"
set "PROJECT_ROOT=%SCRIPT_DIR%.."
set "PROJECT=%PROJECT_ROOT%\BIManageRevit.csproj"
set "ORG_ROOT=%SCRIPT_DIR%BIManageRevit.org\Contents"
set "LOG_DIR=%SCRIPT_DIR%Logs"

if not exist "%LOG_DIR%" mkdir "%LOG_DIR%"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo dotnet CLI not found on PATH. Install the .NET SDK.
    exit /b 1
)

for %%V in (21 22 23 24 25 26 27) do (
    set "VER=%%V"
    set "YEAR=20%%V"

    echo.
    echo === Building Release R%%V ^(Revit !YEAR!^) ===
    rem Wipe obj\project.assets.json so the implicit restore inside dotnet build
    rem cannot reuse a stale TFM from the previous iteration. NuGet's
    rem "Assets file has not changed" optimisation otherwise leaves the file
    rem pinned to whatever TFM family ran last (net48 vs net8.0-windows vs
    rem net10.0-windows), tripping NETSDK1005 here. Wiping forces a clean
    rem restore for the current Configuration with no extra package downloads
    rem (packages stay cached under %UserProfile%\.nuget\packages).
    if exist "%PROJECT_ROOT%\obj\project.assets.json" del /q "%PROJECT_ROOT%\obj\project.assets.json"
    if exist "%PROJECT_ROOT%\obj\project.nuget.cache" del /q "%PROJECT_ROOT%\obj\project.nuget.cache"
    rem PublishAddinFiles=false keeps the staging publish folder but avoids
    rem fighting Revit if it happens to be open with the addin loaded.
    dotnet build "%PROJECT%" -c "Release R%%V" -p:PublishAddinFiles=false -p:CopyLocalLockFileAssemblies=true --nologo
    if errorlevel 1 (
        echo BUILD FAILED for Release R%%V
        exit /b 1
    )

    rem ============================================================
    rem  Trim the Release publish folder before staging + installer
    rem  picks it up. Three categories:
    rem
    rem  1. Roslyn satellite locale packs (cs/de/es/.../zh-Hant) -
    rem     52 .resources.dll across 13 folders, ~6.8 MB. None reach
    rem     the user: Roslyn errors are surfaced via ex.Message which
    rem     is always English. <SatelliteResourceLanguages>en</...>
    rem     in BIManageRevit.csproj is supposed to handle this, but
    rem     Nice3point.Revit.Build.Tasks' PublishRevitAddinFiles
    rem     target bypasses it.
    rem
    rem  2. Linux + macOS native SQLite.Interop on .NET 8+ builds -
    rem     ~5 MB under runtimes/{linux-x64,osx-x64}/native/. They
    rem     can never load in Revit (Windows-only host).
    rem
    rem  3. JetBrains.Annotations.dll - build-time annotations
    rem     (NotNull, CanBeNull). Never invoked at runtime.
    rem ============================================================
    set "PUB_TRIM=%PROJECT_ROOT%\bin\Release R%%V\publish\Revit !YEAR! Release R%%V addin\BIManageRevit"
    if exist "!PUB_TRIM!" (
        for %%L in (cs de es fr it ja ko pl pt-BR ru tr zh-Hans zh-Hant) do (
            if exist "!PUB_TRIM!\%%L" rmdir /s /q "!PUB_TRIM!\%%L"
        )
        if exist "!PUB_TRIM!\runtimes\linux-x64" rmdir /s /q "!PUB_TRIM!\runtimes\linux-x64"
        if exist "!PUB_TRIM!\runtimes\osx-x64"   rmdir /s /q "!PUB_TRIM!\runtimes\osx-x64"
        if exist "!PUB_TRIM!\JetBrains.Annotations.dll" del /q "!PUB_TRIM!\JetBrains.Annotations.dll"
    )

    set "PUB_DIR=%PROJECT_ROOT%\bin\Release R%%V\publish\Revit !YEAR! Release R%%V addin\BIManageRevit"
    set "DEST=%ORG_ROOT%\!YEAR!"

    if not exist "!PUB_DIR!\BIManageRevit.dll" (
        echo Expected publish output missing: !PUB_DIR!\BIManageRevit.dll
        echo Check the csproj Publish target and Release R%%V configuration.
        exit /b 1
    )

    if not exist "!DEST!" mkdir "!DEST!"

    echo === Staging Revit !YEAR! ===
    rem Stage the FULL publish closure (BIManageRevit.dll + BIManage.Addons.dll
    rem + every NuGet/runtime dep + BIManageRevit.deps.json on net8/net10).
    rem The dep set differs per target framework so we cannot just commit a
    rem static one. Dotfuscator also needs the dep DLLs co-located with the
    rem input assembly to resolve references.
    xcopy "!PUB_DIR!\*" "!DEST!\" /E /Y /Q /I >nul
    if errorlevel 1 (echo COPY FAILED: deps from publish & exit /b 1)
    rem BIManageRevit.addin lives one level UP from the per-assembly subfolder
    rem (Nice3point.Revit.Build.Tasks 2.* layout). 3.* keeps it next to the
    rem DLL - drop the leading ..\ from the next line if the package is bumped.
    copy /Y "!PUB_DIR!\..\BIManageRevit.addin" "!DEST!\" >nul
    if errorlevel 1 (echo COPY FAILED: BIManageRevit.addin & exit /b 1)
)

echo.
echo OrgUpdate complete: !date! !time! >> "%LOG_DIR%\BundleUpdateLog.txt"
echo.
echo === All R21..R27 built and staged ===
echo Next: run 02_Obfuscate.cmd (or open the configs in Dotfuscator GUI).
endlocal
pause
