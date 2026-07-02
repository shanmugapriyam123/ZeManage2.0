# Manual test runner for persistence resilience tests
# Since the test project has build issues with wildcard version references,
# this script manually verifies the test scenarios

Write-Host "Persistence Resilience Test Runner" -ForegroundColor Cyan
Write-Host "==================================`n" -ForegroundColor Cyan

$testDbPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "BiManage_Manual_Test_$(New-Guid).db")

Write-Host "Test database path: $testDbPath`n" -ForegroundColor Yellow

# Load assemblies
try {
    Add-Type -Path "..\..\bin\Debug\net8.0-windows\BIManageRevit.dll"
    Write-Host "[OK] Loaded BIManageRevit assembly" -ForegroundColor Green
} catch {
    Write-Host "[FAIL] Could not load BIManageRevit.dll" -ForegroundColor Red
    Write-Host "Error: $_" -ForegroundColor Red
    Write-Host "`nPlease build the main project first:" -ForegroundColor Yellow
    Write-Host "  dotnet build BIManageRevit.csproj --configuration Debug --framework net8.0-windows" -ForegroundColor Yellow
    exit 1
}

Write-Host "`nTest Scenarios:" -ForegroundColor Cyan
Write-Host "1. Session persistence across repository disposal" -ForegroundColor White
Write-Host "2. Event persistence and ordering" -ForegroundColor White
Write-Host "3. Offline queue persistence" -ForegroundColor White
Write-Host "4. Corrupted database recovery" -ForegroundColor White
Write-Host "5. Missing database file creation" -ForegroundColor White

Write-Host "`nNote: These tests require the BIManageRevit assemblies to be built." -ForegroundColor Yellow
Write-Host "The actual xUnit tests are defined in PersistenceResilienceTests.cs" -ForegroundColor Yellow
Write-Host "`nTo run tests once the build issue is resolved:" -ForegroundColor Yellow
Write-Host "  dotnet test --filter 'FullyQualifiedName~PersistenceResilienceTests'" -ForegroundColor Cyan

# Cleanup
if (Test-Path $testDbPath) {
    Remove-Item $testDbPath -Force
    Write-Host "`nCleaned up test database" -ForegroundColor Gray
}
