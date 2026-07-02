# Pre-Release Deployment Checklist

Centralized place for everything that must be verified before producing a `ZeManageSetup.exe` for distribution. Created after the same dependency / packaging issues bit us repeatedly across multiple releases.

## Files

| File | Purpose |
|---|---|
| **`PreReleaseChecklist.md`** | The full human checklist. 11 sections covering every dependency tripwire we've hit. Start here. |
| **`Verify-Deployment.ps1`** | Automated verification of sections §1–§7 (the parts that don't need a running Revit). Run it after Build-All. |
| `SSDLC Checklist.xlsx` / `ZeManage_Test_Checklist.xlsx` | Pre-existing QA artifacts (functional / security testing) — separate from this dependency-focused checklist. |

## Workflow

```
1.  Close all Revit instances
2.  powershell -File Tools/BuildAll/Build-All.ps1     # Publishes all 7 Release configs
3.  powershell -File Tools/Checklist/Verify-Deployment.ps1
        ↓
        Exit code 0 = automated checks pass
        Exit code 1 = read failure list, fix, goto 2
4.  Open PreReleaseChecklist.md → walk through sections that the script can't automate
        - §0 Environment cleanliness
        - §8 SAST/SCA scripts
        - §9 Installer compile
        - §10 Smoke test on clean machine  ← THE most important one
5.  iscc.exe Tools/Installer/ZeManageInstaller.iss
6.  Tag git commit
7.  Distribute Tools/Installer/Output/ZeManageSetup.exe
```

## Why this exists

These 9 issue patterns have each cost multiple debug sessions. The checklist captures the diagnostic + fix for each so we stop rediscovering them:

| # | Recurring symptom | Section |
|---|---|---|
| 1 | `does not have a resource identified by the URI` (WPF dialogs fail to open on R26) | §3 (manifest) + §2 (ALC) |
| 2 | `Unable to access application services` toast on every command (R26) | §2 |
| 3 | `Cannot assign root instance of type X to type X` | §2 |
| 4 | `Utf8JsonWriter.DisposeAsync does not have an implementation` (R21–R24) | §4 |
| 5 | `Unable to find entry point 'SI<hash>' in DLL 'SQLite.Interop.dll'` | §5 |
| 6 | Cloud Collaborate / external resources break when ZeManage enabled | §6 |
| 7 | `MSB3030: Could not copy obj\…\BIManageRevit.dll` mid-publish | §8 |
| 8 | R24 ribbon tab missing entirely after install | §3 |
| 9 | R25 addin not loaded; journal: `'ManifestSettings' tag is incorrect, should be replaced with 'AddIn'` | §3 (R25's schema rejects `<ManifestSettings>` — that element was added in R26) |

## Adding new checks

When a new dependency issue is found:

1. **Diagnose** — capture the exact error and root cause.
2. **Fix** — apply the change to csproj / Application.cs / .addin / etc.
3. **Document** — add a row to `PreReleaseChecklist.md` "Lessons learned" table.
4. **Automate** — if the check can be done from the file system / a built artifact, add it to `Verify-Deployment.ps1`.

## Running the verifier in CI

`Verify-Deployment.ps1` returns exit 0 on success, 1 on any failure. Hook it into your release pipeline:

```yaml
# .github/workflows/release.yml fragment
- run: powershell -File Tools/Checklist/Verify-Deployment.ps1
- run: iscc.exe Tools/Installer/ZeManageInstaller.iss
  if: success()
```
