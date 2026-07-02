# the legacy product Scenario-Based Protections

> **Purpose**: Catalog of specific workflow/scenario protections in the legacy product  
> **Scope**: Event-driven protections for file operations, family management, model upgrades, etc.  
> **For**: CBOX Manage Event Capture & Polling Engine implementation

---

## 📋 Overview

the legacy product monitors and protects **8 major scenario categories** beyond standard command interception:

1. **File Opening Protections** (3 scenarios)
2. **File Saving Protections** (1 scenario)
3. **Model Upgrade Protection** (1 scenario)
4. **Family Loading Protections** (3 scenarios)
5. **Element Pin Protection** (1 scenario)
6. **Sync Traffic Management** (1 scenario)
7. **the legacy product System Control** (1 scenario)
8. **Element Deletion with Dependents** (1 scenario)

---

## 1️⃣ File Opening Protections

### 1.1 Opening from Non-Approved Location

**Trigger Event**: `DocumentOpening`  
**Dummy Command ID**: `"the legacy product_OpenFileFromNonApprovedProtection"`  
**Backend Setting**: `EnableFileOpeningProtection`  

**What It Protects**:
- Users opening Revit files from non-approved network locations
- Enforces "only open files from approved BIM folder paths"

**How It Works**:
```csharp
DocumentOpening event fires
    → Extract file path from DocumentOpeningEventArgs
    → Check if path is in approved locations list (stored in backend)
    → If NOT approved:
        - Monitor: Log event, allow open
        - Guide: Show dialog "Opening from unapproved location", allow override
        - Prevent: Cancel open, block user
```

