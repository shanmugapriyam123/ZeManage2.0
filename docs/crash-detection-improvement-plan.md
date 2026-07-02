# Crash Detection Algorithm Improvements

**Date:** 2026-01-29
**Status:** 🔍 Planning - Fix false positive crash detection
**Task:** Improve crash detection to handle multiple concurrent Revit sessions correctly

---

## Problem Statement

**Current Issue:**
The crash detection algorithm incorrectly marks earlier sessions as "crashed" when a new Revit session opens, even when:
- Multiple Revit instances are legitimately running concurrently
- Sessions are on different machines
- Sessions belong to different users

**User Requirement:**
> "need to hone the crash detection algorithm. Its now marking the earlier session as crashed when a new session is opened.. However its possible to open two or more active sessions. Let's plan how to detect clashes. Will get the help from heartbeat as well."

**Root Causes Identified:**

1. **No Machine-Level Filtering**: Query marks ALL active sessions as crashed globally, not just on current machine
2. **No Session Ownership**: No mechanism to verify which process "owns" a session
3. **Universal Timeout**: 5-minute heartbeat timeout applies to all sessions without considering concurrent instances
4. **Race Conditions**: Multiple Revit instances on the same machine can interfere with each other

---

## Current Implementation Analysis

### Files Involved:

**SessionRepository.cs** ([BIManage/Data/SQLite/SessionRepository.cs](../BIManage/Data/SQLite/SessionRepository.cs))
- Lines 168-204: `DetectCrashedSessionsAsync()` - Main crash detection logic
- Lines 327-398: `EndSessionAsync()` - Normal session termination
- Lines 499-568: `RecordHeartbeatAsync()` - Heartbeat recording (every 60 seconds)

**IdlingService.cs** ([BIManage/Revit/Idling/IdlingService.cs](../BIManage/Revit/Idling/IdlingService.cs))
- Lines 171-224: `ExecutePeriodicHeartbeat()` - Triggers heartbeat every 60 seconds

**Schema_Persistence.sql** ([BIManage/Data/SQLite/Schema_Persistence.sql](../BIManage/Data/SQLite/Schema_Persistence.sql))
- Lines 17-39: `sessions` table - Contains `machine_id`, `is_active`, `crash_detected`, `status`, `last_heartbeat`
- Lines 99-107: `session_heartbeats` table - Tracks individual heartbeat records

### Current Detection Query (PROBLEMATIC):

```sql
-- SessionRepository.cs, lines 176-183
UPDATE sessions
SET crash_detected = 1,
    is_active = 0,
    status = 'Crashed',
    ended_at = last_heartbeat
WHERE is_active = 1
AND datetime(last_heartbeat) < datetime('now', @timeout)
-- ❌ MISSING: machine_id filter
```

**Critical Flaw:** No `machine_id` filtering means:
- Sessions on Machine A can be marked crashed when Machine B opens a new session
- User's concurrent Revit instances on same machine interfere with each other

### Current Heartbeat Workflow:

1. **Startup** (Application.OnStartup):
   - Creates new session → `CreateSessionAsync()`
   - Detects crashed sessions → `DetectCrashedSessionsAsync()` ❌ **Marks ALL stale sessions globally**

2. **Periodic** (IdlingService every 60 seconds):
   - Records heartbeat → `RecordHeartbeatAsync()`
   - Detects crashed sessions → `DetectCrashedSessionsAsync()` ❌ **Marks ALL stale sessions globally**

3. **Shutdown** (Application.OnShutdown):
   - Ends session normally → `EndSessionAsync()`
   - Sets `is_active = 0`, `status = 'Closed'`

---

## Proposed Solution

### Design Principles:

1. **Machine Isolation**: Each machine only manages crash detection for its own sessions
2. **Session Ownership**: Use process ID to verify session ownership
3. **Concurrent Session Support**: Multiple Revit instances on same machine don't interfere
4. **Heartbeat Integrity**: Heartbeats only update if session is owned by current process

### Implementation Strategy:

#### Phase 1: Add Machine-Level Filtering (Quick Fix)

**Immediate Impact:** Prevents cross-machine false positives

**Changes Required:**
1. **DetectCrashedSessionsAsync()** - Add machine_id filter
2. **RecordHeartbeatAsync()** - Verify machine_id matches before updating
3. **GetActiveSessionsAsync()** - Add optional machine_id filter

#### Phase 2: Add Process-Level Ownership (Full Solution)

**Prevents:** Concurrent sessions on same machine from interfering

**Database Schema Changes (v14 Migration):**

