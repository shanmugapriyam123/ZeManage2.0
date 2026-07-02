# The legacy product Role-Based Access Control (RBAC) System

## Executive Summary

the legacy product implements a **two-tier admin system** with granular project-level permissions. Admins can bypass protections based on project assignments and company-wide settings.

---

## 👥 **Role Hierarchy**

### **1. Company Admin (
)**

**Properties:**
- `LicenseInfo.IsAdminLogged =true`
- `LicenseInfo.IsProjectAdminLogged = false`
- `LicenseInfo.Privilege = "Company Admin"`

**Capabilities:**
- ✅ Full access to all projects across the company
- ✅ Can bypass ALL protections (with `EnableMonitorAllCommandsForAdmin` setting)
- ✅ Configure company-wide settings
- ✅ Add/remove project admins
- ✅ Access analytics and audit trails
- ✅ Manage workspace settings

**Detection Logic:**
```csharp
// Line 70
public bool HasAdminPriviledges => 
    this.IsProjectAdminLogged || this.IsAdminLogged;

// Line 436-438
this.IsAdminLogged = !isNormalUser && !isProjectAdmin;
this.IsProjectAdminLogged = isProjectAdmin;
this.Privilege = this.IsProjectAdminLogged ? 
    Resources.ProjectAdmin_Previlage : 
    Resources.CompanyAdmin_Previlage;
```

---

### **2. Project Admin**

**Properties:**
- `LicenseInfo.IsAdminLogged = false`
- `LicenseInfo.IsProjectAdminLogged = true`
- `LicenseInfo.Privilege = "Project Admin"`

**Capabilities:**
- ✅ Admin access for **assigned projects only**
- ✅ Can bypass protections for their projects
- ✅ Configure project-specific settings
- ✅ Manage project-level rules and protections
- ✅ View project analytics
- ❌ No access to company-wide settings

**Project Assignment Check:**
```csharp
// Line 310-332: RevitSessionLocal.IsAdminOfCurrentProject()
private bool IsAdminOfCurrentProject(string projectCookie)
{
    ProjectInfo projectInfo = SessionLightCache.Instance.GetProjectInfo(
        projectCookie, false, nameof(IsAdminOfCurrentProject)
    );
    
    // Check if user's email is in project admin list
    return projectInfo?.ProjectSettings?.Administrators
        ?.Any(x => string.Equals(x.Email, SoapApplication.LicenseInfo.UserEmail)) 
        ?? false;
}
```

---

### **3. Regular User (Non-Admin)**

**Properties:**
- `LicenseInfo.IsAdminLogged = false`
- `LicenseInfo.IsProjectAdminLogged = false`
- `LicenseInfo.Privilege = null` (or empty)

**Capabilities:**
- ✅ View current project
- ✅ Subject to ALL protections (Monitor/Guide/Prevent)
- ✅ Can use the legacy product features (property tracking, etc.)
- ❌ Cannot bypass protections
- ❌ Cannot configure settings
- ❌ Limited analytics access

---

## 🔐 **Permission Checking Logic**

### **Primary Permission Check**

**Location:** `RevitSessionLocal.cs` Line 334-340

```csharp
public bool HasAdminPriviledgesForThisProject(string projectCookie)
{
    if (!string.IsNullOrEmpty(projectCookie))
    {
        // 1. Project Admin check: Is user in this project's admin list?
        // 2. Company Admin check: Global admin flag
        return SoapApplication.LicenseInfo.IsProjectAdminLogged && 
               this.IsAdminOfCurrentProject(projectCookie) || 
               SoapApplication.LicenseInfo.IsAdminLogged;
    }
    
    // No project specified - check company admin only
    LicenseInfo licenseInfo = SoapApplication.LicenseInfo;
    return licenseInfo != null && licenseInfo.IsAdminLogged;
}
```

**Logic Flow:**
```
User requests action on project "ProjectA"
         ↓
HasAdminPriviledgesForThisProject("ProjectA") called
         ↓
    ┌───┴────────────┐
    │                │
Is Company       Is Project
Admin?          Admin?
    │                │
    ↓                ↓
YES → ALLOW    Check email in
              ProjectA.Administrators?
                        │
                   ┌────┴────┐
                  YES       NO
                   │         │
                ALLOW     DENY
```

