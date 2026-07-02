# Local Persistence - Blocker Fix Implementation Plan

**Selected Option:** Option A - Fix Blockers Now
**Total Effort:** 24-35 hours (3-4 days)
**Start Date:** 2026-01-14

---

## Implementation Order

We'll tackle the blockers in priority order to minimize risk:

1. **Integration Tests** (8-12 hours) - Verify current implementation works correctly
2. **Offline Queue Sync** (10-14 hours) - Critical for data integrity
3. **Performance Benchmarks** (6-9 hours) - Validate acceptable performance
4. **Cache Invalidation** (2-3 hours) - Prevent stale data issues
5. **Cleanup Scheduling** (1-2 hours) - Prevent DB bloat

---

## Phase 1: Integration Tests (8-12 hours)

### Objective
Verify that the persistence layer:
- Survives Revit restarts without data loss
- Handles corrupted databases gracefully
- Maintains data integrity across transactions
- Properly recovers from failures

### Files to Create/Modify

#### 1. Create: `BIManageRevit.Tests/Integration/PersistenceResilienceTests.cs`

**Test Categories:**
- Session persistence across restarts
- Event persistence across restarts
- Offline queue persistence across restarts
- Corrupted database recovery
- Locked database handling
- Schema version mismatch handling
- Transaction rollback verification

**Success Criteria:**
- All tests pass on first run
- Tests can be run repeatedly without cleanup
- Tests verify actual file I/O, not just in-memory operations

#### 2. Enhance: `BIManageRevit.Tests/Integration/PersistenceIntegrationTests.cs`

**Additional Tests:**
- Load testing with realistic data volumes
- Concurrent access scenarios (multiple threads)
- Edge cases (empty DB, missing tables, etc.)

### Implementation Steps

**Step 1.1: Session Persistence Tests** (2-3 hours)
```csharp
[TestFixture]
public class SessionPersistenceTests
{
    private string _testDbPath;
    private SessionRepository _repository;
    private ILogger _logger;

    [SetUp]
    public void Setup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_session_{Guid.NewGuid()}.db");
        _logger = new TestLogger();
        _repository = new SessionRepository(_testDbPath, _logger);
    }

    [TearDown]
    public void Teardown()
    {
        _repository?.Dispose();
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
    }

    [Test]
    public async Task Session_SurvivesRepositoryDisposal()
    {
        // Arrange
        var sessionId = "test-session-" + Guid.NewGuid();
        var session = await _repository.StartSessionAsync(
            sessionId: sessionId,
            revitVersion: "2025",
            revitBuild: "20.0.0.1",
            username: "test-user",
            userEmail: "test@example.com",
            computerName: "TEST-PC"
        );

        Assert.IsNotNull(session);
        Assert.AreEqual(sessionId, session.SessionId);

        // Act - Dispose repository (simulates Revit shutdown)
        _repository.Dispose();

        // Create new repository instance (simulates Revit restart)
        _repository = new SessionRepository(_testDbPath, _logger);

        // Assert - Session should still exist
        var retrieved = await _repository.GetSessionAsync(sessionId);
        Assert.IsNotNull(retrieved);
        Assert.AreEqual(sessionId, retrieved.SessionId);
        Assert.AreEqual("test-user", retrieved.Username);
    }

    [Test]
    public async Task Session_HeartbeatsPersistedAcrossRestart()
    {
        // Arrange
        var sessionId = "test-session-" + Guid.NewGuid();
        await _repository.StartSessionAsync(sessionId, "2025", "20.0.0.1", "user", null, "PC");

        // Record 3 heartbeats
        await _repository.RecordHeartbeatAsync(sessionId);
        await Task.Delay(100);
        await _repository.RecordHeartbeatAsync(sessionId);
        await Task.Delay(100);
        await _repository.RecordHeartbeatAsync(sessionId);

        var firstLastActive = (await _repository.GetSessionAsync(sessionId)).LastActiveAt;

        // Act - Restart
        _repository.Dispose();
        _repository = new SessionRepository(_testDbPath, _logger);

        // Assert - Last active timestamp should be preserved
        var session = await _repository.GetSessionAsync(sessionId);
        Assert.AreEqual(firstLastActive, session.LastActiveAt);
    }

    [Test]
    public void SessionRepository_HandlesCorruptedDatabase()
    {
        // Arrange - Create corrupted database file
        _repository.Dispose();
        File.WriteAllText(_testDbPath, "CORRUPTED DATA - NOT A VALID SQLITE FILE");

        // Act & Assert - Repository should detect corruption and recreate schema
        Assert.DoesNotThrow(() =>
        {
            _repository = new SessionRepository(_testDbPath, _logger);
        });

        // Verify schema was recreated by attempting to create a session
        Assert.DoesNotThrowAsync(async () =>
        {
            var session = await _repository.StartSessionAsync(
                "test-session", "2025", "20.0.0.1", "user", null, "PC");
            Assert.IsNotNull(session);
        });
    }

    [Test]
    public async Task SessionRepository_HandlesLockedDatabase()
    {
        // Arrange - Create a second repository instance (simulates another Revit instance)
        var secondRepo = new SessionRepository(_testDbPath, new TestLogger());

        try
        {
            // First repo creates session
            await _repository.StartSessionAsync("session1", "2025", "20.0.0.1", "user1", null, "PC1");

            // Act - Second repo should be able to read (SQLite allows multiple readers)
            var session = await secondRepo.GetSessionAsync("session1");
            Assert.IsNotNull(session);

            // Both repos should be able to write to different sessions
            await secondRepo.StartSessionAsync("session2", "2025", "20.0.0.1", "user2", null, "PC2");
            var sessions = await _repository.GetAllSessionsAsync();

            // Assert - Both sessions exist
            Assert.AreEqual(2, sessions.Count);
        }
        finally
        {
            secondRepo?.Dispose();
        }
    }
}
```

