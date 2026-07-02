# Sync Settings — Technical Reference

Companion to [sync-settings-user-guide.md](sync-settings-user-guide.md). This document explains, for each control in the Sync Settings dialog, **how it actually works inside the code at runtime**: which file reads it, what method runs, what gets logged, and what edge cases exist.

Use this when you need to debug a setting that "isn't working", or when extending the engine.

---

## Architecture overview

```
┌─────────────────────────────────────────────────────────────────┐
│  USER OPENS DIALOG                                              │
│  SyncSettingsCommand.Execute  →  new SyncSettingsDialog(...)    │
│  BIManage/Commands/RibbonCommands/SyncSettingsCommand.cs        │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│  DIALOG CONSTRUCTOR                                             │
│  SyncSettingsDialog.xaml.cs creates SyncSettingsViewModel(...)  │
│  Calls viewModel.LoadAsync() — pulls saved row from SQLite      │
│  BIManage/ViewModels/SyncSettings/SyncSettingsViewModel.cs:266  │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│  USER EDITS + CLICKS SAVE                                       │
│  viewModel.SaveBackgroundSyncSettingsAsync()                    │
│   ├─ syncRepository.SaveBackgroundSyncSettingsAsync(settings)   │
│   │   → writes 1 row to SQLite table background_sync_settings   │
│   └─ featureToggleService.SetFeatureEnabled(...) × 4            │
│       → mirrors 4 bools into in-memory feature toggles          │
│  BIManage/ViewModels/SyncSettings/SyncSettingsViewModel.cs:312  │
└──────────────────────────────┬──────────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────────┐
│  BACKGROUND TICK (every 30s)                                    │
│  BackgroundSyncEngine.OnTimerTick()                             │
│  BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs          │
│   1. Check feature toggles                                      │
│   2. Load latest settings from SQLite                           │
│   3. Active Hours window check                                  │
│   4. Sync mode + interval/idle gates                            │
│   5. Decide: sync / relinquish / compact / auto-exit            │
│   6. Raise ExternalEvent → handler runs on Revit UI thread      │
│      BackgroundSyncExternalEventHandler.cs                      │
└─────────────────────────────────────────────────────────────────┘
```

Every 30 seconds the engine re-evaluates **all** settings from scratch. There's no caching beyond what SQLite gives you — change a setting, click Save, and within ~30s the change takes effect.

---

## Field-by-field execution trace

### Enable Background Sync

| Layer | Detail |
|---|---|
| **UI control** | `<CheckBox IsChecked="{Binding IsBackgroundSyncEnabled}">` |
| **ViewModel property** | `SyncSettingsViewModel.IsBackgroundSyncEnabled` (default `true`) |
| **DB column** | `background_sync_settings.is_enabled` (INTEGER 0/1) |
| **Feature toggle mirror** | `"BackgroundSync"` + `"SyncQueueControl"` |
| **Runtime read** | [BackgroundSyncEngine.cs:155](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L155) — early-return guard at top of `OnTimerTick` |
| **What fires when ON** | Every 30s tick checks the remaining gates and may sync |
| **What fires when OFF** | Tick returns immediately at line 155 unless `EnableRelinquish` is on (relinquish-only mode at line 156) |
| **Sample log (on)** | `[BackgroundSyncEngine] Triggering sync for <guid> (compact: False, interval: 30min)` |
| **Sample log (off)** | No `[BackgroundSyncEngine]` entries beyond the 30s tick heartbeat |

### Sync Mode — "On a schedule" vs "When not in use"

