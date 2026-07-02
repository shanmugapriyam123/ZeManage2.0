# The legacy product — CAD Import & Linked File Protections

> **Source**: `CommandIntervention.cs` · `SoapApplication.cs` · `PinProtectionSettings.cs`  
> **Scope**: All protections related to importing, linking, exploding, and binding CAD/Revit files.

---

## Overview — Four Protection Layers

| # | Protection | Mechanism | Trigger |
|---|---|---|---|
| 1 | **CAD Explode** | Ribbon button swap + `AddInCommandBinding` | User clicks Full/Partial Explode |
| 2 | **Bind Link** | Ribbon button swap | User clicks Bind Link on a Revit link |
| 3 | **Pin After CAD Import** | `DocumentChanged` event | `"Import Vector Data"` transaction committed |
| 4 | **Pin After Copy/Monitor** | `DocumentChanged` event | `"Copy"` → `"Finish Mode"` transaction sequence |

All four share the same **intervention model**: show a configurable dialog → user decides → log result. The protection level (Monitor / Guide / Prevent) is configured from the backend per project.

---

## Protection 1 — CAD Explode

### Commands Protected

| Internal Code | Revit Command | What it Does |
|---|---|---|
| `ID_IMPORT_INSTANCE_EXPLODE` | Full Explode | Destroys the import instance → raw Revit lines/curves/text |
| `ID_IMPORT_INST_PARTIAL_EXPLODE` | Partial Explode | Breaks import one level into nested import symbols |

Both are bound to the same `PostableCommandSettings` object, keyed internally as `legacy product_ImportFullExplode`.

---

### Layer A — Ribbon Button Swap

Revit shows the Explode split button only when an `ImportInstance` is selected. The legacy product intercepts this via `OnPanelsChanged()`, a handler on Revit's ribbon collection `NotifyCollectionChanged` event.

```
NotifyCollectionChangedAction.Add fires (panel becomes visible)
    ↓
FindPanel("Dialog_Essentials_ImportInstanceExplode", ...)
    ↓
Capture reference: explodedSplitButton_Revit
    ↓
Create the legacy product's own split button + two child buttons:
    ├── legacy productCustomButtonId_FullExplode    ("Full Explode" lookalike)
    └── legacy productCustomButtonId_PartialExplode ("Explode" lookalike)
    ↓
SetButtonVisibility(explodedSplitButton_Revit, false)   // hide native
SetButtonVisibility(explodedSplitButton_the legacy product, true)  // show the legacy product's
```

**When the user clicks the legacy product's button** (`OnItemExecuted`):

```csharp
if (e.Item.Id == legacy productCustomButtonId_FullExplode
    || legacy productCustomButtonId_PartialExplode
    || legacy productCustomButtonId_ExplodeSplitButton)
{
    CommandMonitor.CaptureBeforeData(document, command); // screenshot before

    bool allowed = Showthe legacy productInterventionView(document, commandSettings, buttonId);

    if (allowed)
    {
        // Temporarily expose real button so Revit's command executes
        explodedSplitButton_the legacy product.Visible = false;
        explodedSplitButton_Revit.Visible = true;
        CommandMonitor.WatchFor(document, commandSettings); // start watching
    }
    else
    {
        CommandMonitor.LogCancelledCommand(commandSettings); // log blocked
        // The legacy product button remains; native button stays hidden
    }
}
```

> **Key design**: The legacy product's button looks identical to Revit's. When access is granted, the legacy product momentarily swaps the buttons back so Revit's native explode command fires through normally. When blocked, the native button is never shown — the user cannot bypass it.

---

### Layer B — Right-Click Context Menu Interception

The ribbon swap does not cover Revit's right-click context menu. The legacy product separately registers `AddInCommandBinding` for both command IDs using `RegisterWithBeforeExecute()`.

**During `SetupCommandBinding()`** (called on project load and role change):