**Step 1.2: Event Persistence Tests** (2-3 hours)
```csharp
[TestFixture]
public class EventPersistenceTests
{
    private string _testDbPath;
    private EventRepository _repository;
    private ILogger _logger;

    [SetUp]
    public void Setup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_events_{Guid.NewGuid()}.db");
        _logger = new TestLogger();
        _repository = new EventRepository(_testDbPath, _logger);
    }

    [TearDown]
    public void Teardown()
    {
        _repository?.Dispose();
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
    }

    [Test]
    public async Task Events_PreservedAcrossRestart()
    {
        // Arrange
        var sessionId = "test-session";
        await _repository.AppendEventAsync(sessionId, "CommandExecuted", "{\"command\":\"Move\"}");
        await _repository.AppendEventAsync(sessionId, "CommandExecuted", "{\"command\":\"Rotate\"}");
        await _repository.AppendEventAsync(sessionId, "CommandExecuted", "{\"command\":\"Mirror\"}");

        // Act - Restart
        _repository.Dispose();
        _repository = new EventRepository(_testDbPath, _logger);

        // Assert - Events should be preserved
        var events = await _repository.GetEventsForSessionAsync(sessionId);
        Assert.AreEqual(3, events.Count);
        Assert.IsTrue(events.Any(e => e.Payload.Contains("Move")));
        Assert.IsTrue(events.Any(e => e.Payload.Contains("Rotate")));
        Assert.IsTrue(events.Any(e => e.Payload.Contains("Mirror")));
    }

    [Test]
    public async Task Events_MaintainOrderAcrossRestart()
    {
        // Arrange
        var sessionId = "test-session";
        for (int i = 0; i < 100; i++)
        {
            await _repository.AppendEventAsync(sessionId, "Event", $"{{\"index\":{i}}}");
        }

        // Act - Restart
        _repository.Dispose();
        _repository = new EventRepository(_testDbPath, _logger);

        // Assert - Events should be in correct order
        var events = await _repository.GetEventsForSessionAsync(sessionId);
        Assert.AreEqual(100, events.Count);

        for (int i = 0; i < 100; i++)
        {
            Assert.IsTrue(events[i].Payload.Contains($"\"index\":{i}"));
        }
    }

    [Test]
    public async Task Events_TransactionRollbackWorks()
    {
        // This test verifies that if an error occurs mid-transaction,
        // no partial data is committed

        // Arrange
        var sessionId = "test-session";

        // Act & Assert
        try
        {
            // Simulate a batch append that fails halfway
            using (var connection = new SQLiteConnection($"Data Source={_testDbPath};Version=3;"))
            {
                connection.Open();
                using (var transaction = connection.BeginTransaction())
                {
                    // Insert 2 events successfully
                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO events (session_id, event_type, payload, created_at) VALUES (@sid, @type, @payload, @created)";
                        cmd.Parameters.AddWithValue("@sid", sessionId);
                        cmd.Parameters.AddWithValue("@type", "Event1");
                        cmd.Parameters.AddWithValue("@payload", "{}");
                        cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("o"));
                        cmd.ExecuteNonQuery();
                    }

                    using (var cmd = connection.CreateCommand())
                    {
                        cmd.CommandText = "INSERT INTO events (session_id, event_type, payload, created_at) VALUES (@sid, @type, @payload, @created)";
                        cmd.Parameters.AddWithValue("@sid", sessionId);
                        cmd.Parameters.AddWithValue("@type", "Event2");
                        cmd.Parameters.AddWithValue("@payload", "{}");
                        cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("o"));
                        cmd.ExecuteNonQuery();
                    }

                    // Simulate error - don't commit
                    throw new Exception("Simulated error");
                }
            }
        }
        catch
        {
            // Expected
        }

        // Assert - No events should be committed due to rollback
        var events = await _repository.GetEventsForSessionAsync(sessionId);
        Assert.AreEqual(0, events.Count, "Transaction rollback should prevent any events from being committed");
    }
}
```

