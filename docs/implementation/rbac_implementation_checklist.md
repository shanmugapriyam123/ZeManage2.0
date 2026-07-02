# CBOX Manage: RBAC Implementation Checklist

> **Source**: Analysis of the legacy product Role-Based Access Control patterns  
> **Purpose**: Ensure CBOX Manage has all necessary RBAC foundations before advancing to complex workflows

---

## 🎯 Role System Architecture

### 1. Core Role Model (`LicenseInfo` equivalent)

**command interception pattern:**
```csharp
public class LicenseInfo {
    public bool IsAdminLogged { get; set; }           // Company Admin
    public bool IsProjectAdminLogged { get; set; }    // Project Admin
    public bool HasAdminPriviledges =>                // Computed property
        IsProjectAdminLogged || IsAdminLogged;
    
    public string UserEmail { get; set; }
    public string CompanyId { get; set; }
    public string AdminToken { get; set; }
    
    // Auto-enrollment
    public bool AutoAddAsAdminToProjects { get; set; }
    public bool AllowAddAsAdminToProjects { get; set; }
}
```

#### ✅ **Checklist: Core Identity & Role Properties**

- [ ] **`ILicenseInfo` interface** or equivalent central identity class
  - [ ] `UserEmail` (string) - primary identity
  - [ ] `CompanyId` (Guid/string) - tenant isolation
  - [ ] `IsCompanyAdmin` (bool) - top-level admin flag
  - [ ] `IsProjectAdmin` (bool) - project-level admin flag
  - [ ] `HasAnyAdminPrivileges` (computed bool) - convenience property
  - [ ] `AdminToken` (string) - session/auth token

- [ ] **Authentication properties**
  - [ ] `IsAuthorized` (bool?) - overall auth status
  - [ ] `UserName` (string) - display name
  - [ ] `RevitUserName` (string) - Revit identity
  - [ ] Token storage location defined (local file or encrypted store)

- [ ] **Company/Tenant Context**
  - [ ] `CompanyName` (string)
  - [ ] `DefaultCompanyId` (string) - for fallback scenarios
  - [ ] Tenant isolation logic in API calls

- [ ] **Property Change Notifications**
  - [ ] Implement `INotifyPropertyChanged` for role properties
  - [ ] Raise events when role flags change (for UI updates)

---

### 2. Role Enum & Type System

**command interception pattern:**
```csharp
public enum the legacy productUserRole {
    CompanyAdministrator = 100,
    ProjectAdministrator = 101,
    NormalUser = 102
}
```

#### ✅ **Checklist: Role Enumeration**

- [ ] **Define role enum** (e.g., `CBoxUserRole`)
  - [ ] `CompanyAdmin` role
  - [ ] `ProjectAdmin` role
  - [ ] `RegularUser` role
  - [ ] Explicit numeric values for serialization stability

- [ ] **Role resolution logic**
  - [ ] Method to map `LicenseInfo` flags → role enum
  - [ ] Handle multi-role scenarios (e.g., Company Admin **is** Project Admin for all projects)
  - [ ] Store current resolved role in session context

---

### 3. Project-Level Admin Verification

**command interception pattern:**
```csharp
public bool HasAdminPriviledgesForThisProject(string projectCookie) {
    if (string.IsNullOrEmpty(projectCookie))
        return LicenseInfo.IsAdminLogged; // Company admins have all rights
    
    // Check project-specific admin list
    return (LicenseInfo.IsProjectAdminLogged && IsAdminOfCurrentProject(projectCookie))
        || LicenseInfo.IsAdminLogged;
}

private bool IsAdminOfCurrentProject(string projectCookie) {
    var projectInfo = SessionCache.GetProjectInfo(projectCookie);
    return projectInfo?.ProjectSettings?.Administrators
        ?.Any(admin => admin.Email == LicenseInfo.UserEmail) ?? false;
}
```

#### ✅ **Checklist: Project-Scoped Permissions**

