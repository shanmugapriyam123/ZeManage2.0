# The legacy product Sync Traffic Control — Replication Guide

> **Purpose**: A developer-level guide to replicate the legacy product's Sync Traffic Control in CBOX Manage.  
> **Source**: Decompiled from `The legacy product 3.2.9.0` — `ClientSyncManager`, `ProjectSyncManager`, `SyncAndModifySettingsViewModel`

---

## 1. What It Does

Sync Traffic Control prevents **multiple users from syncing a workshared Revit model simultaneously**, which causes merge conflicts. The legacy product implements a **centralised queue and warning system** backed by real-time SignalR messaging.

### Three Core Behaviours

| Mode | Trigger | User Action |
|------|---------|------------|
| **Reminder** | Time interval since last sync | Sync / Snooze |
| **SyncWarning** | Another user is actively syncing | Sync-Over / Add to Queue / Cancel |
| **QueueUpdate** | User is first-in-queue; line is now free | Sync Now / Snooze / Cancel |

---

## 2. Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  CBOX Manage Client Add-in                                       │
│                                                                  │
│  ┌──────────────────────┐     ┌──────────────────────────────┐  │
│  │  ClientSyncManager   │────▶│  ProjectSyncManager          │  │
│  │  (Singleton)         │     │  (one per open workshared    │  │
│  │                      │     │   project)                   │  │
│  │  • SignalR listener  │     │  • Reminder timer            │  │
│  │  • Route messages    │     │  • SyncOperation tracking    │  │
│  │  • Session lifecycle │     │  • Queue management          │  │
│  │  • Reconnect logic   │     │  • Conflict detection        │  │
│  └──────────┬───────────┘     └──────────────┬───────────────┘  │
│             │                                │                   │
│             │ SignalR                        │ Raises dialog     │
│             ▼                                ▼                   │
│  ┌──────────────────────┐     ┌──────────────────────────────┐  │
│  │  SignalListener       │     │  SyncAndModifySettingsView   │  │
│  │  (SyncTraffic group)  │     │  (WPF dialog, configurable)  │  │
│  └──────────────────────┘     └──────────────────────────────┘  │
└─────────────────────────────────────────────────────────────────┘
                     │ WebSocket
                     ▼
         ┌───────────────────────┐
         │  Backend / SignalR Hub │
         │  Message types:       │
         │  103 = SyncTraffic    │
         │  102 = ProjectSession │
         │  107 = ProjectValues  │
         └───────────────────────┘
```

> **Key Design Principle**: `ClientSyncManager` is a singleton. `ProjectSyncManager` is created per project when a workshared session starts and torn down when it ends. The dialog (`SyncAndModifySettingsViewModel`) is ephemeral — created on demand, not persistent.

---

## 3. SignalR Group Topology

the legacy product subscribes to **three SignalR groups per project**, based on role:

```csharp
// Group 1: Sync traffic updates
string syncGroup = IsAdmin
    ? GetGroupName_forProjectSyncTrafficAdmins(companyId, projectGuid)
    : GetGroupName_forProjectSyncTrafficUser(companyId, projectGuid);

// Group 2: Project session changes (who opens/closes the model)
string sessionGroup = IsAdmin
    ? GetGroupName_forFeatureIndependentProjectSession_admin(companyId, projectGuid)
    : GetGroupName_forFeatureIndependentProjectSession_user(companyId, projectGuid);

// Group 3: Project values (settings changes pushed from admin web UI)
string valuesGroup = IsAdmin
    ? GetGroupName_forProjectValuesAdmins(companyId, projectGuid)
    : GetGroupName_forProjectValuesUser(companyId, projectGuid);