**Step 1.3: Offline Queue Persistence Tests** (2-3 hours)
```csharp
[TestFixture]
public class OfflineQueuePersistenceTests
{
    private string _testDbPath;
    private OfflineQueueRepository _repository;
    private ILogger _logger;

    [SetUp]
    public void Setup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_queue_{Guid.NewGuid()}.db");
        _logger = new TestLogger();
        _repository = new OfflineQueueRepository(_testDbPath, _logger);
    }

    [TearDown]
    public void Teardown()
    {
        _repository?.Dispose();
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
    }

    [Test]
    public async Task OfflineQueue_PreservesOrderAcrossRestart()
    {
        // Arrange
        await _repository.EnqueueAsync("AuditLog", "{\"action\":\"Operation1\"}", "endpoint1", "POST");
        await Task.Delay(10); // Ensure different timestamps
        await _repository.EnqueueAsync("AuditLog", "{\"action\":\"Operation2\"}", "endpoint2", "POST");
        await Task.Delay(10);
        await _repository.EnqueueAsync("AuditLog", "{\"action\":\"Operation3\"}", "endpoint3", "POST");

        // Act - Restart
        _repository.Dispose();
        _repository = new OfflineQueueRepository(_testDbPath, _logger);

        // Assert - Queue should preserve order
        var pending = await _repository.GetPendingItemsAsync();
        Assert.AreEqual(3, pending.Count);
        Assert.IsTrue(pending[0].Payload.Contains("Operation1"));
        Assert.IsTrue(pending[1].Payload.Contains("Operation2"));
        Assert.IsTrue(pending[2].Payload.Contains("Operation3"));
    }

    [Test]
    public async Task OfflineQueue_StatusUpdatesPersistedAcrossRestart()
    {
        // Arrange
        var id1 = await _repository.EnqueueAsync("Op1", "{}", "endpoint", "POST");
        var id2 = await _repository.EnqueueAsync("Op2", "{}", "endpoint", "POST");
        var id3 = await _repository.EnqueueAsync("Op3", "{}", "endpoint", "POST");

        // Update statuses
        await _repository.UpdateStatusAsync(id1, "succeeded");
        await _repository.UpdateStatusAsync(id2, "in-progress");
        // id3 remains pending

        // Act - Restart
        _repository.Dispose();
        _repository = new OfflineQueueRepository(_testDbPath, _logger);

        // Assert - Statuses should be preserved
        var allItems = await _repository.GetAllItemsAsync();
        Assert.AreEqual("succeeded", allItems.First(x => x.Id == id1).Status);
        Assert.AreEqual("in-progress", allItems.First(x => x.Id == id2).Status);
        Assert.AreEqual("pending", allItems.First(x => x.Id == id3).Status);

        // Only id3 should be in pending queue
        var pending = await _repository.GetPendingItemsAsync();
        Assert.AreEqual(1, pending.Count);
        Assert.AreEqual(id3, pending[0].Id);
    }

    [Test]
    public async Task OfflineQueue_RetryCountsPreservedAcrossRestart()
    {
        // Arrange
        var id = await _repository.EnqueueAsync("Op", "{}", "endpoint", "POST");

        // Increment retry count 3 times
        await _repository.IncrementRetryAsync(id);
        await _repository.IncrementRetryAsync(id);
        await _repository.IncrementRetryAsync(id);

        // Act - Restart
        _repository.Dispose();
        _repository = new OfflineQueueRepository(_testDbPath, _logger);

        // Assert - Retry count should be preserved
        var item = (await _repository.GetAllItemsAsync()).First(x => x.Id == id);
        Assert.AreEqual(3, item.RetryCount);
    }
}
```