```csharp
// Both bindings reuse the same PostableCommandSettings (legacy product_ImportFullExplode)
PostableCommandSettings masterSetting = DeepClone(source3);

masterSetting.CommandCode = "ID_IMPORT_INSTANCE_EXPLODE";
CommandBinding_FullExplode_ContextMenu =
    new CommandBindingIntervention(App, "ID_IMPORT_INSTANCE_EXPLODE", masterSetting);
CommandBinding_FullExplode_ContextMenu.RegisterWithBeforeExecute();

masterSetting.CommandCode = "ID_IMPORT_INST_PARTIAL_EXPLODE";
CommandBinding_PartialExplode_ContextMenu =
    new CommandBindingIntervention(App, "ID_IMPORT_INST_PARTIAL_EXPLODE", masterSetting);
CommandBinding_PartialExplode_ContextMenu.RegisterWithBeforeExecute();
```

**In the shared `BeforeExecuted` handler**:

```csharp
else if (commandId == "ID_IMPORT_INSTANCE_EXPLODE"
      || commandId == "ID_IMPORT_INST_PARTIAL_EXPLODE")
{
    bool allowed = Showthe legacy productInterventionView(
        document, MasterPostableCommandSetting, commandId);

    if (allowed)
        CommandMonitor.WatchFor(document, MasterPostableCommandSetting);
    else
    {
        CommandMonitor.LogCancelledCommand(MasterPostableCommandSetting);
        if (e.Cancellable)
            e.Cancel = true;  // block the command
    }
}
```

---

### Activation Conditions

```csharp
// Explode protection is active only when ALL of:
the legacy productPausedFeatureFlags.IsMessagesAndPromptsActive   // not paused
&& postableCommandSettings != null                       // configured in backend
&& !postableCommandSettings.AllowTemporarily             // not temporarily bypassed
```

When the legacy product is paused or the feature is disabled:
- `AllowTemporarily` is set to `false` on the settings object
- Both `CommandBinding_FullExplode_ContextMenu` and `CommandBinding_PartialExplode_ContextMenu` are **unregistered** (`Unregister()`)
- The legacy product's ribbon buttons are hidden; Revit's native buttons are restored

---

### Configuration Setting

| Setting Key | Location | Effect |
|---|---|---|
| `legacy product_ImportFullExplode` | `UserInteractionSettings.PostableCommandSettings` | Configures both Full and Partial Explode protection |
| `AllowTemporarily` | On the `PostableCommandSettings` object | Bypasses protection temporarily (e.g., after admin override) |

---

## Protection 2 — Bind Link

Bind Link converts a **Revit linked model** into a group. This is a destructive operation that removes the link relationship.

### Commands Protected

| Internal Code | Revit Command ID |
|---|---|
| `legacy product_BindLinktButton` | `Dialog_Essentials_RvtLinkInstanceStdDbar:Control_Essentials_BindLinkAsGroup` |

> Note: `"legacy product_BindLinktButton"` contains a deliberate typo in the original source.

---

### Ribbon Button Swap

Same pattern as Explode. Triggered when Revit's link properties panel appears:

```
Panel "Dialog_Essentials_RvtLinkInstanceStdDbar" added to ribbon
    ↓
Capture: bindLinkButton_Revit ("Control_Essentials_BindLinkAsGroup")
    ↓
Create: bindLinkButton_the legacy product ("legacy product_BindLinktButton" lookalike)
    ↓
Insert the legacy product button at position 0
RemoveFromPanel(bindLinkButton_Revit)   // physically remove native
SetButtonVisibility(bindLinkButton_the legacy product, true)
```

> **Bind Link uses a different technique than Explode**: Instead of hiding the native button, the legacy product **physically removes it from the panel's Items collection** and re-inserts it when access is granted.

```csharp
private void SetVisibilityOfBindLinkButton(bool showthe legacy productButton, RibbonItem revitButton)
{
    if (showthe legacy productButton)
    {
        // Remove native button from panel entirely
        if (bindLinkPanel.Source.Items.Contains(revitButton))
            bindLinkPanel.Source.Items.Remove(revitButton);
    }
    else
    {
        // Restore native button at position 0
        if (!bindLinkPanel.Source.Items.Contains(revitButton))
            bindLinkPanel.Source.Items.Insert(0, revitButton);
    }
    bindLinkButton_Revit.Visible = !showthe legacy productButton;
    bindLinkButton_the legacy product.Visible = showthe legacy productButton;
}
```