```

**Subscribe** when a workshared session starts. **Unsubscribe** when it ends.

---

## 4. Lifecycle Hooks

### 4.1 Session Start → `WorksharedSessionStarted()`

Called from `DocumentOpened` event when:
- Document is workshared
- Document is not a linked file
- The project is registered AND `EnableSyncManagement = true`

```csharp
public async Task WorksharedSessionStarted(OperationTracker ot, Document document, ProjectInfo projectInfo)
{
    await _semaphore.WaitAsync();
    try
    {
        string projectGuid = projectInfo.ProjectCookieGuid;

        // Guard: must be registered and feature-enabled
        bool enabled = GetApplicableUserInteractionSettings(projectGuid)?.EnableSyncManagement ?? false;
        if (!enabled) return;

        // Create ProjectSyncManager for this project
        ProjectSyncManager psm = TryAddProjectSyncManager(document, projectInfo);
        if (psm != null)
            await psm.WorksharedSessionStarted();
        else
            await GetExistingPSM(projectGuid).WorksharedSessionStarted();
    }
    finally { _semaphore.Release(); }
}
```

**Inside `ProjectSyncManager.WorksharedSessionStarted()`**:
1. Subscribes to the three SignalR groups
2. Starts the **sync reminder timer** (if `EnableSyncReminderSettings = true`)
3. Fetches the current server-side sync state via REST

---

### 4.2 Session End → `WorksharedSessionEnded()`

Called from: `DocumentClosing`, `DocumentClosed`, or explicitly when the feature is disabled.

```csharp
public async Task WorksharedSessionEnded(OperationTracker ot, string projectGuid, bool? mode)
{
    await _semaphore.WaitAsync();
    try
    {
        var psm = GetPSM(projectGuid);
        psm.MarkAsBeingNotMonitoredLocally(mode);
        await psm.WorksharedSessionEnded();

        // Remove from dictionary if no longer monitored
        if (!psm.IsBeingMonitoredLocally())
            _dict.TryRemove(projectGuid, out _);

        // If no projects left, dispose SignalR listener entirely
        if (_dict.Count == 0)
            DisposeSignalRListener();
    }
    finally { _semaphore.Release(); }
}
```

---

### 4.3 Reconnect

When SignalR reconnects after network loss:

```csharp
public async Task OnReconnect(bool isNetworkAvailable)
{
    SetReconnectingStatus(true);  // Buffer incoming messages
    foreach (var projectInfo in GetOpenProjects())
    {
        var psm = GetPSM(projectInfo.ProjectGuid);
        await psm.OnReconnect(projectInfo, isNetworkAvailable);
        // Re-subscribes to groups, re-fetches current state
    }
    SetReconnectingStatus(false); // Replay buffered messages
}
```

> **Important**: Messages that arrive during reconnect are held in a `SignalrBuffer` and replayed once reconnect completes. Implement this to avoid missing operations during network blips.

---

## 5. Incoming SignalR Message Routing

`ClientSyncManager.SyncTrafficListener_SignalRDataReceived()` is the single entry point for all sync messages:

```csharp
public void SyncTrafficListener_SignalRDataReceived(SignalRMessageInfo msg, OperationTracker ot, bool simulated)
{
    // If reconnecting, buffer the message
    if (IsReconnectingInProgress)
    {
        SignalrBuffer.AddToBuffer(msg);
        return;
    }
    SignalrBuffer.RelayBuffered(); // Flush any prior buffered messages first

    switch (msg.MessageType)
    {
        case 103: // SyncTrafficSignalRMessage — a peer started/finished syncing or queue changed
            UIDispatcher.Invoke(() =>
                GetPSM(msg.ProjectGuid)?.SyncTraffic_SignalRDataReceived(msg, ot));
            break;

        case 102: // ProjectSession — someone opened/closed the model
            UIDispatcher.Invoke(() =>
                GetPSM(msg.ProjectGuid)?.ProjectSession_SignalRDataReceived(msg, ot));
            break;

        case 107: // ProjectValuesInfo — admin changed project settings (e.g. sync interval)
            UIDispatcher.Invoke(() =>
                GetPSM(msg.ProjectGuid)?.ProjectValues_SignalRDataReceived(msg, ot));
            break;
    }
}
```

> **Note**: All UI-touching handlers are dispatched on the UI thread via `UIDispatcher.Invoke`.

---

## 6. The Sync Intercept Flow

When the user clicks **Sync with Central**:

```
User clicks Sync with Central
    ↓
CommandIntervention.Binding_BeforeExecuted()
    ↓ (intercepts the command)
