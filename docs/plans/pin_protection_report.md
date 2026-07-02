# The legacy product Pin Protection - Techno-Functional Report

## Executive Summary

the legacy product implements sophisticated pin protection functionality that allows administrators to protect elements from being unpinned by regular users. The system uses Revit's **Extensible Storage** for persistence, **AddInCommandBinding** for command interception, and a **multi-modal intervention system** (Monitor, Guide, Prevent) integrated with RBAC.

---

## 1. System Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                   Pin Protection System                      │
└─────────────────────────────────────────────────────────────┘
                          │
        ┌─────────────────┼─────────────────┐
        │                 │                 │
┌───────▼──────┐  ┌───────▼──────┐  ┌──────▼────────┐
│  Command     │  │   Data       │  │      UI       │
│ Interception │  │  Storage     │  │   Dialogs     │
└──────────────┘  └──────────────┘  └───────────────┘
        │                 │                 │
        │                 │                 │
  Pin/Unpin          Extensible         WPF Views/
  Bindings            Storage           ViewModels
```

### Key Components

| Component | Purpose | Technology |
|-----------|---------|------------|
| **Command Interception** | Intercept Pin/Unpin commands | `AddInCommandBinding` API |
| **Data Storage** | Persist protection metadata | Revit Extensible Storage |
| **UI Layer** | Admin configuration dialogs | WPF MVVM |
| **RBAC Integration** | Role-based access control | Company/Project Admin roles |

---

## 2. Data Model

### 2.1. ProtectedPinInfo Class

**Location:** `Soap.ProtectedPins.ProtectedPinInfo`

The core data model that stores all pin protection metadata:

```csharp
public class ProtectedPinInfo : NotifyPropertyBase
{
    // Element Identification
    public string ElementGuid { get; set; }          // UniqueId of element
    public int ElementId { get; set; }               // ElementId value
    public string Name { get; set; }                 // Display name "Name (Id)"
    
    // Protection Metadata
    public string ProtectedBy { get; set; }          // Username who protected
    public DateTime? DateUTC { get; set; }           // When protected (UTC)
    public string AdminComment { get; set; }         // Plain text comment
    public string AdminCommentRtf { get; set; }      // Rich text comment
    public RTFReference RTFReference { get; set; }   // Rich text formatting
    
    // Protection Configuration
    public int ProtectionMode { get; set; }          // 0=Monitor, 1=Guide, 2=Prevent
    public bool IsRequireCommentForUnpin { get; set; }   // Force user comment
    public bool IsInstantlyNotifyOnUnpin { get; set; }  // Notify admin immediately
    
    // Runtime Properties
    public bool IsPinned { get; set; }               // Current pin state
    public Element RevitElement { get; set; }        // Reference to Revit element
    public bool IsDeleted { get; set; }              // Soft delete flag
    public bool IsDirty { get; set; }                // Change tracking
    
    // Computed Properties
    public InterventionModeType ProtectionModeType => (InterventionModeType)ProtectionMode;
    public DateTime? DateLocalTime { get; }          // Converted to local time
    public string FriendlyDate { get; }              // User-friendly date string
    public string TooltipText { get; }               // "Username, Date"
}
```

### 2.2. Extensible Storage Schema

**Storage Mechanism:** Revit's Extensible Storage (attached to elements)

**Schema Details:**
- **Schema GUID:** Defined in resources as `ProtectedPinInfoSchema_SchemaGuid`
- **Application GUID:** `ProtectedPinInfoSchema_ApplicationGuid`
- **Access Level:** PubliclyVisible (ReadAccessLevel = 1, WriteAccessLevel = 1)
- **Vendor ID:** The legacy product vendor identifier
- **Field Name:** Stored in `SCHEMA_PROTECTEDPININFO_FIELD_NAME`
- **Multi-Tenant:** Uses `CompanyGuid` for tenant isolation

**Key Implementation:**

```csharp
private static Schema CreateProtectedPinInfoSchema()
{
    SchemaBuilder schemaBuilder = new SchemaBuilder(
        new Guid(Resources.ProtectedPinInfoSchema_SchemaGuid));
    
    schemaBuilder.SetApplicationGUID(
        new Guid(Resources.ProtectedPinInfoSchema_ApplicationGuid));
    
    schemaBuilder.SetReadAccessLevel((AccessLevel)1);  // PubliclyVisible
    schemaBuilder.SetWriteAccessLevel((AccessLevel)1);
    schemaBuilder.SetVendorId(Resources.MappingCookieSchema_VendorId);
    
    schemaBuilder.AddSimpleField(
        SCHEMA_PROTECTEDPININFO_FIELD_NAME, 
        typeof(string))
        .SetDocumentation("Protected pins info document identifier");
    
    schemaBuilder.SetSchemaName("ProtectedPinInfoIdentifier");
    
    return schemaBuilder.Finish();
}
```

---

## 3. Command Interception Flow

### 3.1. Pin Command Registration

**Command Binding Setup:**

```csharp
// From CommandIntervention.SetupCommandBinding()
if (interactionSettings != null && (interactionSettings.PromptUserForUnpin ?? false))
{
    CommandBinding_Pin = new CommandBindingIntervention(
        this.App,
        (PostableCommand)32997,  // Pin command enum
        null,
        new InterventionSettings() { IsEnabled = true }
    );
}
else
{
    CommandBinding_Pin = null;
}

