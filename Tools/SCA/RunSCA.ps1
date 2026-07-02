param(
    [string]$ProjectDir = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$OutputDir = ""
)

if (-not $OutputDir) { $OutputDir = Join-Path $ProjectDir "Tools\SCA" }
if (!(Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }
$OutputFile = Join-Path $OutputDir "sca-report.json"

Write-Host "=== SCA (Software Composition Analysis) ===" -ForegroundColor Cyan
Write-Host "Project: $ProjectDir"
Write-Host ""

# Change to project directory
Push-Location $ProjectDir

# Explicit project file — directory has both .csproj and .slnx (MSBUILD error MSB1011).
$projectFile = Join-Path $ProjectDir "BIManageRevit.csproj"

# Ensure packages are restored
& dotnet restore $projectFile --force --verbosity quiet 2>$null

# Detect whether --format json is supported (SDK 8.0.200+)
$supportsJson = $false
try {
    $helpText = & dotnet list package --help 2>&1 | Out-String
    if ($helpText -match '--format') { $supportsJson = $true }
} catch {}

if ($supportsJson) {
    Write-Host "  SDK supports --format json" -ForegroundColor DarkGray
} else {
    Write-Host "  SDK does not support --format json - using text parsing" -ForegroundColor DarkYellow
}

# Helper: run dotnet command with timeout (seconds), return output or $null on timeout
function Invoke-DotnetWithTimeout {
    param([string[]]$Arguments, [int]$TimeoutSec = 60)
    $job = Start-Job -ScriptBlock {
        param($args) & dotnet $args 2>$null
    } -ArgumentList (,$Arguments)
    $completed = $job | Wait-Job -Timeout $TimeoutSec
    if ($completed) { $output = Receive-Job $job; Remove-Job $job; return $output }
    Stop-Job $job; Remove-Job $job
    Write-Host "  [TIMEOUT after ${TimeoutSec}s - skipped]" -ForegroundColor DarkYellow
    return $null
}

# Helper: parse JSON safely
function ConvertFrom-JsonSafe {
    param([object]$Input)
    if (-not $Input) { return $null }
    $text = if ($Input -is [array]) { $Input -join "`n" } else { "$Input" }
    $text = $text.Trim()
    if ($text -notmatch '^\s*[\{\[]') { return $null }
    try { return $text | ConvertFrom-Json } catch { return $null }
}

# Helper: count packages from text output (lines matching "> PackageName")
function Count-PackagesFromText {
    param([object]$Output)
    if (-not $Output) { return 0 }
    $lines = if ($Output -is [array]) { $Output } else { "$Output" -split "`n" }
    # dotnet list package text output shows packages as "> PackageName  CurrentVer  ..."
    $count = ($lines | Where-Object { $_ -match '^\s*>' }).Count
    return $count
}

$vulnObj = $null; $outdObj = $null; $depObj = $null
$vulnCount = 0; $outdCount = 0; $depCount = 0

# 1. Vulnerabilities
Write-Host "[1/3] Scanning for vulnerabilities..." -ForegroundColor Yellow
if ($supportsJson) {
    $vulnJson = Invoke-DotnetWithTimeout @('list', $projectFile, 'package', '--vulnerable', '--format', 'json') -TimeoutSec 60
    $vulnObj = ConvertFrom-JsonSafe $vulnJson
} else {
    $vulnText = Invoke-DotnetWithTimeout @('list', $projectFile, 'package', '--vulnerable') -TimeoutSec 60
    $vulnCount = Count-PackagesFromText $vulnText
}

# 2. Outdated
Write-Host "[2/3] Scanning for outdated packages..." -ForegroundColor Yellow
if ($supportsJson) {
    $outdJson = Invoke-DotnetWithTimeout @('list', $projectFile, 'package', '--outdated', '--format', 'json') -TimeoutSec 60
    $outdObj = ConvertFrom-JsonSafe $outdJson
} else {
    $outdText = Invoke-DotnetWithTimeout @('list', $projectFile, 'package', '--outdated') -TimeoutSec 60
    $outdCount = Count-PackagesFromText $outdText
}

# 3. Deprecated
Write-Host "[3/3] Scanning for deprecated packages..." -ForegroundColor Yellow
if ($supportsJson) {
    $depJson = Invoke-DotnetWithTimeout @('list', $projectFile, 'package', '--deprecated', '--format', 'json') -TimeoutSec 60
    $depObj = ConvertFrom-JsonSafe $depJson
} else {
    $depText = Invoke-DotnetWithTimeout @('list', $projectFile, 'package', '--deprecated') -TimeoutSec 60
    $depCount = Count-PackagesFromText $depText
}

# Count from JSON objects (if JSON mode was used)
if ($vulnObj -and $vulnObj.projects) {
    foreach ($proj in $vulnObj.projects) {
        if ($proj.frameworks) {
            foreach ($fw in $proj.frameworks) {
                if ($fw.topLevelPackages) { $vulnCount += $fw.topLevelPackages.Count }
                if ($fw.transitivePackages) { $vulnCount += $fw.transitivePackages.Count }
            }
        }
    }
}
if ($outdObj -and $outdObj.projects) {
    foreach ($proj in $outdObj.projects) {
        if ($proj.frameworks) {
            foreach ($fw in $proj.frameworks) {
                if ($fw.topLevelPackages) { $outdCount += $fw.topLevelPackages.Count }
            }
        }
    }
}
if ($depObj -and $depObj.projects) {
    foreach ($proj in $depObj.projects) {
        if ($proj.frameworks) {
            foreach ($fw in $proj.frameworks) {
                if ($fw.topLevelPackages) { $depCount += $fw.topLevelPackages.Count }
            }
        }
    }
}

# Single combined JSON report
$report = [ordered]@{
    scanDate    = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    buildUser   = $env:USERNAME
    project     = "BIManageRevit"
    tool        = "dotnet list package (.NET CLI)"
    toolVersion = (& dotnet --version 2>$null)
    jsonFormat  = $supportsJson
    summary     = [ordered]@{
        vulnerablePackages = $vulnCount
        outdatedPackages   = $outdCount
        deprecatedPackages = $depCount
    }
    vulnerabilities = if ($vulnObj) { $vulnObj } else { @{} }
    outdated        = if ($outdObj) { $outdObj } else { @{} }
    deprecated      = if ($depObj) { $depObj } else { @{} }
}

$report | ConvertTo-Json -Depth 20 | Set-Content $OutputFile -Encoding UTF8

# Summary
Write-Host ""
Write-Host "=== SCA Results ===" -ForegroundColor Cyan
Write-Host "  Build User:      $env:USERNAME"
if ($vulnCount -gt 0) { Write-Host "  Vulnerabilities: $vulnCount" -ForegroundColor Red }
else { Write-Host "  Vulnerabilities: 0" -ForegroundColor Green }
if ($outdCount -gt 0) { Write-Host "  Outdated:        $outdCount" -ForegroundColor Yellow }
else { Write-Host "  Outdated:        0" -ForegroundColor Green }
if ($depCount -gt 0) { Write-Host "  Deprecated:      $depCount" -ForegroundColor Yellow }
else { Write-Host "  Deprecated:      0" -ForegroundColor Green }
Write-Host ""
Write-Host "Report: $OutputFile" -ForegroundColor Cyan

Pop-Location