**Step 1.4: Schema Version Mismatch Tests** (2-3 hours)
```csharp
[TestFixture]
public class SchemaVersionTests
{
    private string _testDbPath;
    private ILogger _logger;

    [SetUp]
    public void Setup()
    {
        _testDbPath = Path.Combine(Path.GetTempPath(), $"test_schema_{Guid.NewGuid()}.db");
        _logger = new TestLogger();
    }

    [TearDown]
    public void Teardown()
    {
        if (File.Exists(_testDbPath))
            File.Delete(_testDbPath);
    }

    [Test]
    public async Task SessionRepository_DetectsSchemaVersionMismatch()
    {
        // Arrange - Create DB with schema version 1
        using (var repo = new SessionRepository(_testDbPath, _logger))
        {
            await repo.StartSessionAsync("session1", "2025", "20.0.0.1", "user", null, "PC");
        }

        // Manually downgrade schema version
        using (var connection = new SQLiteConnection($"Data Source={_testDbPath};Version=3;"))
        {
            connection.Open();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = "UPDATE schema_version SET version = 1";
                cmd.ExecuteNonQuery();
            }
        }

        // Act - Open repository with current schema (should be version 2 or 3)
        using (var repo = new SessionRepository(_testDbPath, _logger))
        {
            // Assert - Should detect version mismatch and handle gracefully
            // (Current implementation may log warning or attempt migration)
            var session = await repo.GetSessionAsync("session1");
            // If migration worked, session should still be accessible
            Assert.IsNotNull(session);
        }
    }
}
```

### Deliverables for Phase 1
- [ ] All test files created and passing
- [ ] Test coverage report showing >80% coverage of persistence layer
- [ ] Documentation of test scenarios and expected behavior
- [ ] CI/CD integration (tests run on every build)

---

## Phase 2: Offline Queue Sync (10-14 hours)

### Objective
Implement reliable offline queue synchronization with:
- Automatic sync on network reconnection
- Exponential backoff retry strategy
- Idempotency to prevent duplicate operations
- Proper error handling and logging

### Files to Create/Modify

#### 1. Create: `BIManage/Data/Sync/OfflineQueueSyncService.cs`

