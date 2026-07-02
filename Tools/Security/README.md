# Security Reports - BIManageRevit

## Overview

This folder contains automated security scan reports generated during Release builds. Four complementary tools work together to provide comprehensive security coverage across dependencies, source code, and the live API.

```
Release Build
    |
    v
  SBOM  -->  SCA  -->  SAST  -->  DAST
(what you   (what's   (bugs in   (flaws in
  use)      risky)    your code)  live API)
```

---

## 1. SBOM - Software Bill of Materials

### Purpose
Creates a complete inventory of every third-party component (NuGet package) used in the project, including name, version, license, and publisher.

### Why It Matters
- **Supply chain security** - Know exactly what code is running in your application
- **License compliance** - Track open-source licenses across all dependencies
- **Incident response** - When a vulnerability is announced (e.g., Log4Shell), instantly check if you're affected
- **Regulatory compliance** - Required by NIST, EU Cyber Resilience Act, US Executive Order 14028

### Tool
| Property | Value |
|----------|-------|
| Tool | **CycloneDX** (dotnet global tool) |
| Version | 6.0.0 |
| Format | CycloneDX 1.6 (industry standard) |
| NuGet | `dotnet-CycloneDX` (installed via `dotnet-tools.json`) |
| Cost | Free / Open Source |

### How It Works
1. CycloneDX reads the project's `.csproj` and `packages.lock.json`
2. Resolves the full dependency tree (direct + transitive packages)
3. Fetches metadata (license, author, hash) from NuGet.org
4. Outputs a standardized JSON inventory

### Output
| File | Description |
|------|-------------|
| `sbom.json` | Complete component inventory in CycloneDX 1.6 format |

### Sample Output Structure
```json
{
  "bomFormat": "CycloneDX",
  "specVersion": "1.6",
  "metadata": {
    "timestamp": "2026-04-02T08:33:34Z",
    "tools": [{ "name": "CycloneDX module for .NET", "version": "6.0.0.0" }],
    "component": { "name": "BIManageRevit", "version": "0.0.0" }
  },
  "components": [
    {
      "type": "library",
      "name": "CommunityToolkit.Mvvm",
      "version": "8.4.0",
      "licenses": [{ "license": { "id": "MIT" } }],
      "purl": "pkg:nuget/CommunityToolkit.Mvvm@8.4.0"
    }
  ]
}
```

### How to Run
```powershell
# Automatic (Release build)
dotnet build -c "Release R25"

# Manual (to Security folder)
dotnet dotnet-CycloneDX BIManageRevit.csproj -o Tools\Security --filename sbom.json --json --exclude-dev

# Manual (to SBOM folder)
dotnet dotnet-CycloneDX BIManageRevit.csproj -o Tools\SBOM --filename sbom.json --json --exclude-dev
```

---

## 2. SCA - Software Composition Analysis

### Purpose
Scans all third-party NuGet packages for known security vulnerabilities (CVEs), outdated versions, and deprecated/end-of-life packages.

### Why It Matters
- **Vulnerability detection** - Find packages with known exploits before attackers do
- **Maintenance planning** - Track which packages need updating
- **Risk reduction** - Identify deprecated packages before they become unsupported
- **Compliance** - Demonstrate proactive dependency management

### Tool
| Property | Value |
|----------|-------|
| Tool | **.NET CLI** (built-in `dotnet list package`) |
| Version | .NET SDK 10.0.102 |
| Data Source | NuGet.org Advisory Database (CVE/GHSA) |
| Cost | Free (built into .NET SDK) |

### How It Works
1. Runs three separate scans against the NuGet advisory database:
   - `dotnet list package --vulnerable` - Packages with known CVEs
   - `dotnet list package --outdated` - Packages with newer versions
   - `dotnet list package --deprecated` - Packages marked end-of-life
2. Combines results into a single JSON report with counts

### Output
| File | Description |
|------|-------------|
| `sca-report.json` | Combined vulnerability, outdated, and deprecated report |

