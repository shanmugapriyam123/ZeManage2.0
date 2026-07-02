# Pre-Release Deployment Checklist

This checklist captures every dependency / packaging issue that has bitten us in production.
Run **all** items before producing `ZeManageSetup.exe` for distribution.

> **Workflow:** `Build-All.ps1` → verify each section → `compile installer` → smoke-test on a clean machine.

---

## 0. Environment

- [ ] **Revit closed** on dev machine (for ALL versions: 2021–2027). Auto-deploy step copies into `%AppData%\Autodesk\Revit\Addins\<year>\BIManageRevit\` — Revit running there locks files and blocks the build.
- [ ] No stale processes: `tasklist | grep -i Revit` returns empty.
- [ ] Git working tree is clean (`git status` shows no unintended changes).
- [ ] Tag a release branch / git commit before building.

---

## 1. Version metadata (`BIManageRevit.csproj`)

- [ ] `<Version>` = X.Y.Z (e.g. `0.2.5`) — bumped from previous release.
- [ ] `<FileVersion>` matches `<Version>` with `.0` suffix (e.g. `0.2.5.0`).
- [ ] `<InformationalVersion>` matches `<Version>`.
- [ ] **`<AssemblyVersion>` is pinned to `1.0.0.0`** — must NOT bump per release.
  - Reason: WPF MarkupCompiler embeds the AssemblyVersion into BAML pack URIs (`/BIManageRevit;V1.0.0.0;component/...`). Bumping it invalidates already-compiled BAML if obj/ isn't fully regenerated, throwing `IOException: cannot find resource …` at the first dialog's `InitializeComponent`.
- [ ] `<GenerateAssemblyVersionAttribute>false</GenerateAssemblyVersionAttribute>` — leaves loaded identity at default (avoids version-aware static caches getting confused).
- [ ] `<GenerateAssemblyFileVersionAttribute>true</GenerateAssemblyFileVersionAttribute>` — Support dialog reads this for product version display.

`Tools/Installer/ZeManageInstaller.iss`:

- [ ] `MyAppVersion` matches `Version` from csproj.

---

## 2. ALC / Dynamic-loading config

| Setting | Value | File | Why |
|---|---|---|---|
| `<EnableDynamicLoading>` (main project) | **`false`** | `BIManageRevit.csproj` | Forces default-ALC load on R25/R26/R27. `true` makes Revit create a *collectible* ALC and we end up with TWO copies in different ALCs → WPF picks wrong copy → `IOException: does not have a resource identified by the URI` and `Application._instance == null`. |
| `<EnableDynamicLoading>` (Addons project) | **`false`** | `BIManage.Addons/BIManage.Addons.csproj` | Mirrors main. If `true`, Revit creates a separate ALC for `BIManage.Addons.dll` which then transitively loads `BIManageRevit.dll` a SECOND time → duplicate types, `Cannot assign root instance of type X to type X`. |
| `<CopyLocalLockFileAssemblies>` | **`true`** for ALL TFMs | `BIManageRevit.csproj`, `BIManage.Addons.csproj` | With dynamic loading off, the SDK no longer auto-copies transitive NuGet deps. Without this, third-party deps (CommunityToolkit, MaterialDesign, Nice3point) don't make it into bin → publish folder is incomplete. .NET 8+ SDK is smart enough not to copy framework-provided assemblies, so `true` is safe across all TFMs. |

**Verify:**

- [ ] `grep -E "EnableDynamicLoading|CopyLocalLockFile" BIManageRevit.csproj BIManage.Addons/BIManage.Addons.csproj` — confirm values.
- [ ] After build, scan output: `powershell -Command "[byte[]]\$b=[IO.File]::ReadAllBytes('bin/Release R25/BIManageRevit.dll'); if ([Text.Encoding]::UTF8.GetString(\$b).Contains('EnableDynamicLoading')) { 'STILL HAS METADATA' } else { 'CLEAN' }"` — should print `CLEAN`. Same for `BIManage.Addons.dll`.

---

## 3. Manifest (`BIManageRevit.addin`)

Schema availability (verified 2026-04-30 against `AddInManager.dll` string tables):

| Revit | Recognizes `<ManifestSettings>` | Failure mode if present |
|---|---|---|
| 2021-2024 (.NET 4.8) | NO | Silent rejection — manifest does not load, no journal entry. |
| 2025 (.NET 8) | NO | Explicit rejection — journal: `Failed to load add-in manifest file: The 'ManifestSettings' tag is incorrect, should be replaced with 'AddIn'.` |
| 2026+ (.NET 8/10) | YES | Required for `<UseRevitContext>True</UseRevitContext>` to suppress dual-ALC duplicate load. |

- [ ] **R21-R25** deployed `.addin`: must NOT contain `<ManifestSettings>`.
- [ ] **R26-R29** deployed `.addin`: must contain `<ManifestSettings><UseRevitContext>True</UseRevitContext></ManifestSettings>`. Without it, R26 dual-loads BIManageRevit (default ALC scanner + isolated ALC instantiator) → WPF dialog failures.
- [ ] `<Assembly>` path is `BIManageRevit\BIManageRevit.dll` (subfolder, not flat).
- [ ] `<FullClassName>` is `BIManageRevit.BIManage.Revit.Applications.Application`.
- [ ] `<AddInId>` is `C29E6964-7D84-4BD4-AA1C-37163627DBA9` (do NOT change between releases).

The build target `PreserveManifestSettings` (csproj) runs `AfterTargets="PatchManifest;DeployRevitAddinFiles;Build"` and:
- For R21-R25 publish/deploy: leaves Nice3point's `PatchManifest`-stripped output alone (no `<ManifestSettings>`).
- For R26-R29 publish/deploy: re-injects the source `.addin` (which has `<ManifestSettings>`).

The build target `StripR25ManifestSettings` (csproj) is a defensive post-build sweep that removes a stale `<ManifestSettings>` block from the deployed R25 `.addin` if a prior bad build left one — guarantees R25 always loads.

**Verify post-build:**

```bash
for v in 2021 2022 2023 2024 2025 2026 2027; do
  for base in "$APPDATA\Autodesk\Revit\Addins\$v" \
              "$PROGRAMDATA\Autodesk\Revit\Addins\$v"; do
    f="$base/BIManageRevit.addin"
    if [ -f "$f" ]; then
      has_ms=$(grep -c "ManifestSettings" "$f")
      echo "R$v $base: ManifestSettings=$has_ms"
    fi
  done
