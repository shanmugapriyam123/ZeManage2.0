# The legacy product — License Security Architecture Review

> **Purpose**: Document how the legacy product secures its API endpoints and how its licensing module works for both Users and Admins. This serves as a reference for replicating and improving these patterns in our own product.

---

## Table of Contents

1. [Part A — How Endpoints Are Secured](#part-a--how-endpoints-are-secured)
2. [Part B — How the Licensing Module Works (User Side)](#part-b--how-the-licensing-module-works-user-side)
3. [Part C — How the Licensing Module Works (Admin Side)](#part-c--how-the-licensing-module-works-admin-side)
4. [Appendix — Key File Reference](#appendix--key-file-reference)

---

## Part A — How Endpoints Are Secured

### A1. API Hosts & Transport

| Service              | Host                                                       | Protocol  |
|----------------------|------------------------------------------------------------|-----------|
| Primary REST API     | `https://api.legacy-product.tech`                             | HTTPS     |
| SignalR (real-time)  | `legacy-signalr-defaultmode-prod.service.signalr.net`    | WSS       |
| Backendless (BaaS)   | `https://api.backendless.com`                              | HTTPS     |

**TLS Configuration** (`Soap/Mapping/SoapAPIAppHelper.cs:158`):
```
ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls11 | SecurityProtocolType.Tls12;
```
- TLS 1.1 and 1.2 enforced at the .NET `ServicePointManager` level.
- No custom certificate pinning — relies on the OS certificate store.

---

### A2. Authentication Layers

The legacy product uses a **two-layer** authentication model:

#### Layer 1 — Backendless User Authentication
- Used for initial login (email + password).
- Backendless SDK handles session management.
- Credentials:
  - App ID: `4E129FA8-ED2B-5021-FF84-966E95BC0D00`
  - Secret Key: `3175AF18-7DA1-B423-FF46-B8568043D100`
- Source: `Soap/License/AppManager.cs:16-17`

#### Layer 2 — DES Validation Token (Primary API Auth)
- After Backendless login, a **DES token** is obtained and attached to every REST call.
- **Header name**: `X-LEGACY-REFERENCE-VALIDATION-TOKEN-V23`
- **Secondary header**: `X-LEGACY-REFERENCE-COMPANY` (carries the Company GUID)
- Source: `Soap/Mapping/SoapAPIAppHelper.cs:35-36, 95-98`

**Token Request Payload** (`RequestOptionsGetDESToken`):
```
{
  "LicenseGuid":        "<CompanyId>",
  "Username":           "<user email>",
  "GatewayAccessToken": "c65bd215-2495-40e9-90e5-24e6dec2a997",
  "Privilege":          "Admin | User",
  "RevitInfo":          "2022.x.x",
  "the legacy productInfo":       "3.2.9.0"
}
```

**Token Lifecycle**:
- Token has a `ValidTillUtc` expiration field (`Soap/Models/DESToken.cs`).
- Refreshed if less than **10 minutes** remain before expiry.
- Acquisition timeout: 10 seconds.
- Source: `Soap/Mapping/SoapAPIAppHelper.cs:109-127`

---

### A3. Request Construction

All REST calls follow this pattern:

```
Headers:
  X-LEGACY-REFERENCE-VALIDATION-TOKEN-V23: <token>
  X-LEGACY-REFERENCE-COMPANY:              <company GUID>
  Content-Type:                    application/json; charset=utf-8
  Accept:                          application/json

Body: JSON (Newtonsoft.Json)
  - Null values ignored
  - Circular references ignored
  - ISO 8601 time spans
```

**HTTP Client Configuration**:
- Default timeout: **30 seconds**
- Extended timeout: **240 seconds** (4 min) for heavy operations (mapsets, project configs)
- Source: `Soap/Mapping/SoapAPIAppHelper.cs:55-58, 149`

---

### A4. Response Validation

- **Accepted status codes**: 200 (OK) and 201 (Created) only.
- Any other code throws `HttpOperationException` containing full request/response details.
- JSON deserialization errors are caught as `JsonException`.
- Diagnostic tracing via `ServiceClientTracing`.
- Source: `Soap/SoapProdApiApp.cs:224-251`

---

### A5. Feature Access Tokens (Hardcoded GUIDs)

Each API feature area uses a static GUID as a capability identifier sent in the request body:

| Token Name                     | GUID                                   | Purpose                   |
|--------------------------------|----------------------------------------|---------------------------|
| `CompanyOperationToken`        | `f35ca51b-4b5a-4252-94fd-5f298b916f4d` | Company operations        |
| `projectControllerAccessToken` | `c685c7cc-0dc1-444b-b16f-2f2e6cbf601e` | Project operations        |
| `cloudPropertyAccessToken`     | `228f310f-61d2-438a-b923-a4710eee8384` | Cloud properties          |
| `adminOperationToken`          | `ea463878-e56c-4169-b70c-90c4813df56f` | Admin operations          |
| `dataAccessToken`              | `c6c03a0f-e2ea-4466-84ae-debe6b57c803` | Data access               |
| `resetDataAccessPasswordToken` | `8cb2f9ba-ea85-4c1d-9742-dbb8d0a46360` | Password reset            |
| `messagingToken`               | `b5bf88a0-fcc4-4786-afbc-b2f08dc18462` | Broadcast messaging       |
| `MICIdToken`                   | `318140c0-b6cb-4b41-8276-50f918ce514a` | MIC identification        |
| `inMemoryStateDataToken`       | `de022fdf-6dd3-47de-aa88-bda43514dfa0` | In-memory state           |

Source: `Soap/Mapping/SoapAPIAppHelper.cs:41-51`

---

### A6. SignalR Real-Time Connection

- Hub name: `NotificationHub`
- Authentication via **query string** parameters:
  ```
  ?companyId={CompanyId}&user={Username}
  ```
- Source: `Soap/SignalR/SignalRService.cs:60, 124`

---

### A7. Internet Connectivity Checks

Before any API call, the legacy product verifies connectivity using a 5-step cascade:

1. Windows API `InternetGetConnectedState()`
2. Ping `google.com`
3. Ping the legacy product API host
4. HTTP GET `https://google.com`
5. HTTP GET the legacy product API base URL

Source: `Soap/Utils/InternetConnection.cs`

---

### A8. What Is NOT Present (Security Gaps)

| Missing Feature            | Impact                                                        |
|----------------------------|---------------------------------------------------------------|
| Request signing / HMAC     | No payload integrity verification beyond HTTPS                |
| Certificate pinning        | Trusts OS cert store — vulnerable to local MITM with root CA  |
| Request encryption layer   | No app-level encryption beyond TLS                            |
| Token rotation on logout   | DES token continues to live until `ValidTillUtc`              |
| Rate limiting (client)     | No client-side throttle on API calls                          |
| Obfuscation of static GUIDs | All feature tokens visible in decompiled DLL                 |

---

## Part B — How the Licensing Module Works (User Side)

### B1. License Activation Flow

```
User clicks "Admin Login" in License dialog
  │
  ├─ 1. Internet connectivity check
  │
  ├─ 2. Backendless login (email + password)
  │     └─ BackendlessAPI.UserService.Login()
  │
  ├─ 3. MFA check (if enabled at company or user level)
  │     ├─ Server sends verification email (OTP)
  │     ├─ User enters 6-digit OTP
  │     └─ Server verifies via VerifyEmailCodeAsync()
  │
  ├─ 4. Admin token creation
  │     ├─ Generate GUID: Guid.NewGuid().ToString().ToUpper()
  │     ├─ Send to Backendless: admintoken.create()
  │     │   Parameters (10):
  │     │     [0] UserEmail
  │     │     [1] AdminToken (the GUID)
  │     │     [2] BackendlessUser.ObjectId
  │     │     [3] MotherboardId (WMI: Win32_BaseBoard.SerialNumber)
  │     │     [4] ProcessorId  (WMI: Win32_Processor.ProcessorId)
  │     │     [5] AppVersion
  │     │     [6] RevitVersion
  │     │     [7] UserInfo.User (Windows username)
  │     │     [8] UserInfo.Machine (computer name)
  │     │     [9] Environment.UserName (domain\user)
  │     └─ Save token to: %APPDATA%\the legacy product\admin.config
  │
  └─ 5. AdminValidationAsync() — verify token, fetch license data
```

Source: `Soap/License/Login/LoginView.cs:286-458`, `Soap/License/LicenseInfo.cs:340-426`

---

### B2. Runtime License Validation (Startup)

Every time Revit starts with the legacy product loaded:

```
Step 1: Check internet → if offline, go to Grace Period (B4)

Step 2: Read cached admin token from %APPDATA%\the legacy product\admin.config
         └─ If token exists → call admintoken.validate()

Step 3: Server validates token against hardware fingerprint
         Parameters sent (8):
           [0] Token
           [1] MotherboardId
           [2] ProcessorId
           [3] AppVersion
           [4] RevitVersion
           [5] UserInfo.User
           [6] UserInfo.Machine
           [7] Environment.UserName

Step 4: Server returns license state:
         {
           isAuthorized, name, email, company, companyId,
           expiration, isProjectAdmin, autoAddAsAdminToProjects,
           allowAddAsAdminToProjects, totalAdminLicenses,
           errorMessage
         }

Step 5: If no admin token → call individual.create() for user license
         Parameters (8):
           [0] UserInfo.User        [4] MotherboardId
           [1] DefaultCompanyId     [5] ProcessorId
           [2] AppVersion           [6] Machine
           [3] RevitVersion         [7] Environment.UserName
```

Source: `Soap/License/LicenseInfo.cs:523-932`

---

### B3. Hardware Fingerprinting

The legacy product collects these identifiers for machine binding:

| Data Point        | Source                                         |
|-------------------|------------------------------------------------|
| Motherboard ID    | `WMI: Win32_BaseBoard → SerialNumber`          |
| Processor ID      | `WMI: Win32_Processor → ProcessorId`           |
| Machine Name      | `Environment.MachineName`                      |
| Windows User      | `Environment.UserName`                         |
| OS Name           | `ComputerInfo().OSFullName`                    |
| Processor Count   | `Environment.ProcessorCount`                   |
| Physical Memory   | `ComputerInfo().TotalPhysicalMemory`           |
| Revit Version     | Revit API: version, build, language, login ID  |

Source: `Soap/License/UserInfo.cs:16-115`

**Enforcement**: The server validates that the token is being used on the same hardware. Mismatched hardware may invalidate the token.

---

### B4. Offline Grace Period

When the server is unreachable, the legacy product allows continued use for a limited time.

**Duration**: 72 hours (constant `LICENSE_GRACE_PERIOD = 72`)

**Logic**:
```
1. Retrieve last successful license from cache:
     UserProfileSetting.Instance.GetLastSeenLicense()
     Stored in: %APPDATA%\Local\the legacy product\UserData.dat (AES encrypted)

2. Check if within grace period:
     - Standard: lastLoginTime + 72 hours > now
     - Weekend extension: If last login was Sat/Sun, extend to
       next Tuesday EOD + 72 hours
     - If the 72-hour window lands on a weekend, add another 72 hours

3. If valid grace → restore cached license, allow all features
4. If expired → block all license-protected features
```

Source: `Soap/License/LicenseInfo.cs:42, 813-1113`

---

### B5. Local Storage Locations

| Path                                       | Contents                            | Format              |
|--------------------------------------------|-------------------------------------|---------------------|
| `%PROGRAMDATA%\the legacy product\the legacy product.key`      | Company ID (system-wide)            | Plain text (GUID)   |
| `%APPDATA%\the legacy product\the legacy product.key`          | Company ID (user-specific)          | Plain text (GUID)   |
| `%APPDATA%\the legacy product\admin.config`          | Admin token                         | Plain text (GUID)   |
| `%APPDATA%\Local\the legacy product\UserData.dat`    | Cached license + user settings      | JSON (AES encrypted)|

**Encryption for UserData.dat**:
- Algorithm: AES-256 via `RFC2898DeriveBytes`
- Key: `"A7c8wpFMMazZYHsd"` (hardcoded)
- Salt: `"Ivan Medvedev"` (as ASCII bytes)
- 32-byte key, 16-byte IV
- Source: `Soap/Utils/StringUtils.cs:23, 89-161`

**Cached data includes**:
- `LastSeenLicense` (encrypted `LicenseInfoWrapper`)
- `LastSeenLicenseDateUtc`
- `LastRememberedAdminEmail` (encrypted)
- `LastRememberedAdminPassword` (encrypted)

---

### B6. License Types & Privilege System

**User Roles** (`Soap/License/the legacy productUserRole.cs`):

| Role                    | Value | Description                   |
|-------------------------|-------|-------------------------------|
| `CompanyAdministrator`  | 100   | Full company admin rights     |
| `ProjectAdministrator`  | 101   | Project-level admin           |
| `NormalUser`            | 102   | Standard user, limited access |

**Key License Properties** (`Soap/License/LicenseInfo.cs`):
- `IsAuthorized` (bool?) — primary gate for all features
- `IsAdminLogged` — company admin session active
- `IsProjectAdminLogged` — project admin session active
- `HasAdminPriviledges` — derived: `IsProjectAdminLogged || IsAdminLogged`
- `Expiration` — license expiry date (server-controlled, no client-side check)
- `IsOutage` / `IsComapnyActive` — server health and company status flags

---

### B7. UI Impact Based on License State

| State                       | Effect on Revit UI                                        |
|-----------------------------|-----------------------------------------------------------|
| `IsAuthorized = false`      | All the legacy product commands disabled via `AvailableOnlyIfLicenseAuthorized` |
| `IsAdminLogged = false`     | Admin-only ribbon buttons hidden                          |
| `IsProjectAdminLogged = false` | Project admin buttons hidden                           |
| License expired             | Server sets `isAuthorized = false` — same as unauthorized |
| Grace period active         | Full functionality from cached license                    |
| Grace period expired        | All features blocked                                      |

License dialog shows: user name, email, company, role, expiry date, and error messages (yellow highlight).

Source: `Soap/Revit/Applications/AvailableOnlyIfLicenseAuthorized.cs`, `license/licenseview.xaml`

---

## Part C — How the Licensing Module Works (Admin Side)

### C1. Admin Authentication

Admins authenticate through the **same login flow** as users (Backendless email + password + optional MFA). The distinction is made **server-side** — the Backendless `admintoken.validate()` response includes admin-specific flags:

- `isAuthorized` — license valid
- `isProjectAdmin` — project-level admin
- `autoAddAsAdminToProjects` — auto-assign to new projects
- `allowAddAsAdminToProjects` — permission to be added
- `totalAdminLicenses` — company admin seat count

The client then sets `IsAdminLogged` or `IsProjectAdminLogged` accordingly.

---

### C2. Admin Role Model

**BackendlessAdminInfo** (`Soap/Models/BackendlessAdminInfo.cs:22-337`):
```
Properties:
  UserId, FirstName, LastName, Email, Password, Created,
  IsProjectAdmin (bool: 0 = company, 1 = project),
  IsDeleted (soft delete), RoleValue (0 or 1),
  OriginalEmail, AutoAddAsAdminToProjects,
  AllowAddAsAdminToProjects
```

**Admin Role Enum** (`Soap/Models/AdminRole.cs`):
- `CompanyAdmin` — full control
- `ProjectAdmin` — project-scoped control

---

### C3. Admin Management — Backendless Custom Services

| Operation                          | Backendless Service Call                                     | Purpose                              |
|------------------------------------|--------------------------------------------------------------|--------------------------------------|
| Create admin token                 | `admintoken.create(params[10])`                              | Generate session for admin           |
| Validate admin token               | `admintoken.validate(params[8])`                             | Verify session + hardware            |
| Fetch company admins               | `analyticsservice.fetchCompanyAdmins(companyGuid)`           | List all admins in company           |
| Check email exists                 | `admin.isAdminEmailAddressAlreadyExists(email)`              | Prevent duplicate admin accounts     |
| Fetch deleted user for reactivation| `admin.fetchUserIdIfDeletedWithEmailExistsToReactive(email)` | Reactivate soft-deleted admins       |
| Individual user license            | `individual.create(params[8])`                               | Non-admin user license check         |

Source: `Soap/Models/BackendlessAdminInfo.cs:146-180`, `Soap/CompanySettings/AddEditCompanyAdministratorViewModel.cs:219-259`

---

### C4. Admin UI — Company Settings Panel

**Entry point**: `CompanySettingsCommand.cs` → checks `licenseInfo.IsAdminLogged` (line 44). Denied with "Admin only" prompt if not admin.

**Company Settings ViewModel** (`Soap/CompanySettings/CompanySettingsViewModel.cs`) provides:
- Company branding (logo management)
- Admin user management (add/edit/remove)
- Project management and configuration
- the legacy product intervention settings
- File opening protection settings
- Group protection rules
- Command override settings
- Library settings

---

### C5. Admin User Management (CRUD)

**Add/Edit Company Administrator** (`Soap/CompanySettings/AddEditCompanyAdministratorViewModel.cs`):
- Add new company admin or project admin
- Edit existing admin (email, name, role)
- Role selection: Company Admin (0) or Project Admin (1)
- Validation: email format, uniqueness check
- Reactivate soft-deleted admin accounts
- Set auto-add and allow-add flags

**Project Administrators** (`Soap/CompanySettings/ProjectAdministratorsViewModel.cs`):
- List project-level admins
- Add admin to specific project
- Remove admin from project
- Delete project admin

**Admin Changes Request** (`Soap/Models/RequestOptionsAdminChanges.cs`):
- `UpdatedEmailAddressOldNewList` — batch email changes (old → new tuples)
- `RemovedAdminEmailList` — batch admin removal
- `AdminChangesToken` — authorization for batch operations

---

### C6. Role-Based Access Control (RBAC) in Revit UI

The legacy product enforces role-based command availability through `IExternalCommandAvailability` implementations:

| Class                                              | Condition                                     |
|----------------------------------------------------|-----------------------------------------------|
| `AvailableOnlyToAdmins`                            | `IsAdminLogged \|\| IsProjectAdminLogged`     |
| `AvailableOnlyToCompanyAdmins`                     | `IsAdminLogged` only                          |
| `AvailableOnlyToAdminsWithActiveDocument`           | Admin + active document open                  |
| `AvailableOnlyToAdminsWithActiveProjectDocument`    | Admin + active project document               |
| `AvailableOnlyToAdminsIfAnyDocumentIsOpen`          | Admin + any open document                     |
| `AvailableOnlyToAdminsIfDevModeOn`                  | Admin + dev mode enabled                      |
| `AvailableToAdminForWSDocWithSyncTrafficEnabled`    | Admin + worksharing sync enabled              |

Source: `Soap/Revit/Applications/AvailableOnlyTo*.cs`

**Project-level privilege check**:
Many commands use `RevitSessionLocal.HasAdminPriviledgesForThisProject(soapDocumentCookie)` to verify the admin has access to the **specific project** open in the document. Used in:
- `ManageSyncCommand`, `ParameterPromptSettingsCommand`, `ProjectCleanupCommand`
- `ProtectedFamilySettingsCommand`, `ProtectedMirrorSettingsCommand`, `ProtectedPinSettingCommand`
- `ViewportToolCommand`

---

### C7. Condition-Based Feature Evaluation

**UserRoleCondition** (`SoapAPI/NestedCondition/Condition/UserRoleCondition.cs:19-57`):
- Evaluates user role (100/101/102) against configured feature rules
- Used by the nested condition engine to gate features/actions per role
- Allows dynamic server-driven feature control without client updates

---

### C8. Admin-Only Features

| Feature                    | Access Level       | Source Command                          |
|----------------------------|--------------------|-----------------------------------------|
| Company Settings           | Company Admin only | `CompanySettingsCommand.cs`             |
| Company Properties         | Admin              | `CompanyPropertiesCommand.cs`           |
| Manage Sync                | Project Admin      | `ManageSyncCommand.cs`                  |
| Parameter Prompt Settings  | Project Admin      | `ParameterPromptSettingsCommand.cs`     |
| Project Central            | Admin              | `ProjectCentralCommand.cs`             |
| Project Cleanup            | Project Admin      | `ProjectCleanupCommand.cs`             |
| Protected Family Settings  | Project Admin      | `ProtectedFamilySettingsCommand.cs`     |
| Protected Mirror Settings  | Project Admin      | `ProtectedMirrorSettingsCommand.cs`     |
| Protected Pin Settings     | Project Admin      | `ProtectedPinSettingCommand.cs`         |
| Viewport Tool              | Project Admin      | `ViewportToolCommand.cs`               |
| Broadcast Messages         | Company Admin      | `BroadcastMessageAdapter.cs`           |

---

### C9. Broadcast Messaging (Admin Feature)

- Company admins can send messages to: entire company, project groups, or individual projects.
- Message types: `Company`, `ProjectGroup`, `Project`
- Access gated by `licenseInfo.IsAdminLogged`
- Uses `messagingToken` GUID for API authorization
- Source: `Soap/Messaging/BroadcastMessageAdapter.cs:19-150`

---

### C10. Analytics & Session Tracking

- Admin actions are logged via `AdminControlledCommandEventInfo`
- User sessions track admin status:
  ```
  IsAdmin = licenseInfo.IsAdminLogged
  IsAdminCompanyAdmin = IsAdminLogged && !IsProjectAdminLogged
  ```
- Source: `Soap/Models/UserSession.cs:25-38`

---

## Appendix — Key File Reference

### Core License Files
| File | Purpose |
|------|---------|
| `Soap/License/LicenseInfo.cs` | Core license validation & state (1115 lines) |
| `Soap/License/LicenseViewModel.cs` | License dialog UI binding (386 lines) |
| `Soap/License/UserInfo.cs` | Hardware fingerprinting (116 lines) |
| `Soap/License/AppManager.cs` | Backendless config (25 lines) |
| `Soap/License/the legacy productUserRole.cs` | Role enum (15 lines) |
| `Soap/License/Login/LoginView.cs` | Login/activation dialog (600 lines) |
| `Soap/License/Login/UserProfileSetting.cs` | Local caching (162 lines) |
| `Soap/License/Login/LicenseInfoWrapper.cs` | Cache wrapper (25 lines) |

### API & Security Files
| File | Purpose |
|------|---------|
| `Soap/Mapping/SoapAPIAppHelper.cs` | API orchestration, tokens, DES auth |
| `Soap/SoapProdApiApp.cs` | HTTP client, request/response handling |
| `Soap/Models/DESToken.cs` | Token data structure |
| `Soap/Models/RequestOptionsGetDESToken.cs` | Token request payload |
| `Soap/SignalR/SignalRService.cs` | Real-time communication |
| `Soap/Utils/InternetConnection.cs` | Connectivity validation |
| `Soap/Utils/StringUtils.cs` | AES encryption/decryption |

### Admin & Company Management Files
| File | Purpose |
|------|---------|
| `Soap/CompanySettings/CompanySettingsViewModel.cs` | Main admin panel |
| `Soap/CompanySettings/AddEditCompanyAdministratorViewModel.cs` | Admin CRUD |
| `Soap/CompanySettings/ProjectAdministratorsViewModel.cs` | Project admin management |
| `Soap/Models/BackendlessAdminInfo.cs` | Admin data model (337 lines) |
| `Soap/Models/AdminRole.cs` | Role enum |
| `Soap/CompanySettings/AdminInfo.cs` | Lightweight admin view |
| `Soap/Messaging/BroadcastMessageAdapter.cs` | Admin messaging |

### RBAC Availability Classes
| File | Gate |
|------|------|
| `Soap/Revit/Applications/AvailableOnlyIfLicenseAuthorized.cs` | Any licensed user |
| `Soap/Revit/Applications/AvailableOnlyToAdmins.cs` | Any admin |
| `Soap/Revit/Applications/AvailableOnlyToCompanyAdmins.cs` | Company admin only |
| `SoapAPI/NestedCondition/Condition/UserRoleCondition.cs` | Dynamic role conditions |
