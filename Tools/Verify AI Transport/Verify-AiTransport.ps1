<#
.SYNOPSIS
    Tells you whether the currently-installed ZeManage plugin is using the hardcoded
    OpenAI key or the backend proxy (Azure Key Vault path).

.DESCRIPTION
    Reads three places to give you a definitive answer:
      1. The deployed .config file Revit actually loads (per Revit version)
      2. The OPENAI_API_KEY environment variable (user + machine scopes)
      3. The latest plugin log file for the OpenAIProvider startup line

    Run AFTER opening Revit at least once so the log has the startup banner.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Verify-AiTransport.ps1
    powershell -ExecutionPolicy Bypass -File .\Verify-AiTransport.ps1 -Year 2025
#>

param(
    [int]$Year = 0  # 0 = scan all installed years
)

$ErrorActionPreference = 'Stop'
$addinsRoot = Join-Path $env:APPDATA 'Autodesk\Revit\Addins'
$logsRoot   = Join-Path $env:LOCALAPPDATA 'BIManageRevit\Logs'

Write-Host ""
Write-Host "=== ZeManage AI Transport Verifier ===" -ForegroundColor Cyan
Write-Host ""

# 1. Per-version config inspection
$years = if ($Year -ne 0) { @($Year) } else { 2021..2027 }
foreach ($y in $years) {
    $configPath = Join-Path $addinsRoot "$y\BIManageRevit\BIManageRevit.dll.config"
    if (-not (Test-Path $configPath)) { continue }

    Write-Host "Revit $y" -ForegroundColor Yellow
    Write-Host "  Config: $configPath"

    [xml]$cfg = Get-Content $configPath
    $apiKey = ($cfg.configuration.appSettings.add | Where-Object { $_.key -eq 'AI:ApiKey' }).value
    $useProxy = ($cfg.configuration.appSettings.add | Where-Object { $_.key -eq 'AI:UseBackendProxy' }).value
    $apiBase = ($cfg.configuration.appSettings.add | Where-Object { $_.key -eq 'ApiBaseUrl' }).value

    if ($useProxy -eq 'true') {
        Write-Host "  AI:UseBackendProxy = true" -ForegroundColor Green
        Write-Host "  -> Calls will go to: $apiBase/api/v1/ai/chat" -ForegroundColor Green
        Write-Host "  -> Key source: Azure Key Vault (backend)" -ForegroundColor Green
    }
    elseif ($useProxy -eq 'false' -or -not $useProxy) {
        if ($apiKey -and $apiKey.StartsWith('sk-')) {
            Write-Host "  AI:UseBackendProxy = false (or missing)" -ForegroundColor Red
            Write-Host "  AI:ApiKey is SET to a real key in the config" -ForegroundColor Red
            Write-Host "  -> Calls go DIRECTLY to api.openai.com using the key from this file" -ForegroundColor Red
            $masked = $apiKey.Substring(0, 7) + '...' + $apiKey.Substring($apiKey.Length - 4)
            Write-Host "  -> Key (masked): $masked"
        }
        else {
            Write-Host "  AI:UseBackendProxy = false (or missing)" -ForegroundColor Yellow
            Write-Host "  AI:ApiKey is blank" -ForegroundColor Yellow
            Write-Host "  -> Falls back to OPENAI_API_KEY env var, then HardcodedApiKey constant in OpenAIProvider.cs"
        }
    }
    Write-Host ""
}

# 2. Environment variable check
Write-Host "Environment variable OPENAI_API_KEY" -ForegroundColor Yellow
$envUser    = [Environment]::GetEnvironmentVariable('OPENAI_API_KEY', 'User')
$envMachine = [Environment]::GetEnvironmentVariable('OPENAI_API_KEY', 'Machine')
if ($envUser) { Write-Host "  User scope:    SET (starts $($envUser.Substring(0,7))...)" -ForegroundColor Yellow }
else          { Write-Host "  User scope:    not set" }
if ($envMachine) { Write-Host "  Machine scope: SET" -ForegroundColor Yellow }
else             { Write-Host "  Machine scope: not set" }
Write-Host ""

# 3. Latest log file
Write-Host "Latest plugin log (truth at runtime)" -ForegroundColor Yellow
if (-not (Test-Path $logsRoot)) {
    Write-Host "  Log directory does not exist yet: $logsRoot"
    Write-Host "  Open Revit + Ze AI once, then re-run this script."
}
else {
    $latest = Get-ChildItem $logsRoot -Filter 'BIManageRevit_*.log' |
              Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $latest) {
        Write-Host "  No log files yet."
    }
    else {
        Write-Host "  $($latest.FullName)"
        $modeLines = Select-String -Path $latest.FullName -Pattern 'OpenAIProvider\] Mode=' -ErrorAction SilentlyContinue
        $postLines = Select-String -Path $latest.FullName -Pattern 'OpenAIProvider\] POST '  -ErrorAction SilentlyContinue
        if (-not $modeLines) {
            Write-Host "  -> No OpenAIProvider startup line yet. Open Ze AI once and re-run." -ForegroundColor Gray
        }
        else {
            $line = $modeLines | Select-Object -Last 1
            Write-Host ""
            Write-Host "  STARTUP MODE:" -ForegroundColor Cyan
            Write-Host "  $($line.Line.Trim())" -ForegroundColor White
        }
        if ($postLines) {
            $lastPost = $postLines | Select-Object -Last 1
            Write-Host ""
            Write-Host "  LAST CHAT REQUEST:" -ForegroundColor Cyan
            Write-Host "  $($lastPost.Line.Trim())" -ForegroundColor White
        }
    }
}
Write-Host ""