**Implementation Plan:**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.Sync
{
    /// <summary>
    /// Handles synchronization of offline queue items to remote servers
    /// Implements exponential backoff retry strategy with configurable limits
    /// </summary>
    public class OfflineQueueSyncService
    {
        private readonly OfflineQueueRepository _queueRepository;
        private readonly ILogger _logger;
        private bool _isSyncing;
        private DateTime _lastSyncAttempt = DateTime.MinValue;
        private readonly TimeSpan _minSyncInterval = TimeSpan.FromSeconds(30);

        // Retry configuration
        private const int MaxRetries = 3;
        private const int BaseBackoffSeconds = 2;

        public OfflineQueueSyncService(
            OfflineQueueRepository queueRepository,
            ILogger logger)
        {
            _queueRepository = queueRepository ?? throw new ArgumentNullException(nameof(queueRepository));
            _logger = logger;
        }

        /// <summary>
        /// Synchronize all pending operations in the offline queue
        /// </summary>
        public async Task SyncPendingOperationsAsync()
        {
            // Prevent concurrent sync attempts
            if (_isSyncing)
            {
                _logger?.LogDebug("Sync already in progress, skipping");
                return;
            }

            // Rate limiting - don't sync too frequently
            if (DateTime.UtcNow - _lastSyncAttempt < _minSyncInterval)
            {
                _logger?.LogDebug($"Too soon since last sync ({(DateTime.UtcNow - _lastSyncAttempt).TotalSeconds:F1}s), skipping");
                return;
            }

            _isSyncing = true;
            _lastSyncAttempt = DateTime.UtcNow;

            try
            {
                var pending = await _queueRepository.GetPendingItemsAsync();
                _logger?.LogInfo($"Starting sync of {pending.Count} pending operations");

                if (pending.Count == 0)
                {
                    _logger?.LogDebug("No pending operations to sync");
                    return;
                }

                var succeeded = 0;
                var failed = 0;
                var retried = 0;

                foreach (var item in pending.OrderBy(x => x.QueuedAt))
                {
                    var result = await ProcessQueueItemAsync(item);
                    switch (result)
                    {
                        case SyncResult.Succeeded:
                            succeeded++;
                            break;
                        case SyncResult.Failed:
                            failed++;
                            break;
                        case SyncResult.Retried:
                            retried++;
                            break;
                    }
                }

                _logger?.LogInfo($"Sync completed: {succeeded} succeeded, {retried} retried, {failed} permanently failed");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error during offline queue sync: {ex.Message}", ex);
            }
            finally
            {
                _isSyncing = false;
            }
        }

        /// <summary>
        /// Process a single queue item with retry logic
        /// </summary>
        private async Task<SyncResult> ProcessQueueItemAsync(OfflineQueueItem item)
        {
            try
            {
                _logger?.LogDebug($"Processing queue item {item.Id} (type: {item.OperationType}, retry: {item.RetryCount})");

                // Update status to in-progress
                await _queueRepository.UpdateStatusAsync(item.Id, "in-progress");

                // Replay operation based on type
                await ReplayOperationAsync(item);

                // Mark succeeded
                await _queueRepository.UpdateStatusAsync(item.Id, "succeeded");
                _logger?.LogInfo($"Successfully synced operation {item.Id} ({item.OperationType})");

                return SyncResult.Succeeded;
            }
            catch (Exception ex)
            {
                return await HandleSyncFailureAsync(item, ex);
            }
        }

        /// <summary>
        /// Handle sync failure with exponential backoff retry
        /// </summary>
        private async Task<SyncResult> HandleSyncFailureAsync(OfflineQueueItem item, Exception ex)
        {
            _logger?.LogError($"Sync failed for operation {item.Id} ({item.OperationType}): {ex.Message}", ex);

            if (item.RetryCount < MaxRetries)
            {
                // Increment retry count
                await _queueRepository.IncrementRetryAsync(item.Id);
                var backoffDelay = GetExponentialBackoffDelay(item.RetryCount + 1);

                _logger?.LogWarning($"Will retry operation {item.Id} after {backoffDelay.TotalSeconds}s (attempt {item.RetryCount + 1}/{MaxRetries})");

                // Update status back to pending for next sync cycle
                await _queueRepository.UpdateStatusAsync(item.Id, "pending");

                return SyncResult.Retried;
            }
            else
            {
                // Permanent failure after max retries
                await _queueRepository.UpdateStatusAsync(item.Id, "failed");
                _logger?.LogError($"Operation {item.Id} ({item.OperationType}) permanently failed after {MaxRetries} retries");

                // TODO: Surface to user via diagnostic UI or notification

                return SyncResult.Failed;
            }
        }

        /// <summary>
        /// Calculate exponential backoff delay: 2^retryCount seconds (2s, 4s, 8s)
        /// </summary>
        private TimeSpan GetExponentialBackoffDelay(int retryCount)
        {
            var delaySeconds = Math.Pow(BaseBackoffSeconds, retryCount);
            return TimeSpan.FromSeconds(Math.Min(delaySeconds, 60)); // Cap at 60 seconds
        }

        /// <summary>
        /// Replay the operation by sending it to the appropriate endpoint
        /// </summary>
        private async Task ReplayOperationAsync(OfflineQueueItem item)
        {
            switch (item.OperationType)
            {
                case "AuditLog":
                    await ReplayAuditLogAsync(item);
                    break;

                case "EventLog":
                    await ReplayEventLogAsync(item);
                    break;

                case "RuleUpdate":
                    await ReplayRuleUpdateAsync(item);
                    break;

                default:
                    throw new NotSupportedException($"Operation type not supported for sync: {item.OperationType}");
            }
        }

        /// <summary>
        /// Replay an audit log entry to the server
        /// </summary>
        private async Task ReplayAuditLogAsync(OfflineQueueItem item)
        {
            _logger?.LogDebug($"Replaying audit log: {item.Endpoint}");

            // TODO: Implement actual HTTP call to audit endpoint
            // For now, simulate success
            await Task.Delay(10);

            // Example implementation:
            // var auditEntry = JsonSerializer.Deserialize<AuditLogEntry>(item.Payload);
            // await _apiClient.SendAsync(item.HttpMethod, item.Endpoint, auditEntry);
        }

        /// <summary>
        /// Replay an event log entry to the server
        /// </summary>
        private async Task ReplayEventLogAsync(OfflineQueueItem item)
        {
            _logger?.LogDebug($"Replaying event log: {item.Endpoint}");

            // TODO: Implement actual HTTP call to event endpoint
            await Task.Delay(10);
        }

        /// <summary>
        /// Replay a rule update to the server
        /// </summary>
        private async Task ReplayRuleUpdateAsync(OfflineQueueItem item)
        {
            _logger?.LogDebug($"Replaying rule update: {item.Endpoint}");

            // TODO: Implement actual HTTP call to rule endpoint
            await Task.Delay(10);
        }

        /// <summary>
        /// Get sync statistics for diagnostic purposes
        /// </summary>
        public async Task<SyncStatistics> GetSyncStatisticsAsync()
        {
            var allItems = await _queueRepository.GetAllItemsAsync();

            return new SyncStatistics
            {
                PendingCount = allItems.Count(x => x.Status == "pending"),
                InProgressCount = allItems.Count(x => x.Status == "in-progress"),
                SucceededCount = allItems.Count(x => x.Status == "succeeded"),
                FailedCount = allItems.Count(x => x.Status == "failed"),
                OldestPendingItem = allItems
                    .Where(x => x.Status == "pending")
                    .OrderBy(x => x.QueuedAt)
                    .FirstOrDefault()
            };
        }

        private enum SyncResult
        {
            Succeeded,
            Retried,
            Failed
        }
    }

    /// <summary>
    /// Statistics about offline queue synchronization
    /// </summary>
    public class SyncStatistics
    {
        public int PendingCount { get; set; }
        public int InProgressCount { get; set; }
        public int SucceededCount { get; set; }
        public int FailedCount { get; set; }
        public OfflineQueueItem OldestPendingItem { get; set; }

        public bool HasFailures => FailedCount > 0;
        public bool HasPending => PendingCount > 0;
    }
}
```

#### 2. Modify: `BIManage/Data/SQLite/OfflineQueueRepository.cs`

**Add missing methods:**

```csharp
/// <summary>
/// Update the status of a queue item
/// </summary>
public async Task UpdateStatusAsync(long id, string status)
{
    using (var connection = new SQLiteConnection(_connectionString))
    {
        await connection.OpenAsync();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                UPDATE offline_queue
                SET status = @status,
                    last_attempt_at = CURRENT_TIMESTAMP
                WHERE id = @id";

            command.Parameters.AddWithValue("@id", id);
            command.Parameters.AddWithValue("@status", status);

            await command.ExecuteNonQueryAsync();
        }
    }

    _logger?.LogDebug($"Updated queue item {id} status to '{status}'");
}