```sql
-- Add process_id to sessions table
ALTER TABLE sessions ADD COLUMN process_id INTEGER;

-- Add process_id to heartbeats table
ALTER TABLE session_heartbeats ADD COLUMN process_id INTEGER;

-- Index for ownership queries
CREATE INDEX IF NOT EXISTS idx_sessions_machine_process
ON sessions(machine_id, process_id, is_active);
```

**Session Ownership Logic:**
1. Store `Process.GetCurrentProcess().Id` when creating session
2. Verify process_id matches before allowing heartbeat updates
3. Only mark sessions as crashed if:
   - Heartbeat is stale (> 5 minutes)
   - Process no longer exists on system (via process verification)
   - Session is on current machine

#### Phase 3: Process Verification Helper

**Purpose:** Check if a process ID still exists on the system

```csharp
// BIManage/Common/Helpers/ProcessHelper.cs
public static class ProcessHelper
{
    public static bool IsProcessRunning(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            // Process with given ID doesn't exist
            return false;
        }
        catch (Exception ex)
        {
            // Log error but assume process is not running
            return false;
        }
    }
}
```

---

## Detailed Implementation Steps

### Step 1: Create v14 Schema Migration

**File:** `BIManage/Data/SQLite/Schema_Persistence.sql`

**Action:** Add process_id column and index

```sql
-- ========================================
-- Version 14: Process-Level Session Ownership
-- ========================================
-- Migration v13 → v14
-- Date: 2026-01-29
-- Purpose: Add process ID tracking for concurrent session support

-- Add process_id to sessions table
ALTER TABLE sessions ADD COLUMN process_id INTEGER;

-- Add process_id to session_heartbeats table
ALTER TABLE session_heartbeats ADD COLUMN process_id INTEGER;

-- Create index for ownership queries
CREATE INDEX IF NOT EXISTS idx_sessions_machine_process
ON sessions(machine_id, process_id, is_active);

-- Update schema version
UPDATE schema_info SET version = 14, updated_at = CURRENT_TIMESTAMP;
```

---

### Step 2: Update SchemaMigration.cs

**File:** `BIManage/Data/SQLite/SchemaMigration.cs`

**Action:** Add migration method for v13 → v14

```csharp
private void MigrateToVersion14(SQLiteConnection connection)
{
    Logger.LogInfo("Migrating schema from v13 to v14 (process-level session ownership)...");

    using (var transaction = connection.BeginTransaction())
    {
        try
        {
            // Add process_id to sessions table
            ExecuteNonQuery(connection,
                "ALTER TABLE sessions ADD COLUMN process_id INTEGER");

            // Add process_id to session_heartbeats table
            ExecuteNonQuery(connection,
                "ALTER TABLE session_heartbeats ADD COLUMN process_id INTEGER");

            // Create index for ownership queries
            ExecuteNonQuery(connection,
                @"CREATE INDEX IF NOT EXISTS idx_sessions_machine_process
                  ON sessions(machine_id, process_id, is_active)");

            // Update schema version
            ExecuteNonQuery(connection,
                "UPDATE schema_info SET version = 14, updated_at = CURRENT_TIMESTAMP");

            transaction.Commit();
            Logger.LogInfo("Successfully migrated to v14");
        }
        catch (Exception ex)
        {
            transaction.Rollback();
            Logger.LogError($"Failed to migrate to v14: {ex.Message}", ex);
            throw;
        }
    }
}
```

**Update GetCurrentVersion():** Change `CURRENT_VERSION = 14`

---

### Step 3: Create ProcessHelper

**File:** `BIManage/Common/Helpers/ProcessHelper.cs` (NEW)

```csharp
using System;
using System.Diagnostics;

namespace BIManage.Common.Helpers
{
    /// <summary>
    /// Helper methods for process verification and management
    /// </summary>
    public static class ProcessHelper
    {
        /// <summary>
        /// Checks if a process with the given ID is currently running
        /// </summary>
        /// <param name="processId">Process ID to check</param>
        /// <returns>True if process exists and hasn't exited, false otherwise</returns>
        public static bool IsProcessRunning(int processId)
        {
            try
            {
                var process = Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                // Process with given ID doesn't exist
                return false;
            }
            catch (InvalidOperationException)
            {
                // Process has already exited
                return false;
            }
            catch (Exception)
            {
                // Any other error - assume process is not running
                return false;
            }
        }

        /// <summary>
        /// Gets the current process ID
        /// </summary>
        public static int GetCurrentProcessId()
        {
            return Process.GetCurrentProcess().Id;
        }
    }
}
```