// Register with Executed event (post-execution)
CommandBinding_Pin?.Register();
```

**Why Executed (not BeforeExecuted)?**
- Pin command needs to complete first
- The legacy product then shows dialog to configure protection
- Admin can add comments, set protection mode
- This happens AFTER the element is pinned

### 3.2. Unpin Command Registration

**Command Binding Setup:**

```csharp
// Always registered with custom CompletionValidator
InterventionSettings interventionSettings = new InterventionSettings();
interventionSettings.IsEnabled = true;

PostableCommandSettings unpinSettings = new PostableCommandSettings()
{
    CommandCode = ((PostableCommand)33001).ToString(),  // Unpin
    CommandName = "Unpin Elements",
    PostableCommandEnumValue = 33001
};

// Create custom completion validator
unpinSettings.CompletionValidator = CommandCompletionValidator.Create(unpinSettings);

CommandBinding_Unpin = new CommandBindingIntervention(
    this.App,
    (PostableCommand)33001,
    string.Empty,
    unpinSettings,
    interventionSettings
);

// Register with Executed event
CommandBinding_Unpin?.Register();
```

---

## 4. Pin Protection Workflow

### 4.1. Protecting Elements (Pin Flow)

**Sequence Diagram:**

```
User               Revit           the legacy product         Storage          UI
 │                   │                │                │             │
 │ Selects elements  │                │                │             │
 │ Clicks Pin button │                │                │             │
 ├──────────────────>│                │                │             │
 │                   │ Pin executed   │                │             │
 │                   │ (native Revit) │                │             │
 │                   ├───────────────>│                │             │
 │                   │                │ Executed event │             │
 │                   │                │ triggered      │             │
 │                   │                │                │             │
 │                   │                │ ProcessPinCommand()          │
 │                   │                ├────────────────┤             │
 │                   │                │                │ Show dialog │
 │                   │                ├───────────────────────────> │
 │                   │                │                │             │
 │<─────────────────────────── Admin enters config ───────────────> │
 │                   │                │ (comments,     │             │
 │                   │                │  mode, notify) │             │
 │                   │                │                │             │
 │                   │                │ User confirms  │             │
 │                   │                │<───────────────────────────┤
 │                   │                │                │             │
 │                   │                │ SetProtectedPinInfoForDocument()
 │                   │                ├───────────────>│             │
 │                   │                │  Store metadata│             │
 │                   │                │  in ExtStorage │             │
 │                   │<───────────────┤                │             │
 │                   │  Transaction   │                │             │
 │                   │    committed   │                │             │
 │<──────────────────┤                │                │             │
 │  Elements pinned  │                │                │             │
 │  & protected      │                │                │             │
```

**Code Flow:**

```csharp
// 1. Command executed, event fired
public bool ProcessPinCommand(Document document, IEnumerable<Element> selectedElements)
{
    return this.Showthe legacy productElementPinProtection(document, selectedElements);
}