- [ ] **Project administrator list storage**
  - [ ] `ProjectSettings.Administrators` collection
  - [ ] Administrator POCO: `{ Email, AddedDate, AddedBy, ... }`
  - [ ] Cached per project in session

- [ ] **Permission checking method**
  - [ ] `HasAdminPrivilegesForProject(projectId)` method
  - [ ] Returns `true` if:
    - Company Admin (global override)
    - Project Admin **AND** user is in project's admin list
  - [ ] Returns Company Admin role if `projectId` is null/empty

- [ ] **Auto-add logic** (optional but powerful feature the legacy product has)
  - [ ] `AutoAddAsAdminToProjects` flag
  - [ ] Automatically elevate Company Admins to Project Admin on first project access
  - [ ] Permission to disable auto-add: `AllowAddAsAdminToProjects`

---

## 🔐 Authentication & Session Management

### 4. Login & Token Management

**command interception pattern:**
- Backendless email/password login
- Admin token generated and stored locally (`%AppData%\the legacy product\admin.config`)
- Token validated on startup via `AdminValidationAsync()`
- Logout deletes token locally + invalidates on server

#### ✅ **Checklist: Authentication Flow**

- [ ] **Login mechanism**
  - [ ] Login UI (`LoginView.xaml` / `LoginViewModel`)
  - [ ] Email + Password authentication
  - [ ] MFA/OTP support (see next section)
  - [ ] Backend login endpoint integration

- [ ] **Token lifecycle**
  - [ ] Generate unique `AdminToken` on login (GUID)
  - [ ] Store token locally (encrypted or secured file)
    - The legacy product uses: `%AppData%\the legacy product\admin.config`
  - [ ] Send token with API requests (header: `X-LEGACY-REFERENCE-VALIDATION-TOKEN-V23` or similar)
  - [ ] Token validation on startup (`ValidateTokenAsync()`)
  - [ ] Token refresh strategy (optional but recommended)

- [ ] **Logout flow**
  - [ ] Delete local token file
  - [ ] Invalidate token on server
  - [ ] Clear session cache
  - [ ] Revert to unlicensed/unauthenticated state