| Layer | Detail |
|---|---|
| **UI control** | Radio buttons. ● *On a schedule* → `SyncAllTheTime = true`. ● *When not in use* → `SyncAllTheTime = false` |
| **ViewModel property** | `SyncSettingsViewModel.SyncAllTheTime` (default `false` — "when not in use"). The inverse `SyncWhenPaused` is computed on demand for the other radio |
| **Conditional UI** | `ShowIdleOptions => !SyncAllTheTime` — Wait-before-syncing only shows in idle mode |
| **DB column** | `background_sync_settings.sync_all_the_time` (INTEGER 0/1) |
| **Feature toggle** | None directly — mode logic lives entirely in the engine |
| **Runtime read** | [BackgroundSyncEngine.cs:286](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L286) — `ShouldSync()` branch |
| **Continuous path (true)** | Check `(now - lastSyncedAt) >= SyncIntervalMinutes` → if yes, sync |
| **Idle path (false)** | Check OS idle time (`GetLastInputInfo`) >= `IdleTimeoutMinutes` AND `(now - lastSyncedAt) >= SyncIntervalMinutes` |

### Sync every (minutes)

| Layer | Detail |
|---|---|
| **UI control** | `<TextBox Text="{Binding SyncIntervalMinutes}">` (numeric) |
| **DB column** | `background_sync_settings.sync_interval_minutes` (INTEGER) |
| **Runtime read** | [BackgroundSyncEngine.cs:293](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L293) |
| **Effect** | Minimum elapsed minutes since last successful sync before another sync can fire |
| **Edge case** | Value `0` or `1` produces near-constant syncing — useful for stress testing, painful for users |

### Wait before syncing (minutes) — *idle mode only*

| Layer | Detail |
|---|---|
| **UI control** | `<TextBox Text="{Binding IdleTimeoutMinutes}">` (numeric) |
| **DB column** | `background_sync_settings.idle_timeout_minutes` |
| **Runtime read** | [BackgroundSyncEngine.cs:195](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L195) |
| **Idle source** | Win32 `GetLastInputInfo()` — measures KEYBOARD + MOUSE inactivity at the OS level. Excel/browser/email activity counts as "active" (they generate input) |
| **Effect** | Sync only fires when OS has been idle ≥ this many minutes |
| **Edge case** | If you alt-tab to read a PDF for 20 minutes without moving the mouse, the engine considers you idle |

### Sync even when nothing has changed

