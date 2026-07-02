# Changelog

All notable changes to ZeManage (BIManageRevit) are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/);
versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

While we are pre-1.0, minor-version bumps may include breaking changes; patch
bumps will not.

## [Unreleased]

## [0.7.18-beta] - 2026-05-26

First public beta. Targets Revit 2021 through 2027.

### Added
- Environment switch tooling (`Tools/Environment Switch/`) to flip
  `ApiBaseUrl` and `SignalR:HubUrl` between staging and production
  across `App.config`, per-Revit `dll.config` files, and the installer
  `.iss` in one command.
- Developer panel on the Revit ribbon (gated by `BIMANAGE_DEV_MODE=1`)
  for the test commands (SayHello, SendCode, Safety) so they no longer
  appear on customer ribbons by default.
- `is_crashed` field on `document_sessions` and crash-aware reconciliation
  on startup.
- Offline evidence-upload replay queue: evidence captured while offline is
  re-uploaded once connectivity returns.
- SignalR listeners for `ForceLogout` and `ForceTokenRefresh` server events.

### Changed
- Admin "Sign Out" is now non-destructive: when an admin elevation is
  present, sign-out drops the admin token slot and demotes the in-memory
  user to a regular user; the device session, refresh token, and identity
  file are preserved. Non-admin sign-out still performs the full destructive
  teardown (unchanged).
- Audit log HMAC verification self-heals on key mismatch (re-HMACs the row
  under the current key rather than skipping it forever). Resolves the
  case where rows logged before a DPAPI key rotation or profile migration
  silently stopped syncing.
- Reduced log noise: several high-frequency diagnostic lines downgraded
  from Info to Debug across CommandInterceptionService, CommandProtectionBinding,
  IdlingService, EventProtectionSyncService, and the protection repositories.

### Fixed
- Pin-protection dialog failing to open for workshared models whose local
  `ModelPath` GUID differs from the server-registered GUID. The pin path
  now performs the same GUID reconciliation
  (`FindExistingModelGuidAsync(modelName, centralPath)`) that the
  registration path already used.
- Deep-analysis "Cancel" button now actually stops the expensive metrics
  collection; the post-cancel UI flash is suppressed.
- Several sync-queue dedupe and rule-freshness edge cases (see commits
  `ced4a2f`, `a834113`).

### Known issues
See [docs/releases/v0.7.18-beta/KNOWN_ISSUES.md](docs/releases/v0.7.18-beta/KNOWN_ISSUES.md).

### Environment
This beta build is pinned to **production** (`https://api.zemanage.com`).
Use `Tools/Environment Switch/Switch-Staging.cmd` to retarget a build at
staging before compiling installers for internal QA.

[Unreleased]: https://github.com/RnDConserve/BIManageRevit/compare/v0.7.18-beta...HEAD
[0.7.18-beta]: https://github.com/RnDConserve/BIManageRevit/releases/tag/v0.7.18-beta
