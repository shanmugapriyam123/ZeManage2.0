param(
    [string]$ProjectDir = (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)),
    [string]$BaseUrl = "",
    [string]$OutputDir = ""
)

if (-not $OutputDir) { $OutputDir = Join-Path $ProjectDir "Tools\DAST" }
if (!(Test-Path $OutputDir)) { New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null }
$OutputFile = Join-Path $OutputDir "dast-report.json"

Write-Host "=== DAST (Dynamic Application Security Testing) ===" -ForegroundColor Cyan
Write-Host ""

# Read API base URL from App.config if not provided
if (-not $BaseUrl) {
    $appConfig = Join-Path $ProjectDir "App.config"
    if (Test-Path $appConfig) {
        [xml]$config = Get-Content $appConfig
        $node = $config.configuration.appSettings.add | Where-Object { $_.key -eq "ApiBaseUrl" }
        if ($node) { $BaseUrl = $node.value }
    }
}

if (-not $BaseUrl) {
    Write-Host "ERROR: No API base URL found. Provide -BaseUrl or set ApiBaseUrl in App.config." -ForegroundColor Red
    exit 1
}

Write-Host "Target: $BaseUrl"
Write-Host ""

$findings = @()
$passed = @()
$failed = @()
$errors = @()

# Helper: make HTTP request and return response details
function Test-Endpoint {
    param(
        [string]$Url,
        [string]$Method = "GET",
        [hashtable]$Headers = @{},
        [string]$Body = "",
        [int]$TimeoutSec = 15
    )
    try {
        $params = @{
            Uri                = $Url
            Method             = $Method
            TimeoutSec         = $TimeoutSec
            UseBasicParsing    = $true
            ErrorAction        = "Stop"
            MaximumRedirection = 0
        }
        if ($Headers.Count -gt 0) { $params['Headers'] = $Headers }
        if ($Body) { $params['Body'] = $Body; $params['ContentType'] = 'application/json' }

        $response = Invoke-WebRequest @params
        return @{
            StatusCode = $response.StatusCode
            Headers    = $response.Headers
            Content    = $response.Content
            Error      = $null
        }
    }
    catch {
        $statusCode = 0
        $headers = @{}
        if ($_.Exception.Response) {
            $statusCode = [int]$_.Exception.Response.StatusCode
            foreach ($h in $_.Exception.Response.Headers) {
                $headers[$h] = $_.Exception.Response.Headers[$h]
            }
        }
        return @{
            StatusCode = $statusCode
            Headers    = $headers
            Content    = ""
            Error      = $_.Exception.Message
        }
    }
}

# ============================================================
# Test 1: SSL/TLS Configuration
# ============================================================
Write-Host "[1/8] Testing SSL/TLS configuration..." -ForegroundColor Yellow
$test = "SSL/TLS Configuration"
try {
    # Force TLS 1.2+ only
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13
    $resp = Test-Endpoint -Url $BaseUrl
    if ($resp.StatusCode -gt 0) {
        $passed += [ordered]@{ test = $test; detail = "Server accepts TLS 1.2+"; severity = "info" }
    }
    else {
        $failed += [ordered]@{ test = $test; detail = "Cannot connect with TLS 1.2+: $($resp.Error)"; severity = "high" }
    }
}
catch {
    $errors += [ordered]@{ test = $test; error = $_.Exception.Message }
}

# ============================================================
# Test 2: Security Headers
# ============================================================
Write-Host "[2/8] Testing security headers..." -ForegroundColor Yellow
$test = "Security Headers"
$resp = Test-Endpoint -Url $BaseUrl

$requiredHeaders = [ordered]@{
    "Strict-Transport-Security" = @{ severity = "high";   desc = "HSTS - Prevents downgrade attacks" }
    "X-Content-Type-Options"    = @{ severity = "medium"; desc = "Prevents MIME-type sniffing" }
    "X-Frame-Options"           = @{ severity = "medium"; desc = "Prevents clickjacking" }
    "Content-Security-Policy"   = @{ severity = "medium"; desc = "Prevents XSS and injection attacks" }
    "X-XSS-Protection"         = @{ severity = "low";    desc = "Legacy XSS filter (supplementary)" }
    "Referrer-Policy"           = @{ severity = "low";    desc = "Controls referrer information leakage" }
    "Permissions-Policy"        = @{ severity = "low";    desc = "Controls browser feature access" }
}

foreach ($header in $requiredHeaders.Keys) {
    $info = $requiredHeaders[$header]
    if ($resp.Headers -and $resp.Headers[$header]) {
        $passed += [ordered]@{ test = "Header: $header"; detail = "Present: $($resp.Headers[$header])"; severity = "info" }
    }
    else {
        $failed += [ordered]@{ test = "Header: $header"; detail = "Missing - $($info.desc)"; severity = $info.severity }
    }
}