// 2. Show dialog
private bool Showthe legacy productElementPinProtection(
    Document document,
    IEnumerable<Element> selectedElementsInDocument)
{
    if (!the legacy productPausedFeatureFlags.IsMessagesAndPromptsActive)
        return true;
    
    bool flag = false;
    
    if (selectedElements.Count() > 0)
    {
        // Create and show dialog
        the legacy productElementPinProtectionView pinProtectionView = 
            new the legacy productElementPinProtectionView();
        the legacy productElementPinProtectionViewModel protectionViewModel = 
            new the legacy productElementPinProtectionViewModel(document, selectedElements);
        
        pinProtectionView.DataContext = protectionViewModel;
        protectionViewModel.AssociatedWindow = pinProtectionView;
        pinProtectionView.ShowDialog();
        
        TaskDialogResult result = protectionViewModel.Result;
        
        // 3. Handle dialog result
        if (result == TaskDialogResult.CommandLink1) // Yes, protect
        {
            // Save protection settings
            PinExtensibleStorageHelper.SetProtectedPinInfoForDocument(
                document,
                protectionViewModel.AdminPreferencesForUnpin,
                selectedElements);
            
            flag = true; // Allow pin
        }
        else if (result == TaskDialogResult.CommandLink2) // Clear protection
        {
            if (IsAdmin)
            {
                // Remove protection metadata
                PinExtensibleStorageHelper.ClearProtectedPinInfoFromDocument(
                    document, 
                    selectedElements);
            }
            flag = true; // Allow pin
        }
        else if (result == TaskDialogResult.Cancel)
        {
            flag = false; // Cancel operation
        }
    }
    
    return flag;
}

// 4. Store protection metadata
public static void SetProtectedPinInfoForDocument(
    Document document,
    ProtectedPinInfo adminPreferencesForUnpin,
    IEnumerable<Element> selectedElements)
{
    using (Transaction transaction = new Transaction(document))
    {
        transaction.Start(Resources.the legacy product_Transaction_PinProtection);
        
        Schema schema = CreateProtectedPinInfoSchema();
        
        foreach (Element selectedElement in selectedElements)
        {
            // Special handling for certain element types
            switch (selectedElement)
            {
                case View _:
                case ViewSchedule _:
                case FamilySymbol _:
                case GroupType _:
                    selectedElement.Pinned = true; // Explicitly pin
                    break;
            }
            
            // Store protection metadata in extensible storage
            SetProtectedPinInfoObject(selectedElement, schema, adminPreferencesForUnpin);
        }
        
        transaction.Commit();
    }
}
```

### 4.2. Unpinning Elements (Unpin Flow)

**Sequence Diagram:**

```
User              Revit           the legacy product         Storage          UI
 │                  │                │                │             │
 │ Selects elements │                │                │             │
 │ Clicks Unpin     │                │                │             │
 ├─────────────────>│                │                │             │
 │                  │ Unpin executed │                │             │
 │                  ├───────────────>│                │             │
 │                  │                │ Executed event │             │
 │                  │                │                │             │
 │                  │                │ ProcessUnpinCommand()        │
 │                  │                ├────────────────┤             │
 │                  │                │                │             │
 │                  │                │ Check storage  │             │
 │                  │                ├───────────────>│             │
 │                  │                │ Get protection │             │
 │                  │                │ info           │             │
 │                  │                │<───────────────┤             │
 │                  │                │                │             │
 │                  │                │ IF no protection: Allow unpin│
 │                  │                │ IF protected:  │             │
 │                  │                │   Check mode   │             │
 │                  │                │                │             │
 │                  │                │ Mode=Monitor:  │             │
 │                  │                │   Log & allow  │             │
 │                  │                │                │             │
 │                  │                │ Mode=Guide/Prevent:          │
 │                  │                ├───────────────────────────> │
 │                  │                │                │  Show dialog│
 │<─────────────────────────── User provides comment ────────────> │
 │                  │                │                │    (if req) │
 │                  │                │ User confirms  │             │
 │                  │                │<───────────────────────────┤
 │                  │                │                │             │
 │                  │                │ Mode=Prevent & !Admin:       │
 │                  │                │   Block unpin  │             │
 │                  │                │                │             │
 │                  │                │ Mode=Guide OR Admin:         │
 │                  │                │   Remove metadata            │
 │                  │                ├───────────────>│             │
 │                  │                │   Allow unpin  │             │
 │                  │                │                │             │
 │                  │                │ Send notification            │
 │                  │                │ to admin       │             │
 │                  │<───────────────┤                │             │
 │                  │  Transaction   │                │             │
 │<─────────────────┤    committed   │                │             │
 │  Elements unpinned                │                │             │
