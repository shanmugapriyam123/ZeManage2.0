# Background Sync Deep Analysis — Background Operations
> Source: legacy background sync engine · Extracted from `Application.cs`, `Timer_Rules.cs`, `Settings.cs`

![Options UI](file:///C:/Users/PC/.gemini/antigravity/brain/f6095a5d-1e92-4d7e-b84f-7b233117db20/uploaded_media_1771693493519.png)

---

## Infrastructure: Two Background Threads

On `ApplicationInitialized`, the legacy background sync engine launches **exactly 2 background threads**:

```csharp
// Thread 1 — Main event loop timer (fires every 1000 ms)
Timer timerMain = new System.Timers.Timer();
timerMain.Elapsed += Application.OnTimedEvent;
timerMain.Interval = 1000.0;
timerMain.Enabled = true;
new Thread(() => { /* keeps thread alive */ }) { IsBackground = true }.Start();

// Thread 2 — Schedule checker (fires every 5000 ms)
Timer timerSchedule = new System.Timers.Timer();
timerSchedule.Elapsed += Application.ScheduleOnTimedEvent;
timerSchedule.Interval = 5000.0;
timerSchedule.Enabled = true;
new Thread(() => { /* keeps thread alive */ }).Start();
```

**`OnTimedEvent`** (1 s tick): posts `WM_NULL (0x0000)` to the Revit main window to trigger Revit's idle event so sync actions can run on the UI thread.

All document operations run inside **`OnIdling`** (Revit's idle event handler), which is the safe context for Revit API calls.

**User Input Tracking** uses the Win32 API:
```csharp
[DllImport("user32.dll")]
private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

// Updated on every Revit document event (save, sync, open, close...)
Application.Lastinput = Application.E1pDegJgB56ZvCgPjuF(); // = DateTime.Now from GetLastInputInfo
```

---

## 1. Mode — Continuous vs. When I Take a Pause

### Settings
| Setting | Type | Default | Meaning |
|---------|------|---------|---------|
| `AllTheTime` | `bool` | `false` | **Continuous** — run even during active work |
| `WhenRevitIsNotInUse` | `bool` | `true` | **When Paused** — only when user is idle |

### Logic (in `OnIdling` / `Timer_Rules.On_Revit_Idle`)

```
Mode = "Always On"  →  AllTheTime = true, WhenRevitIsNotInUse = false
Mode = "When Paused" →  AllTheTime = false, WhenRevitIsNotInUse = true
```

**Always On path** (`Timer_Rules.Always_On`):
```text
→ Does NOT check Application.Lastinput
→ Runs time-based triggers regardless of user activity
```

**When Paused path** (`Timer_Rules.On_Revit_Idle`):
```text
→ Checks: Application.idling_operation == false (not already running)
→ Checks: Application.Lastinput.AddMinutes(SaveSyncEvery) < DateTime.Now
→ Only proceeds when user has been inactive ≥ SaveSyncEvery minutes
```

### Replication Script

```csharp
public class SyncModeManager
{
    public bool AllTheTime { get; set; } = false;       // Continuous
    public bool WhenRevitIsNotInUse { get; set; } = true; // When Paused

    private DateTime _lastInput = DateTime.Now;

    // Call this on every user action (keyboard, mouse, doc event)
    public void RecordUserActivity() => _lastInput = DateTime.Now;

    // Call this before attempting any background operation
    public bool CanRunNow(int idleThresholdMinutes)
    {
        if (AllTheTime) return true;  // Always permitted
        
        // Only run if user has been idle for the required interval
        return _lastInput.AddMinutes(idleThresholdMinutes) < DateTime.Now;
    }
}
```

---

## 2. Background Save / Sync

### Settings
| Setting | Type | Default | Meaning |
|---------|------|---------|---------|
| `SaveSync` | `bool` | `true` | Enable background sync |
| `SaveSyncEvery` | `int` | `15` | Interval in minutes |

### Logic

The sync timer is **dual-tracked**: once for `Always_On` mode (uses `current.Synchronised` timestamp), once for idle mode (uses `Application.Lastinput`).

**Always On (time-based, from `Timer_Rules.Always_On` lines 442–451):**
```
dateTime = current.Synchronised          // last sync timestamp from tracking list
if dateTime.AddMinutes(SaveSyncEvery) > DateTime.Now:
    → SKIP (not yet due)
else:
    hashCode = d.GetHashCode()
    Application.documentid = hashCode.ToString()
    Application.docpathname = d.PathName  (or via helper)
    Application.operation = "AutoSync"
    Application.MakeRequest(RequestId.Save, d)
```

**When Paused (idle-sensitive, from `Timer_Rules.On_Revit_Idle` lines 898, 992):**
```
if Application.Lastinput.AddMinutes(SaveSyncEvery) > DateTime.Now:
    → SKIP (user still active recently)
else:
    → Execute sync
```

**Actual Sync execution** (`Application.sync()` lines 4053–4350):
```csharp
Application.IsAutomatic = true;
// Wait until background calculation is complete
while (dh.IsBackgroundCalculationInProgress()) { }

// Build sync options
SynchronizeWithCentralOptions syncOptions = new SynchronizeWithCentralOptions();
TransactWithCentralOptions transactOptions = new TransactWithCentralOptions();
RelinquishOptions relinquishOptions = new RelinquishOptions(true)
{
    FamilyWorksets = false,
    CheckedOutElements = false
};
syncOptions.SetRelinquishOptions(relinquishOptions);

// Execute
dh.SynchronizeWithCentral(transactOptions, syncOptions);
Application.IsAutomatic = false;
```

### Replication Script

```csharp
public class BackgroundSyncManager
{
    private readonly Settings _settings;
    private readonly List<TrackedDocument> _trackedDocs;
    private bool _isRunning = false;

    public void RunSyncCycle(IEnumerable<Document> openDocuments, DateTime lastUserInput)
    {
        if (_isRunning) return;
        _isRunning = true;
        try
        {
            foreach (var doc in openDocuments)
            {
                if (!_settings.SaveSync) continue;
                if (doc.IsLinked) continue;
                if (!doc.IsWorkshared) continue;

                var tracked = FindTracked(doc);
                if (tracked == null) continue;

                bool isDue = _settings.AllTheTime
                    ? tracked.Synchronised.AddMinutes(_settings.SaveSyncEvery) < DateTime.Now
                    : lastUserInput.AddMinutes(_settings.SaveSyncEvery) < DateTime.Now;

                if (isDue)
                {
                    SetDocContext(doc);
                    Application.operation = "AutoSync";
                    QueueRequest(RequestId.Sync, doc);
                }
            }
        }
        finally { _isRunning = false; }
    }

    private void ExecuteSync(Document doc)
    {
        while (doc.IsBackgroundCalculationInProgress())
            Thread.Sleep(100);

        var syncOpts = new SynchronizeWithCentralOptions();
        var relinquishOpts = new RelinquishOptions(true)
        {
            FamilyWorksets = false,
            CheckedOutElements = false
        };
        syncOpts.SetRelinquishOptions(relinquishOpts);
        doc.SynchronizeWithCentral(new TransactWithCentralOptions(), syncOpts);
        
        FindTracked(doc).Synchronised = DateTime.Now;
    }
}
```

---

## 3. Background Relinquish

### Settings
| Setting | Type | Default | Meaning |
|---------|------|---------|---------|
| `Relinquish` | `bool` | `true` | Enable background relinquish |
| `RelinquishEvery` | `int` | `5` | Interval in minutes |
| `EnableRelinquish` | `bool` | `false` | Master toggle (must be true) |
| `RelinquishTime` | `int` | `0` | Alternative timing value |

### Logic (from `Application.OnIdling` lines 3554–3607)

```
For each tracked document:
    dateTime = current.Relinquished              // last relinquish timestamp

    // Two-part check:
    // Part A: time elapsed since last sync
    if dateTime.AddMinutes(RelinquishEvery) < DateTime.Now:
        // Part B: user must be idle
        if Lastinput.AddMinutes(RelinquishEvery) > DateTime.Now:
            → SKIP (user still active)
        // Part C: settings check
        if Settings.Relinquish:
            Application.relinquish(doc)
            current.Relinquished = DateTime.Now
```

**Actual Relinquish** (`Application.relinquish()`):
```csharp
// Uses Revit API WorksharingUtils
// Releases all checked-out elements, worksets, family worksets
```

### Replication Script

```csharp
public void RunRelinquishCycle(Document doc, TrackedDocument tracked, DateTime lastInput)
{
    if (!Settings.EnableRelinquish) return;
    if (!Settings.Relinquish) return;

    bool timeElapsed = tracked.Relinquished.AddMinutes(Settings.RelinquishEvery) < DateTime.Now;
    bool userIsIdle  = lastInput.AddMinutes(Settings.RelinquishEvery) < DateTime.Now;

    if (timeElapsed && userIsIdle)
    {
        // Execute relinquish via Revit API
        var relinquishOpts = new RelinquishOptions(true)
        {
            FamilyWorksets      = false,
            CheckedOutElements  = false,
            UserWorksets        = true
        };
        var result = WorksharingUtils.RelinquishOwnership(
            doc, relinquishOpts, new TransactWithCentralOptions());

        tracked.Relinquished = DateTime.Now;
        LogOperation(doc, "Relinquished", result);
    }
}
```

---

## 4. Exit Revit When Idle

### Settings
| Setting | Type | Default | Meaning |
|---------|------|---------|---------|
| `ExitRevit` | `bool` | `true` | Enable auto-exit |
| `ExitRevitEvery` | `int` | `1440` | Minutes of idle before exit (default = 24 hrs) |

### Logic (from `Application.OnIdling` lines 1973–1993)

```
// Check in OnIdling:
if Application.ConfimPOPupForExit:
    → Show confirmation popup (ConfimPOPupForExit is set elsewhere)
else:
    → Proceed to shutdown sequence

// Shutdown sequence:
if Application.ScheduleGo:   // only during active schedule window
    GetCurrentProcess()
    if process.MainWindowHandle valid:
        PostMessage(mainWindowHandle, WM_NULL=0, 0, 0)
        // Revit handles WM_CLOSE / shutdown internally
```

**The idle check** (from `Timer_Rules` lines 3347, 3609):
```
if Lastinput.AddMinutes(ExitRevitEvery) < DateTime.Now:
    → trigger ConfimPOPupForExit flow
```

```csharp
// Win32 message posting (line 1749)
Application.PostMessage(mainWindowHandle, 0U, 0U, 0U); // WM_NULL triggers idle event
```

### Replication Script

```csharp
public class AutoExitManager
{
    private bool _confirmPending = false;
    private DateTime _lastInput = DateTime.Now;

    // Called on each idle tick
    public void CheckIdleExit()
    {
        if (!Settings.ExitRevit) return;
        
        bool overdue = _lastInput.AddMinutes(Settings.ExitRevitEvery) < DateTime.Now;
        if (!overdue) return;

        if (!_confirmPending)
        {
            _confirmPending = true;
            ShowExitConfirmationAsync(); // Non-blocking popup
        }
    }

    private async void ShowExitConfirmationAsync()
    {
        // Show a countdown dialog: "Revit will close in 60 seconds. Cancel?"
        bool cancelled = await ShowCountdownDialog(seconds: 60);
        if (!cancelled)
        {
            // Post close message to main window
            PostMessage(Process.GetCurrentProcess().MainWindowHandle, 
                        WM_CLOSE, 0, 0); // 0x0010
        }
        _confirmPending = false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, uint wParam, uint lParam);
    private const uint WM_CLOSE = 0x0010;
}
```

---

## 5. Schedule (Optional Time Window)

### Settings
| Setting | Type | Default | Meaning |
|---------|------|---------|---------|
| `chkEnableSchedule` | `bool` | `false` | Enable schedule |
| `startschedule` | `DateTime` | 21:00 | Start time |
| `endschedule` | `DateTime` | 09:00 | End time |

### Key Flag
- `Application.ScheduleGo` — live boolean updated every 5 seconds by schedule timer
- `Application.EnableSchedule` — set at startup from INI/settings

### Logic (`ScheduleOnTimedEvent`, lines 1779–1893)

The schedule handles **two cases**:

**Case A: Start < End (e.g. 09:00–17:00, same day)**
```
now = DateTime.Now.TimeOfDay
startTime = Settings.startschedule.TimeOfDay
endTime   = Settings.endschedule.TimeOfDay

if startTime < endTime:
    // Normal window (doesn't cross midnight)
    if now >= startTime AND now <= endTime:
        ScheduleGo = true
    else:
        ScheduleGo = false
```

**Case B: Start > End → crosses midnight (e.g. 21:00–09:00)**
```
if startTime > endTime:
    // Night window spanning midnight
    if now >= startTime OR now <= endTime:
        ScheduleGo = true
    else:
        ScheduleGo = false
```

**Gate in `OnIdling`** (line 1988):
```csharp
if (!Application.ScheduleGo) return;  // Exit immediately if outside schedule
```

### Replication Script

```csharp
public class ScheduleManager
{
    private System.Timers.Timer _scheduleTimer;
    public bool ScheduleGo { get; private set; } = false;

    public void Initialize()
    {
        _scheduleTimer = new System.Timers.Timer(5000); // Check every 5 seconds
        _scheduleTimer.Elapsed += UpdateScheduleState;
        _scheduleTimer.Enabled = true;
    }

    private void UpdateScheduleState(object sender, ElapsedEventArgs e)
    {
        if (!Settings.chkEnableSchedule)
        {
            ScheduleGo = true;  // No schedule = always active
            return;
        }

        TimeSpan now   = DateTime.Now.TimeOfDay;
        TimeSpan start = Settings.startschedule.TimeOfDay;  // e.g. 21:00
        TimeSpan end   = Settings.endschedule.TimeOfDay;    // e.g. 09:00

        if (start < end)
        {
            // Same-day window: 09:00 → 17:00
            ScheduleGo = now >= start && now <= end;
        }
        else
        {
            // Overnight window: 21:00 → 09:00 (crosses midnight)
            ScheduleGo = now >= start || now <= end;
        }
    }

    // Use in every sync/relinquish gate
    public bool IsWithinSchedule() => ScheduleGo;
}
```

---

## 6. Save/Sync Even If No Changes

### Settings
| Setting | Type | Default | Meaning |
|---------|------|---------|---------|
| `SaveSyncEvenIfNoChanges` | `bool` | `false` | Sync even if the doc has no pending changes |

### Logic (from `Application.save()` lines 3914–3936)

The check happens at the **start of the save function**, before anything else:

```csharp
public static void save(Document dh)
{
    // GATE 1: Check if document has pending changes
    if (!dh.IsModified && !Settings.SaveSyncEvenIfNoChanges)
        return;  // Skip — no changes and option disabled
    
    // If SaveSyncEvenIfNoChanges = true, skip the IsModified check
    // and always proceed to save/sync
    
    // ... rest of save logic
}
```

The flag maps directly to `SynchronizeWithCentralOptions`:
- `false` → Revit's default behavior (skip if nothing changed)
- `true`  → Always sync regardless (equivalent to always calling `SynchronizeWithCentral`)

### Replication Script

```csharp
public bool ShouldSync(Document doc)
{
    // If document has unsaved changes → always sync
    if (doc.IsModified) return true;
    
    // If document is clean but user wants to sync anyway
    if (Settings.SaveSyncEvenIfNoChanges) return true;
    
    return false; // No changes and option disabled → skip
}

// Usage in sync pipeline:
public void ExecuteSyncIfNeeded(Document doc)
{
    if (!ShouldSync(doc)) return;
    ExecuteSync(doc);
}
```

---

## 7. Compact Model

### Settings
| Setting | Type | Default | Meaning |
|---------|------|---------|---------|
| `CompressOnceADay` | `bool` | `false` | **"Once a day"** — compress first sync of each day |
| `CompressAtNightDaily` | `bool` | `false` | **"At night only"** — compress during hours 0–6 |

### Key Tracking: `Application.compacteddocuments`
A `List<string>` that stores keys: `"{ProjectName}{DayNumber}"` — prevents double-compacting on the same day.

### Logic (from `Application.sync()` lines 4155–4330)

```
key = doc.ProjectInformation.Name + DateTime.Today.Day.ToString()

if CompressOnceADay:
    if compacteddocuments.Contains(key):
        → SKIP (already compacted today)
    
    if CompressAtNightDaily:
        // At night path
        dateTime = DateTime.Now
        if dateTime.Hour < 6:           // Between midnight and 6am
            withCentralOptions1.Compact = true
            compacteddocuments.Add(key)
        else:
            withCentralOptions1.Compact = false // Outside night window
    else:
        // Once-a-day path (any time)
        withCentralOptions1.Compact = true
        compacteddocuments.Add(key)

// Compact = false (default) on all other syncs
```

**Exact condition** (lines 4158, 4297, 4317):
```csharp
// Case: CompressOnceADay = true
if (!Settings.CompressOnceADay) { compact = false; break; }

// Case: CompressAtNightDaily sub-check
if (Settings.CompressAtNightDaily)
{
    // Read current hour
    if (DateTime.Now.Hour < 6)  // 00:00–05:59
    {
        syncOptions.Compact = true;
        compacteddocuments.Add(key);
    }
    // else: don't compact (outside night window)
}
else
{
    // Once a day, any time
    syncOptions.Compact = true;
    compacteddocuments.Add(key);
}
```

### Replication Script

```csharp
public class CompactModelManager
{
    // In-memory tracking: "ProjectName+DayOfMonth"
    private readonly List<string> _compactedToday = new List<string>();
    private readonly Settings _settings;

    private string GetCompactKey(Document doc)
        => doc.ProjectInformation.Name + DateTime.Today.Day.ToString();

    public void ApplyCompactSettings(SynchronizeWithCentralOptions syncOptions, Document doc)
    {
        syncOptions.Compact = false; // Default: no compaction

        if (!_settings.CompressOnceADay) return;

        string key = GetCompactKey(doc);
        if (_compactedToday.Contains(key)) return; // Already done today

        if (_settings.CompressAtNightDaily)
        {
            // "At night only" — only compact between 00:00 and 06:00
            if (DateTime.Now.Hour < 6)
            {
                syncOptions.Compact = true;
                _compactedToday.Add(key);
            }
            // else: within same day but outside night window → wait
        }
        else
        {
            // "Once a day" — compact on first sync of the day, any time
            syncOptions.Compact = true;
            _compactedToday.Add(key);
        }
    }

    // Call at midnight or application restart to reset tracking
    public void ResetDailyTracking() => _compactedToday.Clear();
}
```

---

## Complete Integration: The Sync Pipeline

```csharp
public class BackgroundSyncEngine
{
    private readonly ScheduleManager    _schedule;
    private readonly SyncModeManager   _mode;
    private readonly CompactModelManager _compact;
    private readonly AutoExitManager   _exit;
    private bool _isRunning = false;

    // Called every time Revit fires OnIdling
    public void OnIdle(UIApplication uiApp)
    {
        // Gate 1: Schedule window
        if (!_schedule.IsWithinSchedule()) return;
        
        // Gate 2: Already running?
        if (_isRunning) return;
        
        _isRunning = true;
        try
        {
            // Check idle exit
            _exit.CheckIdleExit();

            foreach (Document doc in uiApp.Application.Documents)
            {
                if (doc.IsLinked || !doc.IsWorkshared) continue;

                var tracked = GetOrCreateTracked(doc);
                DateTime lastInput = GetLastUserInput();

                // Gate 3: Mode check (continuous vs. paused)
                if (!_mode.CanRunNow(_settings.SaveSyncEvery)) continue;

                // Save/Sync
                if (_settings.SaveSync)
                {
                    bool syncDue = _settings.AllTheTime
                        ? tracked.Synchronised.AddMinutes(_settings.SaveSyncEvery) < DateTime.Now
                        : lastInput.AddMinutes(_settings.SaveSyncEvery) < DateTime.Now;

                    if (syncDue && ShouldSync(doc))
                    {
                        var syncOpts = new SynchronizeWithCentralOptions();
                        _compact.ApplyCompactSettings(syncOpts, doc);
                        ExecuteSync(doc, syncOpts);
                        tracked.Synchronised = DateTime.Now;
                    }
                }

                // Relinquish
                if (_settings.EnableRelinquish && _settings.Relinquish)
                {
                    bool relinquishDue = 
                        tracked.Relinquished.AddMinutes(_settings.RelinquishEvery) < DateTime.Now
                        && lastInput.AddMinutes(_settings.RelinquishEvery) < DateTime.Now;

                    if (relinquishDue)
                    {
                        ExecuteRelinquish(doc);
                        tracked.Relinquished = DateTime.Now;
                    }
                }
            }
        }
        finally { _isRunning = false; }
    }

    private bool ShouldSync(Document doc)
        => doc.IsModified || _settings.SaveSyncEvenIfNoChanges;
}
```

---

## Settings Reference Table

| Setting | Type | Default | UI Label |
|---------|------|---------|----------|
| `AllTheTime` | bool | false | Mode: Continuous |
| `WhenRevitIsNotInUse` | bool | true | Mode: When I take a pause |
| `SaveSync` | bool | true | Enable Save/Sync |
| `SaveSyncEvery` | int | 15 min | Sync every N minutes |
| `SaveSyncEvenIfNoChanges` | bool | false | ☑ Save/Sync even if there are no changes |
| `ExitRevit` | bool | true | Exit Revit when idle |
| `ExitRevitEvery` | int | 1440 min | Exit after N minutes idle |
| `chkEnableSchedule` | bool | false | ☑ Enable (schedule) |
| `startschedule` | DateTime | 21:00 | Schedule Start |
| `endschedule` | DateTime | 09:00 | Schedule End |
| `Relinquish` | bool | true | Enable Relinquish |
| `RelinquishEvery` | int | 5 min | Relinquish every N minutes |
| `EnableRelinquish` | bool | false | Master toggle for relinquish |
| `CompressOnceADay` | bool | false | Compact Model: Once a day |
| `CompressAtNightDaily` | bool | false | Compact Model: At night only |
