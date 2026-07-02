# The legacy product Initialization & Lifecycle - Techno-Functional Report

## Executive Summary

the legacy product implements a **two-phase initialization strategy**: Application-level initialization during Revit startup (`OnStartup`) and Document-level initialization when documents are opened (`DocumentOpened`). This report documents all functions, services, and configurations initialized at each lifecycle stage.

---

## 1. Application Lifecycle Overview

```
Revit Starts
     │
     ▼
OnStartup()          ← Application-level init
     │
     ├─> Ribbon UI Creation
     ├─> Event Registration
     ├─> External Events Setup
     └─> Updaters Registration
     │
     ▼
[Wait for document...]
     │
     ▼
DocumentOpened()     ← Document-level init
     │
     ├─> Project Validation
     ├─> Session Management
     ├─> Settings Loading
     ├─> Command Binding Setup
     └─> Feature Activation
```

---

## 2. Application Startup (OnStartup)

**Method:** `SoapApplication.OnStartup(UIControlledApplication app)`  
**Lifecycle Stage:** Revit Application Initialization  
**Timing:**  Called once when Revit starts, before any documents are opened

### 2.1. Pre-Initialization Checks

```csharp
public Result OnStartup(UIControlledApplication app)
{
    // 1. Check if Revit opened in viewer mode
    if (this.CheckIfRevitOpenedInViewerMode())
        return Result.Failure; // Exit the legacy product if viewer mode
    
    // 2. Create operation tracker for diagnostics
    OperationTracker ot = OperationTracker.NewEntry();
    
    // Continue initialization...
}
```

**Functions:**
- `CheckIfRevitOpenedInViewerMode()` - Checks command line args for `/viewer` flag
- **Purpose:** The legacy product doesn't run in viewer mode (read-only Revit)

---

### 2.2. Core Services Initialization

| Order | Service | Purpose |
|-------|---------|---------|
| 1 | **UI Dispatcher** | `UIUtils.SetUIDispatch()` - Sets UI thread dispatcher for WPF |
| 2 | **SignalR Dispatcher** | `SignalRConnectionManager.UIDispatcher` - Real-time communication thread |
| 3 | **Event Aggregator** | `Prism.Events.EventAggregator` - Inter-component messaging |
| 4 | **Toast Notifications** | `WindowsToastNotificationService` - Windows notification system |
| 5 | **System Events** | `SystemEventsMethods.Instance.SubscribeEvents()` - System-level events |
| 6 | **Revit Controller App** | Store `ControlledApplication` reference |
| 7 | **Session Manager** | `RevitSessionLocal.Instance` - Session tracking |

```csharp
// Core services initialization
UIUtils.SetUIDispatch();
SignalRConnectionManager.UIDispatcher = Dispatcher.CurrentDispatcher;
SoapApplication.EventAggregator = new Soap.Prism.Events.EventAggregator();
SoapApplication.WindowsToastNotificationService = new WindowsToastNotificationService();
SoapApplication.WindowsToastNotificationService.Start();
SystemEventsMethods.Instance.SubscribeEvents();
SoapApplication.RevitControllerApp = app.ControlledApplication;
RevitSessionLocal instance = RevitSessionLocal.Instance;
```

---

### 2.3. Version & License Initialization

```csharp
// Store version information
SoapApplication.RevitVersionInfo = 
    $"{app.ControlledApplication.VersionName} - {app.ControlledApplication.VersionBuild}";
SoapApplication.the legacy productVersionInfo = 
    Assembly.GetExecutingAssembly()?.GetName()?.Version?.ToString();
SoapApplication.RevitVersionName = app.ControlledApplication.VersionName;

// Check for admin token
bool hasAdminToken = LicenseInfo.AdminTokenExists();

// Initialize license if admin token exists
if (hasAdminToken)
    LicenseInfo.Initialize(app.ControlledApplication);

// If authorized, initialize cache & clock calibration
if (SoapApplication.LicenseInfo_IsAuthorized)
{
    if (hasAdminToken)
        SessionLightCache.Instance.InitBasicCacheLoading();
    
    this.InitializeCalibrate(ot); // Background clock sync
}
```

**Key Points:**
- License initialization happens **before** UI creation
- Cache loading starts in background (if authorized)
- Clock calibration runs async (5-second delay)

---

### 2.4. External Events Registration

the legacy product pre-creates all ExternalEvents during startup:

```csharp
// External events for async operations
ExternalEvent projectExternalEvent = SoapApplication.FetchSettingsForOpenProjectExternalEvent;
ExternalEvent projectOnLoginEvent = SoapApplication.AddAdminToProjectOnLoginEvent;
ExternalEvent bindingExternalEvent1 = SoapApplication.ReviseCommandBindingExternalEvent;
ExternalEvent bindingExternalEvent2 = SoapApplication.ReviseCompanyLevelCommandBindingExternalEvent;
ExternalEvent commandExternalEvent = SoapApplication.ReleaseCompanyLevelCommandExternalEvent;
ExternalEvent bindingExternalEvent3 = SoapApplication.ReviseDeleteWithDependentBindingExternalEvent;
ExternalEvent bindingExternalEvent4 = SoapApplication.RibbonSplitButtonCommandBindingExternalEvent;
ExternalEvent commmonExternalEvent = SoapApplication.ProjectCentralCommmonExternalEvent;
ExternalEvent freeExternalEvent = SoapApplication.ActOnceFreeExternalEvent;
ExternalEvent legacy productExternalEvent = SoapApplication.Registerthe legacy productExternalEvent;
ExternalEvent intentApprovedEvent = SoapApplication.SyncIntentApprovedEvent;
ExternalEvent worksharingExternalEvent = SoapApplication.Registerthe legacy productOnEnablingWorksharingExternalEvent;
ExternalEvent bindingExternalEvent5 = SoapApplication.ReviseSyncCommandsBindingExternalEvent;
```

**Purpose:** External events allow background operations to modify Revit safely (from non-Revit threads)

**Key External Events:**
- `ReviseCommandBindingExternalEvent` - Updates command interceptors dynamically
- `Registerthe legacy productExternalEvent` - Registers new projects
- `ProjectCentralCommmonExternalEvent` - Project Central features
- `SyncIntentApprovedEvent` - Sync workflow management

---

### 2.5. Ribbon UI Creation

the legacy product creates 4 ribbon panels:

```csharp
SoapApplication.propertiesPanel = app.CreateRibbonPanel(Resources.Common_Ribbon_PropertiesPanel);
SoapApplication.projectPanel = app.CreateRibbonPanel(Resources.Common_Ribbon_ProjectPanel);
SoapApplication.legacy productPanel = app.CreateRibbonPanel(Resources.Common_Ribbon_the legacy productPanel);
SoapApplication.devToolsPanel = app.CreateRibbonPanel("Console Tools");
```

#### Properties Panel

**Contains:**
- **Project** split button (All Properties, Sync Management, Fill Patterns, Line Styles, etc.)
- **Company** split button (Cloud properties, Company-level standards)

#### Project Panel

**Contains:**
- **Protection** split button (Project Settings, Protected Pins, Protected Families, Mirror Protection, Viewport Tool, Parameter Prompts)
- **Pause the legacy product** toggle button
- **Project Central** toggle button

#### The legacy product Panel

**Contains:**
- **Register Project** button (admin only, hidden initially)
- **Register Family** button (admin only, hidden initially)
- **Messages** split button (View/Send Messages)
- **Settings** pull-down (Projects, OTP, User Overrides, Workset Manager, Mappings, Company Settings, About, Licensing)

#### Dev Tools Panel

**Contains:**
- **SignalR Monitor** (dev mode only)
- **Sync Traffic Logs** (dev mode only)

```csharp
// Example: Project split button setup
SoapApplication.projectRibbonSplitButton = rHelpers.CreateRibbonSplitButtonData(
    Resources.Common_Project,
    "ProjectProperties",
    (ICommand) new RibbonSplitButtonCommandHandler(),
    (object) SoapApplication.projectCommandParameterOptions,
    Resources.Ribbon_Project_Button_Tooltip,
    false);

// Add sub-items
((Collection<RibbonItem>) ((RibbonListButton) SoapApplication.projectRibbonSplitButton).Items)
    .Add((RibbonItem) SoapApplication.allProjectPropertiesRibbonButton);
((Collection<RibbonItem>) ((RibbonListButton) SoapApplication.projectRibbonSplitButton).Items)
    .Add((RibbonItem) SoapApplication.manageSyncRibbonButton);
// etc...
```

#### Availability Classes

Ribbon buttons use custom availability classes for dynamic visibility:

| Availability Class | Condition |
|--------------------|-----------|
| `AvailableOnlyToAdmins` | Company or Project Admin |
| `AvailableOnlyToCompanyAdmins` | Company Admin only |
| `AvailabilityForSendMessage` | License + appropriate project context |
| `AvailabilityAlways` | Always available |
| `AvailableOnlyToAdminsWithActiveFeature` | Admin + feature enabled |

---

### 2.6. Event Handler Registration

**Method:** `RegisterControlledApplicationEvents(UIControlledApplication app)`

All Revit application events are registered during startup:

