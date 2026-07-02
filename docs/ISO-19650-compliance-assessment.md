# ISO 19650 Compliance Assessment for BIManage

**Date:** February 25, 2026
**Product:** BIManage (BIManageRevit)
**Standard:** ISO 19650 Parts 1, 2, 3 & 5

---

## Overview

ISO 19650 is the international standard for **managing information over the whole life cycle of a built asset using BIM**. This document assesses BIManage's current compliance posture and identifies gaps that must be addressed.

Relevant parts:
- **ISO 19650-1** — Concepts and principles
- **ISO 19650-2** — Delivery phase (design/construction information management)
- **ISO 19650-3** — Operational phase
- **ISO 19650-5** — Security-minded approach to information management

---

## Current Features Mapped to ISO 19650

### What BIManage Already Has

| ISO 19650 Requirement | BIManage Feature | Coverage |
|---|---|---|
| **Audit trail** — All information exchanges must be traceable | AuditRepository (20+ event types), SessionRepository, async logging | **Full** |
| **Access control** — Role-based information access | UserRole (Admin/ProjectAdmin), RBAC, PinProtection, OTP challenge | **Full** |
| **Information security** (Part 5) — Protect sensitive project data | DPAPI encryption, JWT auth, AuthTokenManager, secure storage | **Full** |
| **Quality gates** — Prevent non-compliant actions | Protection modes (Monitor/Guide/Prevent), CommandProtectionService, EventProtectionService | **Full** |
| **Rule-based checks** — Validate information against requirements | RuleService (7 evaluator types), hybrid cache | **Full** |
| **Model metrics** — Measure model health/completeness | ModelFileMetricsCollectorService (fast/medium/expensive tiers) | **Full** |
| **Session tracking** — Who accessed what and when | SessionRepository (heartbeat, crash detection) | **Full** |
| **Offline resilience** — Works without constant connectivity | OfflineQueueRepository, sync fallback chain (SignalR -> HTTP -> Queue) | **Full** |
| **Evidence capture** — Document compliance violations | ScreenshotService (Win32 GDI + SHA-256), EvidenceRepository, background upload | **Full** |

**BIManage covers approximately 40% of ISO 19650 requirements**, particularly the foundational areas of security, auditing, and quality enforcement that are hardest to retrofit.

---

## Gap Analysis — What's Missing

### Gap 1: CDE Workflow States — HIGH PRIORITY

**Requirement:** All information containers (models, drawings, documents) must flow through defined states:
- **WIP** (Work in Progress) — author's workspace
- **Shared** — shared with the team for coordination
- **Published** — approved for use by others
- **Archived** — historical record

**Current state:** BIManage has no concept of document workflow states. No state machine, no transitions, no gate checks.

**What to build:**
- `CdeWorkflowState` enum (WIP, Shared, Published, Archived) + custom states
- `CdeWorkflowService` — state machine with transition rules (e.g., WIP->Shared requires naming check, Shared->Published requires approval)
- Per-model state tracking in SQLite + backend sync
- UI indicator showing current state on ribbon/status bar
- Protection rules that block actions based on state (e.g., prevent editing Published models)

**Action Options:**
- **A) Client-side only** — State tracked in SQLite, transitions via ribbon button. Fastest to build, works offline. No multi-user coordination.
- **B) Backend-driven** — States managed by backend API, synced to client. Enables multi-user visibility and approval gates. Requires backend work.
- **C) Hybrid** — Client tracks state locally, syncs to backend when online. Best of both — works offline, coordinates when connected. *(Recommended)*

---

### Gap 2: Naming Convention Enforcement — HIGH PRIORITY

**Requirement:** All information containers must follow an agreed naming convention, typically:
`Project-Originator-Zone-Level-Type-Role-Number` (e.g., `PRJ-ARC-ZZ-01-M3-A-0001`)

**Current state:** The rule engine has 7 evaluator types (ParameterEquals, CategoryExists, ElementCount, etc.) but **no regex/pattern matching** for file or element names. Cannot enforce naming conventions.

**What to build:**
- New rule evaluator: `NamingConventionEvaluator` with configurable regex patterns
- Apply to: file names, view names, sheet names, family names, parameter values
- Configurable convention templates per project (stored in rules DB)
- Validation on model open, save, and share transitions
- Report of non-compliant names with suggested corrections

**Action Options:**
- **A) New evaluator type** — Add `NamingConventionEvaluator` to existing RuleService with regex support. Plugs into current rule engine. *(Recommended)*
- **B) Standalone naming service** — Separate `NamingConventionService` with its own UI for defining/testing patterns. More flexible but duplicates rule infrastructure.

---

### Gap 3: Approval Workflows — MEDIUM PRIORITY

**Requirement:** Information must be reviewed and approved before state transitions (especially Shared->Published). Requires defined roles: originator, checker, approver.

**Current state:** BIManage has Admin/ProjectAdmin roles and protection modes, but no multi-step approval workflow.

**What to build:**
- `ApprovalWorkflow` — configurable multi-step approval chain
- Approval request creation (originator requests state change)
- Reviewer/Approver assignment (per project, per state transition)
- Approval/rejection with comments
- Notification when approval is needed
- Audit log of all approval decisions

**Action Options:**
- **A) Backend-managed approvals** — Approval requests/decisions go through backend API. Enables email triggers, multi-user. Requires backend endpoints. *(Recommended)*
- **B) Local approval log** — Approvals recorded in local SQLite, synced later. Simpler, but approval flow is per-machine not per-project.
- **C) Defer** — Skip for now, rely on existing Admin role + protection modes as a manual gate. Faster to market but not truly ISO 19650-compliant.

---

### Gap 4: EIR/BEP Templates — MEDIUM PRIORITY