/// <summary>
/// Increment the retry count for a queue item
/// </summary>
public async Task IncrementRetryAsync(long id)
{
    using (var connection = new SQLiteConnection(_connectionString))
    {
        await connection.OpenAsync();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                UPDATE offline_queue
                SET retry_count = retry_count + 1,
                    last_attempt_at = CURRENT_TIMESTAMP
                WHERE id = @id";

            command.Parameters.AddWithValue("@id", id);

            await command.ExecuteNonQueryAsync();
        }
    }

    _logger?.LogDebug($"Incremented retry count for queue item {id}");
}

/// <summary>
/// Get all queue items (for diagnostics)
/// </summary>
public async Task<List<OfflineQueueItem>> GetAllItemsAsync()
{
    var items = new List<OfflineQueueItem>();

    using (var connection = new SQLiteConnection(_connectionString))
    {
        await connection.OpenAsync();

        using (var command = connection.CreateCommand())
        {
            command.CommandText = @"
                SELECT id, operation_type, payload, endpoint, http_method,
                       status, retry_count, queued_at, last_attempt_at
                FROM offline_queue
                ORDER BY queued_at ASC";

            using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    items.Add(new OfflineQueueItem
                    {
                        Id = reader.GetInt64(0),
                        OperationType = reader.GetString(1),
                        Payload = reader.GetString(2),
                        Endpoint = reader.IsDBNull(3) ? null : reader.GetString(3),
                        HttpMethod = reader.IsDBNull(4) ? null : reader.GetString(4),
                        Status = reader.GetString(5),
                        RetryCount = reader.GetInt32(6),
                        QueuedAt = DateTime.Parse(reader.GetString(7)),
                        LastAttemptAt = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8))
                    });
                }
            }
        }
    }

    return items;
}
```

#### 3. Modify: `BIManage/Revit/Applications/RevitBootstrapper.cs`

**Register sync service:**

```csharp
private void RegisterPersistence(ServiceRegistry services, InitContext context)
{
    // ... existing code ...

    // Register Offline Queue Sync Service
    var syncService = new OfflineQueueSyncService(offlineQueueRepository, logger);
    services.RegisterSingleton<OfflineQueueSyncService>(syncService);

    logger.LogInfo("Persistence layer registered (including OfflineQueueSyncService)");
}
```

#### 4. Create: Wire up to network connectivity

**Option A: Periodic sync during Idling**

```csharp
// In IdlingService.cs
private OfflineQueueSyncService _syncService;
private DateTime _lastSyncCheck = DateTime.MinValue;

