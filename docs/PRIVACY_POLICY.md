# ZeManage Privacy Policy

**Effective Date:** April 2, 2026
**Last Updated:** April 2, 2026
**Product:** ZeManage - BIM Plugin for Autodesk Revit
**Company:** ZestineTech
**Website:** https://zestinetechnologies.com

---

## 1. Introduction

This Privacy Policy describes how ZestineTech ("we", "us", "our") collects, uses, stores, protects, and shares information through the ZeManage BIM Plugin for Autodesk Revit ("the Software", "ZeManage").

ZeManage is a Building Information Modeling (BIM) management plugin that provides command protection, element protection, rules enforcement, activity monitoring, model health analytics, and team collaboration features within Autodesk Revit.

By installing and using ZeManage, you acknowledge and agree to the practices described in this Privacy Policy.

---

## 2. Information We Collect

### 2.1 User Identity Information

We collect the following identity information when users authenticate and use ZeManage:

| Data | Purpose |
|------|---------|
| Email address | Primary user identification and authentication |
| Display name | Displayed in audit logs and team presence |
| Revit username | Mapping the Revit user to the ZeManage account |
| User ID / Profile ID | Internal unique identifier for API communication |
| Company ID and Company name | Multi-tenant organization scoping |
| Role information | Determines access level (Normal User, Project Administrator, Company Administrator) |
| Project assignments | Controls which projects the user can manage |

**When collected:** During device registration, authentication, and login.
**Legal basis:** Necessary for the performance of the software license agreement.

### 2.2 Device and Machine Information

We collect hardware identifiers to bind the software license to authorized machines:

| Data | Purpose |
|------|---------|
| Machine ID | Deterministic identifier derived from hardware characteristics (Windows MachineGuid, motherboard serial number, processor ID) for license binding |
| Computer name | Identifies the workstation in session logs |
| Windows username | Associates the operating system user with the session |

**How Machine ID is generated:** A one-way hash (MD5) is computed from the Windows registry MachineGuid, motherboard serial number, and processor ID. The raw hardware values are not stored or transmitted - only the derived hash.

**When collected:** During device registration and each session startup.
**Legal basis:** Necessary to enforce the software license agreement and prevent unauthorized use.

### 2.3 Session and Activity Data

ZeManage tracks Revit session information to provide activity monitoring and crash detection:

| Data | Purpose |
|------|---------|
| Session ID | Unique identifier for each Revit session |
| Session start/end timestamps | Duration tracking and usage analytics |
| Revit version and build number | Compatibility tracking and support |
| Plugin version | Version management |
| Loaded add-in counts | Environment information for troubleshooting |
| Desktop Connector version | Integration status |
| Journal file name | Crash detection (read-only, last 200 lines analyzed locally) |
| Session status | Active, Inactive, Closed, or Crashed |
| Total commands and events count | Activity volume metrics |

**Document session data per opened model:**

| Data | Purpose |
|------|---------|
| Model GUID and path | Identifies which Revit model is open |
| Central model path and name | Tracks the authoritative model location |
| Project name | Associates the model with a project |
| Open/close timestamps | Document usage duration |
| Opening duration | Performance measurement |
| Opened worksets count | Model complexity indicator |
| Modification count | Activity tracking |
| Last saved timestamp | Save frequency monitoring |

**When collected:** Automatically during each Revit session.
**Legal basis:** Legitimate interest in providing BIM management, monitoring, and analytics services.

### 2.4 System Performance Data (Heartbeats)

Periodic heartbeats capture workstation performance to help organizations monitor resource usage:

| Data | Purpose |
|------|---------|
| Memory usage (%) | Resource monitoring |
| CPU usage (%) | Performance tracking |
| Disk usage (%) | Storage monitoring |
| Graphics/GPU usage (%) | Rendering performance |
| Active document reference | Current work context |
| Timestamp | When measurement was taken |

**When collected:** At regular intervals during active sessions.
**Legal basis:** Legitimate interest in providing performance monitoring to administrators.

### 2.5 Protection and Audit Data

When protection features are active (command protection, pin protection, rule protection, event protection), ZeManage logs enforcement actions:

| Data | Purpose |
|------|---------|
| Audit log ID | Unique identifier for each audit entry |
| Timestamp | When the action occurred |
| Username | Who performed the action |
| Admin status at time of action | Whether the user was an administrator |
| Model GUID | Which model was affected |
| Command or event name | What action was attempted |
| Protection mode | Monitor, Guide, or Prevent |
| Action taken | Allowed, Blocked, Override, or Detected |
| Element IDs, count, category, family, name | Which Revit elements were involved |
| Reason / custom message | The protection rule's message to the user |
| User comment | The user's explanation (when required by policy) |
| Override method | How override was authorized (OTP, Admin Password, or none) |
| Email notification flag | Whether an email alert was triggered |

**Data integrity:** Audit log entries are protected with HMAC-SHA256 to detect tampering. The HMAC covers all critical fields including the audit ID, timestamp, user, action, and element metadata.

**When collected:** Each time a protected command, event, or element action is triggered.
**Legal basis:** Legitimate interest in enforcing organizational BIM standards and maintaining an auditable record.

### 2.6 Screenshots and Visual Evidence

When configured by an administrator, ZeManage may capture screenshots of the Revit viewport before and/or after a protected action occurs:

| Data | Purpose |
|------|---------|
| Before screenshot (PNG) | Visual state before the action |
| After screenshot (PNG) | Visual state after the action |
| Capture timestamps | When each screenshot was taken |
| File hash (SHA-256) | Integrity verification |
| Associated audit log ID | Links the screenshot to the audit event |
| Protection type | Which protection triggered the capture |

**Important:** Screenshots are captured only of the **Revit application viewport**, not the entire screen. They capture the state of the 3D/2D model view to document what changed.

**Administrator control:** Screenshot capture is **opt-in per protection rule**. Administrators explicitly enable `capture_before_screenshot` and/or `capture_after_screenshot` for each command, event, or rule protection. Screenshots are never captured unless an administrator has configured the specific protection to do so.

**When collected:** Only when a configured protection is triggered and screenshot capture is enabled for that protection.
**Legal basis:** Legitimate interest in providing visual evidence of BIM changes for compliance and quality control, as configured by the organization's administrator.

### 2.7 Model File Metrics (Model Health)

ZeManage collects model health metrics to help organizations monitor BIM model quality:

**Fast metrics (captured during sync/save operations):**

| Data | Purpose |
|------|---------|
| File size | Model size tracking |
| Levels, grids, design options count | Model structure |
| Linked/imported DWG count | External reference tracking |
| Linked Revit model count | Model dependency tracking |
| Raster images count | Embedded media tracking |
| Warnings count | Model health indicator |
| Duplicate elements count | Quality indicator |
| Model groups, detail groups count | Grouping analysis |
| Views, sheets count | Documentation scope |
| Families count | Component library size |
| Worksets count | Collaboration structure |
| View templates count | Standardization tracking |
| Shared coordinates | Geo-referencing data |

**Medium metrics (captured periodically, default every 24 hours):**

| Data | Purpose |
|------|---------|
| Total/model/annotative elements count | Model complexity |
| In-place families count | Quality concern indicator |
| Unplaced/unenclosed rooms count | Design completeness |
| Views not on sheets count | Documentation gaps |
| Disconnected walls/pipes/ducts count | Model integrity |

**Expensive metrics (manual trigger only by user request):**

| Data | Purpose |
|------|---------|
| Families over 5MB count | Performance concern indicator |
| Purgeable elements count | Cleanup opportunity |

**Metric metadata:** Each metric capture includes the session ID, model identifier, capture timestamp, captured-by username, and capture reason.

**When collected:** Fast metrics during sync/save; medium metrics periodically; expensive metrics only on explicit user request.
**Legal basis:** Legitimate interest in providing model health analytics for BIM management.

### 2.8 Pin Protection Data

Pin protection allows administrators to protect specific Revit elements from modification:

| Data | Purpose |
|------|---------|
| Element ID, GUID, name, category | Identifies the protected element |
| Model GUID and project name | Scoping |
| Protection mode | Monitor, Guide, or Prevent |
| Protected by (username) | Who applied the protection |
| Protected at (timestamp) | When protection was applied |
| Admin comment | Reason for protection |
| Deactivation details | Who removed protection, when, and why |