```csharp
private void RegisterControlledApplicationEvents(UIControlledApplication app)
{
    // Document lifecycle events
    app.ControlledApplication.DocumentOpening += this.DocumentOpening;
    app.ControlledApplication.DocumentCreating += this.DocumentCreating;
    app.ControlledApplication.DocumentChanged += SoapApplication.DocumentChanged;
    app.ControlledApplication.DocumentOpened += this.DocumentOpened;
    app.ControlledApplication.DocumentCreated += this.DocumentCreated;
    app.ControlledApplication.DocumentClosing += this.DocumentClosing;
    app.ControlledApplication.DocumentClosed += this.DocumentClosed;
    
    // Save events
    app.ControlledApplication.DocumentSaving += this.DocumentSaving;
    app.ControlledApplication.DocumentSavingAs += this.DocumentSavingAs;
    app.ControlledApplication.DocumentSavedAs += this.DocumentSavedAs;
    app.ControlledApplication.DocumentSaved += this.DocumentSaved;
    
    // Sync events
    app.ControlledApplication.DocumentSynchronizingWithCentral += this.DocumentSynchronizingWithCentral;
    app.ControlledApplication.DocumentSynchronizedWithCentral += this.DocumentSynchronizedWithCentral;
    
    // Reload events
    app.ControlledApplication.DocumentReloadingLatest += this.DocumentReloadingLatest;
    app.ControlledApplication.DocumentReloadedLatest += this.DocumentReloadedLatest;
    
    // Application events
    app.ControlledApplication.ApplicationInitialized += this.ApplicationInitialized;
    app.ControlledApplication.FailuresProcessing += this.ControlledApplication_FailuresProcessing;
    
    // Family events
    app.ControlledApplication.FamilyLoadingIntoDocument += this.ControlledApplication_FamilyLoadingIntoDocument;
    app.ControlledApplication.FamilyLoadedIntoDocument += this.ControlledApplication_FamilyLoadedIntoDocument;
    
    // UI events
    app.DialogBoxShowing += this.App_DialogBoxShowing;
    app.DisplayingOptionsDialog += this.App_DisplayingOptionsDialog;
    app.ViewActivated += this.App_ViewActivated;
}
```

**Event Categories:**

| Category | Events | Purpose |
|----------|--------|---------|
| **Lifecycle** | Opening, Creating, Opened, Created, Closing, Closed | Track document state |
| **Save** | Saving, SavingAs, Saved, SavedAs | Protect against version downgrades |
| **Sync** | SynchronizingWithCentral, SynchronizedWithCentral | Sync workflow management |
| **Reload** | ReloadingLatest, ReloadedLatest | Disable updaters during reload |
| **Family** | FamilyLoadingIntoDocument, FamilyLoadedIntoDocument | Protect restricted families |
| **UI** | DialogBoxShowing, ViewActivated | Intercept UI interactions |
| **Failures** | FailuresProcessing | Handle Revit warnings/errors |

---

### 2.7. Updater Registration

**Method:** `RegisterUpdaters(UIControlledApplication application)`

the legacy product registers DMU (Dynamic Model Updaters) during startup:

```csharp
private void RegisterUpdaters(UIControlledApplication application)
{
    AddInId activeAddInId = application.ActiveAddInId;
    
    // 1. Viewport Updater
    ViewportUpdater viewportUpdater = new ViewportUpdater(activeAddInId);
    UpdaterRegistry.RegisterUpdater((IUpdater) viewportUpdater);
    UpdaterRegistry.SetIsUpdaterOptional(viewportUpdater.GetUpdaterId(), true);
    ElementClassFilter elementClassFilter = new ElementClassFilter(typeof(Viewport));
    UpdaterRegistry.AddTrigger(viewportUpdater.GetUpdaterId(), 
        (ElementFilter) elementClassFilter, 
        Element.GetChangeTypeElementAddition());
    
    // 2. Add New Element Workset Manager Updater
    AddNewElementWorksetManagerUpdater worksetManagerUpdater1 = 
        new AddNewElementWorksetManagerUpdater(activeAddInId);
    UpdaterRegistry.RegisterUpdater((IUpdater) worksetManagerUpdater1);
    SoapApplication.NewElementUpdaterId = worksetManagerUpdater1.GetUpdaterId();
    UpdaterRegistry.SetIsUpdaterOptional(SoapApplication.NewElementUpdaterId, true);
    
    // 3. Changed Element Workset Manager Updater
    ChangedElementWorksetManagerUpdater worksetManagerUpdater2 = 
        new ChangedElementWorksetManagerUpdater(activeAddInId);
    UpdaterRegistry.RegisterUpdater((IUpdater) worksetManagerUpdater2);
    SoapApplication.ChangedElementUpdaterId = worksetManagerUpdater2.GetUpdaterId();
    UpdaterRegistry.SetIsUpdaterOptional(SoapApplication.ChangedElementUpdaterId, true);
    
    // 4. Pasted Elements Workset Manager Updater
    PastedElementsWorksetManagerUpdater worksetManagerUpdater3 = 
        new PastedElementsWorksetManagerUpdater(activeAddInId);
    UpdaterRegistry.RegisterUpdater((IUpdater) worksetManagerUpdater3);
    SoapApplication.PastedElementUpdaterId = worksetManagerUpdater3.GetUpdaterId();
    UpdaterRegistry.SetIsUpdaterOptional(SoapApplication.PastedElementUpdaterId, true);
    
    // 5. Parameter Prompts Updater
    ParameterPromptsUpdater parameterPromptsUpdater = 
        new ParameterPromptsUpdater(activeAddInId);
    UpdaterRegistry.RegisterUpdater((IUpdater) parameterPromptsUpdater, true);
    ElementIsElementTypeFilter elementTypeFilter = new ElementIsElementTypeFilter(true);
    UpdaterRegistry.AddTrigger(parameterPromptsUpdater.GetUpdaterId(), 
        (ElementFilter) elementTypeFilter, 
        Element.GetChangeTypeElementAddition());
}
```

