# Sync Settings — User Guide

This document explains every option in the **Sync Settings** dialog (ribbon → ZeManage → Sync Control → Sync Settings) exactly as it appears in the UI, what each control does at runtime, and recommended values.

The dialog is organised into six sections:

1. Background Sync
2. Relinquish
3. Active Hours
4. Compact Model
5. Auto-Close Revit
6. Open Views

Plus footer actions: **Export**, **Import**, **Save**, **Close**.

---

## 1. Background Sync

The master section that controls automatic syncing while you work.

### Enable Background Sync *(checkbox)*

| | |
|---|---|
| **Default** | On |
| **Effect** | Master switch. When **off**, no automatic sync happens — you must sync manually with the Revit Synchronize button. Relinquish still works independently. |
| **Recommended** | On — keeps your local model fresh and reduces conflicts at sync time. |

### SYNC MODE *(two radio buttons)*

Choose **how** sync happens.

#### ● On a schedule — sync at regular intervals
- Syncs automatically every **Sync every (minutes)** minutes regardless of what you're doing.
- Good for users on stable, fast networks.

#### ● When not in use — sync when you step away
- Waits until you stop using the keyboard/mouse for **Wait before syncing (minutes)** minutes, then syncs.
- Good for heavy-edit sessions where you don't want sync slowing you down mid-edit.

> Only one of the two modes is active at a time. The fields under each mode only matter when that mode is selected.

### Sync every (minutes)

| | |
|---|---|
| **Default** | 30 |
| **Used by** | "On a schedule" mode |
| **Effect** | Minimum minutes between automatic syncs. Setting to `1` means try to sync every minute (heavy — only for small models). |
| **Recommended** | 15–30 for typical models; 5–10 for small models. |

### Wait before syncing (minutes)

| | |
|---|---|
| **Default** | 15 |
| **Used by** | "When not in use" mode |
| **Effect** | How long the keyboard/mouse must be idle before a sync fires. Setting `1` = sync after just 1 minute of inactivity. |
| **Recommended** | 10–15. Lower values can interrupt you when you pause to think. |

### Sync even when nothing has changed *(checkbox)*

| | |
|---|---|
| **Default** | Off |
| **Effect** | When **on**, syncs run on schedule even if you haven't edited anything — this *pulls* other users' changes into your local copy. When **off**, sync is skipped if your local model has no edits. |
| **Recommended** | **On** for collaborative models where you need to see others' changes promptly. **Off** if you mostly work solo on your slice. |

---

## 2. Relinquish

A second loop that runs alongside sync. **"Relinquish"** means giving up ownership of worksets/elements you've checked out, so other users can edit them without a manual workset borrow.

### Automatically release element ownership *(checkbox)*

| | |
|---|---|
| **Default** | On |
| **Effect** | When **on**, periodically releases your editable elements back to central. When **off**, you keep checked-out elements until you explicitly relinquish or sync. |
| **Recommended** | **On** for shared-model workflows — reduces "X has elements checked out" blocking for teammates. |

### Release after idle (minutes)

| | |
|---|---|
| **Default** | 60 |
| **Effect** | Minimum minutes between relinquish operations. Also acts as the idle threshold — you must be idle this long before a relinquish runs. |
| **Recommended** | 30–60. Too short interrupts your workflow (you'd need to re-borrow elements you're actively editing); too long leaves elements locked. |

---

## 3. Active Hours

Restricts ALL automatic sync activity to a specific time-of-day window.

### Only sync during specific hours *(checkbox)*

| | |
|---|---|
| **Default** | Off |
| **Effect** | When **on**, no automatic sync runs outside the Start/End window. When **off**, sync runs whenever the other rules allow. |
| **Recommended** | **On** for shared-license environments where overnight syncs are preferred. **Off** for normal daytime working. |

### Start Time / End Time (HH:mm)