public void SetSyncService(OfflineQueueSyncService syncService)
{
    _syncService = syncService;
}

private async void OnIdling(object sender, IdlingEventArgs e)
{
    // ... existing code ...

    // Attempt sync every 5 minutes
    if (DateTime.UtcNow - _lastSyncCheck > TimeSpan.FromMinutes(5))
    {
        _lastSyncCheck = DateTime.UtcNow;
        await _syncService?.SyncPendingOperationsAsync();
    }
}
```

**Option B: Manual sync command**

```csharp
// Create SyncOfflineQueueCommand.cs
[Transaction(TransactionMode.Manual)]
public class SyncOfflineQueueCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var app = BIManageRevit.BIManage.Revit.Applications.Application.Instance;
        var syncService = app?.Services?.GetService<OfflineQueueSyncService>();

        if (syncService == null)
        {
            TaskDialog.Show("Sync Error", "Sync service not available");
            return Result.Failed;
        }

        // Run sync asynchronously
        Task.Run(async () =>
        {
            try
            {
                await syncService.SyncPendingOperationsAsync();

                var stats = await syncService.GetSyncStatisticsAsync();
                TaskDialog.Show("Sync Complete",
                    $"Pending: {stats.PendingCount}\n" +
                    $"Succeeded: {stats.SucceededCount}\n" +
                    $"Failed: {stats.FailedCount}");
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Sync Error", $"Sync failed: {ex.Message}");
            }
        });

        return Result.Succeeded;
    }
}
```

### Deliverables for Phase 2
- [ ] OfflineQueueSyncService implemented and tested
- [ ] Retry/backoff logic working correctly
- [ ] Integration tests for sync scenarios
- [ ] Manual sync command available for testing
- [ ] Documentation on sync behavior and configuration

---

## Phase 3: Performance Benchmarks (6-9 hours)

### Objective
Validate that the persistence layer performs acceptably under realistic load:
- Rule loading < 100ms for 1000 rules
- Event appending < 1s for 10k events
- Cache eviction works without OOM
- DB file size stays reasonable over time

### Implementation

**Create: `BIManageRevit.Benchmarks/PersistenceBenchmarks.cs`**

```csharp
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using BIManage.Data.Caching;
using BIManage.Data.SQLite;
using System;
using System.Threading.Tasks;

namespace BIManageRevit.Benchmarks
{
    [MemoryDiagnoser]
    [SimpleJob(warmupCount: 3, targetCount: 5)]
    public class PersistenceBenchmarks
    {
        private SessionRepository _sessionRepo;
        private EventRepository _eventRepo;
        private HybridCacheManager _cacheManager;
        private OfflineQueueRepository _queueRepo;
        private string _testDbPath;

        [GlobalSetup]
        public void Setup()
        {
            _testDbPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"benchmark_{Guid.NewGuid()}.db");

            _sessionRepo = new SessionRepository(_testDbPath, null);
            _eventRepo = new EventRepository(_testDbPath, null);
            _queueRepo = new OfflineQueueRepository(_testDbPath, null);
            _cacheManager = new HybridCacheManager(
                _testDbPath,
                null,
                maxInMemoryItems: 1000,
                defaultTtl: TimeSpan.FromHours(1));

            // Seed cache with some data
            SeedCacheAsync().Wait();
        }

        [GlobalCleanup]
        public void Cleanup()
        {
            _sessionRepo?.Dispose();
            _eventRepo?.Dispose();
            _queueRepo?.Dispose();

            if (System.IO.File.Exists(_testDbPath))
                System.IO.File.Delete(_testDbPath);
        }

        private async Task SeedCacheAsync()
        {
            // Pre-populate cache with 500 items
            for (int i = 0; i < 500; i++)
            {
                await _cacheManager.SetAsync($"rule_{i}", new { Id = i, Name = $"Rule {i}" });
            }
        }