**When collected:** When an administrator pins/unpins elements.
**Legal basis:** Necessary for the performance of the BIM management service.

### 2.9 Authentication and Security Data

| Data | Purpose |
|------|---------|
| Admin password hash (bcrypt) | Local authentication for protection overrides |
| OTP codes (6-digit, time-limited) | One-time override authorization |
| Authentication tokens | API session management |
| Refresh tokens | Token renewal |

**Security measures:**
- Passwords are stored as **bcrypt hashes with salt** - the plaintext password is never stored
- OTP codes are **encrypted with AES-256-CBC** using a machine-specific key derived via PBKDF2 (100,000 iterations)
- The encryption key is derived from the machine ID, Windows user SID, and an application-specific salt
- Authentication tokens expire and are refreshed automatically
- Passwords and OTP codes are **never transmitted** to the server - they are used locally only

**When collected:** When administrators set passwords or generate OTP codes.
**Legal basis:** Necessary for the security of the protection system.

### 2.10 Real-Time Collaboration Data (SignalR)

ZeManage provides real-time collaboration features via SignalR (WebSocket):

| Data | Purpose |
|------|---------|
| User presence (username, session, model) | Shows who is working on which model |
| Chat messages (sender, text, scope, timestamp) | Team communication within Revit |
| Sync queue status | Coordinates model synchronization |
| Protection change notifications | Real-time protection updates |
| Model registration events | New model notifications |

**Chat message scope:** Messages can be scoped to a specific model, project, or sent as direct messages to individual users.

**When collected:** During active sessions with network connectivity.
**Legal basis:** Legitimate interest in providing team collaboration features.

### 2.11 Licensing Data

| Data | Purpose |
|------|---------|
| License key | Software activation |
| License status and expiration | Entitlement verification |
| Enabled modules | Feature access control |
| Seat count (total and active) | Concurrent usage enforcement |

**When collected:** During device registration and periodically during sessions.
**Legal basis:** Necessary for the performance of the software license agreement.

### 2.12 Crash Detection Data

| Data | Purpose |
|------|---------|
| Session crash status | Whether Revit crashed during a session |
| Journal file analysis (last 200 lines, local only) | Determines if crash occurred |

**Important:** ZeManage reads only the last 200 lines of the Revit journal file to detect crash signatures. The journal content is analyzed locally and **not transmitted**. Only the crash detection result (boolean) is stored in the session record.