**Updaters Registered:**

| Updater | Trigger | Purpose |
|---------|---------|---------|
| **ViewportUpdater** | Viewport added | Manage viewport creation |
| **AddNewElementWorksetManagerUpdater** | Element added | Auto-assign worksets to new elements |
| **ChangedElementWorksetManagerUpdater** | Element modified | Track workset changes |
| **PastedElementsWorksetManagerUpdater** | Element pasted | Manage worksets for pasted elements |
| **ParameterPromptsUpdater** | Element type added | Trigger parameter prompts |

**Note:** All updaters are marked as **optional** (`SetIsUpdaterOptional(true)`) - Revit won't fail if they encounter errors

---

### 2.8. Command Interception Setup

**Method:** `CommandIntervention.Instance.Setup(app)`

Command interception framework is initialized but **bindings are NOT registered yet** (happens per-document).

```csharp
// During OnStartup
CommandIntervention.Instance.Setup(app);
```

**What Setup() Does:**
- Stores reference to `UIControlledApplication`
- Initializes singleton instance
- Prepares for later command binding registration

**Important:** Actual command bindings (`Pin`, `Unpin`, `Move`, etc.) are registered on **DocumentOpened**, not during startup.

---

### 2.9. Ribbon UI Interception

```csharp
this.HookEventsToRibbonTabsForIntervention();
```

Hooks into Revit's ribbon tabs to intercept button clicks:

```csharp
private void HookEventsToRibbonTabsForIntervention()
{
    SoapApplication.modifyTab = ((IEnumerable<RibbonTab>) ComponentManager.Ribbon.Tabs)
        .FirstOrDefault<RibbonTab>((Func<RibbonTab, bool>) (x => x.Id == "Modify"));
    
    if (SoapApplication.modifyTab == null)
        return;
    
    // Subscribe to panel collection changes
    ((ObservableCollection<RibbonPanel>) SoapApplication.modifyTab.Panels).CollectionChanged += 
        new NotifyCollectionChangedEventHandler(this.OnPanelsChanged);
    
    // Subscribe to ribbon button clicks
    ComponentManager.UIElementActivated += 
        new EventHandler<UIElementActivatedEventArgs>(this.OnRibbonButtonClicked);
}
```

**Purpose:** Intercept Revit ribbon button clicks for protection rules (e.g., prevent Edit Family)

---

### 2.10. Final Startup Steps

```csharp
// Set button visibility based on license
SoapApplication.SetCommandButtonVisibilityForLicense();

// Check internet connectivity
NetworkUtils.EnsureInternetConnectivity();

return Result.Succeeded;
```

---

## 3. Document Opening (DocumentOpened)

**Method:** `SoapApplication.DocumentOpened(object sender, DocumentOpenedEventArgs e)`  
**Lifecycle Stage:** Document Initialization  
**Timing:** Called every time a document opens (including when Revit starts with a default project)

### 3.1. Pre-Validation Checks

```csharp
private async void DocumentOpened(object sender, DocumentOpenedEventArgs e)
{
    OperationTracker ot = OperationTracker.NewEntry();
    
    // 1. License check
    if (!SoapApplication.LicenseInfo_IsAuthorized)
    {
        DiagnosticLogging.Instance.LogLicenseNotAuthorized("DocumentOpened()", ot);
        return;
    }
    
    // 2. Event status check
    if (((RevitAPIPostEventArgs) e).Status != null)
        return; // Document open failed
    
    // 3. Event validity check
    if (!((RevitAPIEventArgs) e).IsValidObject)
        return;
    
    // 4. Document validity check
    Document document = ((RevitAPIPostDocEventArgs) e).Document;
    if (document == null || !document.IsValidObject)
        return;
    
    // Continue initialization...
}
```

---

### 3.2. Document Validation & Registration

```csharp
// Reset flags
if (SoapApplication.isCentralFileBeingOpnedLocally)
    SoapApplication.isCentralFileBeingOpnedLocally = false;

SoapApplication.isFileBeingOpnedLocallyAfterDownloadingFromBim360 = false;
SoapApplication.isValidForUpgradeProtection = new bool?();

// Check if document should be ignored (temp files, etc.)
SoapApplication.SkipRegistration = this.CheckPathFor_ToIgnoreList(document.PathName);

// Validate document against the legacy product rules
SoapApplication.Validatethe legacy productStateAndVerifyDocument(document);
```

