# PowerShell script to compile and run manual persistence tests
# Bypasses xUnit test project build issues

param(
    [string]$Configuration = "Debug R25"
)

$ErrorActionPreference = "Stop"

Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  BIManageRevit - Manual Persistence Test Runner" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

$projectRoot = Split-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) -Parent
$testFile = Join-Path $PSScriptRoot "ManualPersistenceTests.cs"
$binPath = Join-Path $projectRoot "bin\$Configuration"
$outputExe = Join-Path $PSScriptRoot "ManualPersistenceTests.exe"

Write-Host "[1/3] Checking dependencies..." -ForegroundColor Yellow

if (-not (Test-Path $binPath)) {
    Write-Host "Error: Build output not found at: $binPath" -ForegroundColor Red
    Write-Host "Please build the main project first:" -ForegroundColor Yellow
    Write-Host "  Open solution in Visual Studio and build '$Configuration' configuration" -ForegroundColor Cyan
    exit 1
}

$dllPath = Join-Path $binPath "BIManageRevit.dll"
if (-not (Test-Path $dllPath)) {
    Write-Host "Error: BIManageRevit.dll not found at: $dllPath" -ForegroundColor Red
    exit 1
}

Write-Host "  ✓ Found BIManageRevit.dll" -ForegroundColor Green
Write-Host "  ✓ Found test file" -ForegroundColor Green

Write-Host ""
Write-Host "[2/3] Compiling manual test harness..." -ForegroundColor Yellow

# Find csc.exe
$cscPath = & "C:\Program Files\Microsoft Visual Studio\2022\Community\Common7\Tools\vswhere.exe" `
    -latest -requires Microsoft.Component.MSBuild `
    -find "MSBuild\**\Bin\Roslyn\csc.exe" 2>$null | Select-Object -First 1

if (-not $cscPath) {
    # Try .NET SDK compiler
    $cscPath = "C:\Program Files\dotnet\sdk\10.0.102\Roslyn\bincore\csc.dll"
    if (Test-Path $cscPath) {
        Write-Host "  Using .NET SDK compiler" -ForegroundColor Gray
        $compileCmd = "dotnet `"$cscPath`" /target:exe /out:`"$outputExe`" /reference:`"$dllPath`" /reference:`"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.0\System.Runtime.dll`" /reference:`"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.0\System.Console.dll`" /reference:`"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.0\System.Linq.dll`" /reference:`"C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.0\System.Collections.dll`" /reference:`"$binPath\System.Data.SQLite.dll`" `"$testFile`""
    } else {
        Write-Host "Error: Could not find C# compiler" -ForegroundColor Red
        Write-Host "Attempting alternative approach with dotnet..." -ForegroundColor Yellow

        # Alternative: Use dotnet to run the test file directly
        Write-Host ""
        Write-Host "[Alternative] Running test via dotnet script..." -ForegroundColor Yellow
        Write-Host "Note: This requires creating a temporary .csproj" -ForegroundColor Gray

        # Create temp directory
        $tempDir = Join-Path $env:TEMP "BiManageTestHarness_$(Get-Random)"
        New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

        # Copy test file
        Copy-Item $testFile (Join-Path $tempDir "Program.cs")

        # Create minimal .csproj
        $csprojContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <Reference Include="BIManageRevit">
      <HintPath>$dllPath</HintPath>
    </Reference>
    <PackageReference Include="System.Data.SQLite" Version="1.0.118" />
  </ItemGroup>
</Project>
"@
        $csprojContent | Out-File (Join-Path $tempDir "TestHarness.csproj") -Encoding UTF8

        Write-Host "  Created temporary project at: $tempDir" -ForegroundColor Gray

        # Run the test
        Write-Host ""
        Write-Host "[3/3] Running tests..." -ForegroundColor Yellow
        Write-Host ""

        Push-Location $tempDir
        try {
            dotnet run 2>&1
            $exitCode = $LASTEXITCODE

            Pop-Location
            Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue

            exit $exitCode
        }
        catch {
            Pop-Location
            Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
            Write-Host "Error running tests: $_" -ForegroundColor Red
            exit 1
        }
    }
}

Write-Host "  ✓ Compilation successful" -ForegroundColor Green

Write-Host ""
Write-Host "[3/3] Running tests..." -ForegroundColor Yellow
Write-Host ""

# Run the compiled test harness
& $outputExe
$exitCode = $LASTEXITCODE

# Cleanup
Remove-Item $outputExe -ErrorAction SilentlyContinue

exit $exitCode