ClientSyncManager.ShowApprovalWaitIndicator(projectGuid, isFromSyncNow, ...)
    ↓
ProjectSyncManager.ShowApprovalWaitIndicator()
    ↓ returns SyncInterventionMode enum:
    │
    ├── None       → No conflict, proceed with sync normally
    │
    ├── Reminder   → Sync timer fired; show reminder dialog
    │
    ├── SyncWarning → Another user is actively syncing
    │
    └── QueueUpdate → User was queued; their turn has arrived

    ↓ (if not None)
SyncAndModifySettingsViewModel.Create(mode, syncIntent, ...)
    ↓
new SyncAndModifySettingsView().ShowDialog()
    ↓
User acts → Synchronize / Snooze / AddToQueue / Cancel
```

---

## 7. The Three Dialog Modes

### 7.1 Reminder Mode

**Trigger**: The sync reminder timer fires (set by `SyncManagementSettings.ReminderIntervalMs`).

**Dialog shows**: Custom message, countdown timer, Snooze button.

**Configurable behaviours**:

| Setting | Effect |
|---------|--------|
| `EnableSyncReminderSettings` | Enables reminder feature |
| `PreventSnoozingLastReminder` | Password required to dismiss last reminder |
| `AutoSyncAfterTimeOut` | Automatically syncs when countdown reaches zero |
| **Snooze intervals** | 5 min / 15 min / 30 min / 1 hr / 2 hr |

**Countdown auto-action** (when timer expires):
- Not last reminder → auto-Snooze
- Last reminder + `AutoSyncAfterTimeOut` → auto-Sync
- Last reminder + `PreventSnoozingLastReminder` + no auto sync → lock out (force sync)

---

### 7.2 SyncWarning Mode

**Trigger**: User attempts sync while another user's sync operation is active on the server.

**Dialog shows**: Who is syncing, conflict message, Sync-Override / Add to Queue / Cancel.

**Protection levels** (from `SyncManagementSettings.ProtectionModeEnum`):

| Mode | Behaviour |
|------|-----------|
| **Monitor** | Shows warning, user can proceed freely |
| **Guide** | Shows warning dialog, soft block (can override) |
| **Prevent** | Password required to override |

**Edge case — same user double-sync**: If the active sync operation belongs to the same user (stale server record), the legacy product kills the stale operation and returns `null` (no dialog shown, proceed with sync normally).

**Comment requirement**: If `SyncManagementSettings.ReqComment = true`, the dialog enforces a typed comment before allowing sync.

---

### 7.3 QueueUpdate Mode

**Trigger**: User previously clicked "Add to Queue" and the queue slot is now free (received via SignalR).

**Dialog shows**: "The queue is now free", countdown to auto-sync, Snooze / Let Next User Sync / Cancel.

**Snooze behaviour**:
- Snooze → places user back in the queue with a new timestamp
- "Let Next User Sync" → yields the slot to the next user in queue

---

## 8. User Action Handlers

### 8.1 Synchronize

```csharp
private void Synchronize()
{
    // 1. Optional: verify override password (Prevent mode)
    if (IsPasswordRequired && !VerifyPassword()) return;

    // 2. Start the actual sync
    bool syncStarted = _projectSyncManager.StartSyncWithCentral(options);

    // 3. Log to CommandMonitor (for audit trail)
    if (syncStarted)
    {
        CommandMonitor.UpdateIntermediateUserEventInfo(comment, protectionMode, sendEmail);
        CommandMonitor.SetIntervalSeqNoAndRepetitionCount(intervalSeqNo, repetitionCount);
        CommandMonitor.SetSyncGuid(latestSyncStat.SyncGuid);
        CommandMonitor.LogCompletedCommand();
    }
}
```

### 8.2 Snooze

```csharp
private async Task Snooze(bool considerCurrentIntentTimeDiff)
{
    // Posts a "snooze" intent to the backend
    // Backend will fire SyncTrafficSignalRMessage when the snooze expires
    await ClientSyncManager.Snooze(
        projectGuid,
        isFromQueueUpdate: (mode == QueueUpdate),
        considerCurrentIntentTimeDiff,
        nextPendingUserWhenUTC
    );
}
```

### 8.3 Add to Queue

```csharp
private async Task AddToQueue()
{
    // 1. POST to backend: add this user's SyncIntent to the project queue
    await ClientSyncManager.AddToQueue(projectGuid, isFromSyncNowCommand);

    // 2. Set "ContinueUntilAvailable" flag on ProjectSyncManager
    _projectSyncManager.SetContinueUntilAvailable();

    // 3. Log as abandoned command (user deferred)
    CommandMonitor.AbandonProjectLevelActiveCommand();
}
```

### 8.4 Clear User from Queue (admin action)

```csharp
private async Task ClearUserFromTheQueue()
{
    // Admin forcibly removes another user's sync operation
    await _projectSyncManager.KillSyncOperation(_activeSyncOperation);
    DialogResult = ClearUserFromQueue;
}
```

---

## 9. Key Data Models

### `SyncManagementSettings` (from backend)

```csharp
class SyncManagementSettings
{
    bool?   EnableSyncManagement;         // Master switch
    bool?   EnableSyncReminderSettings;   // Enable reminder timer
    int?    ReminderIntervalMs;           // How often to remind (ms)
    int?    ApprovalTimeoutMs;            // Countdown timer duration
    bool?   PreventSnoozingLastReminder;  // Lock out last reminder
    bool?   AutoSyncAfterTimeOut;         // Auto-sync on timeout
    InterventionModeType ProtectionModeEnum; // Monitor / Guide / Prevent
    string  Message;                      // Custom warning message (plain)
    string  MessageRtf;                   // Custom warning message (RTF)
    bool?   SendEmail;                    // Email on conflict
    bool?   ReqComment;                   // Require comment
    string  ReminderMessage;              // Custom reminder message
    string  OverridePassword;             // Password for Prevent mode
}
```

### `SyncIntent` (server entity)

Represents one user's **intent to sync**, stored by the backend. The backend queues these and signals each user when it's their turn.

```csharp
class SyncIntent
{
    string   Id;                        // Unique ID
    string   ProjectGuid;
    string   CompanyLicenseGuid;
    DateTime SyncIntentApprovalTimeLocal; // When the user's turn started
    // ... status, username, queue position
}
```

### `SyncOperation` (live object, from SignalR)

Represents a **currently in-progress sync** by a user.

```csharp
class SyncOperation
{
    string              Id;
    SyncActivity        SyncActivity;  // Contains Username
    SyncOperationStatus StatusEnum;    // Started, Completed, Killed
    string              ProjectGuid;
}
```

### `SyncInterventionMode` (enum)

```csharp
enum SyncInterventionMode { None, Reminder, SyncWarning, QueueUpdate }
```

### `SyncAndModifyDialogResult` (enum)

```csharp
enum SyncAndModifyDialogResult { None, Sync, Snooze, Cancel, AddToQueue, ClearUserFromQueue }
```

---

## 10. Settings Change Propagation

When an admin changes sync settings via the web UI:

```
Admin saves new SyncManagementSettings
    ↓