---

## ⚙️ **Admin Bypass Mechanism**

### **1. EnableMonitorAllCommandsForAdmin Setting**

**Purpose:** Control whether admins experience same protections as users

**Location:** `CustomInteractionSettings.cs` Line 500-509

```csharp
[JsonProperty(PropertyName = "EnableMonitorAllCommandsForAdmin")]
public bool? EnableMonitorAllCommandsForAdmin { get; set; }
```

**Usage Pattern:**
```csharp
// Line 388: CommandIntervention.cs
this.EnableUserExperienceForAdmins = 
    ((bool?)interactionSettings?.EnableMonitorAllCommandsForAdmin)
    .GetValueOrDefault();
```

**Behavior:**
- `false` (default): Admins bypass protections automatically
- `true`: Admins experience protections same as users (for testing/compliance)

---

### **2. Command Binding Registration Logic**

**Location:** `CommandIntervention.cs` Line 433

```csharp
// Only register command bindings if:
// 1. User is NOT an admin, OR
// 2. User is admin AND EnableUserExperienceForAdmins is TRUE

if (!RevitSessionLocal.Instance.HasAdminPriviledgesForThisProject(projectCookie) || 
    RevitSessionLocal.Instance.HasAdminPriviledgesForThisProject(projectCookie) && 
    this.EnableUserExperienceForAdmins)
{
    // Register command bindings
    if (this.CommandBinding_MasterPostableCommands == null)
        this.CommandBinding_MasterPostableCommands = 
            new Dictionary<string, CommandBindingIntervention>();
    
    // Loop through commands and create bindings
    foreach (PostableCommandSettings commandSettings in configuredCommands)
    {
        CommandBinding_MasterPostableCommands.Add(
            commandSettings.CommandCode,
            new CommandBindingIntervention(...)
        );
    }
}
```

**Result:**
- **Admin (default):** No command bindings created → No intervention dialogs → Commands execute freely
- **Admin (with flag):** Command bindings created → Intervention dialogs shown → Same experience as users
- **Regular User:** Always has command bindings → Always sees interventions

---

### **3. Pin/Unpin Protection Bypass**

**Location:** `CommandIntervention.cs` Line 4213

```csharp
// Pin command execution logic
flag = !RevitSessionLocal.Instance.HasAdminPriviledgesForThisProject(projectCookie) && 
       !((bool?)interactionSettings?.PinProtectionSettings?.IsPinProtectionAllowedForOtherUsers)
           .GetValueOrDefault() || 
       CommandIntervention.Instance.ProcessPinCommand(document, selectedElements);
```

**Simplified Logic:**
```csharp
if (IsAdmin(project))
{
    // Admin bypass - skip protection check
    return AllowPin();
}
else if (IsPinAllowedForOtherUsers)
{
    // Setting allows non-admins to pin
    return AllowPin();
}
else
{
    // Show protection dialog to user
    return ProcessPinCommand(); // Shows intervention dialog
}
```

---

### **4. Unpin Protection Bypass**

**Location:** `CommandIntervention.cs` Line 3978-3992

```csharp
private bool Binding_Execute_Unpin(Document document)
{
    IEnumerable<Element> selectedElements = GetSelectedElements(document);
    string projectCookie = MappingHelper.GetSoapDocumentCookie(document);
    
    // ADMIN BYPASS: If admin AND not testing mode
    if (RevitSessionLocal.Instance.HasAdminPriviledgesForThisProject(projectCookie) && 
        !CommandIntervention.Instance.EnableUserExperienceForAdmins)
    {
        // Clear protection metadata (admin override)
        PinExtensibleStorageHelper.ClearProtectedPinInfoFromDocument(
            document, selectedElements
        );
        
        // Allow unpin for standard elements
        return selectedElements == null || 
               !selectedElements.Any(x => x is View || x is ViewSchedule || 
                                          x is FamilySymbol || x is GroupType);
    }
    else
    {
        // Regular user flow - check protection and show dialog
        bool hasProtectedElements = selectedElements?
            .Select(x => PinExtensibleStorageHelper.GetProtectedPinInfoObject(x))
            .Any(x => x != null) ?? false;
        
        return ProcessUnpinCommand(document, selectedElements, ..., hasProtectedElements);
    }
}
```

