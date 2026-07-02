# the legacy product DocumentChanged Event Scenarios

> **Scope**: Scenarios tracked via `ControlledApplication.DocumentChanged` event  
> **Excluded**: All command binding scenarios (tracked separately)  
> **Location**: [`SoapApplication.DocumentChanged()`](file:///e:/Visual%20Studio/00-LegacyProduct/Soap/Revit/Applications/SoapApplication.cs#L3317)

---

## 📋 Overview

The `DocumentChanged` event is the legacy product's **primary mechanism** for tracking post-transaction changes. It fires after every Revit transaction completes, allowing the legacy product to:
- Monitor specific transaction names
- Track element additions, modifications, and deletions
- Trigger protection workflows based on user actions
- Log analytics for project changes

---

## 🎯 Tracked Scenarios

### 1️⃣ **Toggle Pin Protection Detection**

**Transaction Name**: `"Toggle Pin"`  
**When**: User pins/unpins elements via Revit's built-in pin command

**Detection Logic**:
```csharp
if (e.GetTransactionNames().All(x => string.Equals(x, "Toggle Pin")) 
    && (e.Operation == null || e.Operation == UndoRedoState.TransactionCommitted))
{
    // Collect all modified elements (pinned/unpinned)
    TogglePinExternalEventInfo.Instance.AllModifiedElements = 
        e.GetModifiedElementIds().Select(x => document.GetElement(x));
    
    // Raise external event to check for protected pins
    ToggleExternalEvent.Raise();
}
```

**Purpose**: Detect when user tries to unpin **protected elements** and show intervention dialog.

**Flow**:
```
User clicks "Unpin" on protected element
    ↓
Revit commits "Toggle Pin" transaction
    ↓
DocumentChanged fires
    ↓
the legacy product detects transaction name
    ↓
TogglePinExternalEvent raises
    ↓
Check Extensible Storage for pin protection
    ↓
If protected → Show dialog: "This element is protected. Unpin anyway?"
```

---

### 2️⃣ **Import CAD Pin Protection**

**Transaction Name**: `"Import Vector Data"`  
**When**: User imports DWG/DXF/DGN files

**Detection Logic**:
```csharp
if (interactionSettings.PinProtectionSettings.PromptToPinAndProtectAfterImportingCAD 
    && e.GetTransactionNames().All(x => string.Equals(x, "Import Vector Data"))
    && (e.Operation == null || e.Operation == UndoRedoState.TransactionCommitted))
{
    // Find all imported CAD instances
    IEnumerable<Element> importedCAD = e.GetAddedElementIds()
        .Select(x => document.GetElement(x))
        .Where(x => x is ImportInstance);
    
    if (importedCAD.Any())
    {
        // Prompt user to pin and protect imported CAD
        PinElementsExternalEventInfo.Instance.AllCopyMonitoredElements = importedCAD;
        PinElementsExternalEvent.Raise();
    }
}
```

**Purpose**: **Proactively prompt** user to pin/protect imported CAD files to prevent accidental movement.

**User Experience**:
```
User imports CAD file
    ↓
Import completes
    ↓
the legacy product detects import transaction
    ↓
Shows dialog: "Pin and protect imported CAD? [Yes] [No]"
    ↓
If Yes → Pins element + stores protection in Extensible Storage
```

---

### 3️⃣ **Copy/Monitor Pin Protection**

**Transaction Names**: 
- `"Copy"` (regular copy/paste)
- `"Copy (Copy Monitor link)"` (copy/monitor from linked file)
- `"Finish Mode"` (finish copy/monitor session)

**Detection Logic**:
```csharp
// Stage 1: Track copied elements during Copy transaction
if (e.GetTransactionNames().All(x => string.Equals(x, "Copy") 
    || string.Equals(x, "Copy (Copy Monitor link)")))
{
    // Store copied elements for later
    RevitSessionLocal.Instance.ElementsForPinProtection
        .SetList(projectGuid, copiedElements);
}

// Stage 2: Prompt at end of Copy/Monitor session
if (e.GetTransactionNames().All(x => string.Equals(x, "Finish Mode")))
{
    // Get elements that were copy/monitored
    IEnumerable<Element> monitoredElements = 
        storedElements.Where(x => x.GetMonitoredLinkElementIds().Any());
    
    // Prompt user to pin
    PinElementsExternalEventInfo.Instance.AllCopyMonitoredElements = monitoredElements;
    PinElementsExternalEvent.Raise();
}
```

**Purpose**: Prompt user to **pin copy/monitored elements** to prevent unintentional modifications.

**Multi-Stage Tracking**:
1. **During Copy**: Store element IDs in `ElementsForPinProtection` dictionary
2. **During Finish**: Check if elements are copy/monitored
3. **If monitored**: Prompt user to pin and protect

---

### 4️⃣ **Transfer Project Standards Detection**

**Transaction Name**: `"Transfer Project Standards"`  
**When**: User transfers standards from another Revit file

**Detection Logic**:
```csharp
if (transactions.Contains("Transfer Project Standards", StringComparer.InvariantCultureIgnoreCase)
    && modifiedElementIds.Any(x => document.GetElement(x) is ProjectInfo))
{
    // Project cookie may have changed during transfer
    string correctProjectCookie = 
        FixOrGetProjectCookieAfterTransferProjectStandards(transactions, modifiedElementIds, document);
    
    // Reset project cookie in Extensible Storage
    ResetProjectCookieExternalEvent.Raise();
}
```

**Purpose**: **Fix project registration** after standards transfer (which can corrupt Extensible Storage mappings).

**Why This Matters**: When transferring project standards, Revit sometimes resets the `ProjectInfo` element's UniqueId, which breaks the legacy product's project registration tracking.

---

### 5️⃣ **Element Analytics Tracking**

**When**: **Every transaction** (unless on ignore list)  
**What**: Tracks all Added/Modified/Deleted elements for analytics/reporting

**Detection Logic**:
```csharp
// Create changeset for analytics
ChangesetV2 changeset = GetEventChangest(
    document, 
    projectCookie, 
    e.GetTransactionNames(), 
    e.GetAddedElementIds(), 
    e.GetModifiedElementIds(), 
    e.GetDeletedElementIds()
);

// Store in analytics buffer
AnalyticsStorageHelper.PushToAnalytics(document, changeset);
```

**What's Tracked**:
- **Added Elements**: New walls, doors, families, etc.
- **Modified Elements**: Parameter changes, moved elements, etc.
- **Deleted Elements**: Removed elements (tracked via cached IDs)
- **Transaction Name**: e.g., "Delete", "Move", "Create Wall"
- **Element Properties**: Category, type, family name, parameters

**Use Cases**:
- Project analytics dashboard (show activity heatmap)
- Audit trail (who changed what, when)
- BIM compliance reporting

---

### 6️⃣ **Project Information Tags Sync**

**When**: `ProjectInfo` element is modified  
**What**: Syncs the legacy product custom tags back to backend

**Detection Logic**:
```csharp
if (e.GetModifiedElementIds().Any(x => x == document.ProjectInformation.Id))
{
    // User modified project information
    List<Tag> projectTags = TagManager.Instance.GetProjectInformationTags(document);
    
    // Sync to backend asynchronously
    Task.Run(() => 
        TagManager.Instance.SaveProjectTagValuesAsync(projectGuid, projectTags)
    );
}
```

**Purpose**: Keep **custom project metadata** (stored in Extensible Storage) synced with backend.

**Example Tags**:
- Project phase
- BIM manager name
- Custom client codes
- Project milestones

---

### 7️⃣ **Workset Tracking for Workshared Models**

**When**: Any element Add/Modify/Delete in **workshared document**  
**What**: Tracks which workset each element belongs to

**Detection Logic**:
```csharp
if (document.IsWorkshared)
{
    List<ElementId> addedIds = e.GetAddedElementIds().ToList();
    List<ElementId> modifiedIds = e.GetModifiedElementIds().ToList();
    List<ElementId> deletedIds = e.GetDeletedElementIds().ToList();
    
    // Map elements to their worksets
    RevitSessionLocal.Instance.AddNewElementsWorksetPairs(document, projectGuid, addedIds);
    RevitSessionLocal.Instance.ModifyExistingElementsWorksetPair(document, projectGuid, modifiedIds);
    RevitSessionLocal.Instance.DeleteElementWorksetPairsFromDictionary(projectGuid, deletedIds);
}
```

**Purpose**: Enable **workset-based protection rules** (e.g., "Prevent editing elements on 'Architecture' workset").

**Stored Data**:
```csharp
Dictionary<ElementId, WorksetId> elementWorksetMap;
// Example: { ElementId(123456): WorksetId(10) }
```

---

### 8️⃣ **Command Completion Monitoring**

**When**: **Every DocumentChanged** event  
**What**: Notifies `CommandMonitor` that a transaction completed

**Detection Logic**:
```csharp
public static void DocumentChanged(object sender, DocumentChangedEventArgs e)
{
    // FIRST thing: notify command monitor
    CommandMonitor.Instance.NoticeDocumentChangedEvent(e);
    
    // CommandMonitor checks if active command is waiting for completion
    if (activeCommand.CompletionValidator != null)
    {
        var status = activeCommand.CompletionValidator
            .CheckForCompletionOnDocumentChanged(e, userEventData);
        
        if (status == CompletionStatus.Completed)
        {
            // Capture after-screenshot
            CaptureAfterDataAndLogCompletedCommand(document);
        }
    }
}
```

**Purpose**: Detect when **command has finished** for logging purposes.

**Example**: 
- User clicks "Delete" command
- the legacy product captures before-screenshot
- User selects elements and confirms delete
- Revit commits transaction
- `DocumentChanged` fires
- the legacy product captures after-screenshot
- Both screenshots logged to backend

---

## 📊 Transaction Name Categories

### **Pin/Unpin Transactions**
- `"Toggle Pin"`

### **Import/Link Transactions**
- `"Import Vector Data"` (CAD import)
- `"Copy (Copy Monitor link)"` (copy/monitor)
- `"Finish Mode"` (end copy/monitor session)

### **Transfer Transactions**
- `"Transfer Project Standards"`

### **Standard Revit Transactions** (Tracked for analytics)
- `"Delete"`
- `"Move"`
- `"Copy"`
- `"Create Wall"`
- `"Modify"`
- etc. (100+ standard Revit transactions)

### **Ignored Transactions** (Not tracked)
- `"the legacy product_CloudSync"` (the legacy product's own sync)
- `"the legacy product_FillPatterns"` (the legacy product's pattern management)
- Transactions on `InterceptedTransactionHelper.IgnoreList`

---

## 🔄 Event Flow Diagram

```
┌──────────────────────────────────────────────────────────┐
│  User performs action in Revit                           │
│  (e.g., Pin element, Import CAD, Copy/Monitor)           │
└───────────────┬──────────────────────────────────────────┘
                │
                ▼
┌──────────────────────────────────────────────────────────┐
│  Revit commits transaction                               │
│  Transaction name: "Toggle Pin", "Import Vector Data"    │
└───────────────┬──────────────────────────────────────────┘
                │
                ▼
┌──────────────────────────────────────────────────────────┐
│  ControlledApplication.DocumentChanged event fires       │
│  → SoapApplication.DocumentChanged() called              │
└───────────────┬──────────────────────────────────────────┘
                │
                ▼
┌──────────────────────────────────────────────────────────┐
│  1. Notify CommandMonitor (for command completion)       │
│  2. Check transaction name against scenarios             │
│  3. Extract element IDs (Added/Modified/Deleted)         │
│  4. Trigger appropriate protection workflow              │
└───────────────┬──────────────────────────────────────────┘
                │
                ├─── Toggle Pin? → Check for protected pins
                │
                ├─── Import CAD? → Prompt to pin imported CAD
                │
                ├─── Copy/Monitor? → Track for later pin prompt
                │
                ├─── Transfer Standards? → Fix project cookie
                │
                ├─── Workshared? → Update workset tracking
                │
                └─── Default → Log to analytics
                
```

---

## ⚠️ Key Differences: Event-Based vs. Command-Based

| **Aspect** | **DocumentChanged Event** | **Command Bindings** |
|-----------|-------------------------|---------------------|
| **Trigger** | **After** transaction commits | **Before** command executes |
| **Purpose** | React to completed changes | Intercept/prevent command |
| **Timing** | Post-action monitoring | Pre-action intervention |
| **Use Cases** | Analytics, pin protection detection, sync tracking | Permission checks, rule validation |
| **Example** | Detect "Toggle Pin" transaction → check if protected | Intercept "Delete" command → validate before execution |

---

## ✅ Summary

the legacy product's `DocumentChanged` event tracks **8 main scenarios**:

1. **Toggle Pin Protection** - Detect unpinning of protected elements
2. **Import CAD Pin Protection** - Prompt to pin imported CAD
3. **Copy/Monitor Pin Protection** - Prompt to pin copy/monitored elements  
4. **Transfer Project Standards** - Fix project registration after standards transfer
5. **Element Analytics** - Track all element changes for reporting
6. **Project Information Tags** - Sync custom metadata to backend
7. **Workset Tracking** - Map elements to worksets for protection rules
8. **Command Completion** - Detect when commands finish for logging

**All scenarios** operate on **post-transaction** detection, unlike command bindings which operate **pre-execution**.

---

**End of DocumentChanged Scenarios Documentation**  
*Generated from the legacy product analysis | February 2026*