**Admin Configuration**:
- List of approved folder paths (e.g., `\\server\bim\projects\`)
- Intervention mode (Monitor/Guide/Prevent)
- Custom message ("Only open files from approved BIM360 locations")

---

### 1.2 Opening Central File Directly

**Trigger Event**: `DocumentOpening`  
**Dummy Command ID**: `"the legacy product_OpenCentralFileProtection"`  
**Backend Setting**: `EnableOpenCentralFileProtection`  

**What It Protects**:
- Users opening central file directly (instead of creating local copy)
- Prevents accidental central file corruption

**How It Works**:
```csharp
DocumentOpening event fires
    → Check if file is workshared (has central model)
    → Check if user is opening central file path
    → If opening CENTRAL (not local):
        - Monitor: Log event, allow
        - Guide: Show dialog "You are opening the central file directly. This is not recommended."
        - Prevent: Cancel open
```

**Special**: Company-level protection only (not project-specific)

---

### 1.3 Model Upgrade Protection

**Trigger Event**: `DocumentOpening`  
**Dummy Command ID**: `"the legacy product_ModelUpgradeProtection"`  
**Backend Setting**: `UpgradeRevitFileProtectionSetting`  

**What It Protects**:
- Automatically upgrading Revit files to newer version
- Prevents accidental irreversible upgrades

**How It Works**:
```csharp
DocumentOpening event fires
    → Extract BasicFileInfo from opening file
    → Compare file.Format (e.g., "2021") with current Revit version
    → If file version < current version:
        - Monitor: Log upgrade, allow
        - Guide: Show dialog "This file will be upgraded from 2021 to 2022. Continue?"
        - Prevent: Cancel open
```

**File Type Options**:
- Protect project files (.rvt)
- Protect family files (.rfa)
- Protect both

---

## 2️⃣ File Saving Protections

### 2.1 Save Over Earlier Version

**Trigger Event**: `DocumentSaving`  
**Dummy Command ID**: `"the legacy product_SaveOverEarlierFileVersionProtection"`  
**Backend Setting**: `EnableSaveOverEarlierVersionProtection`  

**What It Protects**:
- Saving a newer version file back to older Revit format
- e.g., Opening 2022 file in 2023, then saving back as 2022

**How It Works**:
```csharp
DocumentSaving event fires
    → Extract save path from DocumentSavingEventArgs
    → Get BasicFileInfo from target file
    → Compare target file version < current document version
    → If downgrading:
        - Monitor: Log event, allow
        - Guide: Show dialog "Saving to older version may lose data. Continue?"
        - Prevent: Cancel save
```

**Real-World Use Case**: Prevent users from accidentally overwriting coordination model with downgraded version

---

## 3️⃣ Family Loading Protections

### 3.1 Family Library Location Protection

**Trigger Event**: `FamilyLoadingIntoDocument`  
**Dummy Command ID**: `"the legacy product_FamilyLibrarySettings"`  
**Backend Setting**: `EnableFamilyLibrarySettings`  

**What It Protects**:
- Loading families from non-approved library locations
- Enforces "only load families from company standard library"

**How It Works**:
```csharp
FamilyLoadingIntoDocument event fires
    → Extract family path from FamilyLoadingIntoDocumentEventArgs
    → Evaluate against FamilyLibraryProtectionRules (nested conditions):
        - Approved Location rule
        - Protected Category rule
        - Family Version rule
    → If rule matched:
        - Monitor: Log family load from unapproved location
        - Guide: Show dialog "Loading from non-standard library. Proceed?"
        - Prevent: Cancel family load
```

**Rule-Based Protection**:
- Uses nested condition engine (like element rules)
- Can check: file path, category, family version, family name pattern
- Example rule: "Block loading Doors from anywhere except `\\server\content\families\doors\`"

---

### 3.2 Overwrite Protected Family

**Trigger Event**: `FamilyLoadingIntoDocument`  
**Dummy Command ID**: `"the legacy product_FamilyLoadingProtection"`  
**Backend Setting**: `EnableEditFamilyProtection`  

**What It Protects**:
- Replacing/overwriting protected families in project
- the legacy product stores list of protected families in Extensible Storage

**How It Works**:
```csharp
FamilyLoadingIntoDocument event fires
    → Check if family with same name already exists in project
    → If exists: Check if existing family is marked as "Protected" (Extensible Storage)
    → If protected:
        - Evaluate EditFamilyProtectionSettings rules
        - Monitor: Log overwrite attempt
        - Guide: Show dialog "This family is protected. Overwrite?"
        - Prevent: Cancel family load
```

**Extensible Storage**:
- the legacy product stores `ProtectedElementExtensibleData` on family elements
- Contains: `InterventionType`, `AdminComment`, `IsRequireComment`, etc.

---

### 3.3 Family Version Mismatch

**Trigger Event**: `FamilyLoadingIntoDocument`  
**Context**: Part of Family Library Protection  

**What It Protects**:
- Loading families created in different Revit versions
- e.g., Loading 2020 family into 2022 project

**How It Works**:
```csharp
FamilyLoadingIntoDocument event fires
    → Extract BasicFileInfo.Format from family file
    → Create ConditionEvaluationContext with family version
    → Evaluate FamilyLibraryProtectionRule with version condition
    → If version mismatch detected:
        - Show warning dialog
        - Allow/block based on rule
```

---

## 4️⃣ Element Pin Protection

### 4.1 Unpin Protected Elements

**Trigger**: `PostableCommand.ToggleUnpinned` (via `CommandBinding_Unpin`)  
**Extensible Storage**: `PinExtensibleStorageHelper`  
**Backend Setting**: `PromptUserForUnpin`  

**What It Protects**:
- Unpinning elements that have been marked as "protected pins"
- the legacy product stores list of protected element IDs in document Extensible Storage

**How It Works**:
```csharp
User clicks Unpin command
    → BeforeExecuted handler fires
    → Get current selection IDs
    → Query Extensible Storage for protected pin list
    → Check if any selected element is in protected list
    → If protected:
        - Monitor: Log unpin, allow
        - Guide: Show dialog "This element is pin-protected. Unpin?"
        - Prevent: Cancel unpin (e.Cancel = true)
```

**Admin UI**:
- the legacy product has ribbon button "Protected Pins"
- Opens `ProtectedPinnedElementsView` dialog
- Admin can:
  - View all protected pins in project
  - Add/remove elements from protected list
  - Configure unpin protection settings

**Storage Schema**:
```csharp
Schema: "the legacy productProtectedPinSchema"
Entity stored on: ProjectInfo element
Field: List<ElementId> ProtectedElementIds
```

---

## 5️⃣ Sync Traffic Management

### 5.1 Sync Conflict Detection

**Trigger Event**: `DocumentSynchronizingWithCentral`  
**SignalR Group**: `"SyncGroup_{ProjectId}"`  
**Backend Setting**: `EnableSyncManagement`  

**What It Protects**:
- Multiple users syncing to central at same time
- Prevents sync conflicts and lost work

**How It Works**:
```csharp
User A clicks "Synchronize with Central"
    ↓
DocumentSynchronizingWithCentral event fires
    ↓
Publish SignalR event: "UserStartedSync" with UserID, Timestamp
    ↓
Other users (B, C, D) receive SignalR notification
    ↓
User B tries to sync 30 seconds later
    ↓
the legacy product checks: Is another user syncing? (via SignalR state)
    ↓
If conflict detected:
        - Monitor: Show notification "User A is syncing", allow sync
        - Guide: Show dialog "User A is syncing. Wait or proceed?", allow override
        - Prevent: Block sync, force wait until User A finishes
    ↓
User A finishes sync
    ↓
DocumentSynchronizedWithCentral event fires
    ↓
Publish SignalR event: "UserFinishedSync"
    ↓
User B can now sync without conflict
```

**Real-Time Coordination**:
- the legacy product maintains sync state in SignalR hub
- All users in same project subscribed to sync events
- Sync start/finish broadcasts to all connected users

**SignalR Events**:
- `OnSyncStarted(userId, projectId, timestamp)`
- `OnSyncCompleted(userId, projectId, timestamp)`
- `OnSyncAborted(userId, projectId, reason)`

---

## 6️⃣ the legacy product System Control

### 6.1 Pause the legacy product Protection

**Trigger**: Custom ribbon button "Pause Protection"  
**Dummy Command ID**: `"the legacy product_Pausethe legacy productProtection"`  
**Backend Setting**: `Pausethe legacy productProtection`  

**What It Protects**:
- Users disabling the legacy product protection temporarily
- Requires admin authorization to pause

**How It Works**:
```csharp
User clicks "Pause the legacy product" ribbon button
    ↓
Check Pausethe legacy productProtectionSetting:
    - Monitor: Log pause request, allow immediately
    - Guide: Show dialog "Pausing protection. Confirm?", allow override
    - Prevent: Block pause (requires admin override/OTP)
    ↓
If allowed:
    Set flag: Global.IS_LEGACY-REFERENCE_PAUSED = true
    Disable all event handlers temporarily
    Show notification: "Protection paused for 30 minutes"
    ↓
Auto-resume after timeout (configurable duration)
```

**Use Case**:
- User needs to perform bulk operations without intervention dialogs
- Admin can configure if this is allowed (Monitor/Guide/Prevent)

**Company-Level Only**: This is a company-wide setting, not project-specific

---

## 7️⃣ Element Deletion with Dependents

### 7.1 Delete Elements with Dependencies

**Trigger Event**: `PostableCommand.Delete` (before execution)  
**Backend Setting**: `PromptUserForDeleteWithDependents`  

**What It Protects**:
- Deleting elements that have other elements dependent on them
- Example: Deleting a level that hosts views, grids, elements

**How It Works**:
```csharp
User selects elements and presses Delete
    ↓
CommandBinding_Delete BeforeExecuted fires
    ↓
For each selected element:
    - Get dependent elements (using Document.GetDependentElements())
    - Filter out "always deleted" dependents (e.g., dimensions, tags)
    - Build list of "important" dependents (e.g., hosted elements, constraints)
    ↓
If dependents found:
    Show dialog:
        "Deleting [Element Name] will affect [N] other elements:
        - View: Level 1 Floor Plan
        - Grid: A
        - Wall: Basic Wall (ID 12345)
        
        Options:
        [ ] Delete selected only
        [ ] Delete selected + dependents
        [Cancel]"
    ↓
User choice:
    - Cancel → e.Cancel = true
    - Delete selected only → proceed, set Revit flag to preserve dependents
    - Delete selected + dependents → proceed, delete all
```

**Dependent Types Checked**:
- Hosted elements (doors in walls, tags on elements)
- Constraint dependencies (aligned/locked elements)
- View-specific elements (annotations visible only in certain views)
- Workset dependencies

---

## 🗂️ Scenario Protection Summary Table

| **Scenario** | **Trigger Event** | **Dummy Command ID** | **Setting Key** | **Protection Type** |
|-------------|------------------|---------------------|----------------|-------------------|
| **Open from Non-Approved Location** | `DocumentOpening` | `the legacy product_OpenFileFromNonApprovedProtection` | `EnableFileOpeningProtection` | Monitor/Guide/Prevent |
| **Open Central File Directly** | `DocumentOpening` | `the legacy product_OpenCentralFileProtection` | `EnableOpenCentralFileProtection` | Monitor/Guide/Prevent (Company) |
| **Model Upgrade** | `DocumentOpening` | `the legacy product_ModelUpgradeProtection` | `UpgradeRevitFileProtectionSetting` | Monitor/Guide/Prevent |
| **Save to Older Version** | `DocumentSaving` | `the legacy product_SaveOverEarlierFileVersionProtection` | `EnableSaveOverEarlierVersionProtection` | Monitor/Guide/Prevent |
| **Family from Unapproved Library** | `FamilyLoadingIntoDocument` | `the legacy product_FamilyLibrarySettings` | `EnableFamilyLibrarySettings` | Rule-Based (Monitor/Guide/Prevent) |
| **Overwrite Protected Family** | `FamilyLoadingIntoDocument` | `the legacy product_FamilyLoadingProtection` | `EnableEditFamilyProtection` | Rule-Based + Extensible Storage |
| **Unpin Protected Elements** | `PostableCommand.ToggleUnpinned` | N/A (uses Extensible Storage) | `PromptUserForUnpin` | Monitor/Guide/Prevent |
| **Sync Conflict** | `DocumentSynchronizingWithCentral` | N/A (SignalR-based) | `EnableSyncManagement` | Real-Time Coordination |
| **Pause the legacy product** | Custom Button | `the legacy product_Pausethe legacy productProtection` | `Pausethe legacy productProtection` | Monitor/Guide/Prevent (Company) |
| **Delete with Dependents** | `PostableCommand.Delete` | N/A (hardcoded check) | `PromptUserForDeleteWithDependents` | Warning Dialog |

---

## 📊 Implementation Architecture

### Dummy Command Pattern

the legacy product uses **"Dummy Command IDs"** for scenarios that don't map to real Revit commands:

```csharp
// Not a real Revit command, just an identifier for logging/tracking
internal const string FAMILY_LOADING_DUMMY_COMMAND_ID = "the legacy product_FamilyLoadingProtection";

// In event handler:
FamilyLoadingIntoDocument event fires
    → Create PostableCommandSettings with CommandCode = FAMILY_LOADING_DUMMY_COMMAND_ID
    → Pass to CommandMonitor.WatchFor() for logging
    → Log event as if it were a command (for reporting consistency)
```

**Purpose**: Allows the legacy product to treat event-based protections the same as command-based protections in logging/reporting

---

### Extensible Storage for Protections

the legacy product stores protection metadata directly in Revit document:

**Use Cases**:
1. **Protected Pins**: List of element IDs that cannot be unpinned
2. **Protected Families**: Families that cannot be overwritten
3. **Protection Settings**: Per-element intervention settings

**Schema Example**:
```csharp
Schema schema = Schema.Lookup(new Guid("the legacy product_ProtectedPins_Schema"));
Entity entity = document.ProjectInformation.GetEntity(schema);
IList<ElementId> protectedIds = entity.Get<IList<ElementId>>("ProtectedElementIds");

// Check if element is protected:
if (protectedIds.Contains(selectedElementId))
{
    // Show protection dialog
}
```

---

### SignalR Real-Time Coordination

the legacy product uses SignalR for real-time multi-user coordination:

**Sync Conflict Example**:
```csharp
// User A starts sync
SignalRService.PublishSyncStarted(userId: "userA", projectId: "proj123");

// User B's the legacy product client receives notification
SignalRHub.OnSyncStarted(userId: "userA", projectId: "proj123")
    → Update local state: IsSyncInProgress = true, SyncingUser = "userA"
    → If User B tries to sync: Show warning dialog

// User A finishes sync
SignalRService.PublishSyncCompleted(userId: "userA", projectId: "proj123");

// User B receives notification
SignalRHub.OnSyncCompleted(userId: "userA", projectId: "proj123")
   → Update local state: IsSyncInProgress = false
    → User B can now sync
```

---

## ✅ CBOX Manage Implementation Checklist

### Phase 1: File Opening Protections
- [ ] **Implement Non-Approved Location Check**
  - Subscribe to `DocumentOpening`
  - Compare file path against approved locations list (from backend)
  - Show intervention dialog (Monitor/Guide/Prevent)
- [ ] **Implement Central File Protection**
  - Detect if opening central file vs. local copy
  - Warn user about risks
- [ ] **Implement Model Upgrade Protection**
  - Extract `BasicFileInfo.Format` from opening file
  - Compare with current Revit version
  - Show intervention dialog if upgrade detected

### Phase 2: File Saving Protections
- [ ] **Implement Save-Over-Earlier-Version Protection**
  - Subscribe to `DocumentSaving`
  - Compare target file version with current document
  - Warn if downgrading

### Phase 3: Family Loading Protections
- [ ] **Implement Family Library Location Check**
  - Subscribe to `FamilyLoadingIntoDocument`
  - Evaluate family path against library rules
  - Integrate with nested condition engine
- [ ] **Implement Protected Family Overwrite Check**
  - Store protected family list in Extensible Storage
  - Check on family load if overwriting protected family
  - Show intervention dialog
- [ ] **Implement Family Version Check**
  - Extract family version from `.rfa` file
  - Compare with project/rule requirements

### Phase 4: Element Pin Protection
- [ ] **Create Extensible Storage Schema for Protected Pins**
- [ ] **Implement Unpin Command Interceptor**
  - Check if element is in protected list
  - Show intervention dialog if protected
- [ ] **Build Admin UI for Managing Protected Pins**

### Phase 5: Sync Traffic Management
- [ ] **Implement SignalR Sync Coordination**
  - Publish `SyncStarted` event on `DocumentSynchronizingWithCentral`
  - Subscribe to sync events from other users
  - Show warning if conflict detected
  - Publish `SyncCompleted` event on `DocumentSynchronizedWithCentral`

### Phase 6: System Control
- [ ] **Implement Pause Protection Feature**
  - Create ribbon button
  - Check pause permission (Monitor/Guide/Prevent)
  - Disable event handlers temporarily
  - Auto-resume after timeout

### Phase 7: Dependent Element Deletion
- [ ] **Implement Delete-with-Dependents Check**
  - Use `Document.GetDependentElements()` API
  - Filter out "always deleted" dependents
  - Show warning dialog with dependent list
  - Allow user to choose delete scope

---

## 🔑 Key command interception patterns to Replicate

1. **Dummy Command IDs**: Use fictional command IDs to track event-based protections in logging system

2. **Extensible Storage**: Store protection metadata (protected pins, protected families) directly in Revit document for persistence

3. **SignalR Coordination**: Use real-time messaging for multi-user awareness (sync conflicts, simultaneous edits)

4. **Nested Condition Integration**: Apply same rule engine to file/family scenarios as element-based rules

5. **Company vs. Project Settings**: Some protections are company-wide (open central file), others are project-specific (family library)

6. **BasicFileInfo Extraction**: Use Revit API to extract file metadata (version, format) without opening file

---

**End of the legacy product Scenario Protections Reference**  
*Generated from the legacy product analysis | February 2026*
