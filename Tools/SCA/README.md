# SCA - Software Composition Analysis

## What is SCA?

**Software Composition Analysis (SCA)** is a security practice that automatically scans your project's third-party dependencies (NuGet packages) to identify:

1. **Vulnerabilities** — Known security flaws (CVEs) in packages you use
2. **Outdated Packages** — Packages with newer versions available
3. **Deprecated Packages** — Packages marked as legacy or end-of-life by their authors

## Why Use SCA?

| Risk | Without SCA | With SCA |
|------|-------------|----------|
| Security vulnerabilities | Unknown until exploited | Detected at build time |
| Outdated dependencies | Manual tracking | Automated alerts |
| Deprecated packages | Discover too late | Flagged immediately |
| Compliance audits | Manual effort | JSON reports ready |

SCA is essential for:
- **Security compliance** — Identify and patch vulnerable dependencies before deployment
- **Risk management** — Know which packages are end-of-life before they become a problem
- **Maintenance planning** — Track which packages need updating

## How It Works

### Tool: .NET CLI (built-in, free)

No external tool installation needed. The .NET SDK includes three SCA commands:

```bash
# Scan for known vulnerabilities (CVEs from NuGet advisory database)
dotnet list package --vulnerable --format json

# Check for newer versions of all packages
dotnet list package --outdated --format json

# Find packages marked as deprecated/legacy by authors
dotnet list package --deprecated --format json
```

### Build Integration

SCA runs automatically after every build via an MSBuild target in `BIManageRevit.csproj`. The `RunSCA.ps1` script executes all 3 scans and generates JSON output.

### Data Sources

- **NuGet.org Advisory Database** — For vulnerability data (CVEs, GHSA advisories)
- **NuGet.org Package Metadata** — For version and deprecation info

## Output Files

All output is generated in `Tools/SCA/`:

| File | Content |
|------|---------|
| `sca-report.json` | Combined report with summary + all 3 scan results |
| `vulnerabilities.json` | Packages with known CVEs/security advisories |
| `outdated.json` | Packages with newer versions available |
| `deprecated.json` | Packages marked as legacy/end-of-life |

### Reading the Combined Report (`sca-report.json`)

```json
{
  "scanDate": "2026-03-17T17:42:00Z",
  "project": "BIManageRevit",
  "summary": {
    "vulnerablePackages": 0,
    "outdatedPackages": 8,
    "deprecatedPackages": 1
  },
  "vulnerabilities": { ... },
  "outdated": { ... },
  "deprecated": { ... }
}
```

- **summary.vulnerablePackages** — Number of packages with known CVEs (should be 0)
- **summary.outdatedPackages** — Number of packages with newer versions
- **summary.deprecatedPackages** — Number of packages marked legacy

## SCA vs SBOM

| Feature | SBOM | SCA |
|---------|------|-----|
| Purpose | List what you use | Find what's wrong |
| Output | Component inventory | Risk assessment |
| Tool | CycloneDX | dotnet CLI |
| Location | `Tools/SBOM/` | `Tools/SCA/` |
| Data | Names, versions, licenses | Vulnerabilities, updates, deprecations |

Both work together: SBOM tells you **what** you have, SCA tells you **what to fix**.

## Manual Scan

To run SCA manually without building:

```powershell
cd C:\Users\Admin\Documents\GitHub\BIManageRevit
powershell -ExecutionPolicy Bypass -File Tools\SCA\RunSCA.ps1
```
