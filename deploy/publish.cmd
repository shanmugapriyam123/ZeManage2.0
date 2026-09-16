@echo off
setlocal
REM Build a self-contained, single-file Windows x64 distribution of the agent.
REM Output: deploy\dist\ZeManage.Agent.exe (plus its WPF resource files).
REM Mirrors D:\ZeManage 2.0\DesktopAgent\deploy\publish.cmd — same publish flags,
REM adjusted for this repo's flat layout (ZeManage.Agent\ at the repo root, no src\ prefix).

cd /d "%~dp0\.."

set OUTDIR=%~dp0dist
if exist "%OUTDIR%" rmdir /s /q "%OUTDIR%"

echo Restoring...
dotnet restore || goto :err

echo Publishing single-file self-contained win-x64 build...
dotnet publish ZeManage.Agent\ZeManage.Agent.csproj ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -o "%OUTDIR%" || goto :err

echo.
echo Done.
echo Output: %OUTDIR%
dir "%OUTDIR%\ZeManage.Agent.exe" 2>nul
exit /b 0

:err
echo.
echo *** Publish failed.
exit /b 1