**Requirement:**
- **EIR** (Exchange Information Requirements) — what information the client needs, in what format, at what stage
- **BEP** (BIM Execution Plan) — how the delivery team will meet those requirements

**Current state:** No EIR/BEP concept exists. Rules are per-command/event, not structured around information delivery milestones.

**What to build:**
- EIR template editor — define information requirements per project stage
- BEP template with auto-populated sections from BIManage config
- Milestone tracking (design stages, LOD requirements per stage)
- Rule sets tied to EIR milestones (e.g., at Stage 3, all elements must have X parameters)

**Action Options:**
- **A) Template-driven rules** — EIR/BEP defined as rule-set templates in the backend. Admin selects a template per project, rules auto-configure. *(Recommended)*
- **B) Document generation** — Generate EIR/BEP documents (Word/PDF) from BIManage config. Good for client deliverables but doesn't enforce checks.
- **C) Defer** — Rely on external EIR/BEP documents, manually configure matching rules. Lowest effort, acceptable for Phase 1.

---

### Gap 5: Compliance Reporting / Dashboards — MEDIUM PRIORITY

**Requirement:** Ability to demonstrate compliance through reports and documentation.

**Current state:** BIManage collects extensive data (audit logs, metrics, sessions, evidence) but has **no reporting or export capability**.

**What to build:**
- Compliance report generator (HTML/PDF export)
- Report types: audit trail summary, naming compliance, model health, workflow state history
- Dashboard panel (WPF) showing compliance KPIs
- Export for external audit purposes

**Action Options:**
- **A) Backend web dashboard** — Reports generated server-side, accessible via browser. Shared across team. Requires backend web UI.
- **B) Client-side export** — Ribbon button exports HTML/PDF report from local SQLite data. Quick to build, per-user only. *(Recommended for Phase 2)*
- **C) Both** — Client exports for individual use, backend dashboard for org-wide view. Full coverage, most effort.

---

### Gap 6: Email Notifications — MEDIUM PRIORITY

**Requirement:** Stakeholders must be notified of state changes, approval requests, and compliance issues.

**Current state:** Zero email infrastructure exists.

**What to build:**
- Email notification service (SMTP or via backend API)
- Triggers: state transition, approval request, approval decision, compliance violation
- Configurable recipients per project/event type

**Action Options:**
- **A) Backend API relay** — Client triggers notification request to backend; backend sends email via SMTP/SendGrid. Centralizes email config. *(Recommended)*
- **B) Direct SMTP from client** — Revit add-in sends email directly. Simpler but exposes SMTP credentials on each machine.
- **C) Defer to backend-only** — No client-side trigger; backend sends notifications based on synced events. Depends on reliable sync.

---

### Gap 7: Federated Model Coordination — LOW PRIORITY

**Requirement:** Spatial coordination between disciplines, clash detection.

**Current state:** No coordination or clash detection features.

**Recommendation:** This is better served by dedicated tools (Navisworks, BIM Collab, Solibri). BIManage could optionally log/track coordination sessions rather than implement clash detection from scratch.

**Action Options:**
- **A) Log/track only** — Record coordination sessions (who, when, tool used, outcome) in audit trail. Minimal effort, proves process happened. *(Recommended)*
- **B) Skip entirely** — Rely on external tools. Acceptable since ISO 19650 doesn't mandate a specific clash detection tool.

---

## Compliance Readiness Summary

| ISO 19650 Area | Current Coverage | What's Needed | Effort |
|---|---|---|---|
| Information security (Part 5) | 90% | Minor gaps | Low |
| Audit & traceability | 85% | Reporting export | Low |
| Access control & roles | 80% | Approval roles | Medium |
| Quality checks & protection | 75% | Naming convention rules | Medium |
| **CDE workflow states** | **0%** | **Full implementation** | **High** |
| **Naming conventions** | **0%** | **Regex evaluator + templates** | **Medium** |
| **Approval workflows** | **0%** | **Full implementation** | **High** |
| **EIR/BEP support** | **0%** | **Template system** | **Medium** |
| **Compliance reporting** | **0%** | **Report generator** | **Medium** |
| **Email notifications** | **0%** | **Full implementation** | **Medium** |
| Federated coordination | 0% | Optional / external tool | N/A |

---

## Recommended Implementation Roadmap

### Phase 1 — Foundation (ISO 19650-aware)
1. **CDE Workflow States** — The core of ISO 19650. Without this, no compliance claim is possible.
2. **Naming Convention Evaluator** — Add regex-based rule evaluator to existing rule engine. Low effort relative to compliance value.

### Phase 2 — Process (compliant workflows)
3. **Approval Workflows** — Multi-step approval chain for state transitions.
4. **Email Notifications** — Required for approval workflows to function.
5. **Compliance Reporting** — Export audit data as compliance evidence.

### Phase 3 — Planning (full compliance toolkit)
6. **EIR/BEP Templates** — Structured information requirements per stage.
7. **Dashboard** — Real-time compliance monitoring KPIs.

---

## When Can BIManage Claim ISO 19650 Compliance?

| Milestone | Marketing Claim | Requirements |
|---|---|---|
| **After Phase 1** | "ISO 19650-ready" / "Supports ISO 19650 workflows" | CDE states + naming enforcement + existing quality gates |
| **After Phase 2** | "ISO 19650 compliant information management" | Full lifecycle: approvals, notifications, audit evidence export |
| **After Phase 3** | "Comprehensive ISO 19650 BIM management solution" | Full toolkit including EIR/BEP and real-time dashboards |

**Important:** ISO 19650 compliance is about the **process**, not just the tool. BIManage enables and enforces compliance, but the organization must also define the right procedures, roles, and responsibilities. The tool evidences the process.