**When user clicks the legacy product's Bind Link button** (`OnItemExecuted`):

```csharp
CommandMonitor.CaptureBeforeData(document, command);

bool allowed = Showthe legacy productInterventionView(document, commandSettings, buttonIdBindLink);

if (allowed)
{
    SetVisibilityOfBindLinkButton(false, bindLinkButton_Revit); // restore native
    CommandMonitor.WatchFor(document, commandSettings);
}
else
    CommandMonitor.LogCancelledCommand(commandSettings);
```

---

### Activation Conditions

```csharp
RevitSessionLocal.Instance.IsDocumentRegisteredAndActive(projectGuid)
&& RevitSessionLocal.Instance.FetchWorkspaceInfo() != null
&& postableCommandSettings != null
&& !postableCommandSettings.AllowTemporarily
&& IsMessagesAndPromptsActive
// Admins bypass if EnableUserExperienceForAdmins = false
```

---

## Protection 3 — Pin After CAD Import

### What It Does

After a user imports a DWG/DXF/DGN file, the legacy product **proactively prompts them to pin and protect** the imported CAD instance. This prevents accidental movement or deletion.

### How It Works — `DocumentChanged` Event

```
Transaction "Import Vector Data" commits
    ↓
DocumentChanged fires → SoapApplication.DocumentChanged()
    ↓
Check conditions:
    ✓ PromptToPinAndProtectAfterImportingCAD = true
    ✓ IsMessagesAndPromptsActive = true
    ✓ All transaction names == "Import Vector Data"
    ✓ Operation == TransactionCommitted (or null)
    ↓
Filter added elements: x => x is ImportInstance
    ↓
If any ImportInstance found:
    PinElementsExternalEventInfo.Instance.AllCopyMonitoredElements = importedCAD
    PinElementsExternalEventInfo.Instance.Document = document
    PinElementsExternalEvent.Raise()
    ↓
PinElementsExternalEvent handler runs on Revit's main thread
    ↓
Show dialog: "Pin and protect this imported CAD? [Yes] [No]"
    ↓
If Yes → Pins element + stores protection in Extensible Storage
```

### Key Code

```csharp
// Inside DocumentChanged, after checking conditions:
IEnumerable<Element> importedCAD = e.GetAddedElementIds()
    .Select(x => document.GetElement(x))
    .Where(x => x is ImportInstance);

if (importedCAD.Any())
{
    PinElementsExternalEventInfo.Instance.AllCopyMonitoredElements = importedCAD.ToList();
    PinElementsExternalEventInfo.Instance.Document = e.GetDocument();
    if (!PinElementsExternalEvent.IsPending)
        PinElementsExternalEvent.Raise();
}
```

### Configuration Setting

| Setting | Model Property |
|---|---|
| Enable | `PinProtectionSettings.PromptToPinAndProtectAfterImportingCAD` |
| Parent guard | `UserInteractionSettings.PromptUserForUnpin` (must also be true) |

---

## Protection 4 — Pin After Copy/Monitor

### What It Does

When a user Copy/Monitors elements from a linked file, the legacy product tracks the copied elements and, when the Copy/Monitor session ends ("Finish Mode"), prompts to pin those that are being monitored.

### Multi-Stage Flow via `DocumentChanged`

```
Stage 1 — Copy Transaction:
    ─────────────────────────────────────────────────────
    Transaction names: "Copy" OR "Copy (Copy Monitor link)"
    Operation: TransactionCommitted
        ↓
    Collect all added elements
    Filter: if (PromptToPinAndProtectAfterImportingCAD)
        → also pick up any ImportInstances copied
    Store IDs: RevitSessionLocal.ElementsForPinProtection.SetList(projectStandards, ids)

Stage 2 — Finish Mode Transaction:
    ─────────────────────────────────────────────────────
    Transaction name: "Finish Mode"  ← end of Copy/Monitor session
    Operation: TransactionCommitted
        ↓
    Retrieve stored IDs from ElementsForPinProtection
    Resolve to Elements, filter: x.GetMonitoredLinkElementIds().Any()
        → only elements that ARE actually copy/monitored
        ↓
    PinElementsExternalEventInfo.Instance.AllCopyMonitoredElements = filtered
    PinElementsExternalEvent.Raise()
        ↓
    Show dialog: "Pin copy/monitored elements? [Yes] [No]"

Cleanup — Abort/Undo:
    ─────────────────────────────────────────────────────
    Transaction "CopyMonitor" with Operation == Undo (1) or Redo (2)
        ↓
    RevitSessionLocal.ElementsForPinProtection.RemoveList(projectStandards)
    (clears the stored IDs — user aborted)
```