---

### 3.3. Project Session Management

```csharp
// Get or create project GUID (cookie)
string projectGuid = MappingHelper.GetSoapDocumentCookie(document);

if (!string.IsNullOrEmpty(projectGuid))
{
    // Mark command monitor as registered
    CommandMonitor.Instance.IsProjectRegistered = true;
    
    // Add project session
    RevitSessionLocal.Instance.UserSession.AddProjectSession(
        SoapApplication.LicenseInfo.CompanyId, 
        projectGuid);
    
    // Ensure document is in project cache
    this.EnsureDocumentInProjectInfo(document);
    
    // Get project info from cache
    ProjectInfo currentOpenProjectInfo = SessionLightCache.Instance.GetProjectInfo(
        projectGuid, 
        true, 
        "DocumentOpened");
    
    // Acquire file size (if active project)
    if (currentOpenProjectInfo?.IsActive == true && 
        currentOpenProjectInfo.ProjectLastSavedDate.HasValue)
    {
        RevitSessionLocal.Instance.UserSession.AcquireProjectFileSize(
            document, 
            SoapApplication.LicenseInfo.CompanyId, 
            projectGuid);
    }
}
```

**Project Cookie (GUID):**
- Unique identifier for the project
- Stored in Revit file's ProjectInformation extensible storage
- Used to link document to the legacy product's cloud database
- Created on first open if doesn't exist

---

### 3.4. Settings & Configuration Loading

```csharp
// Fetch project settings
ProjectSettings.Perform_GetProjectSettings(currentOpenProjectInfo);

// Fetch project configurations
ProjectSettings.Perform_GetProjectConfig(currentOpenProjectInfo, true);

// Load parameter prompt settings
SoapApplication.LoadParameterPromptSettings(document, projectGuid);

// Get applicable user interaction settings
UserInteractionSettings applicableUserInteractionSettings = 
    SoapCommonUtils.GetApplicableUserInteractionSettings(projectGuid, true);
```

**Settings Loaded:**
- **ProjectSettings** - Project-specific configurations
- **ProjectConfig** - Configuration templates
- **UserInteractionSettings** - Protection mode, prompts, etc.
- **ParameterPromptSettings** - Parameter validation rules

---

### 3.5. Workset Manager Activation

```csharp
// Show active workset view
WorksetManagerHelper.ShowSetActiveWorksetView(document);
```

**Purpose:** Displays workset management UI if worksets exist

---

### 3.6. Cloud Property Auto-Linking

```csharp
// Auto-link identical cloud properties and update synced ones
CloudPropertyHelper.AutoLinkIdenticalAndUpdateSyncedPerSetting(document, projectGuid);
```

**Purpose:** Links identical cloud properties across projects based on settings

---

### 3.7. Background Data Population

```csharp
// Prepare ID dictionaries (async)
RevitSessionLocal.Instance.PrepareIDDictionaryAsync(document, projectGuid, true).ConfigureAwait(false);

// Populate family and type ID dictionary
RevitSessionLocal.Instance.PopulateFamilyAndTypeIdDictionary(document, projectGuid);

// Populate warnings
RevitSessionLocal.Instance.PopulateWarnings(document, projectGuid);
```

**Dictionaries Built:**
- Element ID to Category mappings
- Family name to ID mappings
- Type ID to Family mappings
- Warning tracking

---

### 3.8. Auto Admin Assignment

```csharp
// Ensure admin is added to project (if applicable)
SoapApplication.EnsureAdminToProject(currentOpenProjectInfo, false);
```

**Purpose:** Automatically adds Company Admins to new projects

---

### 3.9. Workshared Session Manager

```csharp
if (document.IsWorkshared && 
    !document.IsDetached && 
    applicableUserInteractionSettings?.EnableSyncManagement == true)
{
    // Start workshared session monitoring
    await ClientSyncManager.Instance.WorksharedSessionStarted(
        ot.Forward(), 
        document, 
        currentOpenProjectInfo).ConfigureAwait(false);
}
```

**ClientSyncManager Functions:**
- Monitors sync activity
- Tracks time to sync
- Manages sync traffic
- Provides sync insights

---

### 3.10. **Command Binding Registration**

**This is triggered asynchronously via External Event:**

```csharp
// Triggered elsewhere (e.g., from ViewActivated event):
SoapApplication.ReviseCommandBindingExternalEvent.CheckAndRaise();
```

**External Event Handler:** `ReviseAllCommandBindingExternalEvent`

```csharp
public void Execute(UIApplication app)
{
    Document document = app.ActiveUIDocument_safe()?.Document;
    if (document == null)
        return;
    
    CommandIntervention.EnteringIntoAnEditMode = false;
    CommandMonitor.Instance.CaptureAfterDataAndLogCompletedCommand(document);
    
    // THIS IS WHERE COMMAND BINDINGS ARE ACTUALLY REGISTERED
    CommandIntervention.Instance.SetupCommandBinding(
        document, 
        ReviseAllCommandBindingExternalEvent.Options.ForceRebinding);
}
```

