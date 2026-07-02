# apiRefresh.ps1 — Downloads latest OpenAPI spec from staging server
# Output: bimanage-api.json in the same folder as this script

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$outputPath = Join-Path $scriptDir "bimanage-api.json"
$swaggerUrl = "http://10.10.40.75:5000/swagger/v1/swagger.json"

Write-Host "Downloading API spec from $swaggerUrl ..."
try {
    curl.exe -k "$swaggerUrl" -o "$outputPath" --silent --show-error
    if ($LASTEXITCODE -eq 0 -and (Test-Path $outputPath)) {
        $size = (Get-Item $outputPath).Length
        Write-Host "Done! Saved to $outputPath ($size bytes)"
    } else {
        Write-Host "ERROR: Download failed (exit code: $LASTEXITCODE)" -ForegroundColor Red
        exit 1
    }
} catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