| | |
|---|---|
| **Default** | 21:00 → 09:00 (overnight) |
| **Format** | 24-hour `HH:mm` |
| **Effect** | The window during which automatic sync is allowed. |
| **Overnight support** | If Start > End (e.g. `21:00`–`09:00`), the window is treated as crossing midnight (9 PM through 9 AM the next day). |
| **Same-day window** | If Start < End (e.g. `09:00`–`18:00`), the window is the same calendar day (9 AM to 6 PM). |

> Manual syncs (clicking Revit's Synchronize button) are NOT restricted by Active Hours — only the automatic background loop is gated.

---

## 4. Compact Model

Periodically reduces the central file's on-disk size by purging deleted geometry and unused references. Adds a few seconds to one sync per day.

### Reduce file size once a day *(checkbox)*

| | |
|---|---|
| **Default** | Off |
| **Effect** | When **on**, exactly one sync per calendar day is performed with the "Compact Central File" option enabled. Resets at local midnight. |
| **Recommended** | **On** for large central files (>500 MB) that grow steadily; **off** for small/young models. |

### Only during off-hours (12 AM – 6 AM) *(checkbox, child of above)*

| | |
|---|---|
| **Default** | Off |
| **Effect** | When **on**, the daily compact is restricted to 00:00–05:59 local time. Outside that window the compact is deferred to the next night. |
| **Recommended** | **On** when paired with Active Hours overnight sync — compact runs during quiet hours so daytime users never notice the extra sync time. |

> This option only matters when **Reduce file size once a day** is on.

---

## 5. Auto-Close Revit

Closes Revit cleanly after a long period of inactivity. Pairs well with overnight scheduled syncs.

### Close Revit when not in use *(checkbox)*

| | |
|---|---|
| **Default** | Off |
| **Effect** | When **on**, after the Exit-after time of OS-level inactivity, Revit shows a 60-second countdown dialog and then closes. The countdown can be cancelled by moving the mouse/keyboard. |
| **Recommended** | **On** for overnight unattended sessions; **off** if you frequently step away and want Revit to stay open. |

### Exit after — Hours / Minutes

| | |
|---|---|
| **Default** | 24h 0m (1440 minutes) |
| **Effect** | The combined value is stored as a single minutes integer. `24h 0m` = no exit until you've been idle for a full day. `2h 30m` = exit after 2.5 hours idle. |
| **Recommended** | 12h–24h for overnight scenarios. Don't go below 1h or Revit may close while you're at lunch. |

---

## 6. Open Views

Controls what happens to the views you currently have open when an automatic sync starts. Open views consume memory and slow down syncs significantly on large models.

### Specify what to do with open views *(dropdown)*

Three options:

| Option | What it does | When to use |
|---|---|---|
| **Keep them open** *(default)* | No change — sync runs with all views as-is. | Default; safe and predictable. |
| **Close all views before sync** | Closes every open view before sync, leaves a minimal placeholder view open. After sync you'll need to reopen the views you want. | Large models where you don't mind reopening views after sync. |
| **Close, sync, and reopen** | Takes a snapshot of which views are open, closes them all, syncs, then reopens the same views. | Large models where you want a faster sync but the same window layout afterwards. |

> ⚠️ "Close, sync, and reopen" depends on Revit successfully reopening the saved views. Some view types (3D from selection sets, working views) may not reopen identically.

### Prevent from syncing when more than ___ views are opened

| | |
|---|---|
| **Default** | 10 |
| **Minimum** | 2 |
| **Effect** | Hard guard: if you have more than this many views open when a sync is about to run, the automatic sync is **skipped** and the timer is **not** reset. The skip is logged; nothing else happens. |
| **Recommended** | 10 for typical workflows; lower (4–6) for memory-constrained machines. |

> This guard only blocks **automatic** background syncs. Manual syncs via Revit's Synchronize button are always allowed.

---

## Footer actions

### Export

Saves the current settings to a `.ze` file (JSON). Use this to share a recommended config across the team.

### Import

Loads settings from a `.ze` file. **You must click Save afterwards** to persist them — the status bar will say "Imported from filename.ze — click Save to persist".

### Save

Writes the current dialog values to the SQLite settings table **and** mirrors four feature toggles (`BackgroundSync`, `BackgroundRelinquish`, `IdleSync`, `SyncQueueControl`) so the running background engine picks up changes within ~30 seconds — **no Revit restart needed**.

### Close

Discards unsaved changes and closes the dialog.

---

## How the settings work together

Every 30 seconds, the background engine asks (in this order):

1. Is `Enable Background Sync` on? → If no, skip everything except Relinquish.
2. Is now inside the **Active Hours** window? → If no, skip sync.
3. **Sync mode** — am I in continuous mode, or do I need OS idle?
   - Continuous: has it been ≥ `Sync every` minutes since the last sync?
   - Idle: have I been keyboard/mouse-idle for ≥ `Wait before syncing` minutes AND ≥ `Sync every` minutes since last sync?
4. Does the doc have changes? → If no AND `Sync even when nothing has changed` is off, skip.
5. Are too many views open? → If `> Prevent from syncing when more than N`, skip and log.
6. Is today's compact still owed (and we're inside the off-hours window if `Only during off-hours` is on)? → If yes, run sync with `compact: true`. Otherwise normal sync.
7. Then handle **Open Views** mode: keep them open, close, or close+reopen.
8. After sync completes, check if it's time to relinquish (separate cycle controlled by Relinquish settings).
9. Finally, check OS idle time against `Auto-Close Revit` threshold → close Revit if exceeded.

