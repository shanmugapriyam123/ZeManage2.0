#Requires -Version 5.1
<#
.SYNOPSIS
    Pre-release deployment verification - runs the automated checks from PreReleaseChecklist.md.

.DESCRIPTION
    Inspects bin/Release Rxx/publish/ folders + currently deployed Revit Addins folders for the
    dependency-conflict tripwires that have repeatedly broken production deploys:

      - Manifest schema (sec 3): R21-R25 must NOT have ManifestSettings, R26+ MUST
        (R25's parser explicitly rejects ManifestSettings: "tag is incorrect, should be replaced with 'AddIn'")
      - System.Text.Json + Microsoft.Bcl.AsyncInterfaces version alignment (sec 4)
      - SQLite.Interop.dll deployed in correct paths (sec 5)
      - Newtonsoft.Json in lib/ subfolder, NOT flat (sec 6)
      - DUPLICATE LOAD DETECTED in latest log (sec 7 / sec 10)

    Exit code 0 = all checks pass. Non-zero = at least one check failed (review output).

.EXAMPLE
    .\Tools\Checklist\Verify-Deployment.ps1
    .\Tools\Checklist\Verify-Deployment.ps1 -Configuration "Release"

.NOTES
    Run from an OPEN PowerShell terminal (so the window stays). Double-clicking the
    .ps1 in Explorer will open and immediately close a console window — you'll see
    nothing. Either:
      - Open PowerShell, cd to the repo root, then: .\Tools\Checklist\Verify-Deployment.ps1
      - Or:  powershell -NoExit -ExecutionPolicy Bypass -File Tools\Checklist\Verify-Deployment.ps1
    The -NoExit flag keeps the window open after the script finishes so you can read
    the output even when launched from a shortcut.
#>
param(
    [string]$Configuration = "Release"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Continue"

$projectDir = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$failures = [System.Collections.Generic.List[string]]::new()
$warnings = [System.Collections.Generic.List[string]]::new()

function Write-Pass($msg)  { Write-Host "  [PASS] $msg" -ForegroundColor Green }
function Write-Fail($msg)  { Write-Host "  [FAIL] $msg" -ForegroundColor Red;    $failures.Add($msg) | Out-Null }
function Write-Warn($msg)  { Write-Host "  [WARN] $msg" -ForegroundColor Yellow; $warnings.Add($msg) | Out-Null }
function Write-Info($msg)  { Write-Host "  $msg" -ForegroundColor Gray }
function Write-Section($title) {
    Write-Host ""
    Write-Host "================================================================" -ForegroundColor Cyan
    Write-Host "  $title" -ForegroundColor Cyan
    Write-Host "================================================================" -ForegroundColor Cyan
}

# ----------------------------------------------------------------------
# Section 1. Version alignment between csproj and installer
# ----------------------------------------------------------------------
Write-Section "1. Version metadata"

$csproj   = Join-Path $projectDir "BIManageRevit.csproj"
$issFile  = Join-Path $projectDir "Tools\Installer\ZeManageInstaller.iss"

if (-not (Test-Path $csproj))  { Write-Fail "BIManageRevit.csproj not found"; exit 1 }
if (-not (Test-Path $issFile)) { Write-Fail "ZeManageInstaller.iss not found"; exit 1 }

$csprojContent = Get-Content $csproj -Raw
$issContent    = Get-Content $issFile -Raw

if ($csprojContent -match '<Version>([\d\.]+)</Version>')           { $version = $Matches[1] }       else { $version = $null }
if ($csprojContent -match '<AssemblyVersion>([\d\.]+)</AssemblyVersion>') { $asmVer = $Matches[1] } else { $asmVer = $null }
if ($issContent    -match 'MyAppVersion\s+"([\d\.]+)"')             { $issVer = $Matches[1] }        else { $issVer = $null }

Write-Info "Version=$version  AssemblyVersion=$asmVer  Installer.MyAppVersion=$issVer"

if ($asmVer -eq "1.0.0.0") { Write-Pass "AssemblyVersion pinned to 1.0.0.0 (BAML pack URI stability)" }
else                       { Write-Fail "AssemblyVersion is '$asmVer' - must be 1.0.0.0 (see section 1)" }

if ($version -and $issVer -and $version -eq $issVer) { Write-Pass "Installer version matches csproj ($version)" }
else                                                  { Write-Fail "Installer.MyAppVersion '$issVer' != csproj.Version '$version'" }

# ----------------------------------------------------------------------
# Section 2. ALC config - per-project policy
# ----------------------------------------------------------------------
# Updated 2026-04-30: BIManageRevit MUST have EnableDynamicLoading=true so the
# .NET SDK emits BIManageRevit.runtimeconfig.json next to the DLL. Revit 2025+
# requires that file to set up its add-in AssemblyLoadContext correctly. Without
# it Revit falls back to a fragmented load (DUPLICATE LOAD DETECTED in the log)
# and every IExternalCommand that observes Application.Instance == null toasts
# "Unable to access application services". This was verified empirically on
# Revit 2025 (BIManageRevit_20260430_1238.log).
#
# The cross-ALC dispatcher in BIManage/Common/Helpers/CrossAlcDispatch.cs handles
# the residual dual-ALC case on R25 (whose .addin schema cannot use
# <UseRevitContext>True</UseRevitContext> — that tag is R26+ only, see section 3),
# but the dispatcher only works if the live ALC is correctly set up — which
# requires runtimeconfig.json — which requires EnableDynamicLoading=true.
#
# BIManage.Addons is a peer DLL (loaded as a dependency, not an IExternalApplication
# entry point) so it does NOT need its own runtimeconfig.json — keep it false.
Write-Section "2. ALC / EnableDynamicLoading"

$addonsCsproj = Join-Path $projectDir "BIManage.Addons\BIManage.Addons.csproj"
$addonsContent = Get-Content $addonsCsproj -Raw

# Project-specific expected value
$alcExpected = @{ "BIManageRevit" = "true"; "BIManage.Addons" = "false" }

foreach ($pair in @(@{name="BIManageRevit"; content=$csprojContent}, @{name="BIManage.Addons"; content=$addonsContent})) {
    if ($pair.content -match '<EnableDynamicLoading>(true|false)</EnableDynamicLoading>') {
        $val = $Matches[1]
        $expected = $alcExpected[$pair.name]
        if ($val -eq $expected) {
            Write-Pass "$($pair.name): EnableDynamicLoading=$val (expected $expected)"
        } else {
            if ($pair.name -eq "BIManageRevit" -and $val -eq "false") {
                Write-Fail "$($pair.name): EnableDynamicLoading=false - MUST be true (Revit 2025 needs runtimeconfig.json; otherwise DUPLICATE LOAD DETECTED on R25)"
            } elseif ($pair.name -eq "BIManage.Addons" -and $val -eq "true") {
                Write-Fail "$($pair.name): EnableDynamicLoading=true - peer DLLs should keep it false (no runtimeconfig.json needed)"
            } else {
                Write-Fail "$($pair.name): EnableDynamicLoading=$val (expected $expected)"
            }
        }
    } else {
        Write-Warn "$($pair.name): EnableDynamicLoading not explicitly set (defaults to false)"
    }
}

# Verify the SDK actually produced runtimeconfig.json for BIManageRevit on R25/R26/R27
# (net8.0 / net10.0 builds). This is the artifact that Revit 2025+ reads to set up
# the per-add-in ALC. Missing => fragmented dual-ALC load.
foreach ($cfgSuffix in @("R25", "R26", "R27")) {
    $rcPath = Join-Path $projectDir "bin\$Configuration $cfgSuffix\BIManageRevit.runtimeconfig.json"
    if (Test-Path $rcPath) {
        Write-Pass "${cfgSuffix}: BIManageRevit.runtimeconfig.json present"
    } else {
        Write-Fail "${cfgSuffix}: BIManageRevit.runtimeconfig.json MISSING - EnableDynamicLoading flip didn't take, do a clean rebuild"
    }
}

# Verify the cross-ALC dispatcher source is present (defends against accidental delete).
# Without it, dual-ALC on R25 reverts to the "Unable to access application services" toast.
$dispatcher = Join-Path $projectDir "BIManage\Common\Helpers\CrossAlcDispatch.cs"
if (Test-Path $dispatcher) {
    Write-Pass "CrossAlcDispatch.cs present at BIManage\Common\Helpers"
} else {
    Write-Fail "CrossAlcDispatch.cs MISSING - cross-ALC IExternalCommand dispatch will fail on R25"
}

# Verify every IExternalCommand has the dispatch line at the top of its Execute.
# Pattern check: command file must reference CrossAlcDispatch.TryForward.
$cmdFiles = Get-ChildItem -Path (Join-Path $projectDir "BIManage\Commands\RibbonCommands") -Filter "*.cs"
foreach ($f in $cmdFiles) {
    $content = Get-Content $f.FullName -Raw
    if ($content -match ":\s*IExternalCommand\b") {
        if ($content -match "CrossAlcDispatch\.TryForward") {
            Write-Pass "$($f.Name): cross-ALC dispatch line present"
        } else {
            Write-Fail "$($f.Name): missing CrossAlcDispatch.TryForward in Execute - command will toast on R25 wrong-ALC dispatch"
        }
    }
}

# ----------------------------------------------------------------------
# Section 3. Manifest - R21-R25 stripped, R26+ has ManifestSettings
# ----------------------------------------------------------------------
Write-Section "3. .addin Manifest schema (per Revit version)"

$revitVersions = @{
    2021 = $false; 2022 = $false; 2023 = $false; 2024 = $false  # R21-R24: schema rejects <ManifestSettings> silently
    2025 = $false                                                # R25:     schema rejects <ManifestSettings> explicitly (journal error)
    2026 = $true;  2027 = $true                                  # R26+:    schema recognizes <ManifestSettings><UseRevitContext>
}

$bases = @(
    "$env:APPDATA\Autodesk\Revit\Addins"
    "$env:PROGRAMDATA\Autodesk\Revit\Addins"
)

# Detects the actual <ManifestSettings> XML element (not the bare word — the source
# .addin contains a long explanatory comment that mentions "ManifestSettings" several
# times, so a plain word match false-positives even when the element is correctly stripped).
# Strip XML comments first, then look for an opening element tag.
function Test-AddinHasManifestSettingsElement {
    param([string]$Path)
    $raw = Get-Content -Path $Path -Raw -ErrorAction SilentlyContinue
    if (-not $raw) { return $false }
    # Strip XML comments (<!-- ... --> across multiple lines)
    $stripped = [regex]::Replace($raw, '(?s)<!--.*?-->', '')
    # Match the opening element tag (with optional attributes / namespace prefix)
    return [regex]::IsMatch($stripped, '<\s*(\w+:)?ManifestSettings\b')
}

foreach ($v in ($revitVersions.Keys | Sort-Object)) {
    $expected = $revitVersions[$v]
    foreach ($base in $bases) {
        $addinPath = Join-Path $base "$v\BIManageRevit.addin"
        if (Test-Path $addinPath) {
            $hasMs = Test-AddinHasManifestSettingsElement -Path $addinPath
            $loc   = if ($base -like "*Roaming*") { "Roaming" } else { "ProgramData" }
            if ($hasMs -eq $expected) {
                Write-Pass "R$v ($loc): ManifestSettings=$hasMs (expected $expected)"
            } else {
                Write-Fail "R$v ($loc): ManifestSettings=$hasMs but expected $expected - re-run build (PreserveManifestSettings target)"
            }
        }
    }

    # Also check publish folder
    $pubPath = Join-Path $projectDir "bin\$Configuration R$($v-2000)\publish\Revit $v $Configuration R$($v-2000) addin\BIManageRevit.addin"
    if (Test-Path $pubPath) {
        $hasMs = Test-AddinHasManifestSettingsElement -Path $pubPath
        if ($hasMs -eq $expected) {
            Write-Pass "R$v (publish): ManifestSettings=$hasMs"
        } else {
            Write-Fail "R$v (publish): ManifestSettings=$hasMs but expected $expected"
        }
    }
}

# ----------------------------------------------------------------------
# Section 4. NuGet dependency consistency for net48
# ----------------------------------------------------------------------
Write-Section "4. net48 dependency versions (System.Text.Json + Bcl.AsyncInterfaces)"

foreach ($cfgSuffix in @("R21", "R22", "R23", "R24")) {
    $cfgDir = Join-Path $projectDir "bin\$Configuration $cfgSuffix"
    if (-not (Test-Path $cfgDir)) {
        Write-Warn "${cfgSuffix}: bin folder missing - run Build-All first"
        continue
    }

    foreach ($dll in @("System.Text.Json.dll", "Microsoft.Bcl.AsyncInterfaces.dll", "System.Runtime.CompilerServices.Unsafe.dll")) {
        $path = Join-Path $cfgDir $dll
        if (Test-Path $path) {
            try {
                $asm = [Reflection.Assembly]::LoadFile($path)
                $ver = $asm.GetName().Version.ToString()
                # Expected: S.T.J 8.x, Bcl 8.x, Unsafe 6.x
                $expected = switch ($dll) {
                    "System.Text.Json.dll"                            { "8.*" }
                    "Microsoft.Bcl.AsyncInterfaces.dll"               { "8.*" }
                    "System.Runtime.CompilerServices.Unsafe.dll"      { "6.*" }
                }
                $major = $ver.Split('.')[0]
                $expectedMajor = $expected.Split('.')[0]
                if ($major -eq $expectedMajor) {
                    Write-Pass "$cfgSuffix $dll = $ver"
                } else {
                    Write-Fail "$cfgSuffix $dll = $ver but expected major ${expectedMajor}.x"
                }
            } catch {
                Write-Warn "${cfgSuffix} ${dll} could not read version ($($_.Exception.Message))"
            }
        } else {
            Write-Fail "$cfgSuffix $dll MISSING"
        }
    }
}

# ----------------------------------------------------------------------
# Section 5. SQLite.Interop.dll deployed in correct subfolders
# ----------------------------------------------------------------------
Write-Section "5. SQLite.Interop.dll native deployment"

foreach ($cfgSuffix in @("R21", "R24", "R25", "R26", "R27")) {
    $pubAddinDir = Join-Path $projectDir "bin\$Configuration $cfgSuffix\publish\Revit 20$(($cfgSuffix -replace 'R',''))_$Configuration $cfgSuffix addin\BIManageRevit"
    # Build the actual path correctly
    $year = 2000 + [int]($cfgSuffix.Substring(1))
    $pubAddinDir = Join-Path $projectDir "bin\$Configuration $cfgSuffix\publish\Revit $year $Configuration $cfgSuffix addin\BIManageRevit"

    if (-not (Test-Path $pubAddinDir)) {
        Write-Warn "${cfgSuffix}: publish folder missing"
        continue
    }

    $isNet48 = $cfgSuffix -in @("R21","R22","R23","R24")
    if ($isNet48) {
        # net48: x64/x86 subfolders are the load-time probe path (System.Data.SQLite.Core NetFramework)
        $required = @("SQLite.Interop.dll", "x64\SQLite.Interop.dll", "x86\SQLite.Interop.dll")
        foreach ($rel in $required) {
            $full = Join-Path $pubAddinDir $rel
            if (Test-Path $full) { Write-Pass "$cfgSuffix has $rel" }
            else                  { Write-Fail "$cfgSuffix missing $rel - runtime will throw Unable to find entry point SI hash" }
        }
    } else {
        # net8/net10: runtimes/win-x64/native/ is the canonical path; flat copy is optional/defensive
        $required = @("runtimes\win-x64\native\SQLite.Interop.dll")
        foreach ($rel in $required) {
            $full = Join-Path $pubAddinDir $rel
            if (Test-Path $full) { Write-Pass "$cfgSuffix has $rel" }
            else                  { Write-Fail "$cfgSuffix missing $rel - SQLite native loader will fail" }
        }
        $flat = Join-Path $pubAddinDir "SQLite.Interop.dll"
        if (Test-Path $flat) { Write-Pass "$cfgSuffix also has flat SQLite.Interop.dll (defensive copy)" }
        else                 { Write-Warn "$cfgSuffix missing flat SQLite.Interop.dll (optional defensive copy)" }
    }
}

# ----------------------------------------------------------------------
# Section 6. Newtonsoft.Json in lib/ subfolder ONLY
# ----------------------------------------------------------------------
Write-Section "6. Newtonsoft.Json isolation (must be in lib/, NOT flat)"

foreach ($cfgSuffix in @("R21", "R24", "R25", "R27")) {
    $year = 2000 + [int]($cfgSuffix.Substring(1))
    $pubAddinDir = Join-Path $projectDir "bin\$Configuration $cfgSuffix\publish\Revit $year $Configuration $cfgSuffix addin\BIManageRevit"

    if (-not (Test-Path $pubAddinDir)) { continue }

    $flatPath = Join-Path $pubAddinDir "Newtonsoft.Json.dll"
    $libPath  = Join-Path $pubAddinDir "lib\Newtonsoft.Json.dll"

    $hasFlat = Test-Path $flatPath
    $hasLib  = Test-Path $libPath

    if ($hasFlat) { Write-Fail "$cfgSuffix has FLAT Newtonsoft.Json.dll - will conflict with Revit copy (Cloud Collaborate breaks)" }
    if ($hasLib)  { Write-Pass "$cfgSuffix has lib\Newtonsoft.Json.dll (correct isolation)" }
    elseif (-not $hasFlat) { Write-Warn "$cfgSuffix missing Newtonsoft.Json entirely - Addons APS features will fail" }
}

# Source-code check: main project should not use Newtonsoft
$mainNewtonRef = Get-ChildItem -Path (Join-Path $projectDir "BIManage") -Filter "*.cs" -Recurse |
    Select-String -Pattern "using Newtonsoft" -List

if ($mainNewtonRef) {
    Write-Fail "Main project uses Newtonsoft.Json (must use System.Text.Json only):"
    $mainNewtonRef | ForEach-Object { Write-Info "  $($_.Path)" }
} else {
    Write-Pass "Main project uses System.Text.Json exclusively"
}

# ----------------------------------------------------------------------
# Section 7. DUPLICATE LOAD check in latest runtime log (informational)
# ----------------------------------------------------------------------
Write-Section "7. Latest runtime log - DUPLICATE LOAD detection"

$logDir = Join-Path $env:LOCALAPPDATA "BIManageRevit\Logs"
if (Test-Path $logDir) {
    $latestLog = Get-ChildItem -Path $logDir -Filter "BIManageRevit_*.log" |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($latestLog) {
        Write-Info "Inspecting: $($latestLog.Name) ($($latestLog.LastWriteTime))"
        $content = Get-Content $latestLog.FullName -Raw -ErrorAction SilentlyContinue

        # DUPLICATE LOAD on R25 is INHERENT to Revit 2025's add-in loader: the schema
        # rejects <UseRevitContext>True</UseRevitContext> so we cannot tell Revit to use
        # a single ALC. CrossAlcDispatch handles the duplicate at runtime. So the warning
        # is informational on R25 and a real problem only on R26/R27 (where UseRevitContext
        # IS supported). Detect Revit major version from the log path / content.
        if ($content -match "DUPLICATE LOAD DETECTED") {
            $r25 = $latestLog.Name -match "_2025_" -or $content -match 'Revit\s+2025|Addins\\2025\\'
            $r26plus = $content -match 'Addins\\(2026|2027)\\'
            if ($r26plus) {
                Write-Fail "DUPLICATE LOAD DETECTED on R26/R27 - check ManifestSettings <UseRevitContext>True</UseRevitContext> is in deployed .addin"
            } elseif ($r25) {
                Write-Pass "DUPLICATE LOAD DETECTED on R25 (expected - handled by CrossAlcDispatch)"
            } else {
                Write-Warn "DUPLICATE LOAD DETECTED - confirm CrossAlcDispatch is dispatching commands correctly"
            }
        } else {
            Write-Pass "No DUPLICATE LOAD DETECTED in latest log"
        }

        # Specifically scan for the toast that should be EXTINCT after the dispatcher fix.
        if ($content -match "Unable to access application services|Services not available") {
            Write-Fail "'Unable to access application services' toast appeared - CrossAlcDispatch failed to forward to live ALC"
        } else {
            Write-Pass "No 'Unable to access application services' toasts in latest log"
        }

        if ($content -match "TypeLoadException") {
            Write-Fail "TypeLoadException in latest log - likely System.Text.Json/Bcl.AsyncInterfaces mismatch"
            $content -split "`n" | Where-Object { $_ -match "TypeLoadException|does not have an implementation" } |
                Select-Object -First 3 | ForEach-Object { Write-Info "    $_" }
        }

        if ($content -match "Unable to find entry point") {
            Write-Fail "SQLite native entry-point error - SQLite.Interop.dll missing or wrong arch"
        }

        if ($content -match "does not have a resource identified by the URI") {
            Write-Fail "WPF pack URI lookup failed - duplicate ALC load broke BAML resolution"
        }
    } else {
        Write-Info "No log files found yet - run Revit at least once to generate one"
    }
} else {
    Write-Info "Log directory missing (Revit has not run with the addin yet)"
}

# ----------------------------------------------------------------------
# Summary
# ----------------------------------------------------------------------
Write-Section "SUMMARY"

if ($failures.Count -eq 0) {
    Write-Host "  All automated checks passed." -ForegroundColor Green
    if ($warnings.Count -gt 0) {
        Write-Host "  $($warnings.Count) warning(s) - review output above." -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "  Next:" -ForegroundColor Cyan
    Write-Host "    1. Compile installer:  iscc.exe Tools\Installer\ZeManageInstaller.iss"
    Write-Host "    2. Smoke-test on a clean machine using the manual checks in section 10 of"
    Write-Host "       Tools\Checklist\PreReleaseChecklist.md"
    exit 0
} else {
    Write-Host "  $($failures.Count) check(s) FAILED:" -ForegroundColor Red
    foreach ($f in $failures) { Write-Host "    - $f" -ForegroundColor Red }
    Write-Host ""
    Write-Host "  Refer to Tools\Checklist\PreReleaseChecklist.md for fix anchors." -ForegroundColor Yellow
    exit 1
}