| Layer | Detail |
|---|---|
| **UI control** | `<CheckBox IsChecked="{Binding SyncEvenIfNoChanges}">` |
| **DB column** | `background_sync_settings.sync_even_if_no_changes` |
| **Runtime read** | [BackgroundSyncEngine.cs:322](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L322), [BackgroundSyncExternalEventHandler.cs:77](BIManage/Revit/BackgroundSync/BackgroundSyncExternalEventHandler.cs#L77) |
| **Effect (off)** | Engine checks `doc.IsModified`; if false, skip sync. Local model stays as-is until the user edits something |
| **Effect (on)** | Engine still calls sync — pulls down any peer changes published to central since last sync, even if you have nothing to push up |
| **Why it matters** | Crucial in collaborative models — turning this on means you always see the latest from others; turning off saves bandwidth |

### Automatically release element ownership *(Relinquish)*

| Layer | Detail |
|---|---|
| **UI control** | `<CheckBox IsChecked="{Binding EnableRelinquish}">` |
| **ViewModel property** | `EnableRelinquish` (default `true`) |
| **DB column** | `background_sync_settings.enable_relinquish` |
| **Feature toggle mirror** | `"BackgroundRelinquish"` |
| **Runtime read** | [BackgroundSyncEngine.cs:156, 369](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L156) |
| **Important quirk** | Relinquish loop is **independent** of sync — runs even when `IsBackgroundSyncEnabled` is off (line 156 path) |
| **What it actually does** | Calls Revit's `Document.RelinquishOwnership(...)` on user-owned worksets + checked-out elements, then optionally syncs |

### Release after idle (minutes) *(Relinquish)*

| Layer | Detail |
|---|---|
| **UI control** | `<TextBox Text="{Binding RelinquishIntervalMinutes}">` |
| **DB column** | `background_sync_settings.relinquish_interval_minutes` |
| **Runtime read** | [BackgroundSyncEngine.cs:376](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L376) |
| **Effect** | Minimum minutes between standalone relinquishes; also the idle threshold (you must be idle this long) |
| **Difference from `Wait before syncing`** | Relinquish has its own idle counter — using a longer value means relinquish runs less frequently than sync |

### Only sync during specific hours *(Active Hours)*

| Layer | Detail |
|---|---|
| **UI control** | `<CheckBox IsChecked="{Binding EnableSchedule}">` |
| **DB column** | `background_sync_settings.enable_schedule` |
| **Runtime read** | [BackgroundSyncEngine.cs:185, 426](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L185) |
| **Gate** | When `true`, every tick checks `IsInsideSchedule(now)` first — if no, the entire tick skips sync (relinquish unaffected) |

### Start Time / End Time *(Active Hours)*

| Layer | Detail |
|---|---|
| **UI control** | `<TextBox Text="{Binding ScheduleStartTime}">` / `ScheduleEndTime` |
| **Format** | `HH:mm` 24-hour string (validated server-side via `TimeSpan.Parse`) |
| **DB columns** | `schedule_start_time` / `schedule_end_time` (TEXT) |
| **Runtime read** | [BackgroundSyncEngine.cs:430-434](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L430) |
| **Same-day window** | `Start < End` (e.g. `09:00`→`18:00`) → allow sync only if `Start <= now <= End` |
| **Overnight window** | `Start > End` (e.g. `21:00`→`09:00`) → allow sync if `now >= Start OR now <= End` |
| **Edge case — invalid string** | Parse failure → `TimeSpan.Zero` falls back, which means "always allowed" through midnight. Don't trust the engine if a user typed garbage |

### Reduce file size once a day *(Compact Model)*

| Layer | Detail |
|---|---|
| **UI control** | `<CheckBox IsChecked="{Binding CompactModelOnceADay}">` |
| **DB column** | `background_sync_settings.compact_model_once_a_day` |
| **Runtime read** | [BackgroundSyncEngine.cs:403, 415](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L403) |
| **Tracking** | Engine remembers the last calendar date a compact ran. On the next sync of a new day, sets `compact = true` on the call to `BackgroundSyncExternalEventHandler` |
| **What "compact" does** | Passes `SynchronizeWithCentralOptions.Compact = true` to Revit. Revit then re-writes the central file removing deleted geometry blocks |
| **Side effect** | Compact-flagged sync takes ~2–5x longer than a normal sync. Schedule it for off-hours |

### Only during off-hours (12 AM – 6 AM) *(Compact)*

| Layer | Detail |
|---|---|
| **UI control** | `<CheckBox IsChecked="{Binding CompactAtNightOnly}">` (child of "Reduce file size once a day") |
| **DB column** | `background_sync_settings.compact_at_night_only` |
| **Runtime read** | [BackgroundSyncEngine.cs:417](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L417) |
| **Check** | `DateTime.Now.Hour < 6` — so 00:00 through 05:59 |
| **Effect when both true** | Engine sets `compact = true` only if the current hour is also < 6. Outside this window, the daily compact is *deferred* — it'll fire on the next tick that lands inside the window |
| **Child enable logic** | ViewModel sets `CompactAtNightOnly = false` automatically when `CompactModelOnceADay` is unchecked ([SyncSettingsViewModel.cs:178-183](BIManage/ViewModels/SyncSettings/SyncSettingsViewModel.cs#L178)) |

### Close Revit when not in use *(Auto-Close)*

| Layer | Detail |
|---|---|
| **UI control** | `<CheckBox IsChecked="{Binding ExitRevitOnIdle}">` |
| **DB column** | `background_sync_settings.exit_revit_on_idle` |
| **Runtime read** | [BackgroundSyncEngine.cs:450, 452](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L450) |
| **What fires** | After each tick where sync/relinquish is decided, engine checks OS idle time against `ExitRevitAfterMinutes`. If exceeded, raises `AutoExitExternalEvent` |
| **AutoExit behaviour** | Shows a 60-second countdown dialog. User can move mouse to cancel. After timeout, calls `UIApplication.Application.Exit()` |

### Exit after — Hours / Minutes *(Auto-Close)*

| Layer | Detail |
|---|---|
| **UI control** | Two `<TextBox>` controls bound to `ExitAfterHours` and `ExitAfterMins` |
| **ViewModel properties** | `ExitAfterHours` and `ExitAfterMins` are computed properties that decompose/recompose `ExitRevitAfterMinutes`: `Hours = total / 60`, `Mins = total % 60`, setter combines back ([SyncSettingsViewModel.cs:212-230](BIManage/ViewModels/SyncSettings/SyncSettingsViewModel.cs#L212)) |
| **DB column** | `background_sync_settings.exit_revit_after_minutes` (single INTEGER, total minutes) |
| **Runtime read** | [BackgroundSyncEngine.cs:457](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L457) |
| **Default** | 1440 minutes = 24 h |

### Specify what to do with open views *(Open Views dropdown)*

| Layer | Detail |
|---|---|
| **UI control** | `<ComboBox SelectedIndex="{Binding OpenViewsOnSyncMode}">` with 3 items |
| **DB column** | `background_sync_settings.open_views_on_sync_mode` (INTEGER 0/1/2) |
| **Runtime read** | [BackgroundSyncExternalEventHandler.cs:107, 131-140](BIManage/Revit/BackgroundSync/BackgroundSyncExternalEventHandler.cs#L107) |
| **Mode 0 — Keep them open** | No-op. Sync runs with current view state |
| **Mode 1 — Close all views before sync** | Iterates `Document.GetOpenUIViews()`, closes each, leaves a placeholder view open (Revit requires at least one open view) |
| **Mode 2 — Close, sync, reopen** | Same as mode 1, but first stores `View.Id`s into a list. After sync, iterates the list and calls `UIDocument.RequestViewChange(view)` for each ID that still exists |
| **Edge case (mode 2)** | If a view ID no longer exists post-sync (e.g. a peer deleted it), that view silently fails to reopen — no error |
| **Edge case (mode 1/2)** | A view actively being edited (modify-axis tool active) refuses to close. Handler logs and proceeds without that view |

### Prevent from syncing when more than N views are opened

| Layer | Detail |
|---|---|
| **UI control** | `<TextBox Text="{Binding PreventSyncWhenViewsOpenedOver}">` (min 2) |
| **ViewModel guard** | Setter clamps to `Math.Max(2, value)` ([SyncSettingsViewModel.cs:249](BIManage/ViewModels/SyncSettings/SyncSettingsViewModel.cs#L249)) — UI can't go below 2 |
| **DB column** | `background_sync_settings.prevent_sync_when_views_opened_over` |
| **Runtime read** | [BackgroundSyncExternalEventHandler.cs:108, 113-119](BIManage/Revit/BackgroundSync/BackgroundSyncExternalEventHandler.cs#L108) |
| **Check timing** | Runs **inside** the sync ExternalEvent handler, after the engine has already decided to sync. NOT a pre-tick gate |
| **Effect when triggered** | Sync is skipped, timer NOT reset, `SyncSkipped` event raised, log line written |
| **Sample log** | `[BackgroundSync] Skipping sync — 14 views open exceeds limit (10)` |

---

## Save flow — what happens when you click Save

```csharp
// SyncSettingsViewModel.cs:312
public async Task<bool> SaveBackgroundSyncSettingsAsync()
{
    // 1. Build DTO from current ViewModel state
    var settings = new BackgroundSyncSettings { ... }

    // 2. Persist to SQLite — single-row UPSERT on background_sync_settings
    var success = await _syncRepository.SaveBackgroundSyncSettingsAsync(settings);

    // 3. CRITICAL: mirror 4 bools into feature toggles
    if (success && _featureToggleService != null)
    {
        _featureToggleService.SetFeatureEnabled("BackgroundSync",        IsBackgroundSyncEnabled);
        _featureToggleService.SetFeatureEnabled("BackgroundRelinquish",  EnableRelinquish);
        _featureToggleService.SetFeatureEnabled("IdleSync",              EnableIdleSync);
        _featureToggleService.SetFeatureEnabled("SyncQueueControl",      IsBackgroundSyncEnabled);
    }

    StatusMessage = success ? "Settings saved" : "Failed to save settings";
    return success;
}
```

The feature-toggle mirror is essential. The engine's tick gate at [BackgroundSyncEngine.cs:155-156](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs#L155) reads from `IFeatureToggleService`, NOT from SQLite directly — without the mirror, settings changes would require a Revit restart to take effect.

---

## Export / Import — `.ze` file format

```json
{
  "isEnabled": true,
  "syncAllTheTime": false,
  "syncIntervalMinutes": 30,
  "relinquishIntervalMinutes": 60,
  "enableIdleSync": true,
  "idleTimeoutMinutes": 15,
  "enableRelinquish": true,
  "syncOnSave": false,
  "syncEvenIfNoChanges": false,
  "enableSchedule": false,
  "scheduleStartTime": "21:00",
  "scheduleEndTime": "09:00",
  "compactModelOnceADay": false,
  "compactAtNightOnly": false,
  "exitRevitOnIdle": false,
  "exitRevitAfterMinutes": 1440,
  "openViewsOnSyncMode": 0,
  "preventSyncWhenViewsOpenedOver": 10
}
```

Every field is nullable in the import DTO ([SyncSettingsViewModel.cs:519-574](BIManage/ViewModels/SyncSettings/SyncSettingsViewModel.cs#L519)) — fields missing from the import file fall back to the current ViewModel value. This means **you can ship a partial config file** to flip only the bits you care about.

**Import does NOT auto-save.** After clicking Import, the user must click Save explicitly. The status bar shows: `"Imported from filename.ze — click Save to persist"`.

---

## Tick lifecycle — what runs every 30s

```
BackgroundSyncEngine.OnTimerTick()  [BackgroundSyncEngine.cs ~line 130]
│
├─ 1. Feature toggle check
│     ├─ If "BackgroundSync" off AND "BackgroundRelinquish" off  → return
│     └─ ↓ continue
│
├─ 2. Load latest settings from SQLite (every tick)
│     └─ syncRepository.GetBackgroundSyncSettingsAsync()
│
├─ 3. Active Hours window check
│     ├─ If EnableSchedule && !IsInsideSchedule(now)  → skip sync, may still relinquish
│     └─ ↓ continue
│
├─ 4. ShouldSync(model)?
│     ├─ SyncAllTheTime=true:  (now - lastSync) >= SyncIntervalMinutes?
│     ├─ SyncAllTheTime=false: OS-idle >= IdleTimeoutMinutes AND interval elapsed?
│     ├─ doc.IsModified || SyncEvenIfNoChanges  → otherwise skip
│     └─ ↓ if yes, decide compact and raise ExternalEvent
│
├─ 5. Compact decision
│     ├─ CompactModelOnceADay && lastCompactDate != today?
│     ├─ If CompactAtNightOnly: AND DateTime.Now.Hour < 6
│     └─ → pass compact:true to handler
│
├─ 6. ExternalEvent → BackgroundSyncExternalEventHandler.Execute()
│     ├─ Handle Open Views (mode 0/1/2)
│     ├─ Check PreventSyncWhenViewsOpenedOver — may abort
│     ├─ Revit Document.SynchronizeWithCentral(...)
│     └─ Update lastSyncedAt
│
├─ 7. ShouldRelinquish()?
│     ├─ EnableRelinquish? AND (now - lastRelinquish) >= RelinquishIntervalMinutes?
│     └─ → raise BackgroundRelinquishExternalEvent
│
└─ 8. Auto-Exit check
      ├─ ExitRevitOnIdle && OS-idle >= ExitRevitAfterMinutes?
      └─ → raise AutoExitExternalEvent (60s countdown)
```

---

## How to debug a setting

For any setting that "isn't working":

1. **Confirm Save fired** — status bar should say `"Settings saved"`. If not, the SQLite write or feature-toggle mirror failed.
2. **Confirm the DB has your value** — open `%LOCALAPPDATA%\BIManageRevit\Logs\bimanage.db` in DB Browser for SQLite, `SELECT * FROM background_sync_settings`. The column matching your setting should show the new value.
3. **Wait 30s** — the next tick should pick up the change.
4. **Tail the log** — `%LOCALAPPDATA%\BIManageRevit\Logs\BIManageRevit_<today>_*.log`. Grep for `BackgroundSyncEngine` or `BackgroundSync`. You should see lines like:
   ```
   [BackgroundSyncEngine] Triggering sync for <guid> (compact: False, interval: 30min)
   [BackgroundSync] Auto sync skipped — outside scheduled window (21:00–09:00)
   [BackgroundSync] Skipping sync — 14 views open exceeds limit (10)
   ```
5. **If no log entries appear** — the engine isn't ticking. Possible causes:
   - License is in Breached state (check `License module gating applied: ...`)
   - `BackgroundSyncEngine.Started` is false (engine never started on startup — search the log for `[BackgroundSyncEngine] Started`)

---

## Known issues / quirks

### `SyncOnSave` is dead code

The checkbox `_syncOnSave` field exists in the ViewModel but **no runtime consumer reads it**. It persists to SQLite and to `.ze` files but has no effect. Either wire it to `DocumentSavedToLocal` or remove from the UI.

### `PreventSyncWhenViewsOpenedOver` is reactive, not preventive

The check runs *inside* the sync ExternalEvent rather than as a pre-tick gate. A sync may briefly start before being aborted. Move the check into `BackgroundSyncEngine.ShouldSync()` to make it truly preventive.

### Active Hours doesn't gate relinquish

Even when outside the Active Hours window, the relinquish loop still runs (by design — relinquish is independent of sync). If you want to suppress relinquish overnight, you'd have to add a second schedule for relinquish.

### Compact + small window = drift

If `CompactAtNightOnly` is on (00:00–05:59) and Revit isn't open during those hours, the daily compact never runs. The engine doesn't "catch up" on missed compacts — they're simply skipped until the next time Revit is open during the off-hours window.

---

## File map quick reference

| File | Role |
|---|---|
| [BIManage/Views/SyncSettings/SyncSettingsDialog.xaml](BIManage/Views/SyncSettings/SyncSettingsDialog.xaml) | XAML layout — all UI controls and bindings |
| [BIManage/Views/SyncSettings/SyncSettingsDialog.xaml.cs](BIManage/Views/SyncSettings/SyncSettingsDialog.xaml.cs) | Code-behind — wires Save/Import/Export buttons |
| [BIManage/ViewModels/SyncSettings/SyncSettingsViewModel.cs](BIManage/ViewModels/SyncSettings/SyncSettingsViewModel.cs) | All fields + Load/Save + Export/Import + feature-toggle mirror |
| [BIManage/Data/SQLite/SyncRepository.cs](BIManage/Data/SQLite/SyncRepository.cs) | `BackgroundSyncSettings` DTO + Get/Save methods |
| [BIManage/Data/SQLite/Schema.sql](BIManage/Data/SQLite/Schema.sql) | Table definition for `background_sync_settings` |
| [BIManage/Commands/RibbonCommands/SyncSettingsCommand.cs](BIManage/Commands/RibbonCommands/SyncSettingsCommand.cs) | Ribbon button → opens dialog |
| [BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs](BIManage/Revit/BackgroundSync/BackgroundSyncEngine.cs) | The 30s tick — decides what runs |
| [BIManage/Revit/BackgroundSync/BackgroundSyncExternalEventHandler.cs](BIManage/Revit/BackgroundSync/BackgroundSyncExternalEventHandler.cs) | Runs the sync on Revit's UI thread |
| [BIManage/Core/Features/IFeatureToggleService.cs](BIManage/Core/Features/IFeatureToggleService.cs) | The mirror target — engine reads these |

---

## See also

- [sync-settings-user-guide.md](sync-settings-user-guide.md) — user-facing guide with recommended profiles