### Key Code — Stage 1 (Copy)

```csharp
if (transactionNames.All(x => x == "Copy" || x == "Copy (Copy Monitor link)")
    && operation == TransactionCommitted)
{
    IEnumerable<Element> copiedElements = e.GetAddedElementIds()
        .Select(x => document.GetElement(x));

    // If CAD pin is also enabled, capture any ImportInstances now
    if (PromptToPinAndProtectAfterImportingCAD)
    {
        var cadElements = copiedElements.Where(x => x is ImportInstance).ToList();
        if (cadElements.Any())
        {
            var existing = ElementsForPinProtection.GetList(projectStandards)
                .Select(id => document.GetElement(new ElementId(id)))
                .Where(x => x != null).ToList();
            existing.AddRange(cadElements);
            PinElementsExternalEventInfo.Instance.AllCopyMonitoredElements = existing;
            PinElementsExternalEvent.Raise();
        }
    }

    // Always store all copied element IDs for Stage 2
    ElementsForPinProtection.SetList(projectStandards,
        copiedElements.Where(x => x != null).Select(x => x.Id.GetIdValue()).ToList());
}
```

### Key Code — Stage 2 (Finish Mode)

```csharp
if (transactionNames.All(x => x == "Finish Mode") && operation == TransactionCommitted)
{
    PinElementsExternalEventInfo.Instance.AllCopyMonitoredElements =
        ElementsForPinProtection.GetList(projectStandards)
            .Select(id => document.GetElement(new ElementId(id)))
            .Where(x =>
            {
                if (x == null) return false;
                var monitored = x.GetMonitoredLinkElementIds();
                return monitored != null && monitored.Any(); // only truly monitored
            });

    PinElementsExternalEventInfo.Instance.Document = e.GetDocument();
    if (!PinElementsExternalEvent.IsPending)
        PinElementsExternalEvent.Raise();
}
```

### Configuration Settings

| Setting | Model Property |
|---|---|
| Enable pin-after-copy/monitor | `PinProtectionSettings.PromptToPinAndProtectAfterCopyOrMonitoring` |
| Parent guard | `UserInteractionSettings.PromptUserForUnpin` |

---

## Shared — `PinProtectionSettings` Model

```csharp
class PinProtectionSettings
{
    bool? PromptToPinAndProtectAfterImportingCAD;
    // Enables the pin prompt after any "Import Vector Data" transaction

    bool? PromptToPinAndProtectAfterCopyOrMonitoring;
    // Enables the pin prompt after Copy/Monitor "Finish Mode"
}
```

Both settings live inside `UserInteractionSettings` (per-project, fetched from backend).

---

## Shared — `PinElementsExternalEvent`

All pin prompts funnel through the same `ExternalEvent`:

```csharp
// Created as journalable (survives Revit journal replay)
ExternalEvent.CreateJournalable(new PinElementsExternalEvent())

// Shared state passed via singleton
PinElementsExternalEventInfo.Instance.AllCopyMonitoredElements = <elements>
PinElementsExternalEventInfo.Instance.Document = <document>

// Raise (safe — checks IsPending to avoid double-queuing)
if (!PinElementsExternalEvent.IsPending)
    PinElementsExternalEvent.Raise();
```

> `ExternalEvent` is required because `DocumentChanged` fires **within a transaction context** — you cannot start a new transaction (or show UI) directly. The event handler runs on Revit's next idle cycle.

---

## Comparison — Explode vs Bind Link Button Swap Techniques

