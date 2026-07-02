#Requires -Version 5.1
<#
.SYNOPSIS
    Publishes all Release BIManageRevit configurations (R21-R27).

.DESCRIPTION
    Restores for all TFM families (net48, net8.0-windows, net10.0-windows),
    then publishes every Release configuration. Publish (not build) is required
    because the installer reads from the "publish" folder.

    Logs all output to Build-All.log in the script's directory.

.PARAMETER ConfigFilter
    Optional wildcard filter, e.g. "*R25*" or "Release R2[4-6]".
    Defaults to all 7 Release configurations.

.EXAMPLE
    .\Build-All.ps1
    .\Build-All.ps1 -ConfigFilter "*R26*"
#>
param(
    [string]$ConfigFilter = "*"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

# Start logging to file in script's directory
$logPath = Join-Path $PSScriptRoot "Build-All.log"
Start-Transcript -Path $logPath -Force | Out-Null

try {
    # Script is at Tools\BuildAll\, project file is 2 levels up
    $projectFile = Join-Path $PSScriptRoot "..\..\BIManageRevit.csproj"
    $projectFile = [System.IO.Path]::GetFullPath($projectFile)

    if (-not (Test-Path $projectFile)) {
        Write-Host "ERROR: Project file not found: $projectFile" -ForegroundColor Red
        exit 1
    }

    $allConfigs = @(
        "Release R21", "Release R22", "Release R23", "Release R24",
        "Release R25", "Release R26", "Release R27"
    )

    # Apply filter (force array with @() so .Count works under StrictMode)
    $configs = @($allConfigs | Where-Object { $_ -like $ConfigFilter })

    if ($configs.Count -eq 0) {
        Write-Host "No configurations matched the filter." -ForegroundColor Yellow
        exit 0
    }

    Write-Host ""
    Write-Host "=== BIManageRevit Release Publish ===" -ForegroundColor Cyan
    Write-Host "Time    : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
    Write-Host "Project : $projectFile"
    Write-Host "Log     : $logPath"
    Write-Host "Configs : $($configs.Count) selected"
    Write-Host ""

    # Step 1: Restore for each TFM family
    $needsNet48 = @($configs | Where-Object { $_ -match 'R2[1-4]' }).Count -gt 0
    $needsNet8  = @($configs | Where-Object { $_ -match 'R2[5-6]' }).Count -gt 0
    $needsNet10 = @($configs | Where-Object { $_ -match 'R27' }).Count -gt 0

    if ($needsNet48) {
        Write-Host "[ RESTORE ] net48 (via Release R24)..." -ForegroundColor Cyan
        & dotnet restore $projectFile -p:Configuration="Release R24" --verbosity minimal
        if ($LASTEXITCODE -ne 0) {
            Write-Host "RESTORE FAILED for net48 (exit $LASTEXITCODE)" -ForegroundColor Red
            exit 1
        }
        Write-Host "[ RESTORE ] net48 OK" -ForegroundColor Green
    }

    if ($needsNet8) {
        Write-Host "[ RESTORE ] net8.0-windows (via Release R25)..." -ForegroundColor Cyan
        & dotnet restore $projectFile -p:Configuration="Release R25" --verbosity minimal
        if ($LASTEXITCODE -ne 0) {
            Write-Host "RESTORE FAILED for net8.0-windows (exit $LASTEXITCODE)" -ForegroundColor Red
            exit 1
        }
        Write-Host "[ RESTORE ] net8.0-windows OK" -ForegroundColor Green
    }

    if ($needsNet10) {
        Write-Host "[ RESTORE ] net10.0-windows (via Release R27)..." -ForegroundColor Cyan
        & dotnet restore $projectFile -p:Configuration="Release R27" --verbosity minimal
        if ($LASTEXITCODE -ne 0) {
            Write-Host "RESTORE FAILED for net10.0-windows (exit $LASTEXITCODE)" -ForegroundColor Red
            exit 1
        }
        Write-Host "[ RESTORE ] net10.0-windows OK" -ForegroundColor Green
    }

    Write-Host ""

    # Step 2: Publish each configuration
    $results = [System.Collections.Generic.List[PSObject]]::new()

    foreach ($cfg in $configs) {
        Write-Host "[ PUBLISH ] $cfg..." -ForegroundColor Cyan

        $publishArgs = @(
            "publish", $projectFile,
            "--no-restore",
            "--configuration", $cfg,
            "--verbosity", "minimal"
        )

        $output = & dotnet @publishArgs 2>&1
        $exitCode = $LASTEXITCODE

        $status = if ($exitCode -eq 0) { "OK" } else { "FAIL" }

        $errors = @()
        if ($exitCode -ne 0) {
            $errors = @($output | Where-Object { $_ -match ": error " })
            # Print failure output so it lands in the log
            Write-Host "---- Output ----"
            $output | ForEach-Object { Write-Host $_ }
            Write-Host "---- End Output ----"
        }

        $results.Add([PSCustomObject]@{ Configuration = $cfg; Status = $status; ExitCode = $exitCode; Errors = $errors })

        if ($exitCode -eq 0) {
            Write-Host ("[ PUBLISH ] {0} - OK" -f $cfg) -ForegroundColor Green
        } else {
            Write-Host ("[ PUBLISH ] {0} - FAILED (exit {1})" -f $cfg, $exitCode) -ForegroundColor Red
        }
    }

    # Summary
    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Cyan
    Write-Host "                   PUBLISH REPORT                               " -ForegroundColor Cyan
    Write-Host "================================================================" -ForegroundColor Cyan

    $passed = @($results | Where-Object { $_.Status -eq "OK" }).Count
    $failed = @($results | Where-Object { $_.Status -eq "FAIL" }).Count
    $failedResults = @($results | Where-Object { $_.Status -eq "FAIL" })

    Write-Host ""
    Write-Host ("  SUCCEEDED ({0}/{1}):" -f $passed, $results.Count) -ForegroundColor Green
    foreach ($r in ($results | Where-Object { $_.Status -eq "OK" })) {
        Write-Host ("    [+] {0}" -f $r.Configuration) -ForegroundColor Green
    }

    if ($failed -gt 0) {
        Write-Host ""
        Write-Host ("  FAILED ({0}/{1}):" -f $failed, $results.Count) -ForegroundColor Red
        foreach ($r in $failedResults) {
            Write-Host ("    [x] {0}" -f $r.Configuration) -ForegroundColor Red
            if (@($r.Errors).Count -gt 0) {
                $uniqueErrors = $r.Errors | Sort-Object -Unique
                foreach ($err in $uniqueErrors) {
                    Write-Host ("        {0}" -f $err) -ForegroundColor Yellow
                }
            }
        }
    }

    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Cyan

    if ($failed -gt 0) {
        Write-Host ("  Result: {0} FAILED, {1} passed" -f $failed, $passed) -ForegroundColor Red
    } else {
        Write-Host ("  Result: All {0} configurations published." -f $passed) -ForegroundColor Green
    }
    Write-Host "================================================================" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Finished : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
}
catch {
    Write-Host ""
    Write-Host "UNHANDLED EXCEPTION:" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    Write-Host $_.ScriptStackTrace -ForegroundColor Yellow
    $failed = 1
}
finally {
    Stop-Transcript | Out-Null
}

# Pause for 60 minutes or until keypress. Use Read-Host wrapped in a job
# so window stays open even when launched via "powershell -File script.ps1".
Write-Host ""
Write-Host "Log saved: $logPath" -ForegroundColor Cyan
Write-Host "Press any key to close, or window closes automatically in 20 seconds..." -ForegroundColor Yellow

$endTime = (Get-Date).AddSeconds(20)
while ((Get-Date) -lt $endTime) {
    if ([Console]::KeyAvailable) {
        $null = [Console]::ReadKey($true)
        break
    }
    Start-Sleep -Milliseconds 500
}

if ($failed -gt 0) { exit 1 } else { exit 0 }
