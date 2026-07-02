# SAST - Static Application Security Testing

## What is SAST?

**Static Application Security Testing (SAST)** analyzes your source code at build time to find security vulnerabilities **before** the application runs. It catches issues like:

1. **SQL Injection** — Unsanitized queries (CA2100, CA3001, SCS0002)
2. **XSS** — Cross-site scripting via unescaped output (CA3002, SCS0029)
3. **Path Traversal** — File access outside intended directories (CA3003, SCS0018)
4. **Command Injection** — Unsanitized shell commands (CA3006, SCS0001)
5. **Insecure Deserialization** — Untrusted data deserialization (CA2300-CA2362)
6. **Weak Cryptography** — MD5, SHA1, weak key sizes (CA5350, CA5351)
7. **Hardcoded Credentials** — Secrets in source code (CA5390, SCS0015)
8. **Insecure SSL/TLS** — Disabled certificate validation (CA5359, CA5397)

## SAST vs SCA vs SBOM

| Feature | SBOM | SCA | SAST |
|---------|------|-----|------|
| Purpose | List what you use | Find risky dependencies | Find bugs in your code |
| Scope | Third-party packages | Third-party packages | Your source code |
| When | After build | After build | During build |
| Tool | CycloneDX | dotnet CLI | Roslyn Analyzers |
| Location | `Tools/SBOM/` | `Tools/SCA/` | `Tools/SAST/` |

## How It Works

### Tools

Two Roslyn-based analyzers run during build:

| Analyzer | Rules | Coverage |
|----------|-------|----------|
| **Microsoft.CodeAnalysis.NetAnalyzers** (built-in) | CA2xxx, CA3xxx, CA5xxx | .NET security best practices |
| **SecurityCodeScan.VS2019** (NuGet) | SCS0001-SCS0031 | OWASP Top 10 patterns |

Both are **zero-config** — they run automatically during `dotnet build` and produce warnings in the build output.

### Build Integration

SAST runs automatically on **Release builds** via MSBuild target in `BIManageRevit.csproj`:

```
Build → SBOM → SCA → SAST → DAST
```

The `RunSAST.ps1` script:
1. Builds with all analyzers enabled (`AnalysisLevel=latest-all`)
2. Parses security-relevant warnings from build output
3. Categorizes findings by OWASP vulnerability type
4. Generates a JSON report

## Output Files

| File | Content |
|------|---------|
| `sast-report.json` | Categorized security findings + summary |
| `build-output.log` | Full build output with all analyzer warnings |

### Reading the Report (`sast-report.json`)

```json
{
  "scanDate": "2026-04-01T10:00:00Z",
  "project": "BIManageRevit",
  "summary": {
    "totalWarnings": 1050,
    "securityFindings": 0,
    "qualityFindings": 1050,
    "categories": 0
  },
  "securityFindings": [],
  "categorized": {}
}
```

- **securityFindings** = 0 means no OWASP-category vulnerabilities detected
- **qualityFindings** are non-security code analysis warnings (nullable, style, etc.)
- **categorized** groups findings by vulnerability type (SQL Injection, XSS, etc.)

## Manual Scan

Run SAST without a full Release build:

```powershell
cd C:\Users\Admin\Documents\GitHub\BIManageRevit
powershell -ExecutionPolicy Bypass -File Tools\SAST\RunSAST.ps1
```

With a specific configuration:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\SAST\RunSAST.ps1 -Configuration "Debug R25"
```

## Security Rule Categories

| Category | Rules | Severity |
|----------|-------|----------|
| SQL Injection | CA2100, CA3001, SCS0002 | Critical |
| XSS | CA3002, SCS0029 | High |
| Command Injection | CA3006, SCS0001 | Critical |
| Path Traversal | CA3003, SCS0018 | High |
| Deserialization | CA2300-CA2362, SCS0028 | Critical |
| Weak Cryptography | CA5350, CA5351, SCS0004-0006 | High |
| Insecure SSL/TLS | CA5359, CA5397, CA5398 | High |
| CSRF | CA3147, SCS0016 | Medium |
| Hardcoded Credentials | CA5390, SCS0015 | Critical |
| Open Redirect | CA3007, SCS0027 | Medium |