---

### Step 4: Update RevitSession Model

**File:** `BIManage/Data/SQLite/Models/RevitSession.cs`

**Action:** Add ProcessId property

```csharp
public class RevitSession
{
    // ... existing properties ...

    public int? ProcessId { get; set; }  // NEW: Process ID for ownership verification
}
```

---

### Step 5: Update SessionRepository - CreateSessionAsync

**File:** `BIManage/Data/SQLite/SessionRepository.cs`

**Method:** `CreateSessionAsync()` (around line 72)

**Changes:**

```csharp
public async Task<RevitSession> CreateSessionAsync(
    string revitVersion,
    string revitBuild,
    string? revitUsername,
    string? documentTitle)
{
    // ... existing code ...

    var session = new RevitSession
    {
        SessionId = sessionId,
        MachineId = GetMachineId(),
        ProcessId = ProcessHelper.GetCurrentProcessId(),  // NEW: Store process ID
        // ... rest of properties ...
    };

    var sql = @"
        INSERT INTO sessions (
            session_id, machine_id, process_id,  -- NEW: process_id
            revit_version, revit_build, revit_username,
            document_title, started_at, is_active, status,
            computer_name, journal_file_name, loaded_plugin_count, last_heartbeat
        ) VALUES (
            @SessionId, @MachineId, @ProcessId,  -- NEW: @ProcessId
            @RevitVersion, @RevitBuild, @RevitUsername,
            @DocumentTitle, @StartedAt, 1, 'Active',
            @ComputerName, @JournalFileName, @LoadedPluginCount, @LastHeartbeat
        )";

    await ExecuteNonQueryAsync(sql, session);
    return session;
}
```

---

### Step 6: Update SessionRepository - DetectCrashedSessionsAsync

**File:** `BIManage/Data/SQLite/SessionRepository.cs`

**Method:** `DetectCrashedSessionsAsync()` (lines 168-204)

**CRITICAL CHANGES:**

```csharp
public async Task<int> DetectCrashedSessionsAsync()
{
    try
    {
        var currentMachineId = GetMachineId();  // NEW: Get current machine ID
        var currentProcessId = ProcessHelper.GetCurrentProcessId();  // NEW: Current process
        var timeout = "-5 minutes"; // Sessions without heartbeat for 5 minutes

        // Step 1: Find potentially crashed sessions on THIS MACHINE ONLY
        var getCandidatesSql = @"
            SELECT session_id, process_id
            FROM sessions
            WHERE is_active = 1
            AND machine_id = @MachineId  -- NEW: Only check this machine
            AND datetime(last_heartbeat) < datetime('now', @Timeout)";

        var candidates = new List<(string sessionId, int? processId)>();

        using (var connection = GetConnection())
        {
            await connection.OpenAsync();

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = getCandidatesSql;
                cmd.Parameters.AddWithValue("@MachineId", currentMachineId);
                cmd.Parameters.AddWithValue("@Timeout", timeout);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var sessionId = reader.GetString(0);
                        var processId = reader.IsDBNull(1) ? (int?)null : reader.GetInt32(1);
                        candidates.Add((sessionId, processId));
                    }
                }
            }
        }

        // Step 2: Verify each candidate - only mark as crashed if process is truly dead
        var crashedSessionIds = new List<string>();

        foreach (var (sessionId, processId) in candidates)
        {
            // If no process ID stored (old sessions), mark as crashed based on heartbeat alone
            if (!processId.HasValue)
            {
                crashedSessionIds.Add(sessionId);
                continue;
            }

            // If current process owns the session, DO NOT mark as crashed
            // (Handles edge case where heartbeat hasn't updated yet)
            if (processId.Value == currentProcessId)
            {
                continue;
            }

            // Verify process is actually dead before marking as crashed
            if (!ProcessHelper.IsProcessRunning(processId.Value))
            {
                crashedSessionIds.Add(sessionId);
            }
        }

        // Step 3: Mark verified crashed sessions
        if (crashedSessionIds.Count == 0)
        {
            return 0;
        }

        var updateSql = @"
            UPDATE sessions
            SET crash_detected = 1,
                is_active = 0,
                status = 'Crashed',
                ended_at = last_heartbeat
            WHERE session_id = @SessionId";

        using (var connection = GetConnection())
        {
            await connection.OpenAsync();

            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    foreach (var sessionId in crashedSessionIds)
                    {
                        using (var cmd = connection.CreateCommand())
                        {
                            cmd.CommandText = updateSql;
                            cmd.Parameters.AddWithValue("@SessionId", sessionId);
                            await cmd.ExecuteNonQueryAsync();
                        }
                    }

                    transaction.Commit();
                    Logger?.LogInfo($"Detected and marked {crashedSessionIds.Count} crashed session(s) on machine {currentMachineId}");
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    Logger?.LogError($"Failed to mark crashed sessions: {ex.Message}", ex);
                    throw;
                }
            }
        }

        return crashedSessionIds.Count;
    }
    catch (Exception ex)
    {
        Logger?.LogError($"Error detecting crashed sessions: {ex.Message}", ex);
        return 0;
    }
}
```

