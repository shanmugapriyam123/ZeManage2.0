# BIManageRevit Documentation

This directory contains all project documentation organized by category.

## 📁 Directory Structure

### `/architecture` - Architecture Decision Records (ADRs) and Design Decisions
- [ADR-001: Hybrid Command Binding Pattern](architecture/ADR-001-hybrid-command-binding-pattern.md)
- [Caching Strategy Decision](architecture/caching_strategy_decision.md)
- [Screenshot Capture Mechanism](architecture/screenshot_capture_mechanism.md)

### `/guides` - Implementation and Usage Guides
- [Command Interception Implementation Guide](guides/command_interception_implementation_guide.md)
- [BuiltIn Parameter Usage Guide](guides/builtin_parameter_usage_guide.md)
- [Pin/Unpin Execution](guides/pin_unpin_execution.md)
- [Command Binding Code Reference](guides/command_binding_code_reference.md)

### `/implementation` - RBAC, OTP, and Security Documentation
- [RBAC System](implementation/rbac_system.md)
- [RBAC Implementation Checklist](implementation/rbac_implementation_checklist.md)

### `/project` - Project Management Documents
- [Work Breakdown Structure (WBS)](project/WBS.csv)

### `/reference` - Lookup Tables and Reference Data
- [Postable Command IDs](reference/Postable%20Command%20IDs.xlsx)

### `/status` - Phase Summaries and Status Reports
- [Closeout Summary](status/CLOSEOUT_SUMMARY.md)
- [Command Interception Closeout Analysis](status/command_interception_closeout_analysis.md)
- [Evidence Capture Implementation Summary](status/evidence_capture_implementation_summary.md)
- [Persistence WBS Readiness Assessment](status/persistence_wbs_readiness_assessment.md)
- [Persistence Fix Implementation Plan](status/persistence_fix_implementation_plan.md)
- [Persistence Integration Tests Status](status/persistence_integration_tests_status.md)
- [Persistence Phase 1 Final Status](status/persistence_phase1_final_status.md)
- [Phase 1 Final Summary](status/phase1_final_summary.md)
- [Phase 1 Completion Summary](status/phase1_completion_summary.md)
- [Critical Concerns Verification](status/critical_concerns_verification.md)

### `/plans` - Implementation Plans
- [Protection Modes & Intervention Plan](plans/protection_modes_&_intervention_79543639.plan.md)
- [Task List](plans/task.md)
- [Implementation Plan](plans/implementation_plan.md)

### `/misc` - Miscellaneous Documentation
- [Cursor Initial Greeting](misc/cursor_initial_greeting.md)

---

## 🏗️ Project Overview

BIManageRevit is a multi-version Revit plugin (2021-2026) that provides:
- **Command Interception System** - Monitor, Block, and Guide modes for user actions
- **Rule Engine** - Flexible rule evaluation with hybrid caching
- **RBAC & OTP** - Role-based access control with one-time password overrides
- **Evidence Capture** - Screenshot capture with SHA-256 integrity hashing
- **SQLite Persistence** - Offline-capable data storage with automatic schema migration

## 🚀 Quick Links

- [Architecture Decisions](architecture/)
- [Implementation Guides](guides/)
- [Security Documentation](implementation/)
- [Latest Status Reports](status/)

---

**Last Updated:** 2026-01-16
