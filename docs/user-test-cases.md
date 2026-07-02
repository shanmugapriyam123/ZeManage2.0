# BIManageRevit (ZeManage) — User Test Cases

Test cases written from the **Revit user's** perspective — what they expect to see, click, and verify. Group your testers by module. Audiences: **User** = a BIM modeller; **Admin** = a BIM/project manager; **Both** = applies to anyone signed in.

Companion CSV for Excel import: [`user-test-cases.csv`](./user-test-cases.csv).

## Test environment checklist (read first)

Before running ANY test:

1. Revit version installed and licensed: R21 / R22 / R23 / R24 / R25 / R26 / R27 — pick one per pass.
2. ZeManage installed: per-user (`%AppData%\Autodesk\Revit\Addins\<year>\`) **or** all-users (`%ProgramData%\Autodesk\Revit\Addins\<year>\`).
3. Internet access to the staging API.
4. Two user accounts available: one with **Admin** role, one with **Normal user** role.
5. A workshared "Sample Central" model available on a network/local path (for sync + worksets tests).
6. A model with at least three RVT links (for link-loading tests).
7. Optional: a model with 5000+ warnings and 2 GB+ size (for health monitor stress).

## Priorities

| Priority | Meaning | Ship-blocker? |
|---|---|---|
| **P0** | Core flow. If broken, the plugin is unusable. | Yes |
| **P1** | Important feature. If broken, a major workflow is degraded. | Yes |
| **P2** | Nice-to-have / edge case. If broken, minor inconvenience. | No |

---

## 0. Authentication & Identity (cross-module)

Setup for everything else.

| ID | Audience | Function | Priority |
|---|---|---|---|
| TC-AUTH-001 | Both | First-time sign-in | P0 |
| TC-AUTH-002 | Both | Stay signed in across restarts | P0 |
| TC-AUTH-003 | Both | Sign out clears credentials | P0 |
| TC-AUTH-004 | Both | Bad password is rejected | P1 |
| TC-AUTH-005 | Both | Force logout from server | P1 |
| TC-AUTH-006 | Admin | Device registration | P0 |
| TC-AUTH-007 | User  | Device status indicator | P2 |

---

## 1. Protection module

What it does for the user: stops accidents, blocks unauthorised changes, asks for confirmation on risky commands, captures evidence.

### Admin-facing tests

| ID | Function | Priority |
|---|---|---|
| TC-PROT-001 | Open Command Protection settings | P0 |
| TC-PROT-002 | Set **Notify** mode on a command — banner only, command still runs | P0 |
| TC-PROT-003 | Set **Guide** mode on a command — dialog with educational text + Continue | P0 |
| TC-PROT-004 | Set **Prevent** mode on a command — blocked for non-admins | P0 |
| TC-PROT-005 | Pin Protection — protect selected elements | P0 |
| TC-PROT-007 | Generate an OTP to share with a user | P0 |
| TC-PROT-008 | Event Restriction — block save under conditions | P1 |
| TC-PROT-009 | Event Restriction — block CAD import | P1 |
| TC-PROT-010 | Rule Management — create a custom rule | P1 |
| TC-PROT-011 | View audit log of protection events | P0 |
| TC-PROT-012 | Screenshot evidence captured on critical events | P1 |
| TC-PROT-014 | Admin override via password | P0 |

### User-facing tests

| ID | Function | Priority |
|---|---|---|
| TC-PROT-006 | Unpinning a protected element prompts for an OTP | P0 |
| TC-PROT-013 | Clear warning dialog before unpin (element name + category + reason) | P0 |
| TC-PROT-015 | Notify mode shows a lightweight non-modal banner | P1 |

### Test recipe — end-to-end Protection sanity (10 min)

1. Sign in as admin. Open the test model.
2. Pin a few walls. Apply **Protect Pinned** to them.
3. Set the **Delete** command to **Guide** mode.
4. Set the **Sync to Central** command to **Prevent** for non-admins.
5. Sign out, sign in as a normal user.
6. Re-open the model. Try to delete an unprotected wall → should see a Guide dialog. Click Continue → wall deletes.
7. Try to unpin one of the protected walls → should see the OTP dialog.
8. Ask the admin for an OTP via TC-PROT-007. Enter it → unpin succeeds.
9. Try Sync to Central → blocked with "no permission" dialog with override offer.
10. Go to the admin dashboard → verify every step above appears in the audit log with the right user and screenshot.

---

## 2. Activity Tracker module

What it does for the user: records who used Revit, on which model, for how long, and what happened — invisible during normal use.

### User-facing tests

| ID | Function | Priority |
|---|---|---|
| TC-ACT-001 | Session starts on Revit open | P0 |
| TC-ACT-002 | Document open recorded with model GUID | P0 |
| TC-ACT-003 | Document close recorded | P0 |
| TC-ACT-004 | Heartbeat keeps session alive | P1 |
| TC-ACT-007 | Workset opening time tracked (post-open) | P2 |
| TC-ACT-008 | Link loading time tracked (post-open) | P2 |
| TC-ACT-009 | **Worksets dialog still opens** (regression test) | P0 |
| TC-ACT-010 | **Manage Links dialog still opens** (regression test) | P0 |
| TC-ACT-014 | Tracking does NOT slow down Revit | P0 |
| TC-ACT-012 | Session Information dialog (local view) | P2 |

### Admin-facing tests

| ID | Function | Priority |
|---|---|---|
| TC-ACT-005 | Detect crashed session on next launch | P0 |
| TC-ACT-006 | Detect graceful close | P0 |
| TC-ACT-011 | Model Activities dashboard | P1 |
| TC-ACT-013 | Crash Register | P1 |

### Test recipe — crash detection (5 min)

1. Sign in. Open a model. Stay in it for 30 s so heartbeats start flowing.
2. Open Task Manager. End `Revit.exe` (Force kill).
3. Reopen Revit.
4. Within ~10 s of the next session starting, the previous session should appear as **Crashed** in the dashboard (TC-ACT-005).
5. Repeat the test but close Revit gracefully (File → Close → Save) — session should appear as **Closed**, not Crashed (TC-ACT-006).

---

## 3. Health Monitor module

What it does for the user: surfaces problems in the model (warnings, bloat, complexity) before they become a deadline-breaker.

### User-facing tests

| ID | Function | Priority |
|---|---|---|
| TC-HEALTH-001 | Analyze Model on demand | P0 |
| TC-HEALTH-002 | "Other Elements" count is realistic | P1 |
| TC-HEALTH-005 | Sync-save metrics record before/after counts | P2 |
| TC-HEALTH-006 | Cancel a long-running analysis cleanly | P1 |

### Admin-facing tests

| ID | Function | Priority |
|---|---|---|
| TC-HEALTH-003 | Periodic snapshot at scheduled time (02:00 by default) | P1 |
| TC-HEALTH-004 | Health rules / thresholds — flag exceeding models | P2 |
| TC-HEALTH-007 | Project Complexity Score per model | P2 |

### Test recipe — Analyze Model accuracy (5 min)

1. Open a model where you know the exact number of walls / doors / windows.
2. Run Analyze Model.
3. Compare the report's category counts to the known truth (you can verify with Revit's "Select All Instances" + count).
4. Ensure "Other Elements" is in the **thousands**, not millions (regression for the category-walk fix).

---

## 4. Sync Control module

What it does for the user: coordinates multi-user syncs so two people don't sync at the same time and stomp on each other; ALSO must never break native Revit sync.

### User-facing tests

| ID | Function | Priority |
|---|---|---|
| TC-SYNC-001 | **Manual Sync to Central works as normal** (do-no-harm test) | P0 |
| TC-SYNC-003 | Sync Ready dialog with countdown | P1 |
| TC-SYNC-004 | Background sync runs while idle | P2 |
| TC-SYNC-005 | Idle sync respects active commands (sketch mode) | P0 |
| TC-SYNC-007 | Sync queue dialog shows other users | P2 |
| TC-SYNC-008 | Sync conflict resolution prompt | P1 |
| TC-SYNC-009 | Offline sync queue replays when reconnected | P1 |

### Admin-facing tests

| ID | Function | Priority |
|---|---|---|
| TC-SYNC-002 | Sync queue ordering for shared model (two simultaneous users) | P0 |
| TC-SYNC-006 | Sync Settings dialog (toggle background / relinquish / idle) | P1 |
| TC-SYNC-010 | Other users notified when someone syncs | P2 |

### Test recipe — two-user sync race (15 min, needs 2 machines)

1. Both users sign in and open the same workshared central model.
2. Both users make a small edit (different elements).
3. Both click **Sync to Central** within 2 seconds of each other.
4. Verify: User A's sync runs immediately; User B sees the **Waiting in queue** dialog and progresses to their sync only after A finishes.
5. Verify the toast notification for User A appears in User B's plugin and vice versa.

---

## 5. AI module (Ze AI)

What it does for the user: an in-Revit chat assistant for "how do I" questions and live questions about the current model.

### User-facing tests

| ID | Function | Priority |
|---|---|---|
| TC-AI-001 | Open Ze AI chat | P0 |
| TC-AI-002 | Ask a Revit how-to question | P1 |
| TC-AI-003 | Ask about the current model ("how many walls?") | P1 |
| TC-AI-004 | Ask about plugin features ("what does Pin Protection do?") | P1 |
| TC-AI-005 | Chat history persists | P2 |
| TC-AI-006 | Clear chat history | P2 |
| TC-AI-008 | Offline graceful fallback | P1 |

### Admin-facing tests

| ID | Function | Priority |
|---|---|---|
| TC-AI-007 | Admin can switch the AI provider | P2 |

### Test recipe — AI smoke (5 min)

1. Open Ze AI. Ask: "How many walls in this model?"
2. The reply should contain a number; ideally a breakdown by wall type.
3. Ask: "How do I copy elements to multiple levels?"
4. Reply should mention the **Modify** tab → Copy / Paste Aligned commands by name.
5. Ask: "What does Pin Protection do?"
6. Reply should mention admin / OTP / unpinning protected elements.

---

## 6. Add-ons (optional)

What it does for the user: extra utilities that can be installed alongside the main plugin.

### User-facing tests

| ID | Function | Priority |
|---|---|---|
| TC-ADDON-001 | NWC export single view | P1 |
| TC-ADDON-002 | NWC bulk / batch export | P1 |
| TC-ADDON-003 | Link Remapper — repoint moved links | P2 |

### Admin-facing tests

| ID | Function | Priority |
|---|---|---|
| TC-ADDON-004 | Installer checkbox toggles addons | P1 |

---

## 7. Ribbon UI & icons

Cross-module visual checks.

| ID | Audience | Function | Priority |
|---|---|---|---|
| TC-RIBBON-001 | Both  | All ribbon icons load (no blank squares) | P0 |
| TC-RIBBON-002 | Both  | Dark mode icons render correctly | P0 |
| TC-RIBBON-003 | Both  | Theme switch updates icons live (no restart) | P1 |
| TC-RIBBON-004 | Admin | Admin-only buttons hidden for normal users | P0 |
| TC-RIBBON-005 | Admin | Admin buttons appear on role change without restart | P1 |

---

## 8. Installer & licensing

| ID | Audience | Function | Priority |
|---|---|---|---|
| TC-INST-001 | Both | Per-user install (no admin) | P0 |
| TC-INST-002 | Both | All-users install (admin) | P0 |
| TC-INST-003 | Both | Mode switch wipes the other location | P1 |
| TC-INST-004 | Both | Uninstall is clean | P0 |
| TC-INST-005 | Both | Per-Revit-version selection | P0 |
| TC-INST-006 | Both | Auto-detect installed Revit versions | P2 |
| TC-LIC-001 | Admin | Activate license | P0 |
| TC-LIC-002 | User  | Out-of-license fallback (no crash) | P1 |
| TC-LIC-003 | Admin | Seat count visible | P2 |

---

## 9. Support / privacy / shutdown

| ID | Audience | Function | Priority |
|---|---|---|---|
| TC-SUPPORT-001 | User | Report an issue from inside Revit | P1 |
| TC-SUPPORT-002 | User | View privacy policy | P2 |
| TC-SUPPORT-003 | User | View end-user agreement | P2 |
| TC-SHUTDOWN-001 | User | Clean Revit close ends session correctly | P0 |
| TC-SHUTDOWN-002 | User | Survives missing network at shutdown | P1 |

---

## 10. Performance / non-functional

| ID | Audience | Function | Priority |
|---|---|---|---|
| TC-PERF-001 | Both  | Plugin opening overhead < ~60 s | P0 |
| TC-PERF-002 | Both  | Plugin does not freeze the UI | P0 |
| TC-PERF-003 | Admin | Audit log upload is batched | P2 |
| TC-PERF-004 | Both  | Memory does not grow over an 8-hour session | P2 |

---

## How to report a result

For each test case, record in the CSV columns (add these to your spreadsheet):

| Column | Fill in |
|---|---|
| **Result** | Pass / Fail / Blocked / N/A |
| **Tester** | Your name |
| **Date** | YYYY-MM-DD |
| **Revit version** | e.g. 2025 |
| **Plugin version** | shown on the Support dialog |
| **Defect ref** | Link to the bug ticket if it failed |
| **Screenshot / log path** | %LocalAppData%\BIManageRevit\Logs\ |

The CSV file has every row keyed by `ID`; copy it into Excel, add the columns above, and you have a sign-off sheet ready.
