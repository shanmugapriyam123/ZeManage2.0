# DAST - Dynamic Application Security Testing

## What is DAST?

**Dynamic Application Security Testing (DAST)** tests a running application by sending real HTTP requests to find security vulnerabilities from the **outside**. Unlike SAST (which reads code), DAST acts like an attacker probing the live API.

## What It Tests

| # | Test | What It Checks | Severity |
|---|------|---------------|----------|
| 1 | **SSL/TLS** | Server accepts TLS 1.2+ connections | High |
| 2 | **Security Headers** | HSTS, CSP, X-Frame-Options, X-Content-Type-Options, etc. | Medium-High |
| 3 | **CORS** | Cross-origin policy doesn't allow arbitrary origins | High |
| 4 | **HTTP Methods** | Dangerous methods (TRACE, etc.) are disabled | Medium |
| 5 | **Authentication** | API endpoints require authentication | Info |
| 6 | **SQL Injection** | Safe probe payloads don't trigger SQL errors | Critical |
| 7 | **XSS** | Payloads are not reflected back unescaped | High |
| 8 | **HTTPS Enforcement** | HTTP requests redirect to HTTPS | High |

## DAST vs SAST vs SCA vs SBOM

| Feature | SBOM | SCA | SAST | DAST |
|---------|------|-----|------|------|
| Purpose | Inventory | Risky deps | Code bugs | Runtime flaws |
| Scope | Packages | Packages | Source code | Live API |
| When | After build | After build | During build | After deploy |
| Needs running server? | No | No | No | **Yes** |
| Location | `Tools/SBOM/` | `Tools/SCA/` | `Tools/SAST/` | `Tools/DAST/` |

## How It Works

### Target

The scanner reads the API base URL from `App.config` (`ApiBaseUrl` key) or accepts it as a parameter. It sends safe, non-destructive HTTP requests to test for common vulnerabilities.

### Build Integration

DAST runs automatically on **Release builds** as the final security step:

```
Build → SBOM → SCA → SAST → DAST
```

**Note:** DAST requires network access to the API server. If the server is unreachable, tests will report errors but the build will not fail (`IgnoreExitCode="true"`).

### Safety

All tests are **safe and non-destructive**:
- SQL injection probes use standard payloads that only detect error-based responses
- XSS probes check if payloads are reflected, not executed
- No data is modified, created, or deleted
- No authentication is bypassed or attempted

## Output Files

| File | Content |
|------|---------|
| `dast-report.json` | All test results with severity ratings |

### Reading the Report (`dast-report.json`)

```json
{
  "scanDate": "2026-04-01T10:00:00Z",
  "targetUrl": "https://bimanage-api-staging-...",
  "summary": {
    "totalTests": 25,
    "passed": 20,
    "failed": 3,
    "errors": 2,
    "critical": 0,
    "high": 1,
    "medium": 2,
    "low": 0
  },
  "findings": {
    "passed": [...],
    "failed": [...],
    "errors": [...]
  }
}
```

- **failed** items need attention — sorted by severity (critical > high > medium > low)
- **errors** mean the test couldn't run (network issue, timeout, etc.)
- **passed** confirms the security control is in place

## Manual Scan

Run DAST against the staging API:

```powershell
cd C:\Users\Admin\Documents\GitHub\BIManageRevit
powershell -ExecutionPolicy Bypass -File Tools\DAST\RunDAST.ps1
```

Against a specific URL:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\DAST\RunDAST.ps1 -BaseUrl "https://your-api-url.com"
```

## Security Headers Checked

| Header | Purpose | Severity if Missing |
|--------|---------|-------------------|
| `Strict-Transport-Security` | Prevents HTTPS downgrade | High |
| `X-Content-Type-Options` | Prevents MIME sniffing | Medium |
| `X-Frame-Options` | Prevents clickjacking | Medium |
| `Content-Security-Policy` | Prevents XSS/injection | Medium |
| `X-XSS-Protection` | Legacy XSS filter | Low |
| `Referrer-Policy` | Controls referrer leakage | Low |
| `Permissions-Policy` | Limits browser APIs | Low |

### Information Leak Headers (should NOT be present)

| Header | Risk |
|--------|------|
| `Server` | Exposes web server software |
| `X-Powered-By` | Exposes framework |
| `X-AspNet-Version` | Exposes .NET version |
| `X-AspNetMvc-Version` | Exposes MVC version |
