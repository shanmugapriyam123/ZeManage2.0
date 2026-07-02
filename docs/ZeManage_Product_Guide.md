# ZeManage - Product Documentation

**BIM Management Plugin for Autodesk Revit**
*By ZestineTech*

Supports Revit 2021 - 2026

---

## Overview

ZeManage is an enterprise BIM governance plugin that provides model protection, real-time session monitoring, AI-powered analysis, health dashboards, and cloud synchronization for Autodesk Revit.

---

## 1. Protection

### Pin Protection
Lock individual elements to prevent accidental modification. Protected elements cannot be moved, deleted, or modified without admin authorization.

- **Enable/Disable**: Toggle pin protection per element
- **Status View**: See all protected elements in the model
- **Admin Override**: Use OTP or admin password to bypass protection

### Command Protection
Control which Revit commands users can execute on protected elements.

- **Supported Commands**: Delete, Cut, Copy, Move, Rotate, Mirror, Group, Unpin
- **Protection Modes**:
  - **Notify**: Log the action, allow user to proceed
  - **Assist**: Show guidance dialog, user decides
  - **Protect**: Block action entirely, require OTP/password

### Event Restriction
Restrict Revit-level events based on company policies.

- Open file from non-approved location
- Open central file directly
- Model upgrade protection
- Save over earlier file version
- CAD import/explode protection
- Sync conflict detection
- Family loading/version mismatch protection

### Rule-Based Protection
Define custom rules based on parameters, categories, or element types.

- **Scopes**: Company-wide, Project-wide, Model-specific
- **Rule Types**: Parameter-based, Category-based, Command-specific
- **Features**: Screenshot capture, comment requirements, email notifications, priority ordering

---

## 2. Activity Tracker

### Session Information
View current Revit session details including user, machine, model, and connection status.

### Crash Register
View crash history with timestamps, causes, and recovery information.

### Model Activities
Track all model changes per session with user attribution.

---

## 3. Health Monitor

### Model Health Dashboard
Comprehensive dashboard with real-time metrics:

- **General Statistics**: File size, linked Revit models count
- **Element Breakdown**: Model elements, other elements, annotative elements (donut chart)
- **Comparisons**: Levels vs Grids, Views vs Sheets, Worksets vs Families
- **Performance Impacts**: Warnings count, duplicate elements, imported DWGs
- **Best Practice**: Groups, linked DWGs, raster images, regions
- **Disconnects**: Unconnected walls, pipes, ducts
- **Warnings Trend**: Historical warning count chart
- **Nonnative Objects**: Oversized families, purgeable elements
- **Background Scan**: Views not on sheets, in-place families, room issues
- **Project Complexity Score**: Overall project health percentage

### Analyze Model
Run expensive analysis (families over 5MB, purgeable elements) on demand.

---

## 4. Sync Control

### Background Sync
Automatic synchronization with configurable options:

- **Sync Modes**: Continuous (always on) or When Paused (idle-triggered)
- **Schedule**: Restrict sync to a time window (e.g., 21:00 - 09:00)
- **Compact Model**: Auto-compact on first sync of each day

### Relinquish
Automatically release workset ownership when user is idle.

### Exit Revit on Idle
Optionally close Revit after a configurable idle period.

### Sync Queue
View and manage pending synchronization operations.

---

## 5. Manage Projects

### Model Registration
Register models for BIManage tracking with GUID-based identification.

### OTP Generator (Admin)
Generate one-time passwords for admin override operations.

### Web Dashboard (Admin)
Open the BIManage web dashboard for project management.

---

## 6. AI Assistant

### Ze AI
AI-powered chat assistant for BIM workflows:

- Ask questions about Revit, BIM best practices
- Get context-aware suggestions based on your model
- Conversation history saved per session

---

## 7. Addons

### NWC Export
Batch export 3D views to Navisworks NWC format.

### Link Remapper
Remap and reload Revit links from local or cloud paths.

---

## 8. Authentication & Roles

### User Roles
- **Company Administrator**: Full access across all projects
- **Project Administrator**: Access to assigned projects only
- **Normal User**: Standard user with protections enforced

### Sign In
Cloud authentication for API sync, real-time collaboration, and admin features.

### Device Registration
Register device with license key for API access.

---

## 9. Data & Sync

### Local Database (SQLite)
All data stored locally for offline capability. Auto-syncs when connected.

### API Sync
HTTP REST API sync with offline queue fallback. Failed operations are queued and retried automatically.

### SignalR Real-Time
Real-time push notifications for:
- Protection changes
- Model registration events
- Chat messages
- User presence
- Force logout/token refresh

---

## 10. Audit & Compliance

### Audit Logging
Complete audit trail of all protection actions:
- User identification and role
- Before/after state capture
- Screenshots (evidence capture)
- User comments
- Admin override tracking (OTP/password method)

### Evidence Capture
Automatic screenshot capture before/after protected operations for compliance.

---

## 11. Settings Import/Export

Export and import sync settings as `.ze` files (JSON format) for sharing configurations across machines or projects.

---

## Support

- **Website**: https://zestinetech.com
- **Documentation**: https://zestinetech.com/zemanage/docs
- **Email**: support@zestinetech.com

---

*ZeManage by ZestineTech - BIM Plugin for Revit*