**SetupCommandBinding Flow:**

```csharp
public void SetupCommandBinding(Document document, bool force = false)
{
    string soapDocumentCookie = MappingHelper.GetSoapDocumentCookie(document);
    
    // Get project settings
    ProjectInfo projectInfo = SessionLightCache.Instance.GetProjectInfo(
        soapDocumentCookie, 
        true, 
        "SetupCommandBinding");
    
    // Unregister existing commands
    UnRegisterExistingCommands();
    
    // Register Pin/Unpin
    SetupPinUnpinCommands(settings);
    
    // Register Move/Mirror/Copy
    SetupTransformCommands(settings);
    
    // Register Delete
    SetupDeleteCommands(settings);
    
    // Register special commands (Duplicate, Rename)
    SetupSpecialCommands(settings);
    
    // Register rule-based commands
    SetupRuleBasedCommands(document, settings);
}
```

**Commands Registered:**
- Pin (32997)
- Unpin (33001)
- Move (33127)
- Mirror - Draw Axis (49000)
- Mirror - Pick Axis (32936)
- Copy (33129)
- Copy to Clipboard (57634)
- Array (33121)
- Delete
- Duplicate ("ID_SYM_CLONE")
- Rename ("ID_PRJBROWSER_RENAME")
- + Rule-based commands from project configuration

---

### 3.11. Pin Protection Reapplication

```csharp
if (applicableUserInteractionSettings?.PinProtectionSettings?.RepinProtectedElementsWhenOpeningProjects == true)
{
    this.RepinProtectedElements(document);
}
```

**Purpose:** Re-pins elements that were protected in previous session

---

### 3.12. Updater Triggers

```csharp
if (document.IsWorkshared)
{
    Global.SUPPRESS_WORKSET_DIALOG_ON_WORKSHARING_ENABLEING = false;
    SoapApplication.ExecuteUpdaterTriggers(document);
}
```

**Purpose:** Activates workset management updaters for workshared documents

---

### 3.13. UI Updates

```csharp
// Update registration button visibility
SoapApplication.EnsureCorrectRegistrationSettingPushButton(document);

// Open Project Central for first project
SoapApplication.OpenProjectCentralForFirstProject(sender);

// Update Project Central if already open
if (ProjectCentralViewModel.IsProjectInsightOpen)
    ProjectCentralViewModel.Instance.ChangeDocumentAsync(SoapApplication.ActiveViewDocument);
```

---

### 3.14. Tag Management

```csharp
// Load all project tags
List<Tag> tagList = await TagManager.Instance.LazyAllProjectTags.Value;

// Get tags from project information
List<Tag> projectInformationTags = TagManager.Instance.GetProjectInformationTags(document);

// Save tag values
if (projectInformationTags != null && projectInformationTags.Any())
{
    await TagManager.Instance.SaveProjectTagValuesAsync(
        projectGuid, 
        projectInformationTags).ConfigureAwait(false);
}
```

---

### 3.15. Broadcast Messages

```csharp
// Show unread message count
this.ShowUnreadMessageCountDialog(currentOpenProjectInfo, ot);
```

**Purpose:** Displays badge with unread broadcast messages for the project

---

##4. Initialization Sequence Diagram