done
```

- [ ] R2021-R2025 rows show `ManifestSettings=0`.
- [ ] R2026-R2027 rows show `ManifestSettings=2` (open + close tags).

**If R25 fails to load:** check Revit 2025's journal (`%LOCALAPPDATA%\Autodesk\Revit\Autodesk Revit 2025\Journals\journal.NNNN.txt`) for `Failed to load add-in manifest file`. The fix is to strip `<ManifestSettings>` from `%APPDATA%\Autodesk\Revit\Addins\2025\BIManageRevit.addin`.

---

## 4. NuGet dependency consistency

These are the version-conflict tripwires we've hit. Any mismatch silently produces `TypeLoadException: Method X does not have an implementation` at runtime.

### net48 (R21-R24)

- [ ] `System.Text.Json` Version = **`8.0.5`** (NOT 6.0.x).
  - Reason: `CommunityToolkit.Mvvm 8.3.2` transitively requires `Microsoft.Bcl.AsyncInterfaces >= 8.0.0`. S.T.J 6.0.11 was compiled against Bcl 6.x. Loading both → `Utf8JsonWriter.DisposeAsync does not have an implementation` at every `JsonSerializer.Serialize` call.
- [ ] `System.Runtime.CompilerServices.Unsafe` Version = `6.0.0`.
- [ ] `CommunityToolkit.Mvvm` Version = `8.3.2` (NOT 8.4.0 — pulls Unsafe 6.1.0 which conflicts with deployed 6.0.0).
- [ ] `App.config` has binding redirects for `Microsoft.Bcl.AsyncInterfaces` and `System.Runtime.CompilerServices.Unsafe`. Confirm in deployed `.dll.config`.

### net8.0-windows (R25-R26)

- [ ] `CommunityToolkit.Mvvm` Version = `8.4.0`.
- [ ] No explicit `System.Text.Json` reference (runtime-provided).
- [ ] `System.Drawing.Common` `8.0.0`, `System.Security.Cryptography.ProtectedData` `8.0.0`, `System.Configuration.ConfigurationManager` `8.0.0`.

### net10.0-windows (R27)

- [ ] `EnableUnsafeBinaryFormatterSerialization` = `true` in PropertyGroup for R27 (.NET 10 disables BinaryFormatter by default; WPF BAML uses it).
- [ ] No explicit framework polyfill packages (runtime-provided).

### Nice3point Revit packages

- [ ] All 3 packages use the SAME version family per Revit major:
  - net48: `Nice3point.Revit.Toolkit`, `.Api.RevitAPI`, `.Api.RevitAPIUI` all `2024.0.*`
  - net8/net10: all use `$(RevitVersion).0.*` (e.g. `2025.0.*`, `2026.0.*`, `2027.0.*`)
  - **Pin to `.0.*` minor**, NOT `.*` — otherwise resolves to latest and breaks on users with older Revit service packs (`RevitAPI 26.4.0.0 conflicts with preloaded 26.3.0.0`).

**Verify:**

```bash
grep -E "Nice3point|System\.Text\.Json|CommunityToolkit\.Mvvm|Bcl\.AsyncInterfaces" BIManageRevit.csproj BIManage.Addons/BIManage.Addons.csproj
```

---

## 5. Native dependencies (SQLite)

`SQLite.Interop.dll` is a native PE binary; if missing or wrong arch → `Unable to find entry point 'SI04b638e115f7beb4' in DLL 'SQLite.Interop.dll'` (a hash-mangled name that's misleading — usually means the DLL itself is missing or mismatched).

- [ ] csproj `CopySQLiteNativeDll` target runs `AfterTargets="CoreBuild" BeforeTargets="PublishRevitAddinFiles"` (NOT `AfterTargets="Build"` — Build can fail on the deploy-copy step before our copy fires).
- [ ] Fallback paths point to **Stub** packages (where the binary actually lives), NOT `system.data.sqlite.core` (empty metapackage):
  - net48: `$(UserProfile)\.nuget\packages\stub.system.data.sqlite.core.netframework\1.0.118\build\net46\{x64,x86}\SQLite.Interop.dll`
  - net8/net10: `$(UserProfile)\.nuget\packages\stub.system.data.sqlite.core.netstandard\1.0.118\runtimes\win-x64\native\SQLite.Interop.dll`
- [ ] `App.config` has `<probing privatePath="x64;x86" />` so System.Data.SQLite finds the native interop in subfolders.

**Verify per-config:**

```bash
for cfg in "Release R21" "Release R24" "Release R25" "Release R27"; do
  echo "=== $cfg ==="
  find "bin/$cfg/publish" -iname "SQLite.Interop.dll" 2>/dev/null
