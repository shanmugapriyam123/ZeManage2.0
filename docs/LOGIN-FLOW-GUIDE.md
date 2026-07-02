# Login Flow Guide — BIManageRevit

End-to-end reference for how authentication works in the Revit plugin: ribbon click → API call → token storage → ribbon update, including token refresh, logout, the two-token model (device vs admin), and failure paths.

---

## High-Level Flow Diagram

```
[Revit Ribbon: "Sign In" button]
        ↓
SignInCommand.Execute()           ── pulls auth services from DI
        ↓
SignInDialog (WPF window)         ── owned by Revit main window
        ↓
SignInViewModel                   ── if already authenticated, restore from disk
        ↓
User types email + password → SignInAsync()
        ↓
AuthApiService.AdminLoginAsync(machineId, sessionId, email, password)
        ↓
POST {ApiBaseUrl}/api/v1/tenant/device/auth/admin-login
        ↓
LoginResponse { accessToken, refreshToken, roleName, profileId, … }
        ↓
AuthTokenManager.SetTokensFromLogin()
   • access token → memory (admin slot)
   • refresh token → %APPDATA%\BIManage\auth.dat (DPAPI)
   • _isAdminSession = true
        ↓
Build UserIdentity → UserService.SetCurrentUser()
        ↓
SecureTokenStorage.SaveUserIdentity() → %APPDATA%\BIManage\identity.dat (DPAPI)
        ↓
ViewModel.IsAuthenticated = true → dialog swaps to "signed-in" view
        ↓
SignInCommand → RibbonVisibilityManager.UpdateAllButtonVisibility()
        ↓
   ✓ Logged in. Subsequent API calls use AuthenticatedHttpClient
     which attaches Bearer token and auto-refreshes on 401.
```

---

## Step-by-Step