        [Benchmark]
        public async Task LoadRules_1000Items()
        {
            // Benchmark: Load 1000 rules from cache
            // Success criteria: < 100ms
            for (int i = 0; i < 1000; i++)
            {
                await _cacheManager.GetAsync<object>($"rule_{i}");
            }
        }

        [Benchmark]
        public async Task AppendEvents_1000Items()
        {
            // Benchmark: Append 1000 events
            // Success criteria: < 100ms for 1k events, < 1s for 10k
            var sessionId = "benchmark-session";
            for (int i = 0; i < 1000; i++)
            {
                await _eventRepo.AppendEventAsync(
                    sessionId,
                    "CommandExecuted",
                    $"{{\"command\":\"Test\",\"index\":{i}}}");
            }
        }

        [Benchmark]
        public async Task EnqueueOperations_1000Items()
        {
            // Benchmark: Enqueue 1000 operations
            // Success criteria: < 200ms
            for (int i = 0; i < 1000; i++)
            {
                await _queueRepo.EnqueueAsync(
                    "AuditLog",
                    $"{{\"action\":\"Test{i}\"}}",
                    "/api/audit",
                    "POST");
            }
        }

        [Benchmark]
        public async Task GetPendingQueueItems()
        {
            // Benchmark: Retrieve pending queue items
            // Success criteria: < 50ms for 1000 items
            var pending = await _queueRepo.GetPendingItemsAsync();
        }

        [Benchmark]
        public void CacheEviction_ExceedMaxItems()
        {
            // Benchmark: Verify cache eviction when exceeding maxInMemoryItems
            // Success criteria: No OOM, eviction occurs correctly
            for (int i = 1000; i < 1500; i++)
            {
                _cacheManager.SetAsync($"item_{i}", new byte[1024]).Wait();
            }

            // Memory should not exceed reasonable bounds
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    class Program
    {
        static void Main(string[] args)
        {
            var summary = BenchmarkRunner.Run<PersistenceBenchmarks>();
        }
    }
}
```

**Create: `BIManageRevit.Benchmarks/BIManageRevit.Benchmarks.csproj`**

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net48</TargetFramework>
    <PlatformTarget>x64</PlatformTarget>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="BenchmarkDotNet" Version="0.13.12" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\BIManageRevit.csproj" />
  </ItemGroup>
</Project>
```

### Deliverables for Phase 3
- [ ] Benchmark project created and running
- [ ] All benchmarks pass success criteria
- [ ] Memory profiling shows no leaks
- [ ] Performance report generated
- [ ] Recommendations for optimization (if needed)

---

## Phase 4: Cache Invalidation (2-3 hours)

**Implement cache version checking in HybridCacheManager**

---

## Phase 5: Cleanup Scheduling (1-2 hours)

**Wire cleanup policy to Idling service**

---

## Execution Timeline

| Phase | Duration | Cumulative | Deliverable |
|-------|----------|----------|-------------|
| Phase 1: Integration Tests | 8-12 hours | 8-12 hours | All persistence resilience tests passing |
| Phase 2: Offline Queue Sync | 10-14 hours | 18-26 hours | Sync service functional with retry/backoff |
| Phase 3: Performance Benchmarks | 6-9 hours | 24-35 hours | Benchmark report showing acceptable performance |
| Phase 4: Cache Invalidation | 2-3 hours | 26-38 hours | Cache invalidates on schema version change |
| Phase 5: Cleanup Scheduling | 1-2 hours | 27-40 hours | Cleanup runs automatically during Idling |

**Total: 27-40 hours (3.5-5 days)**

---

## Success Criteria

The persistence layer is ready to close when:

- [ ] All integration tests pass (Phase 1)
- [ ] Offline queue sync works with retry/backoff (Phase 2)
- [ ] All benchmarks meet performance criteria (Phase 3)
- [ ] Cache invalidates on version change (Phase 4)
- [ ] Cleanup runs automatically (Phase 5)
- [ ] Build succeeds with 0 errors
- [ ] No memory leaks or resource leaks
- [ ] Documentation updated

---

## Next Steps

I'll start with Phase 1: Integration Tests. Would you like me to:

1. **Begin implementing immediately** - Start with Session Persistence Tests
2. **Review the plan first** - Discuss any concerns or modifications
3. **Prioritize differently** - Change the order of phases

Please let me know how you'd like to proceed!