### Sample Output Structure
```json
{
  "scanDate": "2026-04-02T08:33:36Z",
  "project": "BIManageRevit",
  "tool": "dotnet list package (.NET CLI)",
  "summary": {
    "vulnerablePackages": 0,
    "outdatedPackages": 0,
    "deprecatedPackages": 0
  }
}
```

**Reading the results:**
- `vulnerablePackages = 0` - No packages with known CVEs (good)
- `outdatedPackages > 0` - Packages have newer versions (review and update)
- `deprecatedPackages > 0` - Packages are end-of-life (plan migration)

### How to Run
```powershell
# Automatic (Release build)
dotnet build -c "Release R25"

# Manual (to Security folder)
powershell -ExecutionPolicy Bypass -File Tools\SCA\RunSCA.ps1 -OutputDir Tools\Security

# Manual (to SCA folder)
powershell -ExecutionPolicy Bypass -File Tools\SCA\RunSCA.ps1
```

---

## 3. SAST - Static Application Security Testing

### Purpose
Analyzes the project's C# source code at build time to detect security vulnerabilities like SQL injection, XSS, command injection, insecure cryptography, and hardcoded credentials.

### Why It Matters
- **Shift-left security** - Catch vulnerabilities during development, not after deployment
- **OWASP Top 10 coverage** - Detects the most common web application security risks
- **Zero cost** - Runs during normal build, no external service needed
- **Developer-friendly** - Shows warnings inline in Visual Studio with file/line references

### Tools
| Analyzer | Rules | What It Detects |
|----------|-------|-----------------|
| **Microsoft.CodeAnalysis.NetAnalyzers** (built-in) | CA2xxx, CA3xxx, CA5xxx | .NET security best practices |
| **SecurityCodeScan.VS2019** (NuGet) | SCS0001-SCS0031 | OWASP Top 10 vulnerability patterns |

| Property | Value |
|----------|-------|
| SecurityCodeScan Version | 5.6.7 |
| Integration | NuGet PackageReference in `.csproj` |
| Cost | Free / Open Source |

### How It Works
1. During build, Roslyn analyzers inspect every line of C# code
2. Rules match against known vulnerability patterns (e.g., string concatenation in SQL queries)
3. `RunSAST.ps1` builds with all analyzers at maximum level (`AnalysisLevel=latest-all`)
4. Parses build output for security-specific warnings (CA2xxx, CA3xxx, CA5xxx, SCSxxxx)
5. Categorizes findings by OWASP vulnerability type

### Security Categories Detected
| Category | Rule IDs | Severity |
|----------|----------|----------|
| SQL Injection | CA2100, CA3001, SCS0002 | Critical |
| Command Injection | CA3006, SCS0001 | Critical |
| Insecure Deserialization | CA2300-CA2362, SCS0028 | Critical |
| Hardcoded Credentials | CA5390, SCS0015 | Critical |
| XSS (Cross-Site Scripting) | CA3002, SCS0029 | High |
| Path Traversal | CA3003, SCS0018 | High |
| Weak Cryptography | CA5350, CA5351, SCS0004-0006 | High |
| Insecure SSL/TLS | CA5359, CA5397, CA5398 | High |
| CSRF | CA3147, SCS0016 | Medium |
| Open Redirect | CA3007, SCS0027 | Medium |
| XML Injection | CA3004, CA3075-CA3077 | Medium |
| Insecure Randomness | CA5394, SCS0005 | Medium |

### Output
| File | Description |
|------|-------------|
| `sast-report.json` | Categorized security findings with summary |
| `build-output.log` | Full build output (all warnings) |

### Sample Output Structure
```json
{
  "scanDate": "2026-04-02T08:33:37Z",
  "project": "BIManageRevit",
  "tool": ".NET Roslyn Security Analyzers",
  "analyzers": [
    "Microsoft.CodeAnalysis.NetAnalyzers (built-in)",
    "SecurityCodeScan.VS2019 (NuGet)"
  ],
  "summary": {
    "totalWarnings": 0,
    "securityFindings": 0,
    "qualityFindings": 0,
    "categories": 0
  },
  "securityFindings": [],
  "categorized": {}
}
```