### 1. Entry — Ribbon click
- Ribbon button registered in [Application.cs:3060](../BIManage/Revit/Applications/Application.cs#L3060)
- Triggers `SignInCommand` (an `IExternalCommand`)

### 2. Command pulls services from DI
- [SignInCommand.cs:19-89](../BIManage/Commands/RibbonCommands/SignInCommand.cs#L19-L89)
- Resolves from `ServiceRegistry`: `AuthApiService`, `AuthTokenManager`, `IUserService`, `SecureTokenStorage`, `SessionSyncService`, `IRevitContext`, `ILogger`
- If any are missing → TaskDialog "Authentication services not available", abort

### 3. Show SignInDialog
- [SignInDialog.xaml.cs](../BIManage/Views/Auth/SignInDialog.xaml.cs)
- Borderless modal, owned by Revit main window
- Constructs `SignInViewModel` with the services from step 2

### 4. ViewModel init — restore prior session if any
- [SignInViewModel.cs:104-126](../BIManage/ViewModels/Auth/SignInViewModel.cs#L104-L126)
- If `IUserService.IsAuthenticated`, repopulate `AuthenticatedUser`, `Email`, `CompanyDisplay`, `RoleDisplay` from `SecureTokenStorage` — user lands directly on "signed-in" view, no re-login needed

### 5. User enters credentials, clicks Sign In
- Email + password fields with placeholder + show/hide toggle
- [SignInDialog.xaml.cs](../BIManage/Views/Auth/SignInDialog.xaml.cs)

### 6. Validate and call API
- [SignInViewModel.SignInAsync()](../BIManage/ViewModels/Auth/SignInViewModel.cs#L129-L276)
- Validates: email non-empty, valid format, password non-empty
- Gets `machineId` via `MachineIdentifier.GetMachineId()` + session ID from `IRevitContext`
- Calls `_authApi.AdminLoginAsync(machineId, sessionId, email, password)`

### 7. HTTP POST
- [AuthApiService.cs:304-328](../BIManage/Infrastructure/Auth/AuthApiService.cs#L304-L328)
- `POST {ApiBaseUrl}/api/v1/tenant/device/auth/admin-login`
- Body: `{ machineId, sessionId, username, password }`
- `ApiBaseUrl` = `https://api.zemanage.com` (from [App.config](../App.config))

### 8. Token storage on success
- [AuthTokenManager.SetTokensFromLogin()](../BIManage/Infrastructure/Auth/AuthTokenManager.cs#L162-L176)
- **Access token** → memory only (`_adminAccessToken`, expires per `tokenExpiry` or +1h)
- **Refresh token** → DPAPI-encrypted file `%APPDATA%\BIManage\auth.dat`
- Sets `_isAdminSession = true` so future refreshes hit `/admin-refresh` (not `/refresh`)
- Two parallel token slots exist: **admin** (has `profileId`, for writes) vs **device** (heartbeats); admin wins when both present

### 9. Identity persisted
- [SignInViewModel.cs:182-215](../BIManage/ViewModels/Auth/SignInViewModel.cs#L182-L215)
- Parses `roleName` → `IsCompanyAdmin` / `IsProjectAdmin` flags
- Builds `UserIdentity` (userId, email, companyId, profileId, role flags, admin token)
- `_userService.SetCurrentUser(identity)` — in-memory
- `_secureStorage.SaveUserIdentity(...)` — DPAPI-encrypted to `%APPDATA%\BIManage\identity.dat`
- Implementation: [SecureTokenStorage.cs](../BIManage/Infrastructure/Auth/SecureTokenStorage.cs) (DPAPI `CurrentUser` scope + file lock + IO retry)

### 10. UI swaps to signed-in view
- [SignInDialog.xaml.cs](../BIManage/Views/Auth/SignInDialog.xaml.cs)
- Hides form panel, shows signed-in panel with role badge:
  - **C** Company Admin — "Full access across all projects"
  - **P** Project Admin — "Manage assigned projects"
  - **U** User — "Standard access with protections"

### 11. Ribbon refresh
- [SignInCommand.cs:59-78](../BIManage/Commands/RibbonCommands/SignInCommand.cs#L59-L78)
- On `dialog.Success == true`, calls `RibbonVisibilityManager.UpdateAllButtonVisibility()` to reveal admin-only buttons per role flags

---

## Bootstrap (happens once at Revit startup, before any login)

[RevitBootstrapper.cs:741-781](../BIManage/Revit/Applications/RevitBootstrapper.cs#L741-L781)
- Creates and registers as singletons: `SecureTokenStorage`, `AuthApiService`, `AuthTokenManager`, `AuthenticatedHttpClient`
- If `identity.dat` exists and contains a `ProfileId`, calls `tokenManager.RestoreAdminSessionFlag(true)` — ensures refresh routes to `/admin-refresh` even before first user action

---

## How subsequent API calls stay authenticated

[AuthenticatedHttpClient.SendWithAuthAsync()](../BIManage/Infrastructure/Auth/AuthenticatedHttpClient.cs#L180-L248)

1. Pull current token via `tokenManager.GetAccessTokenAsync()`
2. Attach `Authorization: Bearer <token>`
3. If response is 401:
   - Force-expire in-memory token
   - Call `GetAccessTokenAsync()` again → triggers refresh against `/admin-refresh`
   - Retry the request **once**
4. If retry also 401 → fire `AuthExhausted` event → user must re-login

Refresh internals: [AuthTokenManager.RefreshAccessTokenAsync()](../BIManage/Infrastructure/Auth/AuthTokenManager.cs#L293-L359) — on 400/401/403 throws `TokenRejectedException` and **wipes** the stored refresh token (it's revoked); on network errors keeps the refresh token for retry.

---

## Logout

[SignInViewModel.SignOut()](../BIManage/ViewModels/Auth/SignInViewModel.cs#L278-L303)

1. Fire-and-forget `POST /api/v1/tenant/device/auth/admin-logout` with sessionId + admin token (server-side revoke)
2. `tokenManager.ClearTokens()` — wipes both memory slots + deletes `auth.dat`
3. `secureStorage.ClearUserIdentity()` — deletes `identity.dat`
4. `userService.Logout()` + `UserDisplayNameCache.Clear()`
5. ViewModel flips back to form view

---

## Auto-logout triggers

| Event | Source | Trigger | Result |
|---|---|---|---|
| `TokenRejected` | [RevitBootstrapper.cs:865-890](../BIManage/Revit/Applications/RevitBootstrapper.cs#L865-L890) | Refresh returns 400/401/403 (token revoked) | Wipe identity, message "Your admin session has expired. Please sign in again." |
| `AuthExhausted` | [RevitBootstrapper.cs:829-863](../BIManage/Revit/Applications/RevitBootstrapper.cs#L829-L863) | 401 with no stored refresh token (throttled to 1/2min) | Wipe all tokens, prompt re-login |

---

## Failure paths during login

| HTTP / cause | Message shown |
|---|---|
| 401 | "Invalid email or password." |
| 403 | "Access denied. Your account may be restricted." |
| 404 | "Service not available. Please contact your administrator." |
| 5xx | "Server is temporarily unavailable. Please try again later." |
| Timeout (`TaskCanceledException`) | "Connection timed out. Please check your network and try again." |
| DNS/refused (`HttpRequestException`) | "Unable to reach the server. Please check your network connection." |

---

## Config keys ([App.config](../App.config))

| Key | Value | Used by |
|---|---|---|
| `ApiBaseUrl` | `https://api.zemanage.com` | All REST calls |
| `LoginUrl` | `https://zemanage.com/login` | Frontend login URL |
| `SignalR:HubUrl` | `https://api.zemanage.com/hubs/notifications` | Real-time notifications |

---

## Persisted files

| File | Contents | Encryption |
|---|---|---|
| `%APPDATA%\BIManage\auth.dat` | Refresh token | DPAPI (CurrentUser) |
| `%APPDATA%\BIManage\identity.dat` | `StoredUserIdentity` JSON (userId, email, company, role, profileId) | DPAPI (CurrentUser) |

Access tokens are **never** written to disk — memory only.

---

## Device Token vs User (Admin) Token — When Each Is Obtained

The app maintains **two parallel token slots** in [AuthTokenManager.cs:24-33](../BIManage/Infrastructure/Auth/AuthTokenManager.cs#L24-L33):

| Slot | Field | Source endpoint | Has `profileId`? | Used for |
|---|---|---|---|---|
| **Device** | `_deviceAccessToken` / `_deviceExpiresAt` | `POST /validate-device` (machineId only, no password) | No | Heartbeats, session sync, read-only metrics |
| **Admin** | `_adminAccessToken` / `_adminExpiresAt` | `POST /admin-login` (email + password) | Yes | Protection writes, command/event/rule changes |

`_isAdminSession` flag routes refresh: `true` → `/admin-refresh`, `false` → `/device/auth/refresh`.

### Timeline — when each token is obtained

```
T0  Revit launches
     │
T1  RevitBootstrapper.Initialize() runs (logging, core, identity, persistence)
     │   ── no auth yet
     │
T2  RegisterApiServices() phase
     │   ── SecureTokenStorage created → loads persisted refresh token + identity.dat
     │   ── AuthApiService, AuthTokenManager, AuthenticatedHttpClient registered
     │   ── If identity.dat had ProfileId → RestoreAdminSessionFlag(true)
     │      (routes future refresh to /admin-refresh)
     │
T3  AutoAuthenticateDeviceAsync() fires (background, 5s timeout, non-blocking)
     │   [RevitBootstrapper.cs:1486-1555]
     │   │
     │   ├─ Step A: Try stored refresh token
     │   │     GetAccessTokenAsync() → if admin flag set: /admin-refresh,
     │   │     else: /device/auth/refresh → populates the matching slot.
     │   │
     │   ├─ Step B: If no stored token, call ValidateDeviceAsync(machineId)
     │   │     POST /validate-device  body: { "machineId": "..." }
     │   │     [AuthApiService.cs:159-185]
     │   │     ↓
     │   │     SetTokensFromDevice() [AuthTokenManager.cs:186-211]
     │   │       • _deviceAccessToken / _deviceExpiresAt populated
     │   │       • device refresh token persisted ONLY if no admin session
     │   │
     │   └─ Step C: If validation fails → red device-status icon, no dialog
     │
T4  SignalR connects using GetAccessTokenAsync() as bearer source
     │   ── prefers admin if _isAdminSession, else device
     │
T5  SessionSyncService heartbeat starts (uses device token)
     │   [RevitBootstrapper.cs:1047-1053, SessionSyncService.cs]
     │
═══════════════════════════════════════════════════════════════════
T6  User clicks Sign In on ribbon ← ADMIN TOKEN ONLY OBTAINED HERE
     │   SignInViewModel.SignInAsync()
     │   AuthApiService.AdminLoginAsync(machineId, sessionId, email, password)
     │   POST /admin-login
     │   ↓
     │   SetTokensFromLogin() [AuthTokenManager.cs:162-176]
     │     • _adminAccessToken / _adminExpiresAt populated
     │     • _isAdminSession = true
     │     • refresh token (admin) persisted to auth.dat
     │     • DEVICE slot is NOT touched — both tokens now coexist
     │
T7  Subsequent API requests
     │   ── AuthenticatedHttpClient picks the right slot per requireAdminToken flag
     │      • admin-scoped writes → GetAdminAccessTokenAsync()
     │      • device-scoped heartbeats → GetDeviceAccessTokenAsync()
     │   ── On 401, force-expire that slot, refresh from the matching endpoint, retry once
═══════════════════════════════════════════════════════════════════
```

### Key timing facts

1. **Device token is obtained automatically at startup** — no user action required. Just needs `machineId` (no password).
2. **Admin token is obtained ONLY when the user manually clicks Sign In.** It is never fetched on startup, even if `identity.dat` exists — the **refresh token** persisted there is used to renew the admin access token, but the access token itself lives only in memory and is re-minted on demand.
3. **Both tokens can be held simultaneously.** Signing in does NOT replace the device token; it adds the admin token alongside it.
4. **The redundant second call** at [RevitBootstrapper.cs:1088-1093](../BIManage/Revit/Applications/RevitBootstrapper.cs#L1088-L1093) re-validates the device before model sync starts — belt-and-suspenders.
5. **Logout** clears both slots and both `.dat` files. **Demoting an admin** clears only the admin slot via `ClearAdminTokens()` at [AuthTokenManager.cs:381](../BIManage/Infrastructure/Auth/AuthTokenManager.cs#L381).

### Why two tokens?

- Server enforces that admin-scoped writes (protections, rules, audit logs) require a token with the `profileId` claim — only `/admin-login` issues that.
- Device endpoints (heartbeat, session sync, telemetry) only need machine identity — `/validate-device` issues a `profileId`-less token for that.
- Keeping them separate means a device heartbeat won't consume/refresh the admin token, and admin sign-out doesn't kill device telemetry.

### File:line index for this section

| Item | File | Line(s) |
|---|---|---|
| Token slot fields | [AuthTokenManager.cs](../BIManage/Infrastructure/Auth/AuthTokenManager.cs#L24-L33) | 24-33 |
| `SetTokensFromDevice()` | [AuthTokenManager.cs](../BIManage/Infrastructure/Auth/AuthTokenManager.cs#L186-L211) | 186-211 |
| `SetTokensFromLogin()` | [AuthTokenManager.cs](../BIManage/Infrastructure/Auth/AuthTokenManager.cs#L162-L176) | 162-176 |
| `GetAccessTokenAsync()` | [AuthTokenManager.cs](../BIManage/Infrastructure/Auth/AuthTokenManager.cs#L218-L226) | 218-226 |
| `GetDeviceAccessTokenAsync()` | [AuthTokenManager.cs](../BIManage/Infrastructure/Auth/AuthTokenManager.cs#L268-L285) | 268-285 |
| `GetAdminAccessTokenAsync()` | [AuthTokenManager.cs](../BIManage/Infrastructure/Auth/AuthTokenManager.cs#L242-L263) | 242-263 |
| `ValidateDeviceAsync()` | [AuthApiService.cs](../BIManage/Infrastructure/Auth/AuthApiService.cs#L159-L185) | 159-185 |
| `AdminLoginAsync()` | [AuthApiService.cs](../BIManage/Infrastructure/Auth/AuthApiService.cs#L304-L328) | 304-328 |
| `AutoAuthenticateDeviceAsync()` | [RevitBootstrapper.cs](../BIManage/Revit/Applications/RevitBootstrapper.cs#L1486-L1555) | 1486-1555 |
| First auto-auth invocation | [RevitBootstrapper.cs:951](../BIManage/Revit/Applications/RevitBootstrapper.cs#L951) | 951 |
| Pre-model-sync re-validation | [RevitBootstrapper.cs:1088-1093](../BIManage/Revit/Applications/RevitBootstrapper.cs#L1088-L1093) | 1088-1093 |

---

## Key files (jump points)

- Entry: [SignInCommand.cs](../BIManage/Commands/RibbonCommands/SignInCommand.cs)
- UI: [SignInDialog.xaml.cs](../BIManage/Views/Auth/SignInDialog.xaml.cs) / [SignInDialog.xaml](../BIManage/Views/Auth/SignInDialog.xaml)
- ViewModel: [SignInViewModel.cs](../BIManage/ViewModels/Auth/SignInViewModel.cs)
- HTTP: [AuthApiService.cs](../BIManage/Infrastructure/Auth/AuthApiService.cs), [AuthenticatedHttpClient.cs](../BIManage/Infrastructure/Auth/AuthenticatedHttpClient.cs)
- Tokens: [AuthTokenManager.cs](../BIManage/Infrastructure/Auth/AuthTokenManager.cs), [SecureTokenStorage.cs](../BIManage/Infrastructure/Auth/SecureTokenStorage.cs)
- DI wiring: [RevitBootstrapper.cs:741-890](../BIManage/Revit/Applications/RevitBootstrapper.cs#L741-L890)
- Config: [App.config](../App.config)

---

## Verification — testing the flow live

1. Start Revit, click the **Sign In** ribbon button → confirm `SignInDialog` opens
2. Enter valid credentials → confirm dialog swaps to "signed-in" view with role badge
3. Check `%APPDATA%\BIManage\` for `auth.dat` and `identity.dat` (should both exist after login)
4. Close and reopen Revit → click Sign In again → should show "signed-in" view directly (restored from `identity.dat`)
5. Click Sign Out → both `.dat` files should be deleted
6. Enter wrong password → confirm "Invalid email or password." message