# Check for information-leaking headers
$leakHeaders = @("Server", "X-Powered-By", "X-AspNet-Version", "X-AspNetMvc-Version")
foreach ($header in $leakHeaders) {
    if ($resp.Headers -and $resp.Headers[$header]) {
        $failed += [ordered]@{ test = "Info Leak: $header"; detail = "Header exposes: $($resp.Headers[$header])"; severity = "low" }
    }
    else {
        $passed += [ordered]@{ test = "Info Leak: $header"; detail = "Not exposed"; severity = "info" }
    }
}

# ============================================================
# Test 3: CORS Configuration
# ============================================================
Write-Host "[3/8] Testing CORS configuration..." -ForegroundColor Yellow
$test = "CORS Configuration"
$corsResp = Test-Endpoint -Url $BaseUrl -Headers @{ "Origin" = "https://evil.example.com" }

if ($corsResp.Headers -and $corsResp.Headers["Access-Control-Allow-Origin"]) {
    $acao = $corsResp.Headers["Access-Control-Allow-Origin"]
    if ($acao -eq "*") {
        $failed += [ordered]@{ test = $test; detail = "Wildcard CORS (*) allows any origin"; severity = "high" }
    }
    elseif ($acao -match "evil\.example\.com") {
        $failed += [ordered]@{ test = $test; detail = "CORS reflects arbitrary origin"; severity = "critical" }
    }
    else {
        $passed += [ordered]@{ test = $test; detail = "CORS restricted to: $acao"; severity = "info" }
    }
}
else {
    $passed += [ordered]@{ test = $test; detail = "No CORS header returned for foreign origin"; severity = "info" }
}

# ============================================================
# Test 4: HTTP Methods
# ============================================================
Write-Host "[4/8] Testing HTTP methods..." -ForegroundColor Yellow
$test = "HTTP Methods"
$dangerousMethods = @("TRACE", "DELETE", "PUT")
foreach ($method in $dangerousMethods) {
    try {
        $methodResp = Test-Endpoint -Url $BaseUrl -Method $method
        if ($method -eq "TRACE" -and $methodResp.StatusCode -eq 200) {
            $failed += [ordered]@{ test = "HTTP Method: $method"; detail = "TRACE enabled - vulnerable to XST attacks"; severity = "medium" }
        }
        elseif ($methodResp.StatusCode -eq 405 -or $methodResp.StatusCode -eq 0) {
            $passed += [ordered]@{ test = "HTTP Method: $method"; detail = "Method not allowed (405)"; severity = "info" }
        }
        else {
            $passed += [ordered]@{ test = "HTTP Method: $method"; detail = "Returns $($methodResp.StatusCode)"; severity = "info" }
        }
    }
    catch {
        $errors += [ordered]@{ test = "HTTP Method: $method"; error = $_.Exception.Message }
    }
}

# ============================================================
# Test 5: Authentication Endpoints
# ============================================================
Write-Host "[5/8] Testing authentication endpoints..." -ForegroundColor Yellow
$test = "Authentication"

# Test unauthenticated access to API
$authEndpoints = @("/api/health", "/api", "/hubs/notifications")
foreach ($ep in $authEndpoints) {
    $url = $BaseUrl.TrimEnd('/') + $ep
    $authResp = Test-Endpoint -Url $url
    if ($authResp.StatusCode -eq 200) {
        $passed += [ordered]@{ test = "Endpoint: $ep"; detail = "Accessible (status 200)"; severity = "info" }
    }
    elseif ($authResp.StatusCode -eq 401 -or $authResp.StatusCode -eq 403) {
        $passed += [ordered]@{ test = "Endpoint: $ep"; detail = "Protected (status $($authResp.StatusCode))"; severity = "info" }
    }
    elseif ($authResp.StatusCode -gt 0) {
        $passed += [ordered]@{ test = "Endpoint: $ep"; detail = "Returns status $($authResp.StatusCode)"; severity = "info" }
    }
    else {
        $errors += [ordered]@{ test = "Endpoint: $ep"; error = $authResp.Error }
    }
}

# ============================================================
# Test 6: SQL Injection Probes (safe, detection-only)
# ============================================================
Write-Host "[6/8] Testing SQL injection resilience..." -ForegroundColor Yellow
$test = "SQL Injection"

$sqliPayloads = @("' OR '1'='1", "1; DROP TABLE --", "' UNION SELECT NULL--")
foreach ($payload in $sqliPayloads) {
    $url = $BaseUrl.TrimEnd('/') + "/api?q=" + [uri]::EscapeDataString($payload)
    $sqliResp = Test-Endpoint -Url $url
    if ($sqliResp.Content -match "sql|syntax|mysql|sqlite|exception|stack trace" -and $sqliResp.StatusCode -eq 500) {
        $failed += [ordered]@{ test = $test; detail = "Possible SQLi - server error with DB-related content for payload: $payload"; severity = "critical" }
    }
    else {
        $passed += [ordered]@{ test = $test; detail = "No SQL error for payload: $($payload.Substring(0, [Math]::Min(20, $payload.Length)))..."; severity = "info" }
    }
}