---

## 📋 **Admin Assignment System**

### **Project Administrator Model**

**Location:** `ProjectAdministrator.cs` Line 15-48

```csharp
[Serializable]
public class ProjectAdministrator
{
    [JsonProperty(PropertyName = "Email")]
    public string Email { get; set; }
    
    [JsonProperty(PropertyName = "DateAdded")]
    public DateTime? DateAdded { get; set; }
    
    public static ProjectAdministrator CreateNewFromCurrent()
    {
        LicenseInfo licenseInfo = SoapApplication.LicenseInfo;
        
        return licenseInfo != null && 
               !string.IsNullOrEmpty(licenseInfo.UserEmail) && 
               licenseInfo.HasAdminPriviledges 
            ? Create(licenseInfo.UserEmail, DateTime.UtcNow) 
            : null;
    }
}
```

**Storage:**
- Stored in `ProjectSettings.Administrators` (List<ProjectAdministrator>)
- Fetched from API on project open
- Cached in `SessionLightCache`

---

### **Auto-Add Admin to Projects**

**Location:** `LicenseInfo.cs` Line 72-82

```csharp
public bool AutoAddAsAdminToProjects { get; set; }
public bool AllowAddAsAdminToProjects { get; set; }
```

**Behavior:**
```csharp
// If AutoAddAsAdminToProjects = true
// When opening a new project:
if (licenseInfo.AutoAddAsAdminToProjects)
{
    ProjectAdministrator newAdmin = ProjectAdministrator.CreateNewFromCurrent();
    await SoapAPIAppHelper.Instance.AddSingleAdminToProjectAsync(new RequestOptions {
        ProjectGuid = projectInfo.ProjectCookieGuid,
        Admin = newAdmin
    });
}
```

**Result:** Company admins automatically added to project admin lists on first open

---

## 🛡️ **Protection Enforcement Patterns**

### **Pattern 1: Skip Binding Registration (Most Common)**

```csharp
// Don't even create command bindings for admins (unless testing)
if (!IsAdmin || IsAdmin && EnableUserExperienceForAdmins)
{
    CreateCommandBindings();
}
```

**Impact:** Commands execute normally without any intervention

---

### **Pattern 2: Early Return (Feature-Specific)**

```csharp
// Check admin status before processing protection logic
if (HasAdminPrivileges(project))
{
    return AllowAction(); // Skip protection entirely
}

// Regular protection logic for users
return ProcessProtection();
```

**Impact:** Protection code never executes for admins

---

### **Pattern 3: Clear Protection Metadata (Unpin)**

```csharp
// Admin can forcibly clear protection metadata
if (IsAdmin && !EnableUserExperienceForAdmins)
{
    ClearProtectedPinInfoFromDocument(document, elements);
    return AllowUnpin();
}
```

**Impact:** Admin can override user-set protections

---

## 🔄 **Admin Flag Update Flow**

### **Initial Login**

```
User logs in with admin credentials
         ↓
Backendless authentication
         ↓
InitAndValidate() called
         ↓
Parse "IsProjectAdmin" property from Backendless
         ↓
Set LicenseInfo.IsProjectAdminLogged
         ↓
If not project admin:
    LicenseInfo.IsAdminLogged = true (Company Admin)
         ↓
UpdateAdminFlags() called
         ↓
Revise all command bindings based on new role
```

### **Role Change (Company → Project Admin)**

```csharp
// Line 428-448: UpdateAdminFlags()
public void UpdateAdminFlags(
    bool isNormalUser,
    bool isProjectAdmin,
    bool autoAddAsAdminToProjects,
    bool allowAddAsAdminToProjects)
{
    if (this.IsProjectAdminLogged != isProjectAdmin)
    {
        // Update flags
        this.IsAdminLogged = !isNormalUser && !isProjectAdmin;
        this.IsProjectAdminLogged = isProjectAdmin;
        this.Privilege = this.IsProjectAdminLogged ? 
            "Project Admin" : "Company Admin";
        
        // Reset logging level (admins get verbose logging)
        LogHelper.ResetLogger();
        
        // Update ribbon button visibility
        SoapApplication.SetCommandButtonVisibilityForLicense();
        
        // 🔑 CRITICAL: Revise ALL command bindings
        CommandIntervention.Instance.ReviseAllCommandBinding(true);
    }
}
```