done
```

Expected:
- R21-R24 (net48): root, `x64/`, `x86/`.
- R25-R26 (net8): root, `runtimes/win-x64/native/`.
- R27 (net10): root, `runtimes/win-x64/native/`.

---

## 6. Newtonsoft.Json isolation

Newtonsoft.Json conflicts with Revit's own copy → Cloud Collaborate / external resources break (`MissingMethodException` on `JsonSerializerSettings`).

- [ ] `Newtonsoft.Json` is referenced ONLY in `BIManage.Addons.csproj`, NOT main project.
- [ ] Main project uses `System.Text.Json` exclusively (`grep -r "using Newtonsoft" BIManage/` returns 0 results).
- [ ] Build deploys `Newtonsoft.Json.dll` to `lib/` subfolder (NOT flat root). `BuildAddons` target in csproj has `<AddonLibFiles>` for it.
- [ ] `RemoveFlatNewtonsoftJson` target deletes any flat `Newtonsoft.Json.dll` from OutputPath and deployed Addins folder.
- [ ] `RegisterManagedAssemblyResolvers` in `BIManage/Revit/Applications/Application.cs` probes `lib/` on-demand for our private deps.

**Verify post-build:**

```bash
for cfg in "Release R24" "Release R25" "Release R27"; do
  echo "=== $cfg ==="
  echo "  flat: $(ls bin/$cfg/Newtonsoft.Json.dll 2>/dev/null && echo PRESENT || echo absent)"
  echo "  lib/: $(ls bin/$cfg/lib/Newtonsoft.Json.dll 2>/dev/null && echo PRESENT || echo absent)"