```
Revit                the legacy product             License        SessionCache       Events
  │                     │                    │                │               │
  │ Starts              │                    │                │               │
  ├────────────────────> OnStartup()         │                │               │
  │                     │                    │                │               │
  │                     ├─ Check viewer mode │                │               │
  │                     ├─ Init UI Dispatcher                 │               │
  │                     ├─ Init EventAggregator               │               │
  │                     ├─ Start Toast Service                │               │
  │                     ├────────────────────> LicenseInfo.Initialize()       │
  │                     │                    │                │               │
  │                     │ License OK?        │                │               │
  │                     │<───────────────────┤                │               │
  │                     │                    │                │               │
  │                     ├────────────────────────────────────> InitBasicCacheLoading()
  │                     │                    │                │               │
  │                     ├─ Create Ribbon Panels              │               │
  │                     ├─ Create Ribbon Buttons             │               │
  │                     ├─ Create ExternalEvents             │               │
  │                     │                    │                │               │
  │                     ├────────────────────────────────────────────────────> Register Events
  │                     │                    │                │               │
  │                     ├─ Register Updaters │                │               │
  │                     ├─ Setup CommandIntervention         │               │
  │                     ├─ Hook Ribbon Events│                │               │
  │                     ├─ Set Visibility    │                │               │
  │                     │                    │                │               │
  │<────────────────────┤ Return Success     │                │               │
  │                     │                    │                │               │
  │ [Wait for document...]                   │                │               │
  │                     │                    │                │               │
  │ Opens Document      │                    │                │               │
  ├─────────────────────────────────────────────────────────────────────────> DocumentOpened
  │                     │                    │                │               │
  │                     ├────────────────────> Check License  │               │
  │                     │                    │                │               │
  │                     ├─ Get ProjectCookie │                │               │
  │                     ├─ Validate Document │                │               │
  │                     │                    │                │               │
  │                     ├────────────────────────────────────> AddProjectSession()
  │                     │                    │                │               │
  │                     ├────────────────────────────────────> GetProjectInfo()
  │                     │                    │                │               │
  │                     ├─ Load Settings     │                │               │
  │                     ├─ Load Configurations               │               │
  │                     ├─ Show Workset View │                │               │
  │                     ├─ Auto-link Properties              │               │
  │                     ├─ Populate Dictionaries             │               │
  │                     ├─ Ensure Admin      │                │               │
  │                     │                    │                │               │
  │                     ├─ Start Sync Manager (if workshared) │               │
  │                     ├─ Repin Protected Elements           │               │
  │                     ├─ Execute Updaters  │                │               │
  │                     ├─ Update UI         │                │               │
  │                     ├─ Load Tags         │                │               │
  │                     │                    │                │               │
  │                     │ (Async) Raise ReviseCommandBindingExternalEvent    │
  │                     ├────────────────────────────────────────────────────>│
  │                     │                    │                │               │
  │                     │ ExternalEvent fires                 │               │
  │                     │<───────────────────────────────────────────────────┤
  │                     │                    │                │               │
  │                     ├─ SetupCommandBinding()             │               │
  │                     │   ├─ Unregister old bindings       │               │
  │                     │   ├─ Register Pin/Unpin            │               │
  │                     │   ├─ Register Move/Mirror          │               │
  │                     │   ├─ Register Delete               │               │
  │                     │   ├─ Register Duplicate/Rename     │               │
  │                     │   └─ Register rule-based commands  │               │
  │                     │                    │                │               │
  │<────────────────────┤ Document Ready     │                │               │
```

---

## 5. Summary Tables

### 5.1. OnStartup Initialization Order

| Order | Component | Function | Timing |
|-------|-----------|----------|--------|
| 1 | Viewer Mode Check | `CheckIfRevitOpenedInViewerMode()` | Sync |
| 2 | UI Dispatcher | `UIUtils.SetUIDispatch()` | Sync |
| 3 | SignalR Dispatcher | Set `SignalRConnectionManager.UIDispatcher` | Sync |
| 4 | Event Aggregator | Create `Prism.Events.EventAggregator` | Sync |
| 5 | Toast Service | `WindowsToastNotificationService.Start()` | Sync |
| 6 | System Events | `SystemEventsMethods.Instance.SubscribeEvents()` | Sync |
| 7 | License Init | `LicenseInfo.Initialize()` | Sync |
| 8 | Cache Loading | `SessionLightCache.Instance.InitBasicCacheLoading()` | Async |
| 9 | Clock Calibration | `InitializeCalibrate()` | Async (5s delay) |
| 10 | Ribbon Panels | `app.CreateRibbonPanel()` | Sync |
| 11 | Ribbon Buttons | Create all ribbon UI | Sync |
| 12 | External Events | Create all ExternalEvents | Sync |
| 13 | Event Registration | `RegisterControlledApplicationEvents()` | Sync |
| 14 | Updaters | `RegisterUpdaters()` | Sync |
| 15 | Command Interception | `CommandIntervention.Instance.Setup()` | Sync |
| 16 | Ribbon Hooks | `HookEventsToRibbonTabsForIntervention()` | Sync |
| 17 | Visibility | `SetCommandButtonVisibilityForLicense()` | Sync |
| 18 | Connectivity Check | `NetworkUtils.EnsureInternetConnectivity()` | Async |

---

### 5.2. DocumentOpened Initialization Order

| Order | Component | Function | Timing |
|-------|-----------|----------|--------|
| 1 | License Check | Verify authorization | Sync |
| 2 | Event Validation | Check event status | Sync |
| 3 | Document Validation | Validate document object | Sync |
| 4 | Project Cookie | `MappingHelper.GetSoapDocumentCookie()` | Sync |
| 5 | Session Management | `AddProjectSession()` | Sync |
| 6 | Project Info | `SessionLightCache.Instance.GetProjectInfo()` | Async |
| 7 | Settings Load | `ProjectSettings.Perform_GetProjectSettings()` | Async |
| 8 | Config Load | `ProjectSettings.Perform_GetProjectConfig()` | Async |
| 9 | Workset View | `WorksetManagerHelper.ShowSetActiveWorksetView()` | Sync |
| 10 | Cloud Properties | `CloudPropertyHelper.AutoLinkIdenticalAndUpdateSyncedPerSetting()` | Sync |
| 11 | ID Dictionaries | `PrepareIDDictionaryAsync()` | Async |
| 12 | Family Dictionary | `PopulateFamilyAndTypeIdDictionary()` | Sync |
| 13 | Warnings | `PopulateWarnings()` | Sync |
| 14 | Parameter Prompts | `LoadParameterPromptSettings()` | Sync |
| 15 | Admin Assignment | `EnsureAdminToProject()` | Sync |
| 16 | Sync Manager | `ClientSyncManager.Instance.WorksharedSessionStarted()` | Async |
| 17 | Pin Protection | `RepinProtectedElements()` | Sync |
| 18 | Updaters | `ExecuteUpdaterTriggers()` | Sync |
| 19 | **Command Binding** | `SetupCommandBinding()` via ExternalEvent | **Async** |
| 20 | UI Updates | Update ribbon buttons | Sync |
| 21 | Tags | `TagManager.Instance.SaveProjectTagValuesAsync()` | Async |
| 22 | Messages | `ShowUnreadMessageCountDialog()` | Sync |

