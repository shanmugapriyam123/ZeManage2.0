# Sync Settings — Descriptions

Short, plain-language descriptions for every control in the Sync Settings dialog. Use these for tooltips, in-app help, marketing copy, or training material.

For implementation details see [sync-settings-technical-reference.md](sync-settings-technical-reference.md).
For end-user how-to see [sync-settings-user-guide.md](sync-settings-user-guide.md).

---

## What Sync Settings is

Sync Settings controls **how and when** your Revit model automatically syncs with the central file while you work. Instead of relying on you remembering to click *Synchronize with Central*, the background sync engine watches your activity, your schedule, and the state of your model, then syncs on your behalf — quietly, in the background, without interrupting your workflow.

The settings dialog also configures **Relinquish** (releasing checked-out elements so teammates aren't blocked), **Compact** (keeping the central file from bloating over time), **Auto-Close** (shutting Revit down gracefully when you're away), and **Open Views** behaviour (managing memory during sync).

All settings are saved per-machine in a local SQLite database and take effect within 30 seconds — no Revit restart needed.

---

## Section 1 — Background Sync

### Enable Background Sync
> The master switch. Turn this on to let ZeManage handle syncing automatically. Turn it off to disable all automatic sync (you can still sync manually with Revit's Synchronize button).

### Sync Mode — On a schedule
> Sync at fixed intervals (e.g. every 30 minutes) regardless of what you're doing. Best for collaborative projects where everyone needs the latest changes promptly.

### Sync Mode — When not in use
> Wait until you stop using the keyboard and mouse, then sync. Best when you're doing heavy edits and don't want sync slowing you down mid-task.

### Sync every (minutes)
> The minimum gap between syncs. Lower values keep your model fresher but use more network. Default is 30 minutes — most teams use 15–30.

### Wait before syncing (minutes)
> *(Only used in "When not in use" mode.)* How long the keyboard and mouse must be idle before sync fires. Default is 15 minutes — short enough to catch real away time, long enough to avoid syncing during thinking pauses.

### Sync even when nothing has changed
> Normally sync is skipped if you haven't edited anything. Turn this on to sync anyway — useful for pulling in *other people's* changes when you're working in a shared model.

---

## Section 2 — Relinquish

### Automatically release element ownership
> Periodically releases your checked-out elements and worksets back to central, so teammates can edit them without asking you. Independent of background sync — works even when sync is turned off.

### Release after idle (minutes)
> How often to release ownership, and how long you must be idle before a release runs. Default is 60 minutes. Set this longer than your typical edit-cycle so you're not constantly re-borrowing elements.

---

## Section 3 — Active Hours

### Only sync during specific hours
> Restricts automatic sync to a specific time-of-day window. Use this to push all sync activity into evenings or overnight, when license seats and network bandwidth are less contested. Manual syncs are not restricted.

### Start Time / End Time
> The window in 24-hour `HH:mm` format. Supports overnight ranges — set Start to 21:00 and End to 09:00 to allow sync only from 9 PM through 9 AM the next day.

---

## Section 4 — Compact Model

### Reduce file size once a day
> Adds the "Compact Central File" step to one sync per day. This cleans up deleted geometry blocks and reduces the central file's on-disk size. Recommended for large models (>500 MB) that grow steadily.

### Only during off-hours (12 AM – 6 AM)
> *(Child of "Reduce file size once a day".)* Restricts the daily compact to the quiet hours of 00:00–05:59. Useful because a compact sync takes 2–5× longer than a normal sync — keeping it overnight means daytime users never notice the extra time.

---

## Section 5 — Auto-Close Revit

### Close Revit when not in use
> After a long period of OS-level inactivity, gracefully shuts Revit down. You'll see a 60-second countdown that you can cancel by moving the mouse. Pairs well with overnight scheduled-sync setups so the workstation can finally sleep.

### Exit after — Hr / Min
> The OS idle time (combined hours + minutes) that triggers the countdown. Default is 24 hours, meaning Revit won't auto-close until you've been away for a full day.

---

## Section 6 — Open Views

### Specify what to do with open views (dropdown)
- **Keep them open** — Default. Sync runs with all views as they are. Safe and predictable but slower with many heavy views.
- **Close all views before sync** — Closes every view before sync, leaves only a placeholder open. Fastest sync. You'll reopen views manually afterwards.
- **Close, sync, and reopen** — Takes a snapshot, closes all views, syncs, then reopens the same views. Faster sync with the same window layout afterwards.

### Prevent from syncing when more than ___ views are opened
> Hard guard against memory blowups. If you have more than this many views open when an automatic sync is about to run, the sync is **skipped** entirely. Default is 10 views. Manual syncs are always allowed regardless of this limit.

---

## Footer actions

### Export
> Saves your current settings to a `.ze` file (JSON format). Share this file with teammates so everyone uses the same sync profile.

### Import
> Loads settings from a `.ze` file into the dialog. Click **Save** afterwards to make them permanent.

### Save
> Writes your changes to the local database and notifies the background engine immediately — no Revit restart needed. The status bar will confirm "Settings saved".

### Close
> Discards any unsaved changes and closes the dialog.

---

## Tooltip-length versions (one line each)

For UI tooltips where you only have room for a single sentence:

| Control | One-line description |
|---|---|
| Enable Background Sync | Master switch for automatic syncing. |
| On a schedule | Sync at fixed intervals regardless of activity. |
| When not in use | Sync only when you stop using the keyboard and mouse. |
| Sync every (minutes) | Minimum minutes between automatic syncs. |
| Wait before syncing (minutes) | Idle minutes required before sync fires. |
| Sync even when nothing has changed | Sync anyway to pull in others' changes. |
| Automatically release element ownership | Periodically return checked-out elements to central. |
| Release after idle (minutes) | How often to relinquish, and how long to wait. |
| Only sync during specific hours | Restrict automatic sync to a time-of-day window. |
| Start Time / End Time | 24-hour window for allowed sync activity. |
| Reduce file size once a day | Adds Compact Central to one sync per day. |
| Only during off-hours | Restrict the daily compact to 12 AM–6 AM. |
| Close Revit when not in use | Auto-close Revit after a long idle period. |
| Exit after | Idle time threshold before the auto-close countdown. |
| Specify what to do with open views | Keep, close, or close-and-reopen views during sync. |
| Prevent from syncing when more than N views | Skip auto-sync if too many views are open. |
| Export | Save settings to a `.ze` file. |
| Import | Load settings from a `.ze` file. |
| Save | Persist changes — takes effect within 30 seconds. |
| Close | Discard unsaved changes and close. |

---

## Two-sentence "what is this?" intro paragraph

For a Help button or onboarding tooltip on the dialog itself:

> Sync Settings controls how ZeManage automatically syncs your Revit model with central while you work — on a schedule, when you step away, or only during off-hours. It also configures relinquish (releasing elements for teammates), compact (keeping the central file small), auto-close (shutting Revit down gracefully overnight), and open-views handling (faster syncs on large models).
