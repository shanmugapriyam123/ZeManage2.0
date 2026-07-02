<#
.SYNOPSIS
    Flips the BIManageRevit plugin between staging and production API endpoints.

.DESCRIPTION
    The plugin reads its API base URL and SignalR hub URL from
    BIManageRevit.dll.config (compiled from App.config at the repo root, and
    also pre-staged under Bundle\BIManageRevit.org\Contents\<year>\ for each
    Revit version). The installer (Tools\Installer\ZeManageInstaller.iss) has
    its own #define for the uninstall endpoint. This script updates ALL of
    those locations together so the team never has to remember which file
    governs which artifact.

.PARAMETER Environment
    "staging" or "production". Required unless -Status is passed.

.PARAMETER Status
    Show which environment is currently active across all config files and
    exit. Does not modify anything.

.EXAMPLE
    .\Tools\"Environment Switch"\SwitchEnvironment.ps1 staging
    .\Tools\"Environment Switch"\SwitchEnvironment.ps1 production
    .\Tools\"Environment Switch"\SwitchEnvironment.ps1 -Status

    # Or use the convenience .cmd wrappers from the same folder:
    .\Tools\"Environment Switch"\Switch-Staging.cmd
    .\Tools\"Environment Switch"\Switch-Production.cmd
    .\Tools\"Environment Switch"\Switch-Status.cmd

.NOTES
    URLs are defined in $ApiBaseUrls below. To add a new environment, add a
    new key to that hashtable and a new validation entry to the parameter
    set. The script is idempotent - running it twice with the same target
    environment is a no-op (reports "already on <env>").
#>
[CmdletBinding(DefaultParameterSetName = 'Switch')]
param(
    [Parameter(ParameterSetName = 'Switch', Position = 0, Mandatory = $true)]
    [ValidateSet('staging', 'production', IgnoreCase = $true)]
    [string]$Environment,

    [Parameter(ParameterSetName = 'Status')]
    [switch]$Status
)

$ErrorActionPreference = 'Stop'

# ------------------------------------------------------------------
# Environment registry. Update here to change URLs or add an environment.
# ------------------------------------------------------------------
$ApiBaseUrls = @{
    'staging'    = 'https://bimanage-api-staging-f2h4fgg8a6cnhwam.centralus-01.azurewebsites.net'
    'production' = 'https://api.zemanage.com'
}

# ------------------------------------------------------------------
# Resolve the repo root by walking up until BIManageRevit.csproj is found.
# Works whether the script is parked at <repo>\Tools\SwitchEnvironment.ps1 or
# nested deeper at <repo>\Tools\Environment Switch\SwitchEnvironment.ps1.
# ------------------------------------------------------------------
$RepoRoot = $PSScriptRoot
while ($RepoRoot -and -not (Test-Path (Join-Path $RepoRoot 'BIManageRevit.csproj'))) {
    $parent = Split-Path -Parent $RepoRoot
    if ($parent -eq $RepoRoot) { $RepoRoot = $null; break }
    $RepoRoot = $parent
}
if (-not $RepoRoot) {
    Write-Error "Could not locate repo root from PSScriptRoot $PSScriptRoot. Expected BIManageRevit.csproj somewhere above this script."
    exit 1
}

# ------------------------------------------------------------------
# The 9 files to keep in sync
# ------------------------------------------------------------------
$ConfigFiles = @(
    (Join-Path $RepoRoot 'App.config')
)
foreach ($year in 2021..2027) {
    $ConfigFiles += (Join-Path $RepoRoot "Bundle\BIManageRevit.org\Contents\$year\BIManageRevit.dll.config")
}
$InstallerFile = Join-Path $RepoRoot 'Tools\Installer\ZeManageInstaller.iss'