**When collected:** At session startup (analyzes previous session's journal).
**Legal basis:** Legitimate interest in crash detection and session reliability monitoring.

---

## 3. How We Use Information

We use the collected information for the following purposes:

| Purpose | Data Used |
|---------|-----------|
| **Software licensing and activation** | Machine ID, license key, seat count |
| **User authentication and authorization** | Email, credentials, roles, tokens |
| **BIM protection enforcement** | Audit logs, element data, screenshots, protection settings |
| **Activity monitoring and reporting** | Session data, heartbeats, command/event counts |
| **Model health analytics** | File metrics, element counts, quality indicators |
| **Team collaboration** | Presence data, chat messages, sync coordination |
| **Crash detection and reliability** | Session status, journal analysis |
| **Administrative oversight** | Audit trails, role management, protection configuration |
| **Offline resilience** | Queued operations for sync when connectivity resumes |
| **Data integrity verification** | HMAC signatures, file hashes |

We do **not** use collected information for:
- Advertising or marketing profiling
- Sale to third parties
- Purposes unrelated to BIM management

---

## 4. Data Storage

### 4.1 Local Storage

ZeManage stores data locally on the user's workstation in an SQLite database:

| Storage | Location | Contents |
|---------|----------|----------|
| SQLite database | Revit Addins directory | Sessions, audit logs, protections, metrics, offline queue |
| Temporary screenshots | Session temp directory | PNG files (deleted after upload or session end) |
| Configuration | App.config | API endpoint URL, plugin settings |

**Local security:**
- Sensitive columns (passwords, OTP codes) encrypted with AES-256-CBC
- Encryption key derived from machine-specific hardware identifiers
- HMAC integrity checks on audit logs and protection tables
- Data is accessible only to the Windows user running Revit

### 4.2 Server Storage

Data synchronized to the ZeManage API is stored on Microsoft Azure infrastructure:

| Component | Service |
|-----------|---------|
| API server | Azure App Service (Central US region) |
| Transport | HTTPS (TLS 1.2+) |
| Real-time | SignalR over WebSocket (TLS) |

### 4.3 Offline Queue

When the server is unreachable, ZeManage queues operations locally in the SQLite `offline_queue` table. Queued operations are automatically synchronized when connectivity is restored, with a maximum of 3 retry attempts per operation.

---

## 5. Data Transmission

### 5.1 Data Sent to Server

The following data is transmitted to the ZeManage API over encrypted HTTPS connections:

| Endpoint | Data | Trigger |
|----------|------|---------|
| Device registration | Machine ID, computer name, license key | First launch / registration |
| Authentication | Email, password (over HTTPS) | Login |
| Session sync | Session metadata, timestamps, environment info | Session start/end |
| Audit log sync | Protection actions, element data, user actions | Each protection trigger |
| Evidence upload | Screenshot images with metadata | When screenshot capture is enabled |
| Metrics sync | Model health data (fast/medium/manual) | Sync/save, periodic, manual |
| Protection sync | Rules, command settings, pin protections | Configuration changes |
| Real-time presence | Username, model, session | While connected |
| Chat messages | Message text, sender, scope | When user sends a message |

### 5.2 Data Never Transmitted

The following data remains local and is **never sent** to our servers:

- Admin passwords (only bcrypt hashes stored locally)
- OTP codes (encrypted locally, used for local authentication)
- Encryption keys and salts
- Raw hardware identifiers (only the derived Machine ID hash is transmitted)
- Revit journal file contents (only crash detection result transmitted)
- Windows registry values (only used to derive Machine ID)

---

## 6. Administrator Controls

Organization administrators have the following controls over data collection:

### 6.1 Protection Configuration

| Control | Description |
|---------|-------------|
| **Protection mode** | Choose Monitor (log only), Guide (warn), or Prevent (block) per command/event/rule |
| **Screenshot capture** | Enable or disable before/after screenshots per protection rule |
| **Comment requirements** | Require users to provide reasons for overrides |
| **Email notifications** | Enable or disable email alerts for specific protections |
| **Override permissions** | Allow or disallow admin overrides per protection |
| **OTP scope** | Limit OTP codes to specific rules, commands, or global use |

### 6.2 Scope Levels

Protections can be configured at three levels:

| Scope | Description |
|-------|-------------|
| **Company** | Applies to all projects and models in the organization |
| **Project** | Applies to all models within a specific project |
| **Model** | Applies only to a specific Revit model |

### 6.3 Role-Based Access Control (RBAC)

| Role | Permissions |
|------|-------------|
| **Normal User** | Subject to protections, can view own activity |
| **Project Administrator** | Configure protections for assigned projects, manage project users |
| **Company Administrator** | Full access: configure all protections, manage all users, view all audit data |

---

## 7. Data Security

### 7.1 Encryption

| Data | Method |
|------|--------|
| Data in transit | TLS 1.2+ (HTTPS and WSS) |
| Admin passwords | Bcrypt hash with salt |
| OTP codes | AES-256-CBC with machine-derived key (PBKDF2, 100,000 iterations) |
| Audit integrity | HMAC-SHA256 per row |
| Evidence integrity | SHA-256 file hash |

### 7.2 Access Controls

- API authentication via JWT tokens with expiration and refresh
- Device binding via hardware-derived Machine ID
- Role-based access for administrative features
- Machine-specific encryption keys (cannot be transferred between machines)

### 7.3 Data Integrity

- Audit log entries include HMAC-SHA256 signatures covering all critical fields
- Protection settings include row-level HMAC verification
- Screenshot files include SHA-256 hashes for integrity verification
- Database tampering is detectable via HMAC verification

---

## 8. Data Retention

| Data Type | Retention |
|-----------|-----------|
| Session records | Retained for the duration of the software license agreement |
| Audit logs | Retained for the duration required by the organization's compliance policies |
| Screenshots | Retained as configured by the organization administrator |
| Model metrics | Retained for historical trend analysis |
| OTP codes | Automatically expire (time-limited) |
| Offline queue | Cleared after successful synchronization |
| Temporary files | Deleted at session end or after successful upload |

Organizations may request data export or deletion by contacting ZestineTech.

---

## 9. Third-Party Services

### 9.1 Infrastructure

| Service | Provider | Purpose |
|---------|----------|---------|
| Cloud hosting | Microsoft Azure | API server and data storage |
| Real-time communication | Azure SignalR Service | WebSocket-based notifications |

### 9.2 Optional AI Features

If the AI assistant feature is enabled (optional), user queries and model context may be sent to:

| Provider | Purpose |
|----------|---------|
| OpenAI | AI-powered BIM assistance (if configured) |

AI features are **optional** and must be explicitly configured by the organization. No data is sent to AI providers unless this feature is enabled.

### 9.3 Autodesk Revit

ZeManage operates as a plugin within Autodesk Revit. It accesses Revit's API to read model data, intercept commands, and monitor events. ZeManage does not transmit data to Autodesk beyond what Revit itself transmits.

---

## 10. User Rights

Users and organizations have the following rights regarding their data:

| Right | Description |
|-------|-------------|
| **Access** | Request a copy of your personal data held by ZeManage |
| **Correction** | Request correction of inaccurate personal data |
| **Deletion** | Request deletion of personal data (subject to legal retention requirements) |
| **Export** | Request data export in a machine-readable format |
| **Restriction** | Request restriction of processing in certain circumstances |
| **Objection** | Object to processing based on legitimate interest |
| **Portability** | Receive your data in a structured, commonly used format |

To exercise these rights, contact us at the address listed in Section 14.

---

## 11. Cookies and Tracking

ZeManage is a desktop application plugin. It does **not** use cookies, web beacons, pixel tags, or browser-based tracking technologies.

---

## 12. Children's Privacy

ZeManage is a professional BIM management tool intended for use by licensed professionals. It is not directed at individuals under the age of 16. We do not knowingly collect personal information from children.

---

## 13. Changes to This Policy

We may update this Privacy Policy from time to time to reflect changes in our practices, technology, or legal requirements. When we make material changes:

- We will update the "Last Updated" date at the top of this document
- We will notify users through the software update mechanism
- Continued use of ZeManage after changes constitutes acceptance of the updated policy

---

## 14. Contact Us

For privacy-related inquiries, data requests, or concerns:

**ZestineTech**
Email: privacy@zestinetechnologies.com
Website: https://zestinetechnologies.com/privacy

---

## 15. Compliance

This Privacy Policy is designed to comply with:

- **GDPR** (General Data Protection Regulation) - EU
- **CCPA** (California Consumer Privacy Act) - US
- **PIPEDA** (Personal Information Protection and Electronic Documents Act) - Canada
- **Australian Privacy Act 1988** - Australia

For region-specific rights and obligations, please contact us.

---

## Appendix A: Complete Data Inventory

### A.1 Data Categories Summary

| # | Category | Collected | Stored Locally | Transmitted | Encrypted |
|---|----------|-----------|----------------|-------------|-----------|
| 1 | User identity (email, name, roles) | Yes | SQLite | HTTPS | TLS in transit |
| 2 | Machine ID (hardware hash) | Yes | SQLite | HTTPS | TLS in transit |
| 3 | Session activity (timestamps, status) | Yes | SQLite | HTTPS | TLS in transit |
| 4 | System performance (CPU, RAM, disk, GPU %) | Yes | SQLite | HTTPS | TLS in transit |
| 5 | Audit logs (protection actions) | Yes | SQLite + HMAC | HTTPS | HMAC-SHA256 + TLS |
| 6 | Screenshots (before/after PNG) | Admin opt-in | Temp files + SQLite | HTTPS multipart | SHA-256 hash + TLS |
| 7 | Model metrics (file size, element counts) | Yes | SQLite | HTTPS | TLS in transit |
| 8 | Pin protection (element, user, timestamp) | Yes | SQLite + HMAC | HTTPS | HMAC-SHA256 + TLS |
| 9 | Admin passwords | Yes | SQLite | Never | Bcrypt + AES-256 |
| 10 | OTP codes | Yes | SQLite | Never | AES-256-CBC |
| 11 | Chat messages | User initiated | Memory | WebSocket (TLS) | TLS in transit |
| 12 | Licensing (key, seats, status) | Yes | Memory + SQLite | HTTPS | TLS in transit |
| 13 | Crash detection (boolean result) | Yes | SQLite | HTTPS (in session) | TLS in transit |

### A.2 API Endpoints Used

| Method | Endpoint | Data Transmitted |
|--------|----------|-----------------|
| POST | `/api/v1/tenant/device/auth/register-device` | License key, machine ID, computer name |
| POST | `/api/v1/tenant/device/auth/validate-device` | Machine ID |
| POST | `/api/v1/tenant/device/auth/admin-login` | Machine ID, username, password |
| POST | `/api/v1/tenant/device/auth/refresh` | Refresh token |
| GET | `/api/v1/tenant/device/auth/license-status` | (auth token only) |
| POST | `/api/v1/Revit/sessions` | Session metadata |
| POST | `/api/v1/Revit/model-syncs` | Sync operation data |
| POST | `/api/v1/Revit/audit-logs` | Audit entries |
| POST | `/api/v1/Revit/audit-logs/{id}/send-mail` | Email notification trigger |
| POST | `/api/v1/Revit/evidence-images/with-images` | Screenshots (multipart) |
| POST | `/api/v1/Revit/metrics/syncsave` | Fast model metrics |
| POST | `/api/v1/Revit/metrics/periodic` | Medium model metrics |
| POST | `/api/v1/Revit/metrics/manual` | Expensive model metrics |
| POST | `/api/v1/Revit/models/register` | Model registration |
| GET/POST/DELETE | `/api/v1/Revit/pin-protections` | Pin protection CRUD |
| POST/PUT/DELETE | `/api/v1/Revit/rule-protections` | Rule protection CRUD |
| POST/PUT/DELETE | `/api/v1/Revit/command-protection` | Command protection CRUD |
| POST/PUT/DELETE | `/api/v1/Revit/event-protection` | Event protection CRUD |

### A.3 SignalR Real-Time Events

| Event | Data | Direction |
|-------|------|-----------|
| User presence join/leave | Username, session, model | Client to Server |
| Active users snapshot | User list per model | Server to Client |
| Chat message | Sender, text, scope, timestamp | Bidirectional |
| Sync queue join/leave | Model, session, username | Bidirectional |
| Protection change | Protection type, change type, model | Server to Client |
| Model registered | Model metadata | Server to Client |
| Metrics update | Model health data | Client to Server |
| 
token refresh | (no data) | Server to Client |
| Force logout | (no data) | Server to Client |

### A.4 Local Database Tables

| Table | Contents | Security |
|-------|----------|----------|
| `sessions` | Session lifecycle data | Plain |
| `document_sessions` | Per-document tracking | Plain |
| `session_heartbeats` | Performance metrics | Plain |
| `audit_log` | Protection action audit trail | HMAC-SHA256 |
| `evidence_capture` | Screenshot metadata | SHA-256 hash |
| `pin_protection` | Protected element registry | HMAC-SHA256 |
| `command_settings` | Command protection config | HMAC-SHA256 |
| `event_protection_settings` | Event protection config | HMAC-SHA256 |
| `rules` | Rule protection config | HMAC-SHA256 |
| `protection_passwords` | Admin password hashes | Bcrypt + AES-256 |
| `otps` | One-time password codes | AES-256-CBC |
| `protection_overrides` | Admin override records | Plain |
| `registered_models` | Model registration data | Plain |
| `model_file_metrics_*` | Model health metrics (3 tables) | Plain |
| `offline_queue` | Queued API operations | Plain |
| `sync_log` | Sync operation history | Plain |