**Reading the results:**
- `securityFindings = 0` - No OWASP-category vulnerabilities in source code (good)
- `qualityFindings` - Non-security code analysis warnings (nullable, style, etc.)
- `categorized` - Groups findings by type (SQL Injection, XSS, etc.) if any found

### How to Run
```powershell
# Automatic (Release build)
dotnet build -c "Release R25"

# Manual (to Security folder)
powershell -ExecutionPolicy Bypass -File Tools\SAST\RunSAST.ps1 -OutputDir Tools\Security

# Manual (specific configuration)
powershell -ExecutionPolicy Bypass -File Tools\SAST\RunSAST.ps1 -Configuration "Debug R25" -OutputDir Tools\Security

# Manual (to SAST folder)
powershell -ExecutionPolicy Bypass -File Tools\SAST\RunSAST.ps1
```

---

## 4. DAST - Dynamic Application Security Testing

### Purpose
Tests the live API server by sending real HTTP requests to detect security misconfigurations, missing headers, injection vulnerabilities, and transport security issues.

### Why It Matters
- **Runtime verification** - Tests what's actually deployed, not just source code
- **Configuration issues** - Catches server misconfigurations that code analysis can't see
- **Attacker perspective** - Simulates what an external attacker would find
- **Header compliance** - Validates OWASP recommended security headers

### Tool
| Property | Value |
|----------|-------|
| Tool | **BIManage DAST Scanner** (custom PowerShell) |
| Version | 1.0.0 |
| Target | API URL from `App.config` (`ApiBaseUrl` key) |
| Cost | Free (built-in) |

### How It Works
1. Reads the API base URL from `App.config` (or accepts `-BaseUrl` parameter)
2. Sends safe, non-destructive HTTP requests to test 8 security areas
3. Analyzes responses for vulnerabilities and misconfigurations
4. Generates a JSON report with pass/fail/severity ratings

**Safety:** All tests are non-destructive. No data is modified, created, or deleted.

### Tests Performed
| # | Test | What It Checks | Severity |
|---|------|---------------|----------|
| 1 | **SSL/TLS** | Server accepts TLS 1.2+ connections | High |
| 2 | **Security Headers** | HSTS, CSP, X-Frame-Options, X-Content-Type-Options, X-XSS-Protection, Referrer-Policy, Permissions-Policy | Medium-High |
| 3 | **Info Leak Headers** | Server, X-Powered-By, X-AspNet-Version not exposed | Low |
| 4 | **CORS** | Cross-origin policy rejects arbitrary origins | High |
| 5 | **HTTP Methods** | TRACE, DELETE, PUT are restricted on root | Medium |
| 6 | **Authentication** | API endpoints require authentication | Info |
| 7 | **SQL Injection** | Safe payloads don't trigger SQL error responses | Critical |
| 8 | **XSS Reflection** | Payloads are not reflected back unescaped | High |
| 9 | **HTTPS Enforcement** | HTTP redirects to HTTPS | High |

### Output
| File | Description |
|------|-------------|
| `dast-report.json` | All test results with severity ratings |

### Sample Output Structure
```json
{
  "scanDate": "2026-04-02T08:33:43Z",
  "targetUrl": "https://bimanage-api-staging-...",
  "tool": "BIManage DAST Scanner (PowerShell)",
  "summary": {
    "totalTests": 26,
    "passed": 18,
    "failed": 8,
    "critical": 0,
    "high": 3,
    "medium": 3,
    "low": 4
  },
  "findings": {
    "passed": [...],
    "failed": [...],
    "errors": [...]
  }
}
```

**Reading the results:**
- `failed` items need attention - sorted by severity (critical > high > medium > low)
- `passed` confirms security controls are in place
- `errors` mean the test couldn't run (network issue, timeout, etc.)

### Latest Scan Results (2026-04-02)