# ------------------------------------------------------------------
# Detect current environment by reading App.config (treated as ground truth
# since it's the file compiled into the build). Returns 'staging',
# 'production', or 'unknown'.
# ------------------------------------------------------------------
function Get-CurrentEnvironment {
    param([string]$ConfigPath)

    if (-not (Test-Path $ConfigPath)) { return 'unknown (file missing)' }
    try {
        # Read with explicit UTF-8 encoding. Get-Content -Raw on Windows PowerShell 5.1
        # defaults to the OS code page (Windows-1252 in en-US), which corrupts multi-byte
        # UTF-8 sequences like the unicode arrows in the comment block. The .NET API
        # honours UTF-8 regardless of the host PowerShell version.
        $raw = [System.IO.File]::ReadAllText($ConfigPath, [System.Text.Encoding]::UTF8)
        [xml]$xml = $raw
        $node = $xml.configuration.appSettings.add | Where-Object { $_.key -eq 'ApiBaseUrl' } | Select-Object -First 1
        if ($null -eq $node) { return 'unknown (no ApiBaseUrl key)' }
        foreach ($entry in $ApiBaseUrls.GetEnumerator()) {
            if ($node.value -eq $entry.Value) { return $entry.Key }
        }
        return "unknown (value=$($node.value))"
    } catch {
        return "unknown (parse error: $_)"
    }
}

# ------------------------------------------------------------------
# Status mode - print current state of every config and exit.
# ------------------------------------------------------------------
if ($PSCmdlet.ParameterSetName -eq 'Status') {
    Write-Host ""
    Write-Host "=== BIManageRevit environment status ===" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "Defined environments:" -ForegroundColor Yellow
    foreach ($entry in $ApiBaseUrls.GetEnumerator()) {
        Write-Host ("  {0,-12} -> {1}" -f $entry.Key, $entry.Value)
    }
    Write-Host ""
    Write-Host "Per-file state:" -ForegroundColor Yellow
    foreach ($file in $ConfigFiles) {
        $env = Get-CurrentEnvironment -ConfigPath $file
        $rel = $file.Substring($RepoRoot.Length + 1)
        $color = if ($env -eq 'staging' -or $env -eq 'production') { 'Green' } else { 'Red' }
        Write-Host ("  [{0,-12}] {1}" -f $env, $rel) -ForegroundColor $color
    }

    # Installer
    if (Test-Path $InstallerFile) {
        $content = [System.IO.File]::ReadAllText($InstallerFile, [System.Text.Encoding]::UTF8)
        $installerEnv = 'unknown'
        foreach ($entry in $ApiBaseUrls.GetEnumerator()) {
            $marker = '#define ApiBaseUrl "' + $entry.Value + '"'
            if ($content.Contains($marker)) {
                $installerEnv = $entry.Key
                break
            }
        }
        $rel = $InstallerFile.Substring($RepoRoot.Length + 1)
        $color = if ($installerEnv -eq 'staging' -or $installerEnv -eq 'production') { 'Green' } else { 'Red' }
        Write-Host ("  [{0,-12}] {1}" -f $installerEnv, $rel) -ForegroundColor $color
    }
    Write-Host ""
    exit 0
}

# ------------------------------------------------------------------
# Switch mode - write the new environment to all 9 files.
# ------------------------------------------------------------------
$target = $Environment.ToLower()
$targetApi = $ApiBaseUrls[$target]
$targetHub = "$targetApi/hubs/notifications"

Write-Host ""
Write-Host "=== Switching BIManageRevit to '$target' ===" -ForegroundColor Cyan
Write-Host "  ApiBaseUrl   = $targetApi" -ForegroundColor Gray
Write-Host "  SignalR:Hub  = $targetHub" -ForegroundColor Gray
Write-Host ""

$changedCount = 0
$skippedCount = 0
$missingCount = 0

