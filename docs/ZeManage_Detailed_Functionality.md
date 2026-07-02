# ZeManage — Detailed Functionality & Importance Guide

**BIM Governance Plugin for Autodesk Revit**
*By ZestineTech | Supports Revit 2021–2026*

---

## Why ZeManage Exists

BIM projects involve multiple users working simultaneously on shared Revit models. Without governance, critical elements get accidentally deleted, models become bloated, non-compliant files get opened, and there is no audit trail when something goes wrong. ZeManage fills that gap by sitting inside Revit as a plugin that intercepts, monitors, and governs every significant action — before or after it happens — while syncing everything to a cloud backend for company-wide visibility.

---

## Table of Contents

1. [Protection System](#1-protection-system)
   - [Pin Protection](#11-pin-protection)
   - [Command Protection](#12-command-protection)
   - [Event Restriction](#13-event-restriction)
   - [Rule-Based Protection](#14-rule-based-protection)
2. [Activity Tracker](#2-activity-tracker)
3. [Health Monitor](#3-health-monitor)
4. [Sync Control](#4-sync-control)
5. [Manage Projects](#5-manage-projects)
6. [AI Assistant — Ze AI](#6-ai-assistant--ze-ai)
7. [Addons](#7-addons)
8. [Authentication & Roles](#8-authentication--roles)
9. [Data & Sync Architecture](#9-data--sync-architecture)
10. [Audit & Compliance](#10-audit--compliance)
11. [Settings Import/Export](#11-settings-importexport)

---

## 1. Protection System

The protection system is ZeManage's core feature. It intercepts Revit user actions at the API level using Revit's command binding mechanism (the "command interception pattern"). Every intercepted action is evaluated against rules and settings before being allowed, guided, or blocked.

### Three Protection Modes

All protection features share the same three-level escalation scale:

| Mode | Level | What Happens |
|------|-------|--------------|
| **Notify** | 1 — Passive | Action is allowed. Event is silently logged to the audit trail. The user sees nothing different. |
| **Assist** | 2 — Guided | A guidance dialog appears explaining the concern. The user can choose to proceed or cancel. Their decision is logged. |
| **Protect** | 3 — Blocked | The action is fully blocked. A dialog appears requiring an admin OTP or admin password to override. Override is audit-logged with the override method. |

When multiple rules match the same action, the highest mode wins (Protect > Assist > Notify).

---

### 1.1 Pin Protection

**What it does:**
Pin Protection lets administrators mark individual Revit elements as protected. Once protected, a normal user cannot unpin, move, delete, or modify that element without authorization.

**Why it matters:**
In large federated models, structural grids, reference levels, site boundaries, and shared coordination elements are pinned for a reason. Without enforcement, a junior user can accidentally unpin and move a grid line that hundreds of other elements depend on, causing cascading errors across every linked model in the project.

**How it works — step by step:**

1. An admin selects an element and clicks Pin in Revit.
2. ZeManage's `PinCommandBinding` intercepts the pin action and opens an admin configuration dialog.
3. The admin configures: protection mode (Notify/Assist/Protect), password requirement, screenshot rules, and notification settings.
4. The configuration is saved to two places: Revit's **ExtensibleStorage** (embedded in the model file itself) and the local **SQLite database** (for audit and sync).
5. When any user later tries to unpin that element, `UnpinCommandBinding` fires.
6. The system evaluates the configured protection mode:
   - **Notify**: logs silently, allows unpin.
   - **Assist**: shows a warning dialog, user decides.
   - **Protect**: blocks the unpin entirely. If the user enters a valid OTP or admin password, the override is logged and the action can proceed. After the operation, the element is automatically re-pinned to its protected state.
7. The entire event — including who did what, on which element, at what time, and whether it was overridden — is written to the audit log and synced to the server.

**Key technical detail:** Pin metadata is stored in Revit's ExtensibleStorage (inside the `.rvt` file), meaning the protection travels with the model even if the SQLite database is not present. The SQLite copy is for audit and sync purposes.

---

### 1.2 Command Protection

**What it does:**
Command Protection intercepts specific Revit commands (Delete, Move, Rotate, Mirror, Copy, Cut, Group, Ungroup, Unpin) when they are applied to protected elements or under defined conditions.

**Why it matters:**
Revit's native tools do not distinguish between "safe" and "protected" elements. A user pressing Delete selects everything and removes it. Command Protection adds a layer that checks the target element against protection rules before the action completes, making it possible to allow the same command on some elements while blocking it on others.

**How it works:**

ZeManage uses a dual-binding strategy:

- **Individual bindings** for commands that need special handling (Pin, Unpin, Delete, Move, Rotate, Mirror, Group): each gets its own binding class with custom pre- and post-execution logic.
- **Centralized handler** for all other commands: a single `BeforeExecuted` event handler evaluates them using the rule engine.

Deduplication logic (`GetIndividuallyBoundCommandIds()`) ensures no command is bound twice, which would cause double-evaluation and double dialogs.

**Event timing:**

| Command | BeforeExecuted | Executed |
|---------|---------------|----------|
| Move, Rotate, Mirror | Yes — cancel before action | No |
| Pin, Unpin | No | Yes — post-execution protection workflow |
| Delete | Yes AND Yes — rule check before, confirm after | Both |
| All others | Yes — rule evaluation and cancellation | No |

**Why Delete uses both events:** Delete is irreversible. The pre-execution check evaluates rules against the selected elements. If blocked, `e.Cancel = true` prevents Revit from executing at all. The post-execution event confirms the deletion and logs the final state.

---

### 1.3 Event Restriction

**What it does:**
Event Restriction governs Revit document-level events that happen outside the scope of individual element commands — things like opening a file from a non-approved location, performing a model upgrade, importing a CAD file, or loading a family from an unapproved source.

**Why it matters:**
Many BIM compliance failures happen not through element edits but through file-level operations. Opening a central file directly (instead of creating a local copy) corrupts collaboration workflows. Importing and exploding DWG files embeds foreign geometry that inflates file size and creates warnings. Model upgrades can lock out team members on older Revit versions. Event Restriction catches all of these.

**Supported event types and their default protections:**

| Revit Event | Default Protections |
|-------------|---------------------|
| `DocumentOpening` | Duplicate username detected; Opening central file directly; Model version upgrade |
| `DocumentSaving` | Saving over an earlier file version |
| `DocumentPrinting` | Document printing restriction |
| `FamilyLoadingIntoDocument` | Loading family from a non-approved source location |
| `CommandProtection` | Export operations; Transfer project standards; CAD import/explode |
| `DocumentChanged` | RVT link pin-status prompt |

Each event protection has its own mode (Notify/Assist/Protect), its own set of enabled conditions, and its own configuration JSON stored in the `event_protection` SQLite table and synced to the server.

---

### 1.4 Rule-Based Protection

**What it does:**
Rule-Based Protection is the most flexible layer. Admins define rules that target elements by category, family name, type name, or parameter values, and apply those rules to specific commands. Rules can be scoped to the entire company, a specific project, or a single model.

**Why it matters:**
One-size-fits-all protection is not enough for real projects. A structural engineer needs to protect all structural columns from being deleted but allow architects to move furniture freely. Rule-Based Protection makes it possible to express that logic precisely without writing code.

**Rule anatomy:**

| Field | Purpose |
|-------|---------|
| `Name` / `Description` | Human-readable identification |
| `Mode` | Notify (1), Assist (2), or Protect (3) |
| `Priority` | Higher numbers are evaluated first |
| `IsEnabled` | Toggle without deleting |
| `RuleScope` | CompanyWide (1), ProjectWide (2), ModelSpecific (3) |
| `CategoryId` | Target a Revit BuiltInCategory (e.g., structural columns) |
| `FamilyName` / `TypeName` | Narrow to specific families or types |
| `Parameters` | Condition-based matching on custom parameters |
| `BuiltInParameters` | Condition-based matching on built-in Revit parameters |
| `CommandIds` | Which Revit commands this rule applies to |
| `CaptureBeforeScreenshot` | Take screenshot before action |
| `CaptureAfterScreenshot` | Take screenshot after action |
| `RequireComment` | Force user to explain their reason |
| `AllowAdminOverride` | Whether OTP/password override is permitted |
| `SendEmail` | Notify admins by email when rule fires |
| `Message` | Custom message shown to user in dialog |

**Condition operators for parameter matching:**
`Equals`, `NotEquals`, `Contains`, `StartsWith`, `GreaterThan`, `LessThan`, `IsEmpty`, `IsNotEmpty`

**Rule evaluation flow:**

1. A Revit command fires and ZeManage intercepts it.
2. `RuleService.EvaluateRules()` is called with the element(s) and command ID.
3. The `HybridRuleCacheManager` returns matching rules from memory (fast lookup, no SQLite hit on hot path).
4. `RuleEvaluator.Evaluate()` checks each rule: does the element's category match? Does the family/type match? Do parameter conditions pass?
5. All matching rules are collected. The `AllRulesApplyResolver` picks the highest protection mode among them.
6. A `RuleEvaluationResult` is returned: `FinalMode`, `MatchedRules`, `ShouldBlock`, combined message.
7. The intervention handler shows the appropriate dialog (nothing / guidance / block).

**Conflict detection:**
When two rules overlap on the same category and commands but have different modes, `RuleService.DetectConflicts()` flags this. The conflict is persisted to the database and visible to admins for review. Resolution always favors the stricter mode.

**Rule scoping:**
- **CompanyWide**: Applied to every project in the company. Useful for universal policies (e.g., "never delete structural grids").
- **ProjectWide**: Applied to all models in a specific project.
- **ModelSpecific**: Applied only to one registered model.

Scopes stack — a model can be governed by rules from all three levels simultaneously.

---

## 2. Activity Tracker

**What it does:**
The Activity Tracker records and displays all Revit session information, model change history, and crash events. It answers the question: "Who was in this model, when, and what did they do?"

**Why it matters:**
On collaborative projects, understanding session history is critical for debugging, compliance, and team coordination. When a model breaks, teams need to know who was working in it and when. When a crash occurs, they need the history to attempt recovery.

**Session Information:**
Every time Revit opens a model, a new session record is created with:
- `SessionId` — unique GUID
- `ModelGuid` — which model
- `CentralModelPath` — exact file path
- `UserName` / `ComputerName` — who and where
- `RevitVersion` — which Revit build
- `StartTime` / `EndTime` — session duration
- `Status` — Active, Closed, or Crashed

Sessions are written to SQLite immediately and synced to the server in the background.

**Crash Detection:**
`RevitJournalCrashDetector` analyzes Revit's journal file on startup. If the previous session's journal did not contain a normal close sequence, the session record is marked as `Crashed`. Admins are notified, and the session status is flagged in the activity view. This is critical for understanding data loss risks after unexpected terminations.

**Model Activities (Real-Time):**
The Model Activities panel shows live presence — who else is currently in the same model. It subscribes to SignalR events:
- `UserJoined` / `UserLeft` — real-time presence
- `ActiveUsersUpdated` — full refresh of active user list
- Chat messages scoped to the model, project, or direct message

This turns ZeManage into a lightweight collaboration hub inside Revit itself, reducing the need to switch to external tools to see who is working on what.

---

## 3. Health Monitor

**What it does:**
The Health Monitor continuously tracks model file health metrics and presents them in a dashboard with a performance grade (A–F), health alerts, and historical trend charts. It identifies problems before they become critical — file size creep, warning count growth, disconnected elements, and oversized families.

**Why it matters:**
Revit model degradation is cumulative and invisible until it causes problems. A model that accumulates imported DWGs, in-place families, excessive warnings, and oversized components will slow down for everyone and eventually become unstable. The Health Monitor provides objective, data-driven health tracking so teams can act before performance collapses.

**Three metric collection tiers:**

**Fast Metrics** — collected automatically on every sync/save (~20 metrics):
- File size
- Levels, grids, design options count
- Linked DWG, imported DWG, RevitLinks count
- Families, types, instances count
- Parameters and shared parameters
- Views, sheets, annotations
- Worksets and groups
- Total warnings count

**Medium Metrics** — collected on daily snapshots (~10 metrics):
- File size history trend over time
- Workset sync times
- Recently modified elements
- Orphan elements
- Large families above size threshold
- Unused families and parameters
- Dependency chain length
- Library conflicts

**Expensive Metrics** — run on demand only (~2 analyses):
- Complete element tree analysis (counts by every category, family, and type)
- Parameter correlation analysis (duplicates, unused, conflicts)

These are separated by cost because some analyses require iterating over every element in the model, which can take minutes on large files. Running them automatically would block the user.

**Health Alerts:**
`ModelHealthAlertService` evaluates collected metrics against thresholds and generates alerts:

| Category | Severity Levels |
|----------|----------------|
| FileSize | Info → Warning → Critical |
| Performance | Info → Warning → Critical |
| Quality | Info → Warning → Critical |
| Capacity | Info → Warning → Critical |

Alerts are shown in the dashboard and can be fed to the Ze AI assistant for contextual diagnosis.

**Performance Grade:**
The dashboard calculates an A–F grade based on weighted metric scores. This single number gives project managers an instant health signal without needing to understand every metric.

**Snapshot History:**
Every `ModelFileMetricsSnapshot` is stored in SQLite and synced to the server. This enables trend charts — seeing whether warnings are increasing week over week, or whether file size grew after a specific sync event.

---

## 4. Sync Control

**What it does:**
Sync Control automates Revit's worksharing synchronization. Instead of relying on users to manually sync at appropriate intervals, ZeManage triggers syncs in the background based on time intervals, idle detection, and configurable schedule windows.

**Why it matters:**
In worksharing environments, infrequent syncing causes two problems: large sync conflicts when multiple users have accumulated many changes, and loss of work if Revit crashes between manual syncs. Automatic sync in the background, especially during idle periods, dramatically reduces both risks without interrupting active work.

**Sync Modes:**

| Mode | Behavior |
|------|----------|
| **Always On** (`SyncAllTheTime = true`) | Syncs on a fixed timer interval regardless of user activity |
| **When Paused** (`SyncAllTheTime = false`) | Syncs only when the user has been idle long enough (no keyboard/mouse) |

**Configurable Settings:**

| Setting | Purpose |
|---------|---------|
| `IsBackgroundSyncEnabled` | Master on/off switch |
| `SyncIntervalSeconds` | How often the sync timer fires |
| `IsAutoRelinquishEnabled` | Automatically release workset ownership when idle |
| `RelinquishIntervalSeconds` | Idle time before relinquishing |
| `IsAutoExitEnabled` | Close Revit after extended idle period |
| `IdleTimeMinutes` | How long until auto-exit fires |
| `DisabledScheduleWindows` | Time windows when auto-sync is suppressed |

**Model Compaction:**
On the first sync of each calendar day, `BackgroundSyncEngine` can optionally compact the model (equivalent to "Compact Central Model" in Revit's Synchronize dialog). This is the primary mechanism for keeping central model file sizes manageable over the life of a project.

**Sync Traffic Control:**
`SyncTrafficControlService` prevents simultaneous syncs on the same document. If a sync is already in progress when the timer fires, the new request is queued rather than proceeding in parallel. Parallel sync attempts in Revit produce conflict dialogs that interrupt users and can corrupt worksharing state.

**Auto-Relinquish:**
When a user leaves their desk (idle), their checked-out worksets prevent others from editing those areas. Auto-relinquish releases ownership automatically, freeing up the model for colleagues. This is especially important in global teams where different time zones overlap.

**Sync Queue Panel:**
The Sync Queue shows a live list of pending and completed sync operations — model name, status, timestamp, and duration. Users can manually trigger or inspect sync history.

---

## 5. Manage Projects

**What it does:**
Manage Projects connects a local Revit model to the ZeManage cloud backend by registering it with a stable GUID identity. This is the prerequisite for protection, audit, and health features to function server-side. It also provides the OTP generator tool for admins.

**Why it matters:**
Revit models are files with paths that change. Users rename folders, move projects to new servers, and archive models. A path-based identity breaks. ZeManage uses a GUID embedded in the model (Revit's own project GUID) as the stable identity, ensuring that all audit history, protection rules, and health metrics remain attached to the model regardless of where it lives on disk.

**Model Registration Flow:**

1. Admin opens the model and clicks "Register Model."
2. ZeManage captures: model GUID, central model path, model name, project name, company name.
3. Data is saved locally to SQLite's `registered_models` table.
4. `ModelSyncService` calls `POST /api/v1/Revit/models/register` to register on the server.
5. Server confirms and activates protection for that model.
6. The Ribbon button changes to "Model Registered" with a green icon confirming success.

After registration, rules can be targeted to this model, protection syncs down to it, and health metrics from it are attributed correctly in the dashboard.

**OTP Generator:**
One-Time Passwords are the mechanism by which admins grant temporary override access without sharing a permanent password.

**OTP Generation Flow:**
1. Admin opens the OTP Generator (requires admin authentication and ProfileId).
2. ZeManage calls `POST /api/v1/Revit/otp/generate` with the admin's identity.
3. Server generates a 6-digit code, valid for 5 minutes.
4. Admin shares the code verbally or via message with the user who needs to override a block.
5. User enters the code in the protection dialog.
6. ZeManage calls `POST /api/v1/Revit/otp/validate` — code is consumed on first use (one-time).
7. Audit log records `OverrideMethod = "OTP"` with the admin's identity.

**Why OTP matters over shared passwords:** OTPs expire and are single-use, so they cannot be shared, written down, and reused. Every use is traceable to the admin who generated it and the user who consumed it.

---

## 6. AI Assistant — Ze AI

**What it does:**
Ze AI is a context-aware chat assistant embedded directly in Revit. It answers questions about the active model's health, Revit workflows, BIM best practices, and ZeManage features — using live model metrics and audit data as context.

**Why it matters:**
BIM managers and project leads spend significant time answering recurring questions: "Why is our file so big?", "What are all these warnings?", "How do I safely reload a linked model?". Ze AI puts that knowledge inside the tool where the question arises, reducing context-switching and supporting users in the moment they need help.

**How it works:**

Ze AI uses an OpenAI GPT-4o-mini backend (configurable), but enriches every query with live data:

1. On first use in a session, `ModelContextService.GetModelContext()` fetches live metrics from the backend API (file size, element counts, health alerts).
2. Health alerts from `ModelHealthAlertService` are fetched and cached.
3. Local database data (audit logs, session history, crash records) is queryable via `LocalDbQueryService`.
4. Before each user message is sent to OpenAI, the live model context is injected as a system block.
5. The last 6 messages of conversation history are included (with assistant messages compressed to 300 characters to manage token costs).
6. OpenAI returns a response, which is shown in the chat panel.
7. The conversation is saved to SQLite's `chat` table, persisting across Revit sessions.

**Intent Classification:**
`IntentClassifier` routes queries to the appropriate knowledge source:
- BIM best practices
- Revit how-to questions
- ZeManage product questions
- Model performance diagnosis
- Audit/activity queries

**Chat Features:**
- **Follow-up suggestions**: AI surfaces related questions the user might ask next.
- **Feedback**: Thumbs up/down on responses helps improve quality over time.
- **Export**: Full conversation can be exported to JSON or CSV.
- **Graceful degradation**: If the API is unavailable, Ze AI still works with locally cached context.

---

## 7. Addons

### 7.1 NWC Export

**What it does:**
Batch-exports 3D Revit views to Navisworks NWC format across multiple models in a single operation, with granular control over export settings and optional cloud-based source files from Autodesk Platform Services (APS).

**Why it matters:**
Coordination workflows depend on Navisworks clash detection, which requires up-to-date NWC files from all discipline models. Manually exporting each model one-by-one is error-prone and time-consuming. NWC Export batches the operation and stores repeatable settings so it can be run consistently on every coordination cycle.

**Export options:**

| Option | Purpose |
|--------|---------|
| Convert Element Properties | Include Revit parameter data in NWC |
| Convert Lights | Include light sources |
| Convert Linked CAD Formats | Include DWG/DXF linked into the model |
| Divide File into Levels | Split NWC output by building level |
| Export Element IDs | Preserve Revit element IDs for traceability |
| Export Links | Include Revit-linked models |
| Export Parts | Include parts (split elements) |
| Export Room as Attribute | Attach room data to elements |
| Export Room Geometry | Include room bounding geometry |
| Find Missing Materials | Flag unmapped materials |
| Internal Coordinates | Use internal coordinate system instead of shared |
| Faceting Factor | Mesh resolution for curved surfaces |

**Source files:**
- **Local files**: Pick Revit files from disk.
- **Cloud files**: Browse APS-connected BIM 360/ACC folders and select specific model versions. Uses OAuth 2.0 authentication against APS.

**Output:**
All selected models and views are exported to a configurable local folder with progress tracking and per-file error handling.

---

### 7.2 Link Remapper

**What it does:**
Lists all Revit links in the active model, shows their load status, and allows remapping them to different file versions — either from local paths or from APS cloud versions.

**Why it matters:**
Revit links break when files are moved, renamed, or archived. On projects with dozens of linked models, manually reloading each broken link through Revit's Manage Links dialog is tedious. Link Remapper surfaces all links at once, lets users filter and bulk-select, and remaps them in a single operation.

**Link status visibility:**

| Status | Meaning |
|--------|---------|
| Loaded | Currently loaded and resolved |
| Unloaded | Link exists but is not loaded |
| Missing | File cannot be found at stored path |
| Nested | Link inside another linked model |

**Remapping workflow:**
1. Open Link Remapper — all links are listed with their current path and status.
2. Filter by name or path to find specific links.
3. Select one or multiple links.
4. Choose a replacement version: local file path or APS cloud version.
5. Apply — Revit reloads all selected links from the new paths.

---

## 8. Authentication & Roles

**What it does:**
ZeManage uses a dual-authentication system: admin sign-in with company credentials for governance and OTP operations, and device registration with a license key for background sync and protection enforcement. Role-based access control (RBAC) restricts which features each user can access.

**Why it matters:**
Governance tools must themselves be governed. If any user could change protection settings, the system provides no real protection. Authentication ensures that rule changes, OTP generation, and model registration are restricted to authorized personnel, while normal users benefit from the protections without being able to circumvent them.

**User Roles:**

| Role | Access Level |
|------|-------------|
| **Company Administrator** | Full access: rules, events, command settings, OTP generation, model registration, web dashboard |
| **Project Administrator** | Access to assigned projects only: rules and settings for their projects |
| **Normal User** | Protection enforced, no configuration access, can request OTP override |

RBAC is enforced via `ICommandAvailability` interfaces on Ribbon buttons. Project admin-only commands return `false` from `IsCommandAvailable()` for normal users, making the buttons appear grayed out or hidden.

**Admin Sign-In Flow:**
1. Admin clicks Sign In, enters company email and password.
2. `AuthApiService` calls `POST /api/v1/tenant/company-auth/login`.
3. Server returns: access token, refresh token, companies list, user info, ProfileId.
4. `AuthTokenManager` stores tokens in memory with `IsAdminSession = true`.
5. ProfileId is stored — required for OTP generation.
6. Ribbon buttons update based on returned role.

**Device Registration Flow:**
1. User/admin clicks Register Device, enters license key.
2. `AuthApiService` calls `POST /api/v1/tenant/device/auth/register-device` with license key, machine ID, computer name.
3. Server returns a device-specific access token tied to this machine.
4. Device token enables background sync and protection enforcement without requiring admin credentials.
5. License status is checked: active seats, modules enabled, passive mode flag.

**Token Refresh:**
`AuthenticatedHttpClient` automatically refreshes tokens before they expire (within 60 seconds of expiry). The refresh strategy is chosen based on whether the session is admin or device. A 401 response triggers one immediate refresh attempt before propagating the error.

---

## 9. Data & Sync Architecture

**What it does:**
ZeManage stores all data locally in SQLite for full offline capability, syncs to the cloud backend via REST API, and receives real-time push updates via SignalR WebSocket connections.

**Why it matters:**
Construction sites and project offices frequently have unreliable internet. A governance plugin that stops working offline provides no protection. ZeManage's offline-first design means protection, audit logging, and health tracking continue working even without a server connection. All accumulated operations are queued and replayed automatically when connectivity is restored.

### SQLite Local Database

The local database has 13+ tables:

| Table | Purpose |
|-------|---------|
| `registered_models` | GUID-to-path model registry |
| `command_settings` | Command protection configurations |
| `event_protection` | Event restriction configurations |
| `rule_table` | Rule-based protection definitions |
| `pin_protection` | Element pin metadata |
| `sessions` | Session start/end/crash records |
| `audit_logs` | All protection action records |
| `evidence` | Screenshot/evidence file metadata |
| `offline_queue` | Failed API operations pending retry |
| `metrics` | Model file metric snapshots |
| `chat` | Ze AI conversation history |
| `overrides` | Admin-granted protection overrides |
| `cache` | Rule and settings cache |

**Schema migrations:** `MigrationRunner` applies versioned SQL scripts on startup, enabling safe database upgrades as ZeManage adds features without breaking existing installations.

### API Sync Services

Each domain has its own sync service:

| Sync Service | Endpoint |
|-------------|---------|
| AuditLogSyncService | `POST /api/v1/Revit/audit-logs` |
| CommandProtectionSyncService | `POST /api/v1/Revit/command-settings` |
| EventProtectionSyncService | `POST /api/v1/Revit/event-protection` |
| PinProtectionSyncService | `POST /api/v1/Revit/pin-protection` |
| RulesSyncService | `POST /api/v1/Revit/rules` |
| SessionSyncService | `POST /api/v1/Revit/sessions` |
| MetricsSyncService | `POST /api/v1/Revit/metrics` |
| ModelSyncService | `POST /api/v1/Revit/models/register` |

### Offline Queue

When an API call fails (network error, server unavailable), an `OfflineOperation` record is written to the `offline_queue` table:

```
OperationId   — GUID
OperationType — "AuditLogSync", "EvidenceUpload", etc.
OperationData — Full JSON payload
Priority      — 1–10 (higher = processed first)
Status        — Pending → Processing → Completed / Failed
AttemptCount  — Tracks retry attempts
```

`OfflineSyncProcessor` runs in the background, polling the queue and replaying operations in priority order when connectivity is restored.

**Ordering matters:** Sessions must be synced before audit logs that reference them. Audit logs must be synced before evidence uploads that reference them. The priority system enforces this dependency order.

### SignalR Real-Time

ZeManage maintains a persistent WebSocket connection to the server via SignalR. When the server has information for a client (protection change, new rule, chat message, force logout), it pushes it instantly rather than waiting for the client to poll.

**Real-time event types:**

| Event | Triggered When |
|-------|---------------|
| `ProtectionChange` | Admin changes a rule or command setting on another machine |
| `ModelRegistration` | A model is registered by another admin |
| `UserJoined` / `UserLeft` | User opens or closes the same model |
| `ActiveUsersUpdated` | Presence list refresh |
| `ChatMessage` | Someone sends a model/project-scoped message |
| `SyncQueueUpdate` | A sync operation completes |
| `ForceTokenRefresh` | Server rotates credentials |
| `ForceLogout` | Admin revokes a session |

`SignalRConnectionManager` handles auto-reconnect with exponential backoff. If the connection drops, it automatically attempts to reconnect and re-subscribes to all event handlers.

---

## 10. Audit & Compliance

**What it does:**
Audit & Compliance creates an immutable, comprehensive record of every protection-related action in ZeManage. It captures who did what, on which element, when, under which rule, and what the outcome was — including screenshots taken before and after the action.

**Why it matters:**
In regulated industries (healthcare facilities, government buildings, critical infrastructure), the audit trail is often a contractual or legal requirement. Even outside regulated industries, the audit trail is the primary tool for investigating mistakes, training team members, and demonstrating due diligence to clients.

### Audit Log Structure

Every protection event produces an `ProtectionAuditEntry`:

| Field | What It Records |
|-------|----------------|
| `AuditLogId` | GUID matching server record |
| `Timestamp` | Exact time of event |
| `UserName` | Who performed the action |
| `WasCompanyAdmin` / `WasProjectAdmin` | Their role at time of action |
| `ModelGuid` | Which model |
| `ProtectionId` | Which rule/command/event triggered |
| `CommandName` | What Revit command was attempted |
| `Mode` | Notify / Assist / Protect |
| `Action` | Allowed / Blocked / Cancelled / Override / Detected |
| `ElementIds` | Comma-separated IDs of affected elements |
| `ElementCount` | How many elements |
| `ElementCategory` / `FamilyType` / `Name` | Element identity |
| `Reason` | Rule message or action description |
| `UserComment` | User's typed reason (if rule required one) |
| `EventSource` | Which protection layer fired |
| `OverrideMethod` | "OTP", "AdminPassword", or null |
| `Synced` | Whether uploaded to server |
| `SentMail` | Whether email notification dispatched |

**Protection Action Types:**

| Action | Meaning |
|--------|---------|
| `Allowed` | Rule matched in Notify mode — logged, action permitted |
| `Blocked` | Rule matched in Protect mode — action prevented |
| `Cancelled` | User cancelled in Assist mode dialog |
| `Override` | Admin password or OTP used to bypass a block |
| `Detected` | Event restriction detected a policy violation |

### Evidence Capture

When a rule has `CaptureBeforeScreenshot` or `CaptureAfterScreenshot` enabled, `ScreenshotService` captures the full Revit window as a PNG:

1. **Before screenshot**: Captured when the command fires, before any dialog is shown. Shows the model state at the moment the action was attempted.
2. **After screenshot**: Captured after the action completes (or after the user cancels). Shows the outcome.

Each screenshot gets a SHA-256 hash for integrity verification. This means the evidence cannot be tampered with after the fact without invalidating the hash.

**Evidence upload grouping:**
Before and after screenshots for the same audit log entry are uploaded together as a multipart form submission. A 5-minute grace period waits for the "after" screenshot before uploading the "before" alone. Temp files are cleaned up 24 hours after successful upload.

**Email Notifications:**
For rules with `SendEmail = true`, the server dispatches email notifications to admins after the audit log and evidence are uploaded. The email includes the audit details and evidence attachments, enabling remote oversight without requiring admins to log into the dashboard continuously.

---

## 11. Settings Import/Export

**What it does:**
ZeManage can export the current configuration of Addons (NWC Export options, Link Remapper mappings, cloud version selections) to a `.ze` file, and import a previously saved `.ze` file to restore the same configuration on another machine.

**Why it matters:**
On multi-seat deployments, reconfiguring each workstation manually is error-prone and time-consuming. A project coordinator can configure the correct export settings once, save them as a `.ze` file, and distribute it to the whole team. All machines then use identical settings for consistent output.

**.ze File Format:**
The `.ze` file is a human-readable JSON file:

```json
{
  "version": "1.0",
  "exportSettings": {
    "convertElementProperties": true,
    "convertLights": false,
    "divideFileIntoLevels": true,
    "exportElementIds": true,
    "facetingFactor": 1,
    "exportFolderPath": "C:\\Projects\\Exports"
  },
  "cloudVersions": [
    {
      "revitLinkPath": "C:\\Projects\\Structure.rvt",
      "version": "6"
    }
  ]
}
```

**Export:** Opens a Save dialog with a `*.ze` file filter. Serializes the entire current ViewModel state to JSON at the chosen path.

**Import:** Opens an Open dialog with a `*.ze` file filter. Deserializes the JSON back into the ViewModel, updating all UI controls to reflect the imported values. The user sees their settings populated immediately.

---

## Summary: How Features Connect

ZeManage is not a collection of independent tools — the features are designed to reinforce each other:

```
User attempts an action in Revit
        ↓
Command/Event intercepted by ZeManage
        ↓
Rule Engine evaluates against active rules
        ↓
Protection Mode determined (Notify / Assist / Protect)
        ↓
If Protect → block dialog shown → OTP/password required
If Assist  → guidance dialog shown → user decides
If Notify  → silent pass-through
        ↓
Audit log written to SQLite
Screenshot captured (if rule requires it)
        ↓
Background sync uploads audit + evidence to server
Email notification dispatched (if rule requires it)
SignalR notifies other admins in real-time
        ↓
Ze AI can query audit history + health metrics
Health Monitor tracks impact over time
Activity Tracker shows who was active during the event
```

Every action in Revit can be traced from interception through protection decision to audit record to evidence to notification — creating a complete, closed loop of governance.

---

*ZeManage by ZestineTech — Enterprise BIM Governance for Autodesk Revit*
*Supports Revit 2021 – 2026*
