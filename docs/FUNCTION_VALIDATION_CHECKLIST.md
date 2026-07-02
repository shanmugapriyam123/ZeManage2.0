# BIManageRevit - Function Validation Checklist

**Project:** BIManageRevit
**Version:** 1.0
**Last Updated:** 2026-01-16
**Purpose:** Comprehensive testing checklist for all core functions

---

## 📋 Table of Contents

1. [Session Tracking](#1-session-tracking)
2. [Rule System](#2-rule-system)
3. [Command Interception](#3-command-interception)
4. [RBAC & OTP](#4-rbac--otp)
5. [Evidence Capture](#5-evidence-capture)
6. [Audit Logging](#6-audit-logging)
7. [Database Schema](#7-database-schema)
8. [Error Handling](#8-error-handling)

---

## 1. Session Tracking

**Component:** SessionRepository
**Files:** `BIManage/Data/SQLite/SessionRepository.cs`, `BIManage/Revit/Applications/RevitBootstrapper.cs`

### ✅ Startup Tests

- [ ] **Test 1.1:** Application starts without errors
  - **Action:** Open Revit and load a project
  - **Expected:** Add-in loads successfully, no error dialogs
  - **Log Check:** `"BIManageRevit Add-in started successfully"`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 1.2:** Database schema migrates to v3
  - **Action:** Check logs after first startup
  - **Expected:** `"Database schema is up to date (v3)"` or successful migration
  - **Log Check:** `"Current schema version: 3"`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 1.3:** Crash detection runs on startup
  - **Action:** Kill Revit process (Task Manager) with project open, then restart
  - **Expected:** Previous session marked as crashed
  - **Log Check:** `"Detected X crashed session(s) from previous runs"` or `"Marked X orphaned session(s) as crashed"`
  - **Database Check:** `SELECT * FROM sessions WHERE crash_detected = 1;`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 1.4:** New session created
  - **Action:** Start Revit normally
  - **Expected:** New session ID logged
  - **Log Check:** `"Persistence session started: [GUID] (Windows: [username], Revit: [username/N/A]"`
  - **Database Check:** `SELECT * FROM sessions WHERE is_active = 1 ORDER BY started_at DESC LIMIT 1;`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 1.5:** revit_username column populated
  - **Action:** Check database after startup
  - **Expected:** `revit_username` column exists and contains value or NULL
  - **Database Check:** `PRAGMA table_info(sessions);` should show `revit_username` column
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ Heartbeat Tests

- [ ] **Test 1.6:** Heartbeat recorded on startup
  - **Action:** Wait 5 seconds after Revit loads
  - **Expected:** Initial heartbeat recorded
  - **Database Check:** `SELECT * FROM session_heartbeats ORDER BY timestamp DESC LIMIT 1;`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 1.7:** Heartbeat updates session.last_heartbeat
  - **Action:** Check sessions table after heartbeat
  - **Expected:** `last_heartbeat` timestamp within last 60 seconds
  - **Database Check:** `SELECT last_heartbeat FROM sessions WHERE is_active = 1;`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 1.8:** Transaction atomicity for heartbeat
  - **Action:** Check if both session update and heartbeat insert succeed/fail together
  - **Expected:** No orphaned heartbeats without session update
  - **Database Check:** Compare timestamps between `sessions.last_heartbeat` and latest `session_heartbeats.timestamp`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ Shutdown Tests

- [ ] **Test 1.9:** Clean shutdown (normal exit)
  - **Action:** Close Revit normally (File > Exit)
  - **Expected:** Session marked as ended with crash_detected = 0
  - **Log Check:** `"Session shutdown complete: [GUID] (Crash: False)"`
  - **Database Check:** `SELECT is_active, crash_detected, ended_at FROM sessions ORDER BY started_at DESC LIMIT 1;`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 1.10:** ShutdownAsync called before Dispose
  - **Action:** Close Revit and check logs
  - **Expected:** No deadlock, shutdown completes in < 5 seconds
  - **Log Check:** No errors like "Session shutdown had issues"
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

---

## 2. Rule System

**Component:** RuleService, RuleRepository, RuleEvaluator
**Files:** `BIManage/Core/Rules/*`

### ✅ Rule Loading Tests

- [ ] **Test 2.1:** Sample rules imported on first run
  - **Action:** Delete database, restart Revit
  - **Expected:** Rules from `sample-rules.json` imported
  - **Log Check:** `"Imported X rules from sample-rules.json"`
  - **Database Check:** `SELECT COUNT(*) FROM rules;` should be > 0
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 2.2:** Rule cache initialized
  - **Action:** Check logs after startup
  - **Expected:** Cache statistics logged
  - **Log Check:** `"Rule Service initialized successfully: [stats]"`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 2.3:** Rules loaded from database on subsequent runs
  - **Action:** Restart Revit (with existing database)
  - **Expected:** Rules loaded from DB, not reimported
  - **Log Check:** Should NOT see "Imported X rules from sample-rules.json"
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ Rule Evaluation Tests

- [ ] **Test 2.4:** Test Rules command works
  - **Action:** Click "Test Rules" button, select element(s), choose command
  - **Expected:** Dialog shows matched rules with mode/priority/message
  - **Log Check:** `"Testing rules on X elements with command [ID]"`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 2.5:** Category-based rule matching
  - **Action:** Select a Wall, run Test Rules with Delete command
  - **Expected:** "RULE-001: Prevent Wall Deletion" should match
  - **Database Check:** `SELECT * FROM rules WHERE category_id = -2000011;` (Walls)
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 2.6:** Parameter-based rule matching
  - **Action:** Pin an element, run Test Rules with Move command
  - **Expected:** "RULE-006: Protect Pinned Elements" should match
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 2.7:** Command-based rule filtering
  - **Action:** Test same element with different commands
  - **Expected:** Only rules with matching command IDs trigger
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 2.8:** Priority-based mode selection
  - **Action:** Select element matching multiple rules (different modes)
  - **Expected:** Highest priority mode wins (Prevent > Guide > Monitor)
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ Cache Tests

- [ ] **Test 2.9:** Cache hit after first evaluation
  - **Action:** Evaluate same element twice in quick succession
  - **Expected:** Second evaluation uses cached result (faster)
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 2.10:** Cache invalidation on rule update
  - **Action:** Update a rule in database, evaluate element
  - **Expected:** Updated rule applies (cache refreshed)
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

---

## 3. Command Interception

**Component:** CommandInterceptionService, RuleCommandInterceptor
**Files:** `BIManage/Revit/Commands/CommandInterceptionService.cs`, `RuleCommandInterceptor.cs`

### ✅ Monitor Mode Tests

- [ ] **Test 3.1:** Monitor mode logs action silently
  - **Action:** Modify a Door (RULE-003: Monitor Door Changes)
  - **Expected:** Action proceeds, logged to audit_log
  - **User Experience:** No dialog shown
  - **Database Check:** `SELECT * FROM audit_log WHERE rule_id = 'RULE-003' ORDER BY timestamp DESC LIMIT 1;`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 3.2:** Before screenshot captured (Monitor mode)
  - **Action:** Delete a monitored element
  - **Expected:** Screenshot saved before deletion
  - **Database Check:** `SELECT * FROM evidence_capture WHERE capture_stage = 'before';`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 3.3:** After screenshot captured (Monitor mode)
  - **Action:** Complete the deletion
  - **Expected:** Screenshot saved after deletion
  - **Database Check:** `SELECT * FROM evidence_capture WHERE capture_stage = 'after';`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ Guide Mode Tests

- [ ] **Test 3.4:** Guide dialog appears
  - **Action:** Modify a Wall (RULE-002: Guide Structural Framing Changes if matched)
  - **Expected:** Yellow warning dialog with message
  - **User Experience:** Dialog shows rule message, "Proceed" and "Cancel" buttons
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 3.5:** User proceeds with action
  - **Action:** Click "Proceed" in Guide dialog
  - **Expected:** Action completes, logged as "allowed"
  - **Database Check:** `SELECT action_allowed FROM audit_log ORDER BY timestamp DESC LIMIT 1;` should be 1
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 3.6:** User cancels action
  - **Action:** Click "Cancel" in Guide dialog
  - **Expected:** Action aborted, logged as "denied"
  - **Database Check:** `SELECT action_allowed FROM audit_log ORDER BY timestamp DESC LIMIT 1;` should be 0
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 3.7:** Comment required (Guide mode)
  - **Action:** Trigger rule with `requireComment = true`
  - **Expected:** Comment field enabled, "Proceed" disabled until text entered
  - **Database Check:** `SELECT user_comment FROM audit_log ORDER BY timestamp DESC LIMIT 1;` should contain comment
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ Prevent Mode Tests

- [ ] **Test 3.8:** Prevent dialog appears
  - **Action:** Delete a Level (RULE-004: Prevent Level Deletion)
  - **Expected:** Red blocking dialog with message
  - **User Experience:** Dialog shows error, "Cancel" and "Override (Admin)" buttons
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 3.9:** User cancels prevent
  - **Action:** Click "Cancel" in Prevent dialog
  - **Expected:** Action blocked, logged as "denied"
  - **Database Check:** `SELECT action_allowed FROM audit_log ORDER BY timestamp DESC LIMIT 1;` should be 0
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 3.10:** Admin override with correct password
  - **Action:** Click "Override (Admin)", enter correct password
  - **Expected:** Action proceeds, logged with override details
  - **Database Check:** `SELECT override_type, override_granted_by FROM audit_log ORDER BY timestamp DESC LIMIT 1;`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 3.11:** Admin override with incorrect password
  - **Action:** Click "Override (Admin)", enter wrong password
  - **Expected:** Error message, action still blocked
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 3.12:** Admin override not allowed (rule setting)
  - **Action:** Trigger rule with `allowAdminOverride = false`
  - **Expected:** Only "Cancel" button shown, no override option
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

---

## 4. RBAC & OTP

**Component:** PasswordManager, OtpService
**Files:** `BIManage/Core/Protection/PasswordManager.cs`, `BIManage/Core/Protection/OtpService.cs`

### ✅ Password Management Tests

- [ ] **Test 4.1:** Set admin password
  - **Action:** Use PasswordConfigDialog to set first admin password
  - **Expected:** Password hashed with PBKDF2, stored in database
  - **Database Check:** `SELECT password_hash, password_salt FROM protection_settings WHERE id = 1;`
  - **Security Check:** Hash should be 64 hex chars, salt should be 32 hex chars
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 4.2:** Verify password (correct)
  - **Action:** Enter correct admin password for override
  - **Expected:** Authentication succeeds
  - **Log Check:** No password verification errors
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 4.3:** Verify password (incorrect)
  - **Action:** Enter wrong admin password
  - **Expected:** Authentication fails, action blocked
  - **Database Check:** Failed attempt NOT logged in audit_log
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 4.4:** Change admin password
  - **Action:** Update password via PasswordConfigDialog
  - **Expected:** Old password required, new password hashed
  - **Database Check:** `password_hash` and `password_salt` should change
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ OTP (One-Time Password) Tests

- [ ] **Test 4.5:** Generate OTP code
  - **Action:** Admin generates temporary OTP for user
  - **Expected:** 6-digit code created, valid for configurable duration
  - **Database Check:** `SELECT otp_code, expires_at FROM otp_codes WHERE used = 0;`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 4.6:** Use OTP for override (valid)
  - **Action:** Enter valid OTP code in Prevent dialog
  - **Expected:** Override granted, OTP marked as used
  - **Database Check:** `SELECT used, used_at FROM otp_codes WHERE otp_code = '[CODE]';`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 4.7:** Use OTP for override (expired)
  - **Action:** Wait for OTP to expire, try to use it
  - **Expected:** Override denied, "OTP expired" error
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 4.8:** Use OTP twice (replay attack)
  - **Action:** Try to reuse same OTP code
  - **Expected:** Second use fails, "OTP already used" error
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 4.9:** OTP cleanup on expiration
  - **Action:** Check database after OTP expiration time
  - **Expected:** Expired OTPs removed or marked invalid
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ Role-Based Access Tests

- [ ] **Test 4.10:** Admin role assigned
  - **Action:** Check user_roles table for admin assignments
  - **Expected:** Admin users have role = 'admin'
  - **Database Check:** `SELECT * FROM user_roles WHERE role = 'admin';`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 4.11:** Non-admin cannot override Prevent
  - **Action:** Regular user tries to override without admin password/OTP
  - **Expected:** Override denied
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

---

## 5. Evidence Capture

**Component:** ScreenshotService, EvidenceUploadQueue
**Files:** `BIManage/Core/Evidence/*`

### ✅ Screenshot Capture Tests

- [ ] **Test 5.1:** Screenshot captured successfully
  - **Action:** Click "Test Screenshot" button on ribbon
  - **Expected:** PNG file saved to temp folder
  - **Log Check:** `"Screenshot captured: [filename], SHA-256: [hash]"`
  - **File Check:** File exists at reported path
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 5.2:** Screenshot quality (compression)
  - **Action:** Check file size of captured screenshot
  - **Expected:** File size reasonable (90% JPEG quality, then PNG)
  - **File Check:** Size should be < 5MB for typical Revit window
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 5.3:** Before screenshot captures correct state
  - **Action:** Trigger rule, check before screenshot
  - **Expected:** Screenshot shows element BEFORE modification
  - **Database Check:** `SELECT file_path, capture_stage FROM evidence_capture WHERE capture_stage = 'before';`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 5.4:** After screenshot captures correct state
  - **Action:** Complete action, check after screenshot
  - **Expected:** Screenshot shows element AFTER modification (or deleted)
  - **Database Check:** `SELECT file_path, capture_stage FROM evidence_capture WHERE capture_stage = 'after';`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ File Integrity Tests

- [ ] **Test 5.5:** SHA-256 hash calculated
  - **Action:** Capture screenshot, check database
  - **Expected:** `file_hash_sha256` column populated with 64-char hex string
  - **Database Check:** `SELECT file_hash_sha256 FROM evidence_capture ORDER BY captured_at DESC LIMIT 1;`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 5.6:** Hash matches file content
  - **Action:** Calculate hash of screenshot file independently
  - **Expected:** Manually calculated hash matches database hash
  - **Command:** `certutil -hashfile [path] SHA256` (Windows)
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 5.7:** Tamper detection (file modified)
  - **Action:** Modify screenshot file after capture, try to upload
  - **Expected:** Upload fails with "File hash mismatch" error
  - **Log Check:** `"File integrity check FAILED for [evidenceId]"`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ Screenshot Throttling Tests

- [ ] **Test 5.8:** Throttling prevents duplicates
  - **Action:** Delete 10 walls rapidly (< 5 seconds between each)
  - **Expected:** Only first deletion captures screenshots (before + after)
  - **Database Check:** `SELECT COUNT(*) FROM evidence_capture WHERE rule_id = 'RULE-001';` should be 2, not 20
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 5.9:** Throttling expires after window
  - **Action:** Delete wall, wait 6 seconds, delete another wall
  - **Expected:** Both deletions capture screenshots
  - **Database Check:** Should have 4 screenshots total (2 per deletion)
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 5.10:** Different rules not throttled together
  - **Action:** Delete wall, immediately delete door
  - **Expected:** Both actions capture screenshots (different rule IDs)
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ Upload Queue Tests

- [ ] **Test 5.11:** Evidence queued for upload
  - **Action:** Capture screenshot, check database
  - **Expected:** `upload_status = 'pending'`
  - **Database Check:** `SELECT upload_status FROM evidence_capture ORDER BY captured_at DESC LIMIT 1;`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 5.12:** Upload retry with exponential backoff
  - **Action:** Simulate network failure (disconnect), trigger screenshot
  - **Expected:** Retries with increasing delays (5s, 10s, 20s, 40s, 80s)
  - **Log Check:** `"Retry #X for evidence [ID] - waiting [delay]s before attempt"`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 5.13:** Upload succeeds after retry
  - **Action:** Reconnect network after failed upload
  - **Expected:** Next retry succeeds, `upload_status = 'uploaded'`
  - **Database Check:** `SELECT upload_status, retry_count FROM evidence_capture WHERE evidence_id = '[ID]';`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 5.14:** Max retry limit enforced
  - **Action:** Keep network disconnected through all retries
  - **Expected:** After 3 attempts, marked as failed
  - **Database Check:** `SELECT upload_status, retry_count FROM evidence_capture;` - retry_count should be ≤ 3
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

---

## 6. Audit Logging

**Component:** AuditService, AuditRepository
**Files:** `BIManage/Core/Protection/AuditService.cs`, `BIManage/Data/SQLite/AuditRepository.cs`

### ✅ Audit Entry Tests

- [ ] **Test 6.1:** Monitor action logged
  - **Action:** Perform monitored action (e.g., modify door)
  - **Expected:** Audit entry created with all details
  - **Database Check:**
    ```sql
    SELECT rule_id, action_type, action_allowed, element_ids, username
    FROM audit_log
    ORDER BY timestamp DESC LIMIT 1;
    ```
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 6.2:** Guide action logged (allowed)
  - **Action:** Proceed through Guide dialog
  - **Expected:** `action_allowed = 1`, user comment recorded
  - **Database Check:** `SELECT action_allowed, user_comment FROM audit_log ORDER BY timestamp DESC LIMIT 1;`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 6.3:** Guide action logged (denied)
  - **Action:** Cancel in Guide dialog
  - **Expected:** `action_allowed = 0`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 6.4:** Prevent action logged (blocked)
  - **Action:** Cancel in Prevent dialog
  - **Expected:** `action_allowed = 0`, `override_type = NULL`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 6.5:** Prevent action logged (overridden)
  - **Action:** Override with admin password
  - **Expected:** `action_allowed = 1`, `override_type = 'password'`, `override_granted_by` = username
  - **Database Check:**
    ```sql
    SELECT override_type, override_granted_by
    FROM audit_log
    WHERE action_allowed = 1 AND rule_mode = 3
    ORDER BY timestamp DESC LIMIT 1;
    ```
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 6.6:** OTP override logged
  - **Action:** Override with OTP code
  - **Expected:** `override_type = 'otp'`, OTP code linked in audit entry
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ Evidence Linking Tests

- [ ] **Test 6.7:** Audit entry linked to evidence
  - **Action:** Trigger rule with screenshot capture
  - **Expected:** `evidence_capture.audit_log_id` matches `audit_log.audit_log_id`
  - **Database Check:**
    ```sql
    SELECT a.audit_log_id, e.evidence_id, e.capture_type
    FROM audit_log a
    JOIN evidence_capture e ON a.audit_log_id = e.audit_log_id
    ORDER BY a.timestamp DESC LIMIT 1;
    ```
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

### ✅ Session Linking Tests

- [ ] **Test 6.8:** Audit entry linked to session
  - **Action:** Perform any audited action
  - **Expected:** `audit_log.session_id` matches active session
  - **Database Check:**
    ```sql
    SELECT a.session_id, s.username, s.computer_name
    FROM audit_log a
    JOIN sessions s ON a.session_id = s.session_id
    ORDER BY a.timestamp DESC LIMIT 1;
    ```
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

---

## 7. Database Schema

**Component:** Schema Migration
**Files:** `BIManage/Data/SQLite/SchemaMigration.cs`, `Schema.sql`, `Schema_Persistence.sql`

### ✅ Schema Tests

- [ ] **Test 7.1:** All tables created
  - **Action:** Check database after first run
  - **Expected:** All 15 tables exist:
    - `schema_version`
    - `rules`, `command_settings`, `protection_settings`
    - `audit_log`, `user_roles`, `override_log`, `otp_codes`
    - `sessions`, `session_heartbeats`, `session_documents`
    - `evidence_capture`, `offline_queue`, `sync_history`
    - `events`
  - **Database Check:** `.tables` in SQLite or `SELECT name FROM sqlite_master WHERE type='table';`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 7.2:** All indices created
  - **Action:** Check database indices
  - **Expected:** 18 indices exist for performance
  - **Database Check:** `SELECT name FROM sqlite_master WHERE type='index';`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 7.3:** Foreign key constraints enforced
  - **Action:** Try to insert audit_log with invalid session_id
  - **Expected:** Insert fails with foreign key error
  - **Database Check:** `PRAGMA foreign_keys;` should return 1 (ON)
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 7.4:** Migration from v0 to v3 works
  - **Action:** Delete database, restart Revit
  - **Expected:** Schema v3 created from scratch
  - **Log Check:** `"Initializing database schema (version 3)"`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 7.5:** revit_username column exists
  - **Action:** Check sessions table schema
  - **Expected:** Column `revit_username TEXT NULL` present
  - **Database Check:** `PRAGMA table_info(sessions);`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

---

## 8. Error Handling

**Component:** Global Error Handling
**Files:** All repositories, services, bootstrapper

### ✅ Robustness Tests

- [ ] **Test 8.1:** Database file locked
  - **Action:** Open database in another tool (DB Browser for SQLite), trigger action
  - **Expected:** Graceful error, retry or queue operation
  - **Log Check:** Should log database busy error, not crash
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 8.2:** Database file deleted during session
  - **Action:** Delete database file while Revit is running
  - **Expected:** New operations recreate database or log error
  - **Log Check:** Error logged but application doesn't crash
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 8.3:** Screenshot folder inaccessible
  - **Action:** Make temp folder read-only, trigger screenshot
  - **Expected:** Error logged, action still proceeds (screenshot optional)
  - **Log Check:** `"Failed to capture screenshot"` but no application crash
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 8.4:** Network unavailable for uploads
  - **Action:** Disconnect network, trigger evidence capture
  - **Expected:** Upload queued for retry, no blocking
  - **Database Check:** `SELECT upload_status FROM evidence_capture;` should be 'pending'
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 8.5:** Corrupted rule in database
  - **Action:** Manually insert malformed JSON in `rule_data` column
  - **Expected:** Rule skipped with warning, other rules still work
  - **Log Check:** `"Failed to deserialize rule [ID]"`
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

- [ ] **Test 8.6:** Memory leak prevention
  - **Action:** Run Revit for 1+ hour with frequent rule triggers
  - **Expected:** Memory usage stable, no continuous growth
  - **Task Manager:** Monitor BIManageRevit memory usage over time
  - **Status:** ⬜ Not Tested / ✅ Pass / ❌ Fail

---

## 📊 Test Results Summary

| Component | Total Tests | Passed | Failed | Not Tested |
|-----------|-------------|--------|--------|------------|
| Session Tracking | 10 | 0 | 0 | 10 |
| Rule System | 10 | 0 | 0 | 10 |
| Command Interception | 12 | 0 | 0 | 12 |
| RBAC & OTP | 11 | 0 | 0 | 11 |
| Evidence Capture | 14 | 0 | 0 | 14 |
| Audit Logging | 8 | 0 | 0 | 8 |
| Database Schema | 5 | 0 | 0 | 5 |
| Error Handling | 6 | 0 | 0 | 6 |
| **TOTAL** | **76** | **0** | **0** | **76** |

---

## 🔧 Useful Database Queries

### Check Active Sessions
```sql
SELECT session_id, username, revit_username, started_at, last_heartbeat, is_active
FROM sessions
WHERE is_active = 1;
```

### Check Recent Audit Logs
```sql
SELECT
    timestamp,
    rule_id,
    action_type,
    action_allowed,
    override_type,
    username
FROM audit_log
ORDER BY timestamp DESC
LIMIT 20;
```

### Check Evidence Capture Status
```sql
SELECT
    evidence_id,
    rule_name,
    capture_stage,
    upload_status,
    retry_count,
    file_hash_sha256
FROM evidence_capture
ORDER BY captured_at DESC
LIMIT 10;
```

### Check OTP Codes
```sql
SELECT
    otp_code,
    created_by,
    expires_at,
    used,
    used_at,
    used_by
FROM otp_codes
ORDER BY created_at DESC;
```

### Check Crashed Sessions
```sql
SELECT
    session_id,
    username,
    started_at,
    ended_at,
    crash_detected
FROM sessions
WHERE crash_detected = 1
ORDER BY started_at DESC;
```

---

## 📝 Test Execution Notes

**Database Location:**
```
C:\Users\PC\AppData\Local\BIManageRevit\Logs\bimanage.db
```

**Log File Location:**
```
C:\Users\PC\AppData\Local\BIManageRevit\Logs\bimanage-YYYY-MM-DD.log
```

**Evidence Folder:**
```
%TEMP%\BIManage\Evidence\
```

**Recommended Testing Order:**
1. Session Tracking (validates core persistence)
2. Database Schema (validates data integrity)
3. Rule System (validates business logic)
4. Command Interception (validates user interaction)
5. RBAC & OTP (validates security)
6. Evidence Capture (validates forensics)
7. Audit Logging (validates compliance)
8. Error Handling (validates robustness)

---

## ✅ Sign-Off

**Tester Name:** _______________________
**Test Date:** _______________________
**Revit Version:** _______________________
**Overall Result:** ⬜ Pass / ⬜ Fail / ⬜ Partial
**Notes:**

---

**Last Updated:** 2026-01-16
**Version:** 1.0
**Document Status:** Ready for Use