**Impact:** Command bindings recreated based on new admin status

---

## 💡 **RBAC Design Recommendations for CBOX Manage**

### **1. Adopt Two-Tier Model**

```csharp
public enum UserRole
{
    CompanyAdmin,      // Full access across organization
    ProjectAdmin,      // Access to assigned projects only
    User               // Standard user with protections
}

public interface IAuthorizationService
{
    UserRole GetUserRole();
    bool HasProjectAccess(string projectId);
    bool CanBypassProtections();
}
```

---

### **2. Permission Check Pattern**

```csharp
public bool HasAdminPrivileges(string projectId)
{
    var role = _authService.GetUserRole();
    
    return role switch
    {
        UserRole.CompanyAdmin => true,  // Global admin
        UserRole.ProjectAdmin => _authService.HasProjectAccess(projectId),
        UserRole.User => false,
        _ => false
    };
}
```

---

### **3. Protection Bypass Configuration**

```csharp
public class ProtectionSettings
{
    // Global toggle: should admins experience protections?
    public bool EnableProtectionsForAdmins { get; set; } = false;
    
    // Per-protection override capability
    public bool AllowAdminOverride { get; set; } = true;
}

// Usage in command intervention
if (!HasAdminPrivileges(project) || 
    settings.EnableProtectionsForAdmins)
{
    // Show protection intervention
    ProcessProtection();
}
```

---

### **4. Admin Assignment Storage**

```csharp
// SQLite schema for project admins
public class ProjectAdmin
{
    public int Id { get; set; }
    public string ProjectId { get; set; }
    public string UserEmail { get; set; }
    public DateTime DateAdded { get; set; }
    public string AddedBy { get; set; }  // Audit trail
}

// Check if user is project admin
public bool IsProjectAdmin(string projectId, string userEmail)
{
    return _dbContext.ProjectAdmins
        .Any(pa => pa.ProjectId == projectId && 
                   pa.UserEmail == userEmail);
}
```

---

### **5. Audit Trail for Admin Bypasses**

```csharp
// Log when admin bypasses protection
public void LogAdminBypass(string commandName, string reason)
{
    _auditService.Log(new AuditEvent
    {
        EventType = "AdminBypass",
        UserEmail = _authService.CurrentUser.Email,
        UserRole = _authService.GetUserRole().ToString(),
        Action = commandName,
        Reason = reason,
        Timestamp = DateTime.UtcNow
    });
}
```

---

## 📊 **Summary Table**

| Feature | Company Admin | Project Admin | User |
|---------|--------------|--------------|------|
| **Command Bindings** | Skip (default) | Skip (if assigned) | Always active |
| **Delete Protection** | Bypass | Bypass (assigned projects) | Enforced |
| **Pin Protection** | Bypass | Bypass (assigned projects) | Enforced |
| **Unpin Protected** | Override metadata | Override metadata | Blocked/Dialog |
| **Rule Configuration** | ✅ All projects | ✅ Assigned projects | ❌ |
| **Analytics Access** | ✅ Full | ✅ Project-level | ⚠️ Limited |
| **Settings Management** | ✅ Company-wide | ✅ Project-specific | ❌ |

---

## 🔑 **Key Takeaways**

1. **Two-tier system:** Company Admin (global) + Project Admin (per-project)
2. **Email-based matching:** Project admin check uses email in administrator list
3. **Bypass via non-registration:** Skip creating command bindings for admins entirely
4. **Testing mode:** `EnableMonitorAllCommandsForAdmin` lets admins test protections
5. **Dynamic role changes:** `UpdateAdminFlags()` revises bindings when role changes
6. **Early returns:** Most protection logic checks admin status first and returns early
7. **Metadata override:** Admins can clear protection metadata (e.g., protected pins)

the legacy product's RBAC is **simple but effective** - check admin status early and skip protection logic entirely rather than implementing complex permission layers.