| Test | Result | Severity |
|------|--------|----------|
| SSL/TLS 1.2+ | PASSED | - |
| CORS Policy | PASSED | - |
| SQL Injection (3 payloads) | PASSED | - |
| XSS Reflection (3 payloads) | PASSED | - |
| SignalR Hub Auth | PASSED (401) | - |
| Info Leak: X-Powered-By | PASSED | - |
| Info Leak: X-AspNet-Version | PASSED | - |
| Header: Strict-Transport-Security | FAILED (missing) | High |
| Header: X-Content-Type-Options | FAILED (missing) | Medium |
| Header: X-Frame-Options | FAILED (missing) | Medium |
| Header: Content-Security-Policy | FAILED (missing) | Medium |
| Header: X-XSS-Protection | FAILED (missing) | Low |
| Header: Referrer-Policy | FAILED (missing) | Low |
| Header: Permissions-Policy | FAILED (missing) | Low |
| Info Leak: Server | FAILED (exposes "Kestrel") | Low |

### How to Run
```powershell
# Automatic (Release build)
dotnet build -c "Release R25"

# Manual (to Security folder, uses App.config URL)
powershell -ExecutionPolicy Bypass -File Tools\DAST\RunDAST.ps1 -OutputDir Tools\Security

# Manual (specific URL)
powershell -ExecutionPolicy Bypass -File Tools\DAST\RunDAST.ps1 -BaseUrl "https://your-api.com" -OutputDir Tools\Security

# Manual (to DAST folder)
powershell -ExecutionPolicy Bypass -File Tools\DAST\RunDAST.ps1
```

---

## How Each Tool Complements the Others

| Question | Tool | Answer |
|----------|------|--------|
| What packages do I use? | **SBOM** | Full dependency inventory |
| Are any packages vulnerable? | **SCA** | CVE/GHSA advisory check |
| Does my code have security bugs? | **SAST** | Source code analysis |
| Is my deployed API secure? | **DAST** | Live endpoint testing |

```
SBOM  = "Here are all your ingredients"
SCA   = "This ingredient has been recalled"
SAST  = "Your recipe has a dangerous step"
DAST  = "The finished dish made someone sick"
```

All four together provide **defense in depth** - each catches issues the others miss.

---

## Implementation Details

### Build Integration
All four tools run automatically on Release builds via MSBuild targets in `BIManageRevit.csproj`:

```xml
<!-- Pipeline: Build -> SBOM -> SCA -> SAST -> DAST -->
<Target Name="GenerateSBOM" AfterTargets="Build" />
<Target Name="GenerateSCA"  AfterTargets="GenerateSBOM" />
<Target Name="GenerateSAST" AfterTargets="GenerateSCA" />
<Target Name="GenerateDAST" AfterTargets="GenerateSAST" />
```

### NuGet Audit (Bonus)
In addition to the four tools above, `NuGetAudit` is enabled in the project to warn about vulnerable packages during every `dotnet restore`:

```xml
<NuGetAudit>true</NuGetAudit>
<NuGetAuditLevel>low</NuGetAuditLevel>
<NuGetAuditMode>all</NuGetAuditMode>
```

### File Locations
| Component | Location |
|-----------|----------|
| Reports output | `Tools/Security/` |
| SBOM config | `dotnet-tools.json` (CycloneDX tool) |
| SCA script | `Tools/SCA/RunSCA.ps1` |
| SAST script | `Tools/SAST/RunSAST.ps1` |
| SAST analyzer | `SecurityCodeScan.VS2019` NuGet in `.csproj` |
| DAST script | `Tools/DAST/RunDAST.ps1` |
| MSBuild targets | `BIManageRevit.csproj` (bottom of file) |

### Quick Reference
```powershell
# Run all 4 (automatic)
dotnet build -c "Release R25"

# Run individually (to Tools/Security/)
dotnet dotnet-CycloneDX BIManageRevit.csproj -o Tools\Security --filename sbom.json --json --exclude-dev
powershell -ExecutionPolicy Bypass -File Tools\SCA\RunSCA.ps1 -OutputDir Tools\Security
powershell -ExecutionPolicy Bypass -File Tools\SAST\RunSAST.ps1 -OutputDir Tools\Security
powershell -ExecutionPolicy Bypass -File Tools\DAST\RunDAST.ps1 -OutputDir Tools\Security
```