---

## Recommended profiles

### "Default office user"
- Enable Background Sync ✅
- Mode: On a schedule, 30 min
- Sync even when nothing has changed ✅
- Relinquish ✅ at 60 min
- Active Hours ❌
- Compact ❌
- Auto-Close ❌
- Open Views: Keep them open
- Prevent at: 10

### "Heavy editor (don't interrupt me)"
- Enable Background Sync ✅
- Mode: When not in use, 15 min idle
- Sync even when nothing has changed ❌
- Relinquish ✅ at 30 min
- Active Hours ❌
- Compact ❌
- Auto-Close ❌
- Open Views: Keep them open
- Prevent at: 10

### "Overnight unattended (compact + auto-close)"
- Enable Background Sync ✅
- Mode: On a schedule, 60 min
- Sync even when nothing has changed ✅
- Relinquish ✅ at 60 min
- Active Hours ✅ 21:00 → 09:00
- Compact ✅ Off-hours only ✅
- Auto-Close ✅ 12h
- Open Views: Close, sync, reopen
- Prevent at: 10

---

## Storage location

Settings are persisted to:

```
%LOCALAPPDATA%\BIManageRevit\Logs\bimanage.db
  └─ table: background_sync_settings  (single row, key=user)
```

The same row is exported when you click **Export** and re-imported by **Import**.

## Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| Settings save but nothing changes | Background engine not picking up new values | Confirm the Save button shows "Settings saved" in the status bar; otherwise the feature-toggle mirror didn't fire — try again |
| Background sync never runs | Active Hours window doesn't include current time, or License is in Breached state | Check Active Hours; check License Status in the ribbon |
| Sync runs but compact never happens | `Only during off-hours` is on and you tested during the day | Test between 00:00–05:59, or temporarily disable the off-hours restriction |
| Auto-Close never fires | OS idle resets every time Revit's own timers fire (rare); or `Exit after` is set higher than your test window | Check Windows idle time matches your expectation; lower `Exit after` for testing |
| "Open views" close+reopen leaves views missing | Some Revit view types can't be reopened from a session snapshot | Use "Close all views before sync" instead, and reopen manually |

For deeper diagnosis, check `%LOCALAPPDATA%\BIManageRevit\Logs\BIManageRevit_<date>_*.log` and grep for `BackgroundSyncEngine`.