---

## 6. Implementation Checklist for CBOX Manage

### Application Startup

- [ ] Implement `CheckIfRevitOpenedInViewerMode()`
- [ ] Initialize UI Dispatcher (`UIUtils.SetUIDispatch()`)
- [ ] Create Event Aggregator for inter-component messaging
- [ ] Initialize Toast notification service
- [ ] Implement license initialization system
- [ ] Create ribbon panels (Properties, Project, the legacy product, DevTools)
- [ ] Create all ribbon buttons with appropriate availability classes
- [ ] Create all ExternalEvent handlers
- [ ] Register all application event handlers
- [ ] Register all DMU updaters (Viewport, Workset, ParameterPrompts)
- [ ] Setup command interception framework (without bindings)
- [ ] Hook ribbon button click events
- [ ] Implement dynamic button visibility based on license/role
- [ ] Add internet connectivity check

### Document Opening

- [ ] Validate license before processing
- [ ] Generate or retrieve project cookie (GUID in extensible storage)
- [ ] Create project session tracking
- [ ] Load project settings from cache/API
- [ ] Load project configurations
- [ ] Initialize workset manager
- [ ] Auto-link cloud properties
- [ ] Build ID dictionaries (async)
- [ ] Populate family/type mappings
- [ ] Load parameter prompt settings
- [ ] Assign admins to projects (if applicable)
- [ ] Start sync manager for workshared files
- [ ] Repin protected elements (if configured)
- [ ] Execute updater triggers
- [ ] **Setup command bindings via external event**
- [ ] Update ribbon UI visibility
- [ ] Load and save project tags
- [ ] Show unread message notifications

### Command Binding (Deferred via ExternalEvent)

- [ ] Create `ReviseAllCommandBindingExternalEvent` handler
- [ ] Implement `SetupCommandBinding()` method
- [ ] Unregister previous bindings before registering new ones
- [ ] Register Pin/Unpin commands
- [ ] Register Move/Mirror/Copy commands
- [ ] Register Delete commands
- [ ] Register Duplicate/Rename override classes
- [ ] Register rule-based commands from configuration
- [ ] Trigger binding setup from appropriate events (ViewActivated, etc.)

---

## 7. Key Differences from Simple Initialization

### Why Two-Phase Initialization?

**Phase 1 (OnStartup):**
- No documents open yet
- Can't create command bindings (no active document)
- Setup infrastructure only

**Phase 2 (DocumentOpened):**
- Document context available
- Can register command bindings
- Load document-specific settings
- Configure per-project features

### Why External Events for Command Binding?

```csharp
// Can't call directly from DocumentOpened because it's async
// Must use ExternalEvent to execute on Revit's main thread
SoapApplication.ReviseCommandBindingExternalEvent.CheckAndRaise();
```

**Reasons:**
1. Command bindings require valid Revit API context
2. DocumentOpened is async - can't guarantee timing
3. ExternalEvents ensure execution on main thread with valid context
4. Allows dynamic rebinding when settings change

---

## 8. Critical Implementation Notes

> [!IMPORTANT]
> **Command bindings are NOT registered during OnStartup**. They are registered asynchronously via ExternalEvent after DocumentOpened completes.

> [!WARNING]
> **All updaters must be marked optional** (`SetIsUpdaterOptional(true)`) to prevent Revit failures if updater encounters errors.

> [!CAUTION]
> **License check is critical** - many operations silently exit if license is not authorized. Always check `SoapApplication.LicenseInfo_IsAuthorized`.

---

## Conclusion

the legacy product's initialization is a **sophisticated, layered process** that carefully manages:

1. **Application-level** - Infrastructure setup (UI, events, updaters)
2. **Document-level** - Project-specific configuration (settings, bindings, features)
3. **Asynchronous operations** - Background loading without blocking Revit

This architecture ensures the legacy product activates smoothly regardless of:
- How Revit starts (with/without documents)
- License state
- Network connectivity
- Document type (workshared/local)

**For CBOX Manage:** Follow this exact pattern to ensure reliable initialization across all scenarios.