# ============================================================
# Test 7: XSS Probes (safe, detection-only)
# ============================================================
Write-Host "[7/8] Testing XSS resilience..." -ForegroundColor Yellow
$test = "XSS Reflection"

$xssPayloads = @("<script>alert(1)</script>", "<img src=x onerror=alert(1)>", "javascript:alert(1)")
foreach ($payload in $xssPayloads) {
    $url = $BaseUrl.TrimEnd('/') + "/api?q=" + [uri]::EscapeDataString($payload)
    $xssResp = Test-Endpoint -Url $url
    if ($xssResp.Content -and $xssResp.Content.Contains($payload)) {
        $failed += [ordered]@{ test = $test; detail = "Reflected XSS - payload returned unescaped: $payload"; severity = "high" }
    }
    else {
        $passed += [ordered]@{ test = $test; detail = "Payload not reflected: $($payload.Substring(0, [Math]::Min(20, $payload.Length)))..."; severity = "info" }
    }
}

# ============================================================
# Test 8: HTTPS Redirect
# ============================================================
Write-Host "[8/8] Testing HTTPS enforcement..." -ForegroundColor Yellow
$test = "HTTPS Enforcement"

$httpUrl = $BaseUrl -replace "^https://", "http://"
if ($httpUrl -ne $BaseUrl) {
    try {
        $httpResp = Test-Endpoint -Url $httpUrl
        if ($httpResp.StatusCode -ge 300 -and $httpResp.StatusCode -lt 400) {
            $passed += [ordered]@{ test = $test; detail = "HTTP redirects to HTTPS (status $($httpResp.StatusCode))"; severity = "info" }
        }
        elseif ($httpResp.StatusCode -eq 200) {
            $failed += [ordered]@{ test = $test; detail = "HTTP served without redirect to HTTPS"; severity = "high" }
        }
        else {
            $passed += [ordered]@{ test = $test; detail = "HTTP returns status $($httpResp.StatusCode)"; severity = "info" }
        }
    }
    catch {
        $passed += [ordered]@{ test = $test; detail = "HTTP connection refused (HTTPS-only)"; severity = "info" }
    }
}
else {
    $errors += [ordered]@{ test = $test; error = "URL is not HTTPS, cannot test redirect" }
}

# ============================================================
# Generate Report
# ============================================================
$criticalCount = ($failed | Where-Object { $_.severity -eq "critical" }).Count
$highCount     = ($failed | Where-Object { $_.severity -eq "high" }).Count
$mediumCount   = ($failed | Where-Object { $_.severity -eq "medium" }).Count
$lowCount      = ($failed | Where-Object { $_.severity -eq "low" }).Count

$report = [ordered]@{
    scanDate    = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
    buildUser   = $env:USERNAME
    project     = "BIManageRevit"
    targetUrl   = $BaseUrl
    tool        = "BIManage DAST Scanner (PowerShell)"
    toolVersion = "1.0.0"
    summary     = [ordered]@{
        totalTests = $passed.Count + $failed.Count + $errors.Count
        passed     = $passed.Count
        failed     = $failed.Count
        errors     = $errors.Count
        critical   = $criticalCount
        high       = $highCount
        medium     = $mediumCount
        low        = $lowCount
    }
    findings    = [ordered]@{
        passed = $passed
        failed = $failed
        errors = $errors
    }
}

$report | ConvertTo-Json -Depth 20 | Set-Content $OutputFile -Encoding UTF8

# Summary
Write-Host ""
Write-Host "=== DAST Results ===" -ForegroundColor Cyan
Write-Host "  Build User: $env:USERNAME"
Write-Host "  Target:     $BaseUrl"
Write-Host "  Tests Run:  $($passed.Count + $failed.Count + $errors.Count)"
Write-Host "  Passed:     $($passed.Count)" -ForegroundColor Green

if ($failed.Count -gt 0) {
    Write-Host "  Failed:     $($failed.Count)" -ForegroundColor Red
    if ($criticalCount -gt 0) { Write-Host "    - Critical: $criticalCount" -ForegroundColor Red }
    if ($highCount -gt 0)     { Write-Host "    - High:     $highCount" -ForegroundColor Red }
    if ($mediumCount -gt 0)   { Write-Host "    - Medium:   $mediumCount" -ForegroundColor Yellow }
    if ($lowCount -gt 0)      { Write-Host "    - Low:      $lowCount" -ForegroundColor Yellow }
}
else {
    Write-Host "  Failed:     0" -ForegroundColor Green
}

if ($errors.Count -gt 0) { Write-Host "  Errors:     $($errors.Count)" -ForegroundColor DarkYellow }
Write-Host ""
Write-Host "Report: $OutputFile" -ForegroundColor Cyan