```

**Code Flow:**

```csharp
// 1. Unpin command executed
public bool ProcessUnpinCommand(
    Document document,
    IEnumerable<Element> selectedElements,
    InterventionSettings interventionSettings,
    PostableCommand postableCommand,
    PostableCommandSettings postableCommandSettings,
    bool hasProtectedElements,
    bool isforToggle = false)
{
    bool flag = this.Showthe legacy productInterventionForUnpinProtection(
        document,
        selectedElements,
        interventionSettings,
        postableCommand,
        postableCommandSettings,
        hasProtectedElements,
        isforToggle);
    
    // Special handling for certain element types
    if (flag && selectedElements != null && selectedElements.Any(x =>
        x is View || x is ViewSchedule || x is FamilySymbol || x is GroupType))
    {
        flag = false; // Prevent unpin of these types
    }
    
    return flag;
}

// 2. Show intervention dialog
private bool Showthe legacy productInterventionForUnpinProtection(
    Document document,
    IEnumerable<Element> selectedElements,
    InterventionSettings interventionSettings,
    PostableCommand postableCommand,
    PostableCommandSettings postableCommandSettings,
    bool hasProtectedElements,
    bool isforToggle)
{
    bool flag = false;
    
    // Get protected elements from selection
    List<ProtectedPinInfo> protectedElements = selectedElements
        .Select(x => PinExtensibleStorageHelper.GetProtectedPinInfoObject(x))
        .Where(x => x != null)
        .ToList();
    
    if (protectedElements.Count > 0)
    {
        // Check if prompting is enabled
        if (settings?.PromptUserForUnpin ?? false)
        {
            // Get protection mode
            InterventionModeType mode = GetInterventionMode(
                postableCommandSettings.GetEffectiveModeType(documentCookie));
            
            if (mode == InterventionModeType.Monitor)
            {
                // MONITOR MODE: Log but allow
                CommandMonitor.Instance.UpdateIntermediateUserEventInfo(
                    /* log details */);
                flag = true; // Allow unpin
                
                if (hasProtectedElements && !isforToggle)
                    CommandMonitor.Instance.WatchFor(document, postableCommandSettings);
            }
            else
            {
                // GUIDE or PREVENT MODE: Show dialog
                the legacy productInterventionView interventionView = 
                    new the legacy productInterventionView(null);
                the legacy productInterventionViewModel interventionViewModel = 
                    the legacy productInterventionViewModel.Create(
                        document,
                        interventionSettings,
                        postableCommand,
                        protectedElements,
                        postableCommandSettings);
                
                interventionView.DataContext = interventionViewModel;
                interventionViewModel.AssociatedWindow = interventionView;
                
                bool? dialogResult = interventionView.ShowDialog();
                flag = dialogResult ?? false;
                
                if (hasProtectedElements && flag && !isforToggle)
                    CommandMonitor.Instance.WatchFor(document, postableCommandSettings);
                
                if (dialogResult ?? false)
                {
                    // User approved: Remove protection metadata
                    RemoveSelectedProtectedUnpinElementsFromStorage(
                        document, 
                        protectedElements);
                }
            }
        }
        else
        {
            // Prompting disabled: Remove metadata and allow
            RemoveSelectedProtectedUnpinElementsFromStorage(document, protectedElements);
            flag = true;
        }
    }
    else if (selectedElements != null && selectedElements.Count() > 0)
    {
        // No protected elements: Allow unpin
        flag = true;
    }
    
    return flag;
}