---

### Step 7: Update SessionRepository - RecordHeartbeatAsync

**File:** `BIManage/Data/SQLite/SessionRepository.cs`

**Method:** `RecordHeartbeatAsync()` (lines 499-568)

**CRITICAL CHANGES:**

```csharp
public async Task RecordHeartbeatAsync(string sessionId)
{
    try
    {
        var currentProcessId = ProcessHelper.GetCurrentProcessId();  // NEW: Verify ownership

        // Step 1: Verify session ownership before updating heartbeat
        var verifySql = @"
            SELECT process_id, machine_id
            FROM sessions
            WHERE session_id = @SessionId
            AND is_active = 1";

        using (var connection = GetConnection())
        {
            await connection.OpenAsync();

            int? storedProcessId;
            string storedMachineId;

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = verifySql;
                cmd.Parameters.AddWithValue("@SessionId", sessionId);

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    if (!await reader.ReadAsync())
                    {
                        // Session not found or not active
                        Logger?.LogWarning($"Cannot record heartbeat: session {sessionId} not found or inactive");
                        return;
                    }

                    storedProcessId = reader.IsDBNull(0) ? (int?)null : reader.GetInt32(0);
                    storedMachineId = reader.GetString(1);
                }
            }

            // Step 2: Verify ownership
            var currentMachineId = GetMachineId();

            if (storedMachineId != currentMachineId)
            {
                Logger?.LogError($"Heartbeat denied: session {sessionId} belongs to different machine (stored: {storedMachineId}, current: {currentMachineId})");
                return;
            }

            if (storedProcessId.HasValue && storedProcessId.Value != currentProcessId)
            {
                Logger?.LogError($"Heartbeat denied: session {sessionId} belongs to different process (stored: {storedProcessId}, current: {currentProcessId})");
                return;
            }

            // Step 3: Update heartbeat (only if ownership verified)
            var updateSql = @"
                UPDATE sessions
                SET last_heartbeat = @Heartbeat
                WHERE session_id = @SessionId
                AND is_active = 1";

            var insertHeartbeatSql = @"
                INSERT INTO session_heartbeats (
                    session_id, heartbeat_at, machine_id, process_id
                ) VALUES (
                    @SessionId, @HeartbeatAt, @MachineId, @ProcessId
                )";

            using (var transaction = connection.BeginTransaction())
            {
                try
                {
                    var now = DateTime.UtcNow;

                    // Update session heartbeat timestamp
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = updateSql;
                        cmd.Parameters.AddWithValue("@Heartbeat", now);
                        cmd.Parameters.AddWithValue("@SessionId", sessionId);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    // Insert heartbeat record
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = insertHeartbeatSql;
                        cmd.Parameters.AddWithValue("@SessionId", sessionId);
                        cmd.Parameters.AddWithValue("@HeartbeatAt", now);
                        cmd.Parameters.AddWithValue("@MachineId", currentMachineId);
                        cmd.Parameters.AddWithValue("@ProcessId", currentProcessId);
                        await cmd.ExecuteNonQueryAsync();
                    }

                    transaction.Commit();
                }
                catch (Exception ex)
                {
                    transaction.Rollback();
                    Logger?.LogError($"Failed to record heartbeat: {ex.Message}", ex);
                    throw;
                }
            }
        }
    }
    catch (Exception ex)
    {
        Logger?.LogError($"Error recording heartbeat for session {sessionId}: {ex.Message}", ex);
    }
}
```

---

### Step 8: Update SessionRepository - EndSessionAsync

**File:** `BIManage/Data/SQLite/SessionRepository.cs`

**Method:** `EndSessionAsync()` (lines 327-398)

**Changes:** Add ownership verification (similar to RecordHeartbeatAsync)