- [ ] **Offline/grace mode**
  - [ ] Cache last successful auth state
  - [ ] Allow limited operations when disconnected
  - [ ] 72-hour grace period logic (The legacy product's approach)

---

### 5. OTP (One-Time Password) System for Rule Overrides

**command interception pattern:**
```csharp
// Generate OTP (Company Admin only)
public async Task<string> CreateOTPAsync() {
    var otp = await apiClient.CreateOTPAsync(companyId, options);
    return otp; // Returns 6-digit code
}

// Validate OTP (Anyone can attempt)
public async Task<bool> UseOTPAsync(string password) {
    var result = await apiClient.UseOTPIfValidAsync(
        companyId, new RequestOptionsUseOTP { Password = password }
    );
    return result; // true if valid and not expired
}
```

**Usage Context:**
- Prevent mode: User blocks action → Admin provides OTP → User can proceed
- OTP expires after single use or time limit

#### ✅ **Checklist: OTP Override System**

- [ ] **OTP generation (Company Admin only)**
  - [ ] `GenerateOTPAsync()` method
  - [ ] 6-digit random code
  - [ ] Store OTP server-side with:
    - Company ID
    - Expiration timestamp (e.g., 10 minutes)
    - Single-use flag
  - [ ] Return OTP to admin (display in UI)

- [ ] **OTP validation (for non-admins)**
  - [ ] `ValidateOTPAsync(code)` method
  - [ ] Check against active OTPs for company
  - [ ] Mark as used after first successful validation
  - [ ] Auto-expire based on timestamp

- [ ] **OTP UI workflows**
  - [ ] Admin: Generate OTP dialog (read-only text box, copy button)
  - [ ] User: Enter OTP dialog when blocked by Prevent rule
  - [ ] Error messaging (invalid, expired, already used)

- [ ] **Security considerations**
  - [ ] Rate limiting on OTP validation attempts
  - [ ] Audit log: OTP generated by whom, used by whom, for what rule
  - [ ] Optional: SMS/Email delivery instead of manual sharing

---

## 🎛️ Command & UI Permission Gates

### 6. Ribbon Command Availability

**command interception pattern: `IExternalCommandAvailability` classes**

```csharp
// Company Admin only
public class AvailableOnlyToCompanyAdmins : IExternalCommandAvailability {
    public bool IsCommandAvailable(UIApplication app, CategorySet categories) {
        return SoapApplication.LicenseInfo?.IsAdminLogged ?? false;
    }
}

// Any admin (Company or Project)
public class AvailableOnlyToAdmins : IExternalCommandAvailability {
    public bool IsCommandAvailable(UIApplication app, CategorySet categories) {
        var license = SoapApplication.LicenseInfo;
        if (license?.IsAdminLogged) return true;
        if (license?.IsProjectAdminLogged) return true;
        return false;
    }
}

// Project-specific admin with active document
public class AvailableOnlyToAdminsWithActiveDocument : IExternalCommandAvailability {
    public bool IsCommandAvailable(UIApplication app, CategorySet categories) {
        var doc = app.ActiveUIDocument?.Document;
        if (doc == null) return false;
        
        string projectId = GetProjectId(doc);
        return RevitSessionLocal.Instance.HasAdminPrivilegesForProject(projectId);
    }
}
```

#### ✅ **Checklist: Command Availability System**

- [ ] **Base availability classes**
  - [ ] `AvailableOnlyToCompanyAdmins` : `IExternalCommandAvailability`
  - [ ] `AvailableOnlyToProjectAdmins` : `IExternalCommandAvailability`
  - [ ] `AvailableOnlyToAnyAdmin` : `IExternalCommandAvailability`
  - [ ] `AvailableWithActiveDocument` : `IExternalCommandAvailability`
  - [ ] Combine patterns: `AvailableToAdminsWithActiveDocument`, etc.

- [ ] **Ribbon button integration**
  - [ ] Set `AvailabilityClassName` on `PushButtonData`
  - [ ] Buttons gray out when `IsCommandAvailable() == false`
  - [ ] Real-time updates when role changes (login/logout events)

- [ ] **Permission-gated commands** (examples from WBS)
  - [ ] Company Settings: Company Admin only
  - [ ] Project Settings: Project Admin or Company Admin
  - [ ] Rule Management: Admin only
  - [ ] View Audit Logs: Admin only (or configurable)
  - [ ] OTP Generation: Company Admin only

---

### 7. WPF UI Permission Binding

**command interception pattern:**
```xaml
<!-- XAML -->
<Button IsEnabled="{Binding IsAdminLoggedIn}" />
<Separator Visibility="{Binding IsAdminLoggedIn, Converter={c:BoolToVisibilityConverter}}" />

<!-- ViewModel -->
public bool IsAdminLoggedIn =>
    SoapApplication.LicenseInfo?.IsAdminLogged ?? false;
```

#### ✅ **Checklist: UI Permission Binding**

- [ ] **ViewModel properties for binding**
  - [ ] `IsCompanyAdmin` (bool)
  - [ ] `IsProjectAdmin` (bool)
  - [ ] `HasAnyAdmin` (bool)
  - [ ] Update these properties when `LicenseInfo` changes

- [ ] **Value converters**
  - [ ] `BoolToVisibilityConverter` (hide UI for non-admins)
  - [ ] `InverseBoolConverter` (show message when NOT admin)
  - [ ] Inverse visibility converter

- [ ] **Common UI patterns**
  - [ ] Admin-only menu items
  - [ ] Admin-only buttons (Create, Delete, Edit)
  - [ ] Role-based tabs/sections
  - [ ] Conditional prompts ("You don't have permission...")

---

## ⚙️ Rule Engine RBAC Integration

### 8. User Role Conditions in Rules

**command interception pattern:**
```csharp
public class UserRoleCondition : INestedCondition {
    public the legacy productUserRole UserRole { get; set; } // Enum value from rule config
    
    public bool DoesPass() {
        var license = SoapApplication.LicenseInfo;
        the legacy productUserRole currentRole = !license.HasAdminPriviledges
            ? The legacy productUserRole.NormalUser
            : (license.IsProjectAdminLogged
                ? The legacy productUserRole.ProjectAdministrator
                : The legacy productUserRole.CompanyAdministrator);
        
        // Rule evaluates: current user role == configured role
        return currentRole == this.UserRole;
    }
}
```

**Rule JSON Example:**
```json
{
  "conditionType": "UserRole",
  "userRole": "NormalUser",  // Only apply rule to regular users
  "operator": "Equals"
}
```

#### ✅ **Checklist: Role-Based Rule Evaluation**

- [ ] **`UserRoleCondition` class**
  - [ ] Implements `INestedCondition`
  - [ ] `UserRole` property (enum)
  - [ ] `DoesPass()` logic: compare current user role vs. rule's target role

- [ ] **Rule schema support**
  - [ ] Add `"conditionType": "UserRole"` to rule types
  - [ ] Serialize/deserialize `UserRole` enum in rule JSON
  - [ ] Rule builder UI: dropdown for role selection

- [ ] **Use cases**
  - [ ] Exempt admins from certain protections
    - *Example:* Block delete for `NormalUser` but allow for `ProjectAdministrator`
  - [ ] Admin-only warnings
    - *Example:* "Guide" mode message only shown to regular users
  - [ ] Graduated enforcement
    - *Example:* Prevent for users, Guide for project admins, Monitor for company admins

---

### 9. Admin Bypass / "Enable User Experience for Admins"

**command interception pattern:**
```csharp
public class CommandIntervention {
    public bool EnableUserExperienceForAdmins { get; set; }
    
    // Set from configuration
    void Initialize() {
        this.EnableUserExperienceForAdmins =
            interactionSettings?.EnableMonitorAllCommandsForAdmin ?? false;
    }
    
    // Rule enforcement check
    bool ShouldEnforce() {
        bool isAdmin = RevitSessionLocal.Instance.HasAdminPrivilegesForProject(projectId);
        
        // Bypass if admin AND bypass is disabled
        if (isAdmin && !EnableUserExperienceForAdmins)
            return false; // Skip rule enforcement for admins
        
        // Otherwise enforce for everyone (including admins if enabled)
        return true;
    }
}
```

**Meaning:**
- `EnableUserExperienceForAdmins = false` → Admins bypass all rules (default)
- `EnableUserExperienceForAdmins = true` → Admins also experience rules (testing/dogfooding)

#### ✅ **Checklist: Admin Bypass Toggle**

- [ ] **Configuration property**
  - [ ] `EnableRuleEnforcementForAdmins` (bool)
  - [ ] Stored in `CompanySettings` or `ProjectSettings`
  - [ ] Configurable via admin UI

- [ ] **Enforcement logic**
  - [ ] Before evaluating rules, check:
    ```csharp
    if (HasAdminPrivileges(project) && !config.EnableRuleEnforcementForAdmins)
        return RuleResult.Bypassed; // Don't process rules
    ```
  - [ ] Log bypass events for audit trail

- [ ] **Use cases**
  - [ ] Production: Admins bypass rules to fix mistakes quickly
  - [ ] Testing: Enable enforcement to test rule behavior as admin
  - [ ] Training: Show admins what users experience

---

## 📊 Audit & Logging with Roles

### 10. Role-Enriched Activity Logs

**command interception pattern:**
```csharp
public class UserSession {
    public string UserEmail { get; set; }
    public bool? IsAdminCompanyAdmin { get; set; }
    
    public static UserSession Create(UserInfo userInfo, LicenseInfo license) {
        return new UserSession {
            UserEmail = license.UserEmail,
            IsAdminCompanyAdmin = license.IsAdminLogged && !license.IsProjectAdminLogged,
            // ... other session data
        };
    }
}

public class ChangesetV2 {
    public string UserEmail { get; set; }
    public bool? WasUserAdmin { get; set; } // Captured at event time
    // ... element changes, timestamps, etc.
}
```

#### ✅ **Checklist: Role in Audit Trail**

- [ ] **Session logging**
  - [ ] `UserSession` includes role flags
    - `IsCompanyAdmin` (bool?)
    - `IsProjectAdmin` (bool?)
  - [ ] Log session start/end with role snapshot

- [ ] **Activity/Changeset logging**
  - [ ] Capture user role at time of action:
    ```json
    {
      "userEmail": "admin@company.com",
      "wasCompanyAdmin": true,
      "wasProjectAdmin": false,
      "actionType": "DELETE",
      "elementId": 12345,
      "ruleBypass": true
    }
    ```
  - [ ] `ruleBypass` flag: did admin bypass rule enforcement?

- [ ] **Audit queries**
  - [ ] Filter logs by role: "Show all Company Admin actions"
  - [ ] Detect privilege escalation: User role change mid-session
  - [ ] Compliance reports: "Which admins overrode rules this month?"

---

## 🔄 Role Change Notifications

### 11. Dynamic Role Updates (Login/Logout/Elevation)

**command interception pattern:**
```csharp
public void UpdateAdminFlags(
    bool isNormalUser,
    bool isProjectAdmin,
    bool autoAddAsAdmin,
    bool allowAddAsAdmin) {
    
    if (this.IsProjectAdminLogged != isProjectAdmin) {
        this.IsAdminLogged = !isNormalUser && !isProjectAdmin;
        this.IsProjectAdminLogged = isProjectAdmin;
        this.Privilege = isProjectAdmin ? "Project Admin" : "Company Admin";
        
        // System-wide updates
        LogHelper.ResetLogger();  // Logging verbosity changes
        SoapApplication.SetCommandButtonVisibilityForLicense();
        CommandIntervention.Instance.ReviseAllCommandBinding(true);
    }
    
    this.RefreshAllProperties(); // Trigger UI updates
}
```

#### ✅ **Checklist: Role Change Propagation**

- [ ] **Role change method**
  - [ ] `UpdateRoleFlags(newRole)` or similar
  - [ ] Triggered by:
    - Login/logout
    - Admin elevation (project admin added)
    - Admin demotion (removed from project admin list)

- [ ] **Side effects of role change**
  - [ ] **UI refresh**
    - Ribbon button availability recalculated
    - ViewModel properties re-evaluated
    - Dialogs refreshed (if open)
  - [ ] **Command binding revision**
    - Re-hook `BeforeExecuted` handlers if needed
    - Adjust feature toggles based on new role
  - [ ] **Logging level**
    - Enable diagnostic logs for admins
    - Suppress for regular users
  - [ ] **Session cache refresh**
    - Reload rules cache (admin may have access to different rules)
    - Reload project settings

- [ ] **Event aggregator pattern**
  - [ ] Publish `RoleChangedEvent` via event aggregator
  - [ ] Subscribers:
    - `CommandInterventionManager` → revise bindings
    - `RibbonManager` → update button states
    - `ProjectCentralViewModel` → refresh admin panel

---

## 🧪 Testing & Verification Strategies

### 12. RBAC Testing Checklist

#### ✅ **Unit Tests**

- [ ] **Role resolution tests**
  - [ ] `IsAdminLogged=true, IsProjectAdmin=false` → `CompanyAdministrator`
  - [ ] `IsAdminLogged=false, IsProjectAdmin=true` → `ProjectAdministrator`
  - [ ] Both false → `NormalUser`

- [ ] **Permission check tests**
  - [ ] Company Admin: `HasAdminPrivilegesForProject(anyProject)` → `true`
  - [ ] Project Admin: `HasAdminPrivilegesForProject(assignedProject)` → `true`
  - [ ] Project Admin: `HasAdminPrivilegesForProject(otherProject)` → `false`
  - [ ] Normal User: `HasAdminPrivilegesForProject(anyProject)` → `false`

- [ ] **OTP tests**
  - [ ] Generate OTP → valid 6-digit code
  - [ ] Validate correct OTP → `true`
  - [ ] Validate incorrect OTP → `false`
  - [ ] Validate expired OTP → `false`
  - [ ] Validate already-used OTP → `false`

#### ✅ **Integration Tests**

- [ ] **Login/logout flow**
  - [ ] Login as Company Admin → role flags set correctly
  - [ ] Logout → role flags reset, token deleted
  - [ ] Login as Project Admin → role flags + project admin list verified

- [ ] **Command availability**
  - [ ] Company Settings command: enabled for Company Admin, disabled for others
  - [ ] Project Settings command: enabled for Project Admin (on their project) + Company Admin
  - [ ] Rule violation override: OTP dialog appears for non-admins only

#### ✅ **Manual Test Scenarios** (share with user for validation)

- [ ] **Scenario 1: Company Admin Global Access**
  1. Login as Company Admin
  2. Open any project
  3. Verify: Can access all admin commands
  4. Verify: Can modify project settings
  5. Verify: Can bypass rules (if `EnableRuleEnforcementForAdmins=false`)

- [ ] **Scenario 2: Project Admin Scoped Access**
  1. Login as Project Admin (assigned to Project A)
  2. Open Project A → Can access project settings
  3. Open Project B → Cannot access project settings
  4. Verify: Company Settings command is disabled

- [ ] **Scenario 3: OTP Override**
  1. Login as Regular User
  2. Attempt action blocked by Prevent rule
  3. Verify: OTP entry dialog appears
  4. Company Admin generates OTP (in separate UI)
  5. User enters OTP → action proceeds
  6. Attempt same action again → OTP invalid (single-use)

- [ ] **Scenario 4: Role Change Propagation**
  1. Login as Regular User
  2. Verify: Admin commands disabled
  3. Elevate to Project Admin via backend (simulate)
  4. Verify: Admin commands enable without logout/login
  5. Ribbon buttons update
  6. Dialogs refresh if open

---

## 🚨 Critical Integration Points in Your Existing Code

### 13. Where to Inject RBAC Checks

Based on your completed WBS components:

#### **Event Handlers** (`DocumentChanged`, `CommandInterception`)
```csharp
void OnDocumentChanged(DocumentChangedEventArgs e) {
    string projectId = GetProjectId(e.GetDocument());
    
    // Skip admin tracking if bypass enabled
    if (HasAdminPrivileges(projectId) &&
        !config.EnableRuleEnforcementForAdmins) {
        return; // Bypass
    }
    
    // Continue with normal changeset extraction
    ExtractChangeset(e);
}
```

**Checklist:**
- [ ] Add admin bypass check at top of `DocumentChanged` handler
- [ ] Add admin bypass check in `BeforeExecuted` / `AfterExecuted` handlers
- [ ] Log when bypass occurs (for audit trail)

---

#### **Rule Evaluation Engine**
```csharp
RuleResult EvaluateRule(Rule rule, RuleContext context) {
    // 1. Check UserRoleCondition first
    if (!rule.Conditions.All(c => c.DoesPass(context)))
        return RuleResult.NotApplicable;
    
    // 2. Check if user is admin + bypass enabled
    if (context.HasAdminPrivileges && !config.EnableRuleEnforcementForAdmins)
        return RuleResult.AdminBypassed;
    
    // 3. Normal rule execution
    return EvaluateProtectionMode(rule, context);
}
```

**Checklist:**
- [ ] Add `UserRoleCondition` to rule condition types
- [ ] Add admin bypass logic before rule enforcement
- [ ] Return distinct result: `RuleResult.AdminBypassed` for logging

---

#### **Protection Modes** (Monitor/Guide/Prevent)
```csharp
void Prevent(RuleViolation violation) {
    // Check if admin bypass applies
    if (HasAdminPrivileges(violation.ProjectId) &&
        !config.EnableRuleEnforcementForAdmins) {
        LogBypassed(violation);
        return; // Allow action
    }
    
    // Check for OTP override
    if (violation.AllowOTPOverride) {
        if (PromptForOTP() == OTPResult.Valid) {
            LogOTPUsed(violation);
            return; // Allow action
        }
    }
    
    // Block action
    RollbackTransaction();
    ShowPreventDialog(violation);
}
```

**Checklist:**
- [ ] Inject admin bypass check in `Prevent` mode
- [ ] Inject OTP challenge prompt in `Prevent` mode
- [ ] Log all override events (admin bypass, OTP usage)
- [ ] Ensure `Guide` mode respects admin bypass (optional)

---

#### **Ribbon Command Registration**
```csharp
void RegisterCommands(RibbonPanel panel) {
    var companySettingsBtn = new PushButtonData(...);
    companySettingsBtn.AvailabilityClassName =
        "CBox.Manage.Revit.Availability.AvailableOnlyToCompanyAdmins";
    
    var projectSettingsBtn = new PushButtonData(...);
    projectSettingsBtn.AvailabilityClassName =
        "CBox.Manage.Revit.Availability.AvailableOnlyToProjectAdmins";
}
```

**Checklist:**
- [ ] Create availability classes for each privilege level
- [ ] Assign `AvailabilityClassName` to all admin commands
- [ ] Test button enable/disable on role change

---

#### **WPF ViewModels** (Settings, Admin Panels)
```csharp
public class ProjectSettingsViewModel : ViewModelBase {
    public bool CanEditSettings =>
        SessionContext.HasAdminPrivilegesForProject(ProjectId);
    
    public bool ShowAdminPanel =>
        SessionContext.IsCompanyAdmin || SessionContext.IsProjectAdmin;
    
    void OnLicenseChanged() {
        RaisePropertyChanged(nameof(CanEditSettings));
        RaisePropertyChanged(nameof(ShowAdminPanel));
    }
}
```

**Checklist:**
- [ ] Add permission properties to ViewModels
- [ ] Subscribe to `RoleChangedEvent` to update properties
- [ ] Bind UI elements to permission properties

---

## 📋 Priority Implementation Order

Based on critical dependency chain:

### **Phase 1: Foundation** (Do First)
1. ✅ Core role properties in `LicenseInfo` class
2. ✅ Role enum definition
3. ✅ Basic authentication (login/logout, token storage)
4. ✅ `HasAdminPrivilegesForProject()` method

### **Phase 2: Permission Gates** (Early Integration)
5. ✅ `IExternalCommandAvailability` classes
6. ✅ Ribbon button availability assignment
7. ✅ WPF UI permission binding

### **Phase 3: Rule Engine Integration** (Align with Rule WBS)
8. ✅ `UserRoleCondition` in rule evaluator
9. ✅ Admin bypass toggle (`EnableRuleEnforcementForAdmins`)
10. ✅ Inject RBAC checks in event handlers

### **Phase 4: Advanced Features** (Post-MVP)
11. ✅ OTP generation + validation
12. ✅ OTP UI dialogs
13. ✅ Auto-add admin to projects
14. ✅ Role change event propagation

### **Phase 5: Audit & Testing**
15. ✅ Role-enriched activity logs
16. ✅ Unit tests + integration tests
17. ✅ Manual test scenarios

---

## 🔍 the legacy product's Key Learnings

### **What the legacy product Got Right:**

1. **Computed `HasAdminPriviledges` Property**  
   Simplifies checks: `if (license.HasAdminPriviledges)` instead of `if (IsAdmin || IsProjectAdmin)`

2. **Project-Scoped Admin Model**  
   Clean separation: Company Admins have global power, Project Admins are scoped to specific projects

3. **`AvailabilityClassName` Pattern**  
   Declarative permission model for ribbon commands → easy to audit which commands need which roles

4. **Admin Bypass Toggle**  
   `EnableUserExperienceForAdmins` is brilliant for:
   - Testing rule behavior as admin
   - Training admins on what users see
   - Dogfooding

5. **OTP for Temporary Overrides**  
   Solves "user stuck, needs emergency override" without giving permanent admin rights

6. **Role in Audit Logs**  
   Capturing role at event time is critical for compliance (user role may change post-action)

### **Avoid the legacy product's Pitfalls:**

1. **Typo in Property Name** 😅  
   the legacy product has `HasAdminPriviledges` (should be "Privileges")  
   → Use spell-check before shipping!

2. **Over-reliance on Boolean Flags**  
   Leads to: `if (IsAdmin || (IsProjectAdmin && IsAdminOfProject))`  
   → Consider fluent API: `PermissionCheck.For(project).RequiresAdminAccess()`

3. **No Fine-Grained Permissions**  
   the legacy product only has Admin/Non-Admin → no "CanDeleteElements", "CanModifyRules", etc.  
   → For CBOX Manage, consider future expansion to capability-based model

---

## ✅ Final Checklist Summary

**Before advancing to complex workflows, confirm:**

- [ ] **Core identity model is complete**: `LicenseInfo` with all role flags
- [ ] **Role resolution works**: Method to determine current user role
- [ ] **Project-scoped admin checks work**: `HasAdminPrivilegesForProject(id)`
- [ ] **At least 3 command availability classes exist**: Company Admin, Project Admin, Any Admin
- [ ] **Ribbon buttons have availability classes assigned**
- [ ] **WPF ViewModels have permission properties for binding**
- [ ] **Rule evaluator can skip admins if bypass enabled**
- [ ] **Unit tests for role resolution + permission checks**
- [ ] **Manual test plan reviewed with user**

---

## 📚 Reference Code Locations in the legacy product

| **Component** | **the legacy product File** |
|--------------|------------------|
| Core role model | [`Soap/License/LicenseInfo.cs`](file:///e:/Visual%20Studio/00-LegacyProduct/Soap/License/LicenseInfo.cs) (lines 44-70) |
| Role enum | [`Soap/License/the legacy productUserRole.cs`](file:///e:/Visual%20Studio/00-LegacyProduct/Soap/License/the legacy productUserRole.cs) |
| Project admin check | [`Soap/Revit/Applications/RevitSessionLocal.cs`](file:///e:/Visual%20Studio/00-LegacyProduct/Soap/Revit/Applications/RevitSessionLocal.cs) (lines 334-340) |
| Command availability | [`Soap/Revit/Applications/AvailableOnlyToAdmins.cs`](file:///e:/Visual%20Studio/00-LegacyProduct/Soap/Revit/Applications/AvailableOnlyToAdmins.cs) |
| OTP generation | [`Soap/Mapping/SoapAPIAppHelper.cs`](file:///e:/Visual%20Studio/00-LegacyProduct/Soap/Mapping/SoapAPIAppHelper.cs) (search `CreateOTPAsync`) |
| OTP validation | [`Soap/Mapping/SoapAPIAppHelper.cs`](file:///e:/Visual%20Studio/00-LegacyProduct/Soap/Mapping/SoapAPIAppHelper.cs) (search `UseOTPAsync`) |
| Admin bypass toggle | [`Soap/Revit/Applications/CommandIntervention.cs`](file:///e:/Visual%20Studio/00-LegacyProduct/Soap/Revit/Applications/CommandIntervention.cs) (line 169, search `EnableUserExperienceForAdmins`) |
| User role condition | [`SoapAPI/NestedCondition/Condition/UserRoleCondition.cs`](file:///e:/Visual%20Studio/00-LegacyProduct/SoapAPI/NestedCondition/Condition/UserRoleCondition.cs) |
| Role change handler | [`Soap/License/LicenseInfo.cs`](file:///e:/Visual%20Studio/00-LegacyProduct/Soap/License/LicenseInfo.cs) (lines 428-448, `UpdateAdminFlags`) |

---

**End of RBAC Implementation Checklist**  
*Generated from the legacy product analysis | January 2026*