// 3. Remove protection metadata
private void RemoveSelectedProtectedUnpinElementsFromStorage(
    Document document,
    List<ProtectedPinInfo> selectedRestrictedUnpinElements)
{
    if (document == null || selectedRestrictedUnpinElements == null)
        return;
    
    using (Transaction transaction = new Transaction(document))
    {
        transaction.Start(Resources.the legacy product_Transaction_PinProtection);
        
        foreach (ProtectedPinInfo protectedInfo in selectedRestrictedUnpinElements)
        {
            Element element = protectedInfo.RevitElement;
            if (element != null)
            {
                PinExtensibleStorageHelper.ClearProtectedPinInfoFromDocument(
                    document, 
                    element);
            }
        }
        
        transaction.Commit();
    }
}
```

---

## 5. Protection Modes (Intervention System)

the legacy product implements three protection modes:

### 5.1. Monitor Mode (Mode = 0)

**Behavior:**
- Logs unpin action
- Sends notification to admin (if configured)
- **ALLOWS** unpin to proceed
- No user prompt shown

**Use Case:** Audit trail without blocking users

### 5.2. Guide Mode (Mode = 1)

**Behavior:**
- Shows dialog with admin's comment
- Requests user justification (if configured)
- **ALLOWS** unpin after user acknowledgment
- Logs action and sends notification

**Use Case:** Educate users but allow exceptions

### 5.3. Prevent Mode (Mode = 2)

**Behavior:**
- Shows dialog with strong warning
- **BLOCKS** unpin for regular users
- **ALLOWS** unpin for admins only
- Requires admin override

**Use Case:** Hard protection for critical elements

---

## 6. RBAC Integration

### 6.1. Role Hierarchy

```
Company Administrator
    │
    ├─ Can protect/unprotect any elements
    ├─ Can configure protection settings
    └─ Can override any protection

Project Administrator
    │
    ├─ Can protect elements in their projects
    ├─ Can unprotect elements they protected
    └─ Can configure project-level settings

Regular User
    │
    ├─ Can pin elements (if protection enabled)
    ├─ Cannot unpin protected elements (Prevent mode)
    └─ Must justify unpinning (Guide mode)
```

### 6.2. Permission Checks

```csharp
// Check if user is admin
bool isAdmin = RevitSessionLocal.Instance.HasAdminPriviledgesForThisProject(
    MappingHelper.GetSoapDocumentCookie(document));

// Different behavior based on role
if (isAdmin)
{
    // Admin can always clear protection
    PinExtensibleStorageHelper.ClearProtectedPinInfoFromDocument(document, elements);
}
else
{
    // Regular user follows protection mode
    if (protectionMode == InterventionModeType.Prevent)
    {
        // Block unpin
        return false;
    }
}
```

---

## 7. Storage Operations

### 7.1. Create/Update Protection

```csharp
// Store protection info
PinExtensibleStorageHelper.SetProtectedPinInfoForDocument(
    document,
    adminPreferences,  // ProtectedPinInfo with config
    selectedElements);

// What gets stored:
// - ProtectedBy: Current username
// - DateUTC: Current timestamp
// - AdminComment: Admin's message
// - ProtectionMode: 0/1/2
// - IsRequireCommentForUnpin: bool
// - IsInstantlyNotifyOnUnpin: bool
```

### 7.2. Read Protection

```csharp
// Check if element is protected
bool isProtected = PinExtensibleStorageHelper.IsElementProtected(element);

// Get protection details
ProtectedPinInfo info = PinExtensibleStorageHelper.GetProtectedPinInfoObject(element);

// Get all protected elements in document
List<ProtectedPinInfo> allProtected = 
    PinExtensibleStorageHelper.GetProtectedPinInfoFromDocument(document);
```

### 7.3. Delete Protection

```csharp
// Remove protection from single element
PinExtensibleStorageHelper.ClearProtectedPinInfoFromDocument(document, element);

// Remove protection from multiple elements
PinExtensibleStorageHelper.ClearProtectedPinInfoFromDocument(document, elements);
```

---

## 8. Special Element Type Handling

the legacy product handles certain element types specially:

```csharp
switch (selectedElement)
{
    case View _:
    case ViewSchedule _:
    case FamilySymbol _:
    case GroupType _:
        selectedElement.Pinned = true; // Explicitly set pinned state
        break;
}
```

**Why?**
- These element types may not respond to normal pin operations
- Direct property manipulation ensures consistent state
- Required for Views, Schedules, Family Symbols, Group Types

---

## 9. Multi-Tenant Isolation

Pin protection data is isolated per company:

```csharp
private static string CompanyGuid => SoapApplication.LicenseInfo?.CompanyId;

// All storage operations include company context
MultiCompanyExtensibleStorageHelper<ProtectedPinInfo>.Set(
    CompanyGuid,  // Tenant identifier
    protectedPinInfoObject,
    selectedElement,
    schema,
    fieldName,
    out field,
    out entity);
