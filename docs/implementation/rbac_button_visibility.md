# the legacy product RBAC: Real-Time Button Visibility Enforcement

> **Purpose**: Document how the legacy product enforces role-based button appearance dynamically  
> **Mechanism**: Revit's `IExternalCommandAvailability` interface + automatic polling  
> **For**: CBOX Manage RBAC UI implementation

---

## 🎯 Overview

the legacy product achieves **real-time button visibility** using Revit's built-in mechanism: **`IExternalCommandAvailability`**. Revit automatically polls these classes to determine if ribbon buttons should be:
- **Visible/Hidden**
- **Enabled/Grayed out**

**No manual refresh needed** - Revit handles this automatically on:
- Idling events (when Revit is idle)
- Selection changes
- Document activation
- View changes

---

## 🏗️ Architecture Pattern

### 1. Button Registration with Availability Class

**Location**: [`SoapApplication.cs`](file:///e:/Visual%20Studio/00-LegacyProduct/Soap/Revit/Applications/SoapApplication.cs#L940-L991)

```csharp
// Create button
PushButtonData registerProjectButton = new PushButtonData(
    "RegisterProject",
    "Register Project",
    assemblyPath,
    "Soap.Revit.Commands.RegisterProjectCommand"
);

// 🔑 KEY: Assign availability class
registerProjectButton.AvailabilityClassName = "Soap.Revit.Applications.AvailableOnlyToAdminsWithActiveFeature";

// Add to ribbon
ribbon.AddItem(registerProjectButton);
```

**What this does**:
- Tells Revit: "Poll the `AvailableOnlyToAdminsWithActiveFeature` class to check if this button should be enabled"
- Revit calls `IsCommandAvailable()` method **automatically** at regular intervals

---

### 2. Availability Class Implementation

**Example**: [`AvailableOnlyToAdmins.cs`](file:///e:/Visual%20Studio/00-LegacyProduct/Soap/Revit/Applications/AvailableOnlyToAdmins.cs)

```csharp
public class AvailableOnlyToAdmins : IExternalCommandAvailability
{
    // Revit calls this method automatically
    public bool IsCommandAvailable(UIApplication app, CategorySet selectedCategories)
    {
        return AvailableOnlyToAdmins.IsCommandAvailable();
    }

    public static bool IsCommandAvailable()
    {
        // Check if user has admin privileges
        LicenseInfo licenseInfo = SoapApplication.LicenseInfo;
        
        return licenseInfo?.IsAdminLogged == true 
            || licenseInfo?.IsProjectAdminLogged == true;
    }
}
```

**Return Values**:
- `true` → Button is **enabled** (white icon)
- `false` → Button is **grayed out** (disabled)

---

## 📋 the legacy product's Availability Classes (15+)

| **Class Name** | **Logic** | **Use Case** |
|---------------|-----------|-------------|
| **`AvailableOnlyToAdmins`** | `IsAdminLogged OR IsProjectAdminLogged` | Standard admin-only buttons |
| **`AvailableOnlyToCompanyAdmins`** | `IsAdminLogged` (Company Admin only) | Company-wide settings buttons |
| **`AvailableOnlyToAdminsWithActiveDocument`** | Admin + document open + project registered | Project-specific admin actions |
| **`AvailableOnlyToAdminsWithActiveProjectDocument`** | Admin + project document (not family) | Buttons that don't work in family editor |
| **`AvailableOnlyToAdminsWithActiveFeature`** | Admin + active document + feature enabled | Feature-toggle dependent buttons |
| **`AvailableOnlyToAdminsIfAnyDocumentIsOpen`** | Admin + any document open | Works even without project registration |
| **`AvailableOnlyToAdminsIfDevModeOn`** | Admin + dev mode enabled | Debug/testing buttons |
| **`AvailableToAllWithProjectDocument`** | Any user + project document open | Available to everyone (e.g., Help, About) |
| **`AvailableToAllWithActiveDocument`** | Any user + any document open | Universal buttons |
| **`AvailableToAllIfAnyDocumentIsOpen`** | Any user + document exists | Broad availability |
| **`AvailableToAdminForWSDocWithSyncTrafficEnabled`** | Admin + workshared + sync traffic enabled | Sync management buttons |
| **`AvailabilityAlways`** | Always true | Buttons that are never disabled |
| **`AvailabilityAlwaysExceptFamily`** | True unless in family editor | Project-only buttons |
| **`AvailableOnlyIfLicenseAuthorized`** | License is valid | Core buttons (require license) |
| **`AvailabilityForSendMessage`** | License valid + document open | Messaging buttons |

---

## ⚙️ How Revit Triggers Updates

### Revit's Automatic Polling Mechanism

Revit calls `IsCommandAvailable()` on **every availability class** when:

1. **Idling Event** (most common)
   - Fires when Revit UI is idle (no operations in progress)
   - Frequency: ~2-3 times per second when idle

2. **Selection Changed**
   - User selects/deselects elements

3. **Active View Changed**
   - User switches views

4. **Document Activated/Deactivated**
   - User switches between open documents

5. **After Major Operations**
   - After sync with central
   - After undo/redo
   - After deleting elements

---

## 🔄 Real-Time Update Flow

```
┌─────────────────────────────────────────────────────────────┐
│  User logs in as Project Admin                              │
│  → LicenseInfo.IsProjectAdminLogged = true                 │
└───────────────┬─────────────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────────────────────────┐
│  Revit Idling Event fires (automatic, ~every 500ms)         │
└───────────────┬─────────────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────────────────────────┐
│  Revit calls all IExternalCommandAvailability classes       │
│  ┌──────────────────────────────────────────────────────┐  │
│  │ AvailableOnlyToAdmins.IsCommandAvailable()           │  │
│  │   → Returns true (user is project admin)             │  │
│  └──────────────────────────────────────────────────────┘  │
└───────────────┬─────────────────────────────────────────────┘
                │
                ▼
┌─────────────────────────────────────────────────────────────┐
│  Revit updates ribbon buttons                               │
│  → "Company Settings" button: Enabled                       │
│  → "Register Project" button: Enabled                       │
│  → Normal user buttons: Remain enabled                      │
└─────────────────────────────────────────────────────────────┘
```

---

## 🔑 Key Pattern: Static `LicenseInfo` as Single Source of Truth

**Location**: `SoapApplication.LicenseInfo` (single static instance)

```csharp
// All availability classes check the SAME static instance
public static bool IsCommandAvailable()
{
    LicenseInfo licenseInfo = SoapApplication.LicenseInfo;
    
    // Check role flags
    return licenseInfo?.IsAdminLogged == true 
        || licenseInfo?.IsProjectAdminLogged == true;
}
```

**When role changes** (e.g., user re-authenticates with different role):

```csharp
// Step 1: Login completes
await AuthenticationService.LoginAsync(username, password);

// Step 2: Update static LicenseInfo
SoapApplication.LicenseInfo.IsAdminLogged = backendUser.IsCompanyAdmin;
SoapApplication.LicenseInfo.IsProjectAdminLogged = backendUser.IsProjectAdmin;

// Step 3: Wait for next Idling event (automatic)
// Revit polls availability classes → buttons update automatically

// THAT'S IT! No manual refresh needed.
```

---

## 📊 Example: Multi-Level Button Visibility

### Button: "Company Settings"

**AvailabilityClassName**: `"Soap.Revit.Applications.AvailableOnlyToCompanyAdmins"`

**Logic**:
```csharp
public static bool IsCommandAvailable(UIApplication app)
{
    LicenseInfo licenseInfo = SoapApplication.LicenseInfo;
    return licenseInfo != null && licenseInfo.IsAdminLogged;
}
```

**Behavior**:
- **Company Admin**: Button **enabled** ✅
- **Project Admin**: Button **grayed out** ⚫
- **Normal User**: Button **grayed out** ⚫

---

### Button: "Register Project"

**AvailabilityClassName**: `"Soap.Revit.Applications.AvailableOnlyToAdminsWithActiveFeature"`

**Logic**:
```csharp
public bool IsCommandAvailable(UIApplication app, CategorySet selectedCategories)
{
    // Check if user is admin
    if (!AvailableOnlyToAdmins.IsCommandAvailable())
        return false;

    // Check if document is open
    Document activeDoc = app.ActiveUIDocument?.Document;
    if (activeDoc == null || !activeDoc.IsValidObject)
        return false;

    // Check if feature is enabled
    bool featureEnabled = SoapApplication.LicenseInfo?.FeatureFlags?.ProjectRegistration == true;
    
    return featureEnabled;
}
```

**Behavior**:
- **Company Admin + Document Open + Feature On**: **Enabled** ✅
- **Company Admin + No Document**: **Grayed out** ⚫
- **Normal User**: **Grayed out** ⚫

---

## 🚀 the legacy product's Button Update Scenarios

### Scenario 1: User Role Changes (Re-login)

```
User logs out
    ↓
LicenseInfo.IsAdminLogged = false
LicenseInfo.IsProjectAdminLogged = false
    ↓
User logs in as Project Admin
    ↓
LicenseInfo.IsProjectAdminLogged = true
    ↓
Next Idling event (< 1 second)
    ↓
Revit calls all availability classes
    ↓
Admin buttons automatically enable
```

**No manual intervention** - Revit detects the change via normal polling.

---

### Scenario 2: Document Opens/Closes

```
User opens document
    ↓
Revit fires DocumentOpened event
    ↓
Revit automatically triggers availability refresh
    ↓
Buttons with "WithActiveDocument" logic update:
    - "Register Project" → Enabled (if admin)
    - "Protected Pins" → Enabled
```

---

### Scenario 3: Feature Toggle Changes (via SignalR)

```
Admin enables "Project Registration" feature in web UI
    ↓
SignalR publishes: ProjectSettings updated
    ↓
the legacy product receives message via SignalRMessageInfoReceivedEvent
    ↓
the legacy product updates: LicenseInfo.FeatureFlags.ProjectRegistration = true
    ↓
Next Idling event
    ↓
AvailableOnlyToAdminsWithActiveFeature.IsCommandAvailable() → returns true
    ↓
"Register Project" button enables
```

---

## 🔍 Advanced Pattern: Composite Availability

Some availability classes **combine multiple conditions**:

```csharp
public class AvailableOnlyToAdminsWithActiveProjectDocument : IExternalCommandAvailability
{
    public static bool IsCommandAvailable(UIApplication app)
    {
        // Condition 1: Must be admin
        if (!AvailableOnlyToAdmins.IsCommandAvailable())
            return false;

        // Condition 2: Must have active document
        Document activeDoc = app.ActiveUIDocument?.Document;
        if (activeDoc == null || !activeDoc.IsValidObject)
            return false;

        // Condition 3: Document must be project (not family)
        if (activeDoc.IsFamilyDocument)
            return false;

        // Condition 4: Project must be registered with the legacy product
        string projectGuid = MappingHelper.GetSoapDocumentCookie(activeDoc);
        if (string.IsNullOrEmpty(projectGuid))
            return false;

        return true;
    }
}
```

**This pattern allows granular control** without duplicating logic.

---

## ⚠️ Important Notes

### 1. **Performance**: Keep Availability Checks Fast

Revit calls these methods **frequently**. the legacy product keeps checks lightweight:

```csharp
// ✅ GOOD: Fast static property access
return SoapApplication.LicenseInfo?.IsAdminLogged == true;

// ❌ BAD: Database query in availability check
return DatabaseService.CheckIfUserIsAdmin(userId); // TOO SLOW!
```

**Rule**: Availability checks should complete in **< 1ms**.

---

### 2. **No Direct Button Manipulation**

the legacy product **never** does:
```csharp
// ❌ BAD: Manual button enable/disable
myRibbonButton.Enabled = true;
```

Instead, it relies on **Revit's polling**:
```csharp
// ✅ GOOD: Update state, let Revit poll
SoapApplication.LicenseInfo.IsAdminLogged = true;
// Revit will detect change on next Idling event
```

---

### 3. **No Explicit Refresh Call Needed**

Unlike some UI frameworks, **the legacy product doesn't call**:
- `RefreshRibbon()`
- `InvalidateButtons()`
- `UpdateButtonStates()`

**Revit's Idling event handles everything automatically.**

---

## ✅ CBOX Manage Implementation Checklist

### Step 1: Define Availability Classes
- [ ] Create base availability classes:
  - `AvailableOnlyToCompanyAdmins`
  - `AvailableOnlyToProjectAdmins`
  - `AvailableToAnyAdmin`
  - `AvailableToAll`
- [ ] Implement `IExternalCommandAvailability` interface
- [ ] Add role checks via `LicenseInfo` equivalent

### Step 2: Assign to Buttons
- [ ] Set `AvailabilityClassName` for each ribbon button
- [ ] Test button states with different roles

### Step 3: Ensure Fast Role Checks
- [ ] Store role info in static class (like `LicenseInfo`)
- [ ] Keep `IsCommandAvailable()` methods < 1ms execution time
- [ ] Avoid database/API calls in availability methods

### Step 4: Handle Role Changes
- [ ] Update static role properties on login/logout
- [ ] Let Revit's Idling event handle UI updates automatically
- [ ] Test role switching scenarios

### Step 5: Test Real-Time Updates
- [ ] Verify buttons update within 1 second of role change
- [ ] Test with document open/close
- [ ] Test with feature toggle changes

---

## 🎯 Summary

the legacy product's real-time button visibility works through:

1. **`IExternalCommandAvailability`**: Revit's built-in interface for button state control
2. **Static Role Storage**: `SoapApplication.LicenseInfo` as single source of truth
3. **Automatic Polling**: Revit calls availability methods on Idling events (~2-3 times/sec)
4. **No Manual Refresh**: Just update role properties, Revit handles the rest

**For CBOX Manage**: Use the exact same pattern - define availability classes, assign to buttons, update role state, and let Revit's polling do the work. **No custom refresh mechanism needed.**

---

**End of RBAC Button Visibility Documentation**  
*Generated from the legacy product analysis | February 2026*