| Aspect | CAD Explode | Bind Link |
|---|---|---|
| **Native button control** | `SetButtonVisibility(Visible = false)` | Physically **removed** from panel items list |
| **Restore on allow** | `Visible = true` | Re-**inserted** at index 0 |
| **the legacy product button type** | `RibbonSplitButton` with 2 child items | Single `RibbonButton` |
| **Panel trigger** | `"Dialog_Essentials_ImportInstanceExplode"` | `"Dialog_Essentials_RvtLinkInstanceStdDbar"` |
| **Context-menu binding** | Yes (both Full + Partial) | No separate binding needed |

---

## Full Protection Flow Diagram

```
┌──────────────────────────────────────────────────────────────────┐
│  CAD / Link Operations                                            │
│                                                                   │
│  ┌─────────────┐  ┌─────────────┐  ┌──────────────┐ ┌────────┐  │
│  │  Import CAD │  │  Explode    │  │  Bind Link   │ │ Copy/  │  │
│  │  (DWG/DXF) │  │(Full+Partial│  │  (Rvt Link)  │ │Monitor │  │
│  └──────┬──────┘  └──────┬──────┘  └──────┬───────┘ └───┬────┘  │
│         │                │                 │             │        │
│  DocChanged         Command              Command      DocChanged  │
│  "Import            Binding              Binding      "Finish     │
│   Vector Data"      BeforeExecuted       + Ribbon      Mode"      │
│         │                │                 │             │        │
│         ▼                ▼                 ▼             ▼        │
│  ┌──────────────────────────────────────────────────────────────┐ │
│  │         Showthe legacy productInterventionView()                        │ │
│  │   [Monitor / Guide / Prevent] — configured per-project       │ │
│  └──────────────────┬──────────────────────────────────────────┘ │
│                     │                                             │
│            ┌────────┴────────┐                                    │
│            │                 │                                    │
│          Allow             Block                                  │
│            │                 │                                    │
│   Log + let native     e.Cancel = true                           │
│   command fire         LogCancelledCommand()                     │
│   (swap buttons back)  (keep the legacy product button)                    │
└──────────────────────────────────────────────────────────────────┘
```

---

## CBOX Manage — Replication Checklist

### CAD Explode
- [ ] Register `AddInCommandBinding` for `ID_IMPORT_INSTANCE_EXPLODE` and `ID_IMPORT_INST_PARTIAL_EXPLODE` with `BeforeExecuted`
- [ ] Intercept `OnPanelsChanged` / ribbon load event → find explode panel
- [ ] Create the legacy product split button with Full Explode + Partial Explode child buttons
- [ ] Hide native `explo​dedSplitButton_Revit`; show the legacy product's instead
- [ ] On allowed: swap buttons back; on blocked: `e.Cancel = true`
- [ ] Unregister bindings and restore native buttons when paused/disabled

### Bind Link
- [ ] Register ribbon panel load event → find `"Dialog_Essentials_RvtLinkInstanceStdDbar"`
- [ ] Create the legacy product Bind Link button, **remove** native button from Items collection
- [ ] On allow: re-insert native button; on block: keep removed
- [ ] Tie to `legacy product_BindLinktButton` PostableCommandSettings

### Pin After CAD Import
- [ ] Handle `DocumentChanged`; detect transaction `"Import Vector Data"`
- [ ] Filter `GetAddedElementIds()` for `ImportInstance`
- [ ] If found + setting enabled: raise `PinElementsExternalEvent`
- [ ] Implement `IExternalEventHandler.Execute()` to show pin dialog and call `Element.Pinned = true`
- [ ] Store pin protection in Extensible Storage

### Pin After Copy/Monitor
- [ ] Handle `DocumentChanged`; detect `"Copy"` / `"Copy (Copy Monitor link)"` → store element IDs
- [ ] Handle `DocumentChanged`; detect `"Finish Mode"` → filter stored IDs for monitored elements → raise pin event
- [ ] Handle Undo → clear stored IDs
- [ ] Use session-local storage (not Extensible Storage) for the staging list

---

*Generated from the legacy product 3.2.9.0 source analysis | February 2026*