```

**Isolation Guarantees:**
- Company A cannot see Company B's protections
- Each company's data stored separately in extensible storage
- Shared schema but data partitioned by CompanyGuid

---

## 10. UI Components

### 10.1. Pin Protection Dialog

**ViewModel:** `The legacy productElementPinProtectionViewModel`  
**View:** `The legacy productElementPinProtectionView`

**Features:**
- Set protection mode (Monitor/Guide/Prevent)
- Add admin comment (plain or RTF)
- Configure notification settings
- Require user comment on unpin

### 10.2. Unpin Intervention Dialog

**ViewModel:** `The legacy productInterventionViewModel`  
**View:** `The legacy productInterventionView`

**Features:**
- Display admin's protection message
- Show who protected and when
- Request user justification
- Admin override capability

### 10.3. Protected Elements Manager

**ViewModel:** `ProtectedPinnedElementsViewModel`  
**View:** `ProtectedPinnedElementsView`

**Features:**
- List all protected elements in document
- Filter by protection mode
- Bulk unprotect (admin only)
- View protection history

---

## 11. Configuration Settings

### 11.1. Project-Level Settings

```csharp
public class UserInteractionSettings
{
    // Enable/disable pin protection feature
    public bool? PromptUserForUnpin { get; set; }
    
    // Default protection mode for new protections
    public int? DefaultProtectionMode { get; set; }
    
    // Allow regular users to pin elements
    public bool? IsPinProtectionAllowedForOtherUsers { get; set; }
}
```

### 11.2. Feature Flags

```csharp
// Global feature flag
if (!the legacy productPausedFeatureFlags.IsMessagesAndPromptsActive)
    return true; // Bypass protection system
```

---

## 12. Event Flow Summary

```
PIN WORKFLOW:
User Pins → Revit Executes Pin → the legacy product Executed Event →
→ Show Dialog → Admin Configures → Save to ExtStorage → Done

UNPIN WORKFLOW:
User Unpins → Revit Executes Unpin → the legacy product Executed Event →
→ Check ExtStorage → If Protected: Evaluate Mode →
→ Monitor: Log & Allow
→ Guide: Show Dialog → User Justifies → Allow
→ Prevent: Block (unless Admin) →
→ Remove ExtStorage → Done
```

---

## 13. Key Design Decisions

### 13.1. Why Executed (not BeforeExecuted)?

**Pin:**
- Element must be pinned first
- Then admin adds protection metadata
- Two-step process: Revit pins, the legacy product protects

**Unpin:**
- Element attempts to unpin
- The legacy product checks protection
- Can still block by re-pinning in transaction
- CompletionValidator handles final state

### 13.2. Why Extensible Storage?

- Persistent across sessions
- Survives sync to central
- Company-specific partitioning
- Queryable with ExtensibleStorageFilter
- No database dependency

### 13.3. Why Three Modes?

- **Monitor:** Audit without friction
- **Guide:** Educate while allowing
- **Prevent:** Hard block for critical elements
- Flexibility for different protection levels

---

## 14. Implementation Checklist for CBOX Manage

To replicate the legacy product's pin protection:

- [ ] Create `ProtectedPinInfo` data model
- [ ] Implement Extensible Storage helper class
- [ ] Set up Pin command binding (Executed event)
- [ ] Set up Unpin command binding (Executed event)
- [ ] Create pin protection UI dialog
- [ ] Create unpin intervention UI dialog
- [ ] Implement three protection modes
- [ ] Add RBAC checks for admin override
- [ ] Implement multi-tenant isolation
- [ ] Add special element type handling
- [ ] Create protected elements manager view
- [ ] Integrate with project settings
- [ ] Add logging and notifications

---

## 15. Conclusion

the legacy product's pin protection is a **comprehensive, enterprise-grade system** that:

✅ Uses Revit's native capabilities (Extensible Storage, AddInCommandBinding)  
✅ Provides flexible protection modes (Monitor/Guide/Prevent)  
✅ Integrates with RBAC for admin overrides  
✅ Maintains audit trails and notifications  
✅ Ensures multi-tenant data isolation  
✅ Handles edge cases (Views, Schedules, etc.)

The architecture is **proven in production** and serves as an excellent reference for implementing similar functionality in CBOX Manage.
