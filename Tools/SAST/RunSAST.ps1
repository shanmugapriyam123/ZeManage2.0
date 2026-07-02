param(
    [string]$ProjectDir = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$Configuration = "Release R25",
    [string]$OutputDir = ""
)

if (-not $OutputDir) { $OutputDir = Join-Path $ProjectDir "Tools\SAST" }
if (!(Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }
$OutputFile = Join-Path $OutputDir "sast-report.json"
$SarifFile = Join-Path $OutputDir "sast-results.sarif"

Write-Host "=== SAST (Static Application Security Testing) ===" -ForegroundColor Cyan
Write-Host "Project: $ProjectDir"
Write-Host "Configuration: $Configuration"
Write-Host ""

Push-Location $ProjectDir

# Step 1: Build with all analyzers enabled, capture warnings as SARIF
Write-Host "[1/3] Building with security analyzers..." -ForegroundColor Yellow

$buildLog = Join-Path $OutputDir "build-output.log"

# Build with analyzers, treat security warnings as warnings (not errors), output to log.
# Explicit project file required — directory has both .csproj and .slnx (MSBUILD error MSB1011).
# CRITICAL: use isolated obj/bin paths so this nested build doesn't corrupt the parent
# publish's obj directory. When invoked from the csproj's GenerateSAST target during
# dotnet publish, sharing obj/$(Configuration)/ caused the parent's BIManageRevit.dll
# to be deleted mid-publish (MSB3030 error).
$projectFile = Join-Path $ProjectDir "BIManageRevit.csproj"
$sastObjDir = Join-Path $ProjectDir "obj\SAST\$Configuration\"
$sastBinDir = Join-Path $ProjectDir "bin\SAST\$Configuration\"
$buildArgs = @(
    'build'
    $projectFile
    "--configuration", $Configuration
    "/p:IntermediateOutputPath=$sastObjDir"
    "/p:OutputPath=$sastBinDir"
    "/p:DeployRevitAddin=false"
    "/p:IncludeAddons=false"
    "/p:EnforceCodeStyleInBuild=true"
    "/p:EnableNETAnalyzers=true"
    "/p:AnalysisLevel=latest-all"
    "/p:RunAnalyzersDuringBuild=true"
    "-consoleloggerparameters:NoSummary"
    "-v", "normal"
)

$buildOutput = & dotnet @buildArgs 2>&1 | Out-String
$buildOutput | Set-Content $buildLog -Encoding UTF8

# Step 2: Parse security-relevant warnings from build output
Write-Host "[2/3] Analyzing security warnings..." -ForegroundColor Yellow

# Security analyzer rule prefixes:
#   CA2xxx  - Security rules (Microsoft.CodeAnalysis.NetAnalyzers)
#   CA3xxx  - Security rules
#   CA5xxx  - Security rules
#   SCS     - SecurityCodeScan rules
#   S       - Sonar-like rules (if present)
$securityPatterns = @(
    'CA2100', 'CA2109', 'CA2119', 'CA2153', 'CA2300', 'CA2301', 'CA2302', 'CA2305',
    'CA2310', 'CA2311', 'CA2312', 'CA2315', 'CA2321', 'CA2322', 'CA2326', 'CA2327',
    'CA2328', 'CA2329', 'CA2330', 'CA2350', 'CA2351', 'CA2352', 'CA2353', 'CA2354',
    'CA2355', 'CA2356', 'CA2361', 'CA2362',
    'CA3001', 'CA3002', 'CA3003', 'CA3004', 'CA3005', 'CA3006', 'CA3007', 'CA3008',
    'CA3009', 'CA3010', 'CA3011', 'CA3012', 'CA3061', 'CA3075', 'CA3076', 'CA3077',
    'CA3147',
    'CA5350', 'CA5351', 'CA5358', 'CA5359', 'CA5360', 'CA5361', 'CA5362', 'CA5363',
    'CA5364', 'CA5365', 'CA5366', 'CA5367', 'CA5368', 'CA5369', 'CA5370', 'CA5371',
    'CA5372', 'CA5373', 'CA5374', 'CA5375', 'CA5376', 'CA5377', 'CA5378', 'CA5379',
    'CA5380', 'CA5381', 'CA5382', 'CA5383', 'CA5384', 'CA5385', 'CA5386', 'CA5387',
    'CA5388', 'CA5389', 'CA5390', 'CA5391', 'CA5392', 'CA5393', 'CA5394', 'CA5395',
    'CA5396', 'CA5397', 'CA5398', 'CA5399', 'CA5400', 'CA5401', 'CA5402', 'CA5403',
    'CA5404', 'CA5405',
    'SCS'
)

# Build regex pattern for matching security warnings
$patternRegex = ($securityPatterns | ForEach-Object { [regex]::Escape($_) }) -join '|'

$allWarnings = @()
$securityFindings = @()
$qualityFindings = @()

# Parse all warnings from build output
$lines = $buildOutput -split "`n"
foreach ($line in $lines) {
    if ($line -match ':\s*warning\s+([\w]+)\s*:\s*(.+)') {
        $ruleId = $Matches[1]
        $message = $Matches[2].Trim()
        $file = ""
        $lineNum = 0

        if ($line -match '^\s*(.+?)\((\d+),\d+\)') {
            $file = $Matches[1].Trim()
            $lineNum = [int]$Matches[2]
        }

        $finding = [ordered]@{
            ruleId  = $ruleId
            message = $message
            file    = $file
            line    = $lineNum
        }

        $allWarnings += $finding

        if ($ruleId -match "^($patternRegex)") {
            $securityFindings += $finding
        }
        else {
            $qualityFindings += $finding
        }
    }
}

# Step 3: Categorize security findings
Write-Host "[3/3] Categorizing findings..." -ForegroundColor Yellow

$categories = [ordered]@{
    "SQL Injection"           = @('CA2100', 'CA3001', 'SCS0002')
    "XSS"                     = @('CA3002', 'SCS0029')
    "Path Traversal"          = @('CA3003', 'SCS0018')
    "XML Injection"           = @('CA3004', 'CA3075', 'CA3076', 'CA3077', 'SCS0007')
    "LDAP Injection"          = @('CA3005', 'SCS0031')
    "Command Injection"       = @('CA3006', 'SCS0001')
    "Open Redirect"           = @('CA3007', 'SCS0027')
    "XPath Injection"         = @('CA3008', 'SCS0003')
    "Deserialization"         = @('CA2300', 'CA2301', 'CA2302', 'CA2305', 'CA2310', 'CA2311',
                                  'CA2312', 'CA2315', 'CA2321', 'CA2322', 'CA2326', 'CA2327',
                                  'CA2328', 'CA2329', 'CA2330', 'CA2350', 'CA2351', 'CA2352',
                                  'CA2353', 'CA2354', 'CA2355', 'CA2356', 'CA2361', 'CA2362',
                                  'SCS0028')
    "Weak Cryptography"       = @('CA5350', 'CA5351', 'CA5358', 'CA5379', 'CA5384', 'CA5385',
                                  'SCS0004', 'SCS0005', 'SCS0006')
    "Insecure SSL/TLS"        = @('CA5359', 'CA5364', 'CA5386', 'CA5397', 'CA5398')
    "CSRF"                    = @('CA3147', 'SCS0016')
    "Hardcoded Credentials"   = @('CA5390', 'SCS0015')
    "Information Disclosure"  = @('CA2109', 'CA2119', 'CA2153', 'CA3009', 'CA3010', 'CA3011', 'CA3012')
    "Insecure Randomness"     = @('CA5394', 'SCS0005')
    "File Operations"         = @('CA5360', 'CA5361', 'CA5362', 'CA5363')
}

$categorized = [ordered]@{}
foreach ($cat in $categories.Keys) {
    $rules = $categories[$cat]
    $matching = $securityFindings | Where-Object { $rules -contains $_.ruleId -or ($_.ruleId -match "^SCS" -and $rules | Where-Object { $_.ruleId -match "^$_" }) }
    if ($matching) {
        $categorized[$cat] = @($matching)
    }
}

# Generate JSON report
$report = [ordered]@{
    scanDate      = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    buildUser     = $env:USERNAME
    project       = "BIManageRevit"
    configuration = $Configuration
    tool          = ".NET Roslyn Security Analyzers"
    toolVersion   = (& dotnet --version 2>$null)
    analyzers     = @(
        "Microsoft.CodeAnalysis.NetAnalyzers (built-in)"
        "SecurityCodeScan.VS2019 (NuGet)"
    )
    summary       = [ordered]@{
        totalWarnings      = $allWarnings.Count
        securityFindings   = $securityFindings.Count
        qualityFindings    = $qualityFindings.Count
        categories         = ($categorized.Keys | Measure-Object).Count
    }
    securityFindings = $securityFindings
    categorized      = $categorized
    qualitySummary   = [ordered]@{
        totalQualityWarnings = $qualityFindings.Count
        note                 = "Quality warnings are non-security code analysis findings"
    }
}

$report | ConvertTo-Json -Depth 20 | Set-Content $OutputFile -Encoding UTF8

# Summary output
Write-Host ""
Write-Host "=== SAST Results ===" -ForegroundColor Cyan
Write-Host "  Build User:        $env:USERNAME"
Write-Host "  Configuration:     $Configuration"
Write-Host "  Total Warnings:    $($allWarnings.Count)"

if ($securityFindings.Count -gt 0) {
    Write-Host "  Security Findings: $($securityFindings.Count)" -ForegroundColor Red
    foreach ($cat in $categorized.Keys) {
        $count = $categorized[$cat].Count
        Write-Host "    - ${cat}: $count" -ForegroundColor Red
    }
}
else {
    Write-Host "  Security Findings: 0" -ForegroundColor Green
}

Write-Host "  Quality Warnings:  $($qualityFindings.Count)" -ForegroundColor Yellow
Write-Host ""
Write-Host "Report:    $OutputFile" -ForegroundColor Cyan
Write-Host "Build Log: $buildLog" -ForegroundColor Cyan

Pop-Location