```csharp
public async Task EndSessionAsync(string sessionId)
{
    try
    {
        var currentProcessId = ProcessHelper.GetCurrentProcessId();  // NEW: Verify ownership
        var currentMachineId = GetMachineId();

        // Verify ownership before allowing end session
        var verifySql = @"
            SELECT process_id, machine_id
            FROM sessions
            WHERE session_id = @SessionId
            AND is_active = 1";

        using (var connection = GetConnection())
        {
            await connection.OpenAsync();

            // ... ownership verification similar to RecordHeartbeatAsync ...

            var updateSql = @"
                UPDATE sessions
                SET is_active = 0,
                    status = 'Closed',
                    ended_at = @EndedAt
                WHERE session_id = @SessionId
                AND is_active = 1";

            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = updateSql;
                cmd.Parameters.AddWithValue("@EndedAt", DateTime.UtcNow);
                cmd.Parameters.AddWithValue("@SessionId", sessionId);
                await cmd.ExecuteNonQueryAsync();
            }

            Logger?.LogInfo($"Session {sessionId} ended normally");
        }
    }
    catch (Exception ex)
    {
        Logger?.LogError($"Error ending session {sessionId}: {ex.Message}", ex);
    }
}
```

---

## Verification Steps

### Test Scenario 1: Single Session (Baseline)
1. Open Revit
2. Wait for 2-3 heartbeats (check logs)
3. Close Revit normally
4. Verify session status is "Closed", NOT "Crashed"

### Test Scenario 2: Multiple Concurrent Sessions (Same Machine)
1. Open Revit instance 1
2. Open Revit instance 2 (different model)
3. Wait 2 minutes
4. Verify both sessions have `is_active = 1`
5. Check logs - no false "crashed" detections
6. Close instance 1
7. Verify instance 1 = "Closed", instance 2 still "Active"

### Test Scenario 3: Simulated Crash
1. Open Revit
2. Kill Revit process via Task Manager (simulate crash)
3. Wait 6 minutes (> 5 minute timeout)
4. Open new Revit instance
5. Verify old session detected as "Crashed"
6. Verify new session is "Active"

### Test Scenario 4: Machine Isolation
1. Machine A: Open Revit, leave running
2. Machine B: Open Revit
3. Wait 2 minutes
4. Verify Machine B does NOT mark Machine A's session as crashed
5. Check logs on both machines

### Database Verification Queries:

```sql
-- Check active sessions per machine
SELECT machine_id, process_id, COUNT(*) as active_count
FROM sessions
WHERE is_active = 1
GROUP BY machine_id, process_id;

-- Check heartbeat history
SELECT s.session_id, s.machine_id, s.process_id, s.status,
       COUNT(h.heartbeat_id) as heartbeat_count,
       MAX(h.heartbeat_at) as last_heartbeat
FROM sessions s
LEFT JOIN session_heartbeats h ON s.session_id = h.session_id
WHERE s.started_at > datetime('now', '-1 hour')
GROUP BY s.session_id;

-- Check for any crashed sessions
SELECT session_id, machine_id, process_id, status, crash_detected,
       started_at, ended_at, last_heartbeat
FROM sessions
WHERE crash_detected = 1
ORDER BY ended_at DESC
LIMIT 10;
```

---

## Benefits

1. **Accuracy**: No more false positive crash detections
2. **Machine Isolation**: Sessions on different machines don't interfere
3. **Concurrent Support**: Multiple Revit instances work correctly
4. **Process Verification**: Only marks sessions crashed when process is truly dead
5. **Ownership Security**: Sessions can only be modified by owning process
6. **Backwards Compatible**: Handles old sessions without process_id gracefully

---

## Rollback Plan

If issues occur:

1. **Database**: Rollback schema to v13 using backup
2. **Code**: Revert SessionRepository.cs changes
3. **Quick Fix**: Temporarily disable crash detection:
   ```csharp
   // In RevitBootstrapper.cs, comment out:
   // await _sessionRepository.DetectCrashedSessionsAsync();
   ```

---

## Files Summary

### Files to Create:
1. `BIManage/Common/Helpers/ProcessHelper.cs` (NEW)

### Files to Modify:
1. `BIManage/Data/SQLite/Schema_Persistence.sql` - Add v14 migration SQL
2. `BIManage/Data/SQLite/SchemaMigration.cs` - Add MigrateToVersion14() method
3. `BIManage/Data/SQLite/Models/RevitSession.cs` - Add ProcessId property
4. `BIManage/Data/SQLite/SessionRepository.cs` - Update 4 methods:
   - CreateSessionAsync()
   - DetectCrashedSessionsAsync() ⚠️ CRITICAL
   - RecordHeartbeatAsync() ⚠️ CRITICAL
   - EndSessionAsync()