done
```

Expected: `flat: absent`, `lib/: PRESENT`.

---

## 7. Build pipeline integrity

- [ ] `dotnet clean` followed by `dotnet build` for each TFM produces zero `error CS` (warnings ok).
- [ ] `obj/<Configuration>/BIManage/Views/*/<Dialog>.g.cs` pack URIs all read `;V1.0.0.0;` (NOT a stale older version).
- [ ] `obj/project.assets.json` contains the expected `Stub.System.Data.SQLite.Core.NetFramework` (net48) or `Stub.System.Data.SQLite.Core.NetStandard` (net8/net10).
- [ ] No `_wpftmp.csproj` orphans in repo root (`ls *_wpftmp.csproj 2>/dev/null` empty).

**Run `Build-All.ps1`:**

```bash
powershell -File Tools/BuildAll/Build-All.ps1
```

- [ ] All 7 configs report `[+] Release Rxx`.
- [ ] No FAILED entries (file lock failures = Revit was running, retry after closing).

---

## 8. SAST / SCA scripts (don't poison the build)

- [ ] `Tools/SAST/RunSAST.ps1` uses `IntermediateOutputPath=obj\SAST\` and `OutputPath=bin\SAST\` — keeps SAST's nested `dotnet build` from corrupting the parent publish's obj.
- [ ] `Tools/SAST/RunSAST.ps1` passes `DeployRevitAddin=false` and `IncludeAddons=false` to skip Nice3point deploy + addons during analysis.
- [ ] `Tools/SAST/RunSAST.ps1` and `Tools/SCA/RunSCA.ps1` pass explicit project file path `BIManageRevit.csproj` (directory has both `.csproj` and `.slnx` — bare `dotnet` commands hit MSB1011).

---

## 9. Installer (`Tools/Installer/ZeManageInstaller.iss`)

- [ ] `BuildRoot` resolves to `bin/` and `VP` resolves to `bin/Release Rxx/publish/Revit XXXX Release Rxx addin/`.
- [ ] Source uses **recursive wildcard** (`Source: "{#VP}\BIManageRevit\*"; Flags: ignoreversion recursesubdirs createallsubdirs;`) NOT per-file enumeration. Per-file lists silently break when new NuGet deps are added.
- [ ] `BIManage.Addons.dll` is the single explicit `Source:` entry (separate user checkbox `InstallAddon{#VS}`).
- [ ] Defensive explicit copies of `SQLite.Interop.dll` (root + x64/ + x86/) — wildcard catches them but defense-in-depth.
- [ ] `PreviousInstall` cleanup removes both `%AppData%\Autodesk\Revit\Addins\<year>\BIManageRevit*` AND `%ProgramData%\Autodesk\Revit\Addins\<year>\BIManageRevit*` — prevents duplicate addin loads.

**Compile:**
```
iscc.exe Tools/Installer/ZeManageInstaller.iss
```

- [ ] Output `Tools/Installer/Output/ZeManageSetup.exe` exists.
- [ ] File size is reasonable (typically 20–50 MB).

---

## 10. Smoke test on clean machine

The MOST important step. A "build succeeded" doesn't mean a "deploy works".

For each Revit version (at minimum R24 net48, R25 net8.0, R27 net10.0):

1. **Uninstall** any previous ZeManage from Apps & Features.
2. **Delete** stale per-user folders: `%AppData%\Autodesk\Revit\Addins\<year>\BIManageRevit*` AND `%ProgramData%\Autodesk\Revit\Addins\<year>\BIManageRevit*`.
3. **Run installer** as the target user (NOT admin unless production deploys via SCCM).
4. **Open Revit**.
5. - [ ] ZeManage ribbon tab appears.
6. - [ ] Logger writes to `%LocalAppData%\BIManageRevit\Logs\BIManageRevit_<date>_<time>.log`.
7. - [ ] Log shows `[AsmIdentity] Loaded BIManageRevit AssemblyVersion=1.0.0.0 FileVersion=<version>`.
8. - [ ] Log does NOT show `DUPLICATE LOAD DETECTED`.
9. - [ ] Log does NOT show `TypeLoadException` or `FileNotFoundException`.
10. - [ ] Log does NOT show `[AsmResolver] UNRESOLVED:` for any of our own DLLs.
11. - [ ] Open SignIn dialog → renders, no `does not have a resource identified by the URI`.
12. - [ ] Open Support dialog → renders, version displays correctly.
13. - [ ] Open Sync Activity dialog → renders.
14. - [ ] Open NWC Export (Addons) → renders, Newtonsoft.Json works (folder browse loads APS data).
15. - [ ] Cloud Collaborate → File → Initiate Cloud Collaboration works (Newtonsoft isolation verified).
16. - [ ] Open a workshared local model → SQLite repository initializes, no `Unable to find entry point` error.

---

## 11. Post-release verification

- [ ] `Tools/Security/sast-report.json` has `securityFindings.length == 0` (or all triaged).
- [ ] `Tools/Security/sca-report.json` has no `vulnerableFindings`.
- [ ] `Tools/Security/sbom.json` matches deployed assemblies.
- [ ] `Tools/BuildAll/Build-All.log` archived alongside the installer.
- [ ] Tag the git commit: `git tag v<version> && git push origin v<version>`.

---

## Quick diagnostic commands

```bash
# Which TFM is the obj/ currently restored for?
grep -oE '"(net[0-9\.]+-windows[0-9\.]*|\.NETFramework,Version=v[0-9\.]+)": \{' obj/project.assets.json | sort -u

# Which version of System.Text.Json + Bcl.AsyncInterfaces are deployed?
for dll in "Microsoft.Bcl.AsyncInterfaces.dll" "System.Text.Json.dll"; do
  v=$(powershell -Command "[Reflection.Assembly]::LoadFile((Resolve-Path 'bin/Release R24/$dll').Path).GetName().Version" 2>&1 | tail -1)
  echo "  $dll = $v"
done

# Are there two copies of BIManageRevit at runtime? (run from a Revit add-in)
grep -E "AsmIdentity|DUPLICATE LOAD" "$LOCALAPPDATA/BIManageRevit/Logs/$(ls -t $LOCALAPPDATA/BIManageRevit/Logs | head -1)"

# Are all needed dependencies in the publish folder for Rxx?
find "bin/Release Rxx/publish" -name "*.dll" | sort
```

---

## Lessons learned (don't re-introduce these)

| Symptom | Root cause | Fix anchor |
|---|---|---|
| `does not have a resource identified by the URI` (R26) | Duplicate load: scanner default ALC + isolated ALC | §2 (`EnableDynamicLoading=false` in BOTH csprojs) + §3 (`UseRevitContext=True` only on R26+) |
| `Unable to access application services` (R26) | Same — `_instance` set in wrong ALC copy | §2 |
| `Cannot assign root instance of type X to type X` | Loaded same DLL into Default + isolated ALC; `Baml2006Reader` resolves type from one, RootObjectInstance is from the other | §2 — never `LoadFromAssemblyPath` our own DLL into Default ALC |
| `Utf8JsonWriter.DisposeAsync does not have an implementation` (net48) | System.Text.Json 6.x + Microsoft.Bcl.AsyncInterfaces 8.x version skew | §4 (S.T.J 8.0.5 for net48) |
| `Unable to find entry point 'SI<hash>' in DLL 'SQLite.Interop.dll'` | Native interop DLL missing from deploy | §5 (CopySQLiteNativeDll fallback paths to Stub.* packages) |
| Cloud Collaborate / external resources fail when ZeManage enabled | Newtonsoft.Json type-identity conflict with Revit's copy | §6 (deploy to `lib/` only, on-demand resolve) |
| `MSB3030: Could not copy obj\…\BIManageRevit.dll` (random Release configs) | SAST script's nested build shares `obj/` with parent publish | §8 (isolated `obj\SAST\`) |
| R24 ribbon tab missing entirely | `<ManifestSettings>` in deployed `.addin` — Revit 2024 silently rejects unknown XML | §3 (PreserveManifestSettings target restricted to R26+) |
| R25 addin not loaded — journal: `'ManifestSettings' tag is incorrect, should be replaced with 'AddIn'` | `<ManifestSettings>` in deployed R25 `.addin`; R25 schema does NOT recognize the element (added in R26) | §3 (PreserveManifestSettings excludes R25; `StripR25ManifestSettings` defensive scrub target) |
| NETSDK1005 on config switch | `obj/project.assets.json` has wrong TFM | csproj `EnsureCurrentTfmAssets` self-heal target |

---

*Last updated: see `git log -- Tools/Checklist/PreReleaseChecklist.md` for revision history.*