Backend broadcasts MessageType 107 (ProjectValuesInfo) via SignalR
    ↓
ClientSyncManager.SyncTrafficListener_SignalRDataReceived
    ↓
ProjectSyncManager.ProjectValues_SignalRDataReceived
    ↓
ClientSyncManager.HandleSyncSettingChanges(document, projectInfo, newSettings)
    ↓
Scenario A: Feature turned ON
    → WorksharedSessionStarted() — creates PSM, starts timer
    → Refresh SyncMonitor view

Scenario B: Feature turned OFF
    → WorksharedSessionEnded() — tears down PSM, removes from dictionary
    → SyncMonitor.RemoveSyncMonitoring()

After either:
    → CommandIntervention.ReviseSyncCommandsBinding()
      (shows/hides "Sync with Central" command intercept)
```

---

## 11. Pause / Resume Integration

When an admin pauses the legacy product protection for a project:

```csharp
public async Task HandlePauseResumeSettingChanges(string projectGuid)
{
    var psm = GetPSM(projectGuid);

    if (psm.SyncManagementSettings.EnableSyncReminderSettings && !IsSyncTrafficActive)
        // Protection paused → stop reminder timer
        await psm.OnDisableReminder();
    else if (psm.SyncManagementSettings.EnableSyncReminderSettings && IsSyncTrafficActive)
        // Protection resumed → restart reminder timer
        await psm.WorksharedSessionStarted();
}
```

---

## 12. Compact File Check

After sync, the legacy product checks with the backend whether a model compact is recommended:

```csharp
bool compactRequired = ClientSyncManager.IsCompactRequired(projectGuid);
// → Delegates to ProjectSyncManager.IsCompactRequired()
// → Based on backend-provided ProjectValues (from SignalR message 107)
```

---

## 13. CBOX Manage — Implementation Roadmap

### Phase 1 — Core Infrastructure (do first)
- [ ] **`ClientSyncManager`** singleton with `ConcurrentDictionary<string, ProjectSyncManager>`
- [ ] **`SignalListener`** with `SignalrBuffer` for reconnect resilience
- [ ] Subscribe/unsubscribe to **three SignalR groups** per project (role-aware)
- [ ] **`WorksharedSessionStarted`** / **`WorksharedSessionEnded`** lifecycle with `SemaphoreSlim`
- [ ] **`OnReconnect`** with message buffering

### Phase 2 — Conflict Detection
- [ ] **`ProjectSyncManager`** per project, tracking `SyncOperation` collection
- [ ] Handle `MessageType 103` (`SyncTrafficSignalRMessage`) → update active operation list
- [ ] Handle `MessageType 102` (`ProjectSession`) → track who has the model open
- [ ] Expose `IsAnySyncOperationActive(out List<string> usernames)` 

### Phase 3 — Intercept & Dialog
- [ ] **Intercept `SyncWithCentral`** command before execution
- [ ] Call `ShowApprovalWaitIndicator()` → returns `SyncInterventionMode`
- [ ] Build **`SyncAndModifySettingsView`** WPF dialog with dynamic layout per mode
- [ ] Implement all user actions: **Sync, Snooze, AddToQueue, Cancel, ClearFromQueue**

### Phase 4 — Reminders
- [ ] **Reminder timer** in `ProjectSyncManager` (configurable interval)
- [ ] Multi-stage snooze with escalating lock-out on last reminder
- [ ] Auto-sync and auto-snooze on timeout counter

### Phase 5 — Settings Propagation & Polish
- [ ] Handle `MessageType 107` → live settings push from admin web UI
- [ ] Wire **pause/resume** to start/stop reminder timer
- [ ] **Compact check** after sync
- [ ] **Sync Monitor view** (read-only list of all users syncing per project)
- [ ] Admin **"Kit user from queue"** action

---

## 14. Critical Implementation Notes

> [!IMPORTANT]
> **Thread safety**: `ClientSyncManager` uses a `SemaphoreSlim(1,1)` to serialise `WorksharedSessionStarted` and `WorksharedSessionEnded` calls. Without this, concurrent document events can create duplicate `ProjectSyncManager` instances.

> [!IMPORTANT]
> **UI Thread**: All `ProjectSyncManager` state updates that touch WPF must be dispatched via `UIDispatcher.Invoke()`. SignalR callbacks arrive on background threads.

> [!WARNING]
> **Same-user stale operations**: Always check if the conflicting sync operation belongs to the current user (case-insensitive username compare). If it does → kill the stale operation server-side and skip the dialog entirely. Without this, a user whose previous sync crashed will be permanently blocked.

> [!NOTE]
> **`SyncAndModifySettingsViewModel` is created fresh** for each dialog invocation. It is not a singleton. It subscribes to events in its constructor and must unsubscribe and dispose on close.

> [!NOTE]
> **Snooze interval labels**: The legacy product maps millisecond values to human-readable button labels (5 min = 300000 ms, 15 min = 900000 ms, 30 min = 1,800,000 ms, 1 hr = 3,600,000 ms, 2 hr = 7,200,000 ms).

---

*Generated from the legacy product 3.2.9.0 source analysis | February 2026*