# 1. Update each .config file's ApiBaseUrl + SignalR:HubUrl via XML edit
foreach ($file in $ConfigFiles) {
    $rel = $file.Substring($RepoRoot.Length + 1)
    if (-not (Test-Path $file)) {
        Write-Host "  [MISSING] $rel" -ForegroundColor Yellow
        $missingCount++
        continue
    }
    try {
        # Read with explicit UTF-8 to preserve multi-byte chars in comment blocks
        # (Get-Content -Raw uses the OS code page on Windows PowerShell 5.1).
        $raw = [System.IO.File]::ReadAllText($file, [System.Text.Encoding]::UTF8)
        [xml]$xml = $raw
        $apiNode = $xml.configuration.appSettings.add | Where-Object { $_.key -eq 'ApiBaseUrl' } | Select-Object -First 1
        $hubNode = $xml.configuration.appSettings.add | Where-Object { $_.key -eq 'SignalR:HubUrl' } | Select-Object -First 1
        if ($null -eq $apiNode) {
            Write-Host "  [SKIP]    $rel (no ApiBaseUrl key)" -ForegroundColor Yellow
            $skippedCount++
            continue
        }
        $changed = $false
        if ($apiNode.value -ne $targetApi) {
            $apiNode.value = $targetApi
            $changed = $true
        }
        if ($hubNode -and $hubNode.value -ne $targetHub) {
            $hubNode.value = $targetHub
            $changed = $true
        }
        if ($changed) {
            # Preserve UTF-8 without BOM (matches the existing files' encoding)
            $settings = New-Object System.Xml.XmlWriterSettings
            $settings.Indent = $true
            $settings.IndentChars = '  '
            $settings.Encoding = New-Object System.Text.UTF8Encoding($false)
            $settings.OmitXmlDeclaration = $false
            $writer = [System.Xml.XmlWriter]::Create($file, $settings)
            try {
                $xml.Save($writer)
            } finally {
                $writer.Close()
            }
            Write-Host "  [UPDATED] $rel" -ForegroundColor Green
            $changedCount++
        } else {
            Write-Host "  [OK]      $rel (already $target)" -ForegroundColor DarkGray
        }
    } catch {
        Write-Host "  [ERROR]   $rel - $_" -ForegroundColor Red
        throw
    }
}

# 2. Update the installer .iss file (#define ApiBaseUrl "...")
# Use a literal-string approach over regex to avoid PowerShell parser quirks
# with embedded double-quotes in patterns.
$rel = $InstallerFile.Substring($RepoRoot.Length + 1)
if (-not (Test-Path $InstallerFile)) {
    Write-Host "  [MISSING] $rel" -ForegroundColor Yellow
    $missingCount++
} else {
    $original = [System.IO.File]::ReadAllText($InstallerFile, [System.Text.Encoding]::UTF8)
    $newDefine = '#define ApiBaseUrl "' + $targetApi + '"'

    # Find the existing '#define ApiBaseUrl "..."' line and replace it as a block
    $lines = $original -split "`r`n"
    $found = $false
    $modified = $false
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $trimmed = $lines[$i].TrimStart()
        if ($trimmed.StartsWith('#define ApiBaseUrl ')) {
            $found = $true
            if ($lines[$i] -ne $newDefine) {
                $lines[$i] = $newDefine
                $modified = $true
            }
            break
        }
    }

    if (-not $found) {
        Write-Host "  [SKIP]    $rel (no #define ApiBaseUrl line found)" -ForegroundColor Yellow
        $skippedCount++
    } elseif ($modified) {
        $updated = $lines -join "`r`n"
        # Write with UTF-8 WITHOUT BOM to match the existing file's encoding.
        # Set-Content -Encoding UTF8 on PS 5.1 emits a BOM which Inno Setup tolerates
        # but isn't what was there before — keep the diff minimal.
        $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($InstallerFile, $updated, $utf8NoBom)
        Write-Host "  [UPDATED] $rel" -ForegroundColor Green
        $changedCount++
    } else {
        Write-Host "  [OK]      $rel (already $target)" -ForegroundColor DarkGray
    }
}

Write-Host ""
Write-Host "Summary: $changedCount updated, $skippedCount skipped, $missingCount missing" -ForegroundColor Cyan
Write-Host ""

if ($changedCount -gt 0) {
    Write-Host "Next steps:" -ForegroundColor Yellow
    Write-Host "  1. Rebuild the plugin so the new App.config is copied into bin\<config>\BIManageRevit.dll.config:" -ForegroundColor Gray
    Write-Host "     dotnet build BIManageRevit.csproj -c `"Debug R25`"" -ForegroundColor Gray
    Write-Host "  2. If publishing through the obfuscation pipeline, the Bundle\...\Contents\<year>\BIManageRevit.dll.config files are already in sync." -ForegroundColor Gray
    Write-Host "  3. To rebuild the installer, run Tools\Installer (or 06_Installer.cmd if present)." -ForegroundColor Gray
    Write-Host ""
}

exit 0
