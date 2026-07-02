# Local Persistence & Offline Cache - WBS Readiness Assessment

**Assessment Date:** 2026-01-14
**Assessed By:** Claude Sonnet 4.5

---

## Executive Summary

The Local Persistence & Offline Cache implementation is **functionally complete for MVP** but has **gaps in production readiness** that should be addressed before moving to the next WBS.

**Recommendation:** ⚠️ **Address critical "Before Next WBS" items** (18-24 hours) before proceeding.

---

## Assessment Against "Before Next WBS" Checklist

### 1. Correctness & Resilience ⚠️ **PARTIAL**

| Requirement | Status | Evidence | Action Needed |
|-------------|--------|----------|---------------|
| Schema matches domain needs | ✅ **PASS** | Schema.sql and Schema_Persistence.sql created with sessions, events, rules, queue tables | None |
| Schema passes CRUD tests across restarts | 🟡 **UNKNOWN** | No automated tests exist | **ADD TESTS** (8-12 hours) |
| DAL methods are transactionally safe | ✅ **PASS** | All repositories use `using var transaction` pattern | None |
| Handles missing/locked/corrupted DB | ⚠️ **PARTIAL** | Has try-catch but limited recovery logic | **ENHANCE** (4-6 hours) |

**Critical Gap:** No automated integration tests verify persistence survives Revit restarts or handles corruption scenarios.

**Recommended Action:**
1. Add integration test: [PersistenceIntegrationTests.cs](../../BIManageRevit.Tests/Integration/PersistenceIntegrationTests.cs) exists but may be incomplete
2. Add resilience tests for:
   - Missing database file (should recreate schema)
   - Locked database (another Revit instance)
   - Corrupted database (backup and recreate)
   - Schema version mismatch (migration needed)

**Effort:** 8-12 hours

---

### 2. Performance & Footprint ⚠️ **UNKNOWN**

| Requirement | Status | Evidence | Action Needed |
|-------------|--------|----------|---------------|
| Common queries execute within acceptable time | 🟡 **UNKNOWN** | No benchmarks exist | **ADD BENCHMARKS** (4-6 hours) |
| Memory cache doesn't grow unbounded | ⚠️ **RISK** | `maxInMemoryItems: 1000` set but no eviction tested | **VERIFY** (2-3 hours) |
| Background sync doesn't affect UI | 🟡 **UNKNOWN** | AsyncAuditQueue exists but no performance tests | **TEST** (2-3 hours) |

**Critical Gap:** No performance benchmarks exist. We don't know if the system can handle:
- Loading 1000 rules on startup
- Appending 10k events in a session
- Cache eviction at 1000+ items
- Offline queue replay with 100+ pending operations

**Recommended Action:**
1. Add performance benchmarks:
   ```csharp
   [Benchmark]
   public void LoadRulesPerformance()
   {
       // Load 1000 rules from cache - should be < 100ms
   }

   [Benchmark]
   public void AppendEventsPerformance()
   {
       // Append 10k events - should be < 1s
   }

   [Benchmark]
   public void CacheEvictionPerformance()
   {
       // Fill cache to 1500 items, verify eviction - should not OOM
   }
   ```

2. Add load testing:
   - Simulate 8-hour Revit session with realistic command volume
   - Measure peak memory usage
   - Verify DB file size stays reasonable

**Effort:** 6-9 hours

---

### 3. Offline & Sync Behavior ⚠️ **PARTIAL**

| Requirement | Status | Evidence | Action Needed |
|-------------|--------|----------|---------------|
| Operations queued when offline | ✅ **PASS** | `OfflineQueueRepository` exists | None |
| Sync queue replays on reconnection | 🟡 **UNKNOWN** | No sync replay logic implemented | **IMPLEMENT** (6-8 hours) |
| Safe retry behavior with backoff | ❌ **MISSING** | No retry/backoff logic exists | **IMPLEMENT** (4-6 hours) |
| Conflicts/failures logged and surfaced | ⚠️ **PARTIAL** | Logged but not surfaced to user | **ENHANCE** (2-3 hours) |

**Critical Gap:** Offline queue exists but **sync replay logic is not implemented**.

**Evidence from Code Review:**
- `OfflineQueueRepository.cs` has `EnqueueAsync()` and `GetPendingItemsAsync()` ✅
- **NO CODE** calls `GetPendingItemsAsync()` to replay queue ❌
- **NO CODE** implements retry/backoff strategy ❌
- **NO CODE** updates queue item status (pending → succeeded/failed) ❌

**Recommended Action:**
1. Implement `OfflineQueueSyncService`:
   ```csharp
   public class OfflineQueueSyncService
   {
       public async Task SyncPendingOperationsAsync()
       {
           var pending = await _queueRepository.GetPendingItemsAsync();

           foreach (var item in pending.OrderBy(x => x.QueuedAt))
           {
               // Update status to in-progress
               await _queueRepository.UpdateStatusAsync(item.Id, "in-progress");

               try
               {
                   // Replay operation (call API, etc.)
                   await ReplayOperationAsync(item);

                   // Mark succeeded
                   await _queueRepository.UpdateStatusAsync(item.Id, "succeeded");
               }
               catch (Exception ex)
               {
                   // Increment retry count, implement exponential backoff
                   if (item.RetryCount < 3)
                   {
                       await _queueRepository.IncrementRetryAsync(item.Id);
                       await Task.Delay(GetBackoffDelay(item.RetryCount));
                   }
                   else
                   {
                       await _queueRepository.UpdateStatusAsync(item.Id, "failed");
                       _logger.LogError($"Offline operation permanently failed: {ex.Message}");
                   }
               }
           }
       }
   }
   ```

2. Wire up to network connectivity change event
3. Add integration tests for offline → online transitions

**Effort:** 10-14 hours

---

### 4. Cache Validity & Cleanup ⚠️ **PARTIAL**

| Requirement | Status | Evidence | Action Needed |
|-------------|--------|----------|---------------|
| Cache invalidated on version/config change | ⚠️ **PARTIAL** | `CacheCleanupPolicy` exists but no version invalidation | **IMPLEMENT** (2-3 hours) |
| Old sessions/events pruned by policy | ✅ **PASS** | `CleanupConfiguration` has TTL settings | None |
| DB file stays within acceptable size | 🟡 **UNKNOWN** | Cleanup exists but not tested over time | **TEST** (2-3 hours) |
| Maintenance scheduled in low-impact windows | ⚠️ **PARTIAL** | Cleanup policy exists but not scheduled | **SCHEDULE** (1-2 hours) |

**Critical Gap:** Cache cleanup policy exists but is **not actively scheduled** during Revit Idling.

**Evidence from Code Review:**
- `CacheCleanupPolicy.cs` exists with `CleanExpiredCacheEntriesAsync()` ✅
- **NO CODE** schedules cleanup to run periodically ❌
- **NO CODE** invalidates cache when schema version changes ❌

**Recommended Action:**
1. Add cache version invalidation:
   ```csharp
   public async Task ValidateCacheVersionAsync()
   {
       var cachedVersion = await _cacheManager.GetAsync<int>("schema_version");
       var currentVersion = await _sessionRepository.GetSchemaVersionAsync();

       if (cachedVersion != currentVersion)
       {
           _logger.LogWarning($"Schema version changed ({cachedVersion} → {currentVersion}), invalidating cache");
           await _cacheManager.ClearAllAsync();
           await _cacheManager.SetAsync("schema_version", currentVersion);
       }
   }
   ```

2. Schedule cleanup during Idling:
   ```csharp
   // In IdlingService.cs
   private async void OnIdling(object sender, IdlingEventArgs e)
   {
       if (_lastCleanup.Add(TimeSpan.FromHours(1)) < DateTime.UtcNow)
       {
           await _cleanupPolicy.CleanExpiredCacheEntriesAsync();
           _lastCleanup = DateTime.UtcNow;
       }
   }
   ```

**Effort:** 3-5 hours

---

### 5. Observability 🟡 **ADEQUATE**

| Requirement | Status | Evidence | Action Needed |
|-------------|--------|----------|---------------|
| Logging covers key operations | ✅ **PASS** | All repositories have detailed logging | None |
| Cache hit/miss metrics | ❌ **MISSING** | No metrics tracking | **ADD** (2-3 hours) |
| Diagnostic view for cache/queue/DB health | ❌ **MISSING** | No diagnostic UI | **ADD** (4-6 hours) |

**Critical Gap:** No visibility into cache performance or queue health.

**Recommended Action:**
1. Add cache metrics:
   ```csharp
   public class CacheMetrics
   {
       public long Hits { get; set; }
       public long Misses { get; set; }
       public double HitRate => Hits / (double)(Hits + Misses);
   }

   // In HybridCacheManager
   public async Task<T?> GetAsync<T>(string key)
   {
       if (_memoryCache.TryGetValue(key, out T value))
       {
           _metrics.Hits++;
           return value;
       }

       _metrics.Misses++;
       // ... load from SQLite
   }
   ```

2. Add diagnostic command:
   ```csharp
   [Transaction(TransactionMode.Manual)]
   public class DiagnosticsCommand : IExternalCommand
   {
       public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
       {
           var cacheManager = ServiceRegistry.GetService<HybridCacheManager>();
           var queueRepo = ServiceRegistry.GetService<OfflineQueueRepository>();

           var metrics = cacheManager.GetMetrics();
           var queueStats = queueRepo.GetQueueStats();

           TaskDialog.Show("BIManage Diagnostics",
               $"Cache Hit Rate: {metrics.HitRate:P}\n" +
               $"Pending Queue Items: {queueStats.PendingCount}\n" +
               $"Failed Queue Items: {queueStats.FailedCount}\n" +
               $"DB Size: {GetDbSize():N0} bytes");

           return Result.Succeeded;
       }
   }
   ```

**Effort:** 6-9 hours

---

## Summary of Gaps

### Blocking Issues (Must Fix Before Next WBS)

| Issue | Severity | Effort | Priority |
|-------|----------|--------|----------|
| **No integration tests for persistence resilience** | 🔴 HIGH | 8-12 hours | P0 - BLOCKER |
| **Offline queue sync not implemented** | 🔴 HIGH | 10-14 hours | P0 - BLOCKER |
| **No performance benchmarks** | 🟡 MEDIUM | 6-9 hours | P1 - CRITICAL |

**Total P0 Effort:** 18-26 hours (2-3 days)

### Important But Deferrable (Can Do in Parallel with Next WBS)

| Issue | Severity | Effort | Priority |
|-------|----------|--------|----------|
| Cache invalidation on schema change | 🟡 MEDIUM | 2-3 hours | P2 |
| Cleanup scheduling | 🟡 MEDIUM | 1-2 hours | P2 |
| Retry/backoff for sync | 🟡 MEDIUM | 4-6 hours | P2 |
| Cache metrics | 🟢 LOW | 2-3 hours | P3 |
| Diagnostic UI | 🟢 LOW | 4-6 hours | P3 |

**Total P2-P3 Effort:** 13-20 hours (1.5-2.5 days)

---

## Recommendation: Three Options

### Option A: Fix Blockers Now (Recommended) ⭐
**Before moving to next WBS, address P0 issues:**
1. Add integration tests for persistence resilience (8-12 hours)
2. Implement offline queue sync with retry (10-14 hours)
3. Add basic performance benchmarks (6-9 hours)

**Effort:** 24-35 hours (3-4 days)
**Outcome:** Persistence layer is production-ready, no risk of data loss or corruption

### Option B: Minimal Fix + Parallel Work
**Fix only the most critical blocker, defer rest:**
1. Add basic integration tests (4-6 hours minimum)
2. Implement minimal offline queue sync (no retry/backoff) (6-8 hours)
3. Move to next WBS
4. Address P1-P3 items in parallel

**Effort:** 10-14 hours (1-2 days) upfront
**Risk:** Medium - Offline sync may fail silently, no performance validation

### Option C: Defer All (Not Recommended) ❌
**Mark persistence as "technical debt" and proceed:**
- Move to next WBS immediately
- Add all items to Phase 2 backlog

**Effort:** 0 hours upfront
**Risk:** HIGH - Potential data loss, corruption, or performance issues in production

---

## Detailed Action Plan (Option A - Recommended)

### Phase 1: Integration Tests (8-12 hours)

**Test File:** `BIManageRevit.Tests/Integration/PersistenceIntegrationTests.cs`

```csharp
[TestFixture]
public class PersistenceResilienceTests
{
    [Test]
    public async Task SessionRepository_SurvivesRevitRestart()
    {
        // 1. Create session and commit to DB
        var session = await _sessionRepo.StartSessionAsync("test-session", ...);

        // 2. Dispose repository (simulates Revit shutdown)
        _sessionRepo.Dispose();

        // 3. Create new repository instance (simulates Revit restart)
        _sessionRepo = new SessionRepository(_dbPath, _logger);

        // 4. Verify session still exists
        var retrieved = await _sessionRepo.GetSessionAsync("test-session");
        Assert.IsNotNull(retrieved);
    }

    [Test]
    public async Task SessionRepository_HandlesCorruptedDatabase()
    {
        // 1. Corrupt the database file
        File.WriteAllText(_dbPath, "CORRUPTED DATA");

        // 2. Repository should detect corruption and recreate schema
        var repo = new SessionRepository(_dbPath, _logger);

        // 3. Verify schema was recreated
        var session = await repo.StartSessionAsync("test-session", ...);
        Assert.IsNotNull(session);
    }

    [Test]
    public async Task OfflineQueue_PreservesOrderAcrossRestarts()
    {
        // 1. Enqueue 3 operations
        await _queueRepo.EnqueueAsync("op1", ...);
        await _queueRepo.EnqueueAsync("op2", ...);
        await _queueRepo.EnqueueAsync("op3", ...);

        // 2. Restart (dispose and recreate)
        _queueRepo.Dispose();
        _queueRepo = new OfflineQueueRepository(_dbPath, _logger);

        // 3. Verify operations preserved in order
        var pending = await _queueRepo.GetPendingItemsAsync();
        Assert.AreEqual(3, pending.Count);
        Assert.AreEqual("op1", pending[0].OperationType);
    }
}
```

**Test Coverage:**
- [x] Schema creation on first run
- [x] Session persistence across restarts
- [x] Event persistence across restarts
- [x] Offline queue persistence across restarts
- [x] Corrupted database recovery
- [x] Locked database handling
- [x] Schema version mismatch (migration)
- [x] Transaction rollback on error

### Phase 2: Offline Queue Sync (10-14 hours)

**New File:** `BIManage/Data/Sync/OfflineQueueSyncService.cs`

```csharp
public class OfflineQueueSyncService
{
    private readonly OfflineQueueRepository _queueRepository;
    private readonly ILogger _logger;
    private bool _isSyncing;

    public async Task SyncPendingOperationsAsync()
    {
        if (_isSyncing)
        {
            _logger.LogDebug("Sync already in progress, skipping");
            return;
        }

        _isSyncing = true;
        try
        {
            var pending = await _queueRepository.GetPendingItemsAsync();
            _logger.LogInfo($"Starting sync of {pending.Count} pending operations");

            foreach (var item in pending.OrderBy(x => x.QueuedAt))
            {
                await ProcessQueueItemAsync(item);
            }
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private async Task ProcessQueueItemAsync(OfflineQueueItem item)
    {
        try
        {
            // Update status to in-progress
            await _queueRepository.UpdateStatusAsync(item.Id, "in-progress");

            // Replay operation based on type
            switch (item.OperationType)
            {
                case "AuditLog":
                    await ReplayAuditLogAsync(item);
                    break;
                case "EventLog":
                    await ReplayEventLogAsync(item);
                    break;
                default:
                    throw new NotSupportedException($"Operation type not supported: {item.OperationType}");
            }

            // Mark succeeded
            await _queueRepository.UpdateStatusAsync(item.Id, "succeeded");
            _logger.LogInfo($"Successfully synced operation {item.Id}");
        }
        catch (Exception ex)
        {
            await HandleSyncFailureAsync(item, ex);
        }
    }

    private async Task HandleSyncFailureAsync(OfflineQueueItem item, Exception ex)
    {
        _logger.LogError($"Sync failed for operation {item.Id}: {ex.Message}", ex);

        if (item.RetryCount < 3)
        {
            // Increment retry count and schedule retry with backoff
            await _queueRepository.IncrementRetryAsync(item.Id);
            var backoffDelay = GetExponentialBackoffDelay(item.RetryCount + 1);
            _logger.LogWarning($"Will retry operation {item.Id} after {backoffDelay.TotalSeconds}s");

            // Update status back to pending
            await _queueRepository.UpdateStatusAsync(item.Id, "pending");
        }
        else
        {
            // Permanent failure
            await _queueRepository.UpdateStatusAsync(item.Id, "failed");
            _logger.LogError($"Operation {item.Id} permanently failed after 3 retries");

            // TODO: Surface to user via diagnostic UI
        }
    }

    private TimeSpan GetExponentialBackoffDelay(int retryCount)
    {
        // 2^retryCount seconds: 2s, 4s, 8s
        return TimeSpan.FromSeconds(Math.Pow(2, retryCount));
    }

    private async Task ReplayAuditLogAsync(OfflineQueueItem item)
    {
        // Deserialize payload and send to API
        var auditEntry = JsonSerializer.Deserialize<AuditLogEntry>(item.Payload);
        // await _apiClient.SendAuditLogAsync(auditEntry);
    }
}
```

**Integration:**
1. Register `OfflineQueueSyncService` in `RevitBootstrapper`
2. Wire to network connectivity change event
3. Call `SyncPendingOperationsAsync()` on reconnection
4. Add integration tests for sync scenarios

### Phase 3: Performance Benchmarks (6-9 hours)

**New File:** `BIManageRevit.Benchmarks/PersistenceBenchmarks.cs`

```csharp
[MemoryDiagnoser]
public class PersistenceBenchmarks
{
    private SessionRepository _sessionRepo;
    private EventRepository _eventRepo;
    private HybridCacheManager _cacheManager;

    [GlobalSetup]
    public void Setup()
    {
        _sessionRepo = new SessionRepository(":memory:", null);
        _eventRepo = new EventRepository(":memory:", null);
        _cacheManager = new HybridCacheManager(":memory:", null, 1000, TimeSpan.FromHours(1));
    }

    [Benchmark]
    public async Task LoadRules_1000Items()
    {
        // Benchmark loading 1000 rules from cache
        // Should be < 100ms
        for (int i = 0; i < 1000; i++)
        {
            await _cacheManager.GetAsync<RuleDefinition>($"rule_{i}");
        }
    }

    [Benchmark]
    public async Task AppendEvents_10000Items()
    {
        // Benchmark appending 10k events
        // Should be < 1s
        for (int i = 0; i < 10000; i++)
        {
            await _eventRepo.AppendEventAsync("test-session", "CommandExecuted", $"{{\"index\":{i}}}");
        }
    }

    [Benchmark]
    public void CacheEviction_1500Items()
    {
        // Benchmark cache eviction when exceeding maxInMemoryItems
        // Should not throw OOM
        for (int i = 0; i < 1500; i++)
        {
            _cacheManager.SetAsync($"item_{i}", new byte[1024]).Wait(); // 1KB each
        }

        // Verify eviction occurred
        var stats = _cacheManager.GetStats();
        Assert.LessOrEqual(stats.InMemoryCount, 1000);
    }
}
```

**Success Criteria:**
- Load 1000 rules: < 100ms
- Append 10k events: < 1s
- Cache eviction: No OOM, correct eviction
- Peak memory: < 500MB for 8-hour session

---

## Conclusion

The Local Persistence & Offline Cache implementation has a **solid foundation** but **lacks production hardening** in three critical areas:

1. **Resilience Testing** - No verification that data survives corruption/restarts
2. **Offline Sync** - Queue exists but sync replay not implemented
3. **Performance Validation** - No benchmarks to verify acceptable performance

**Recommended Path Forward:**
- ✅ **Option A** (24-35 hours) - Fix all P0 issues before next WBS
- This ensures the persistence layer is rock-solid and won't cause data loss or performance issues later

**Alternative:**
- 🟡 **Option B** (10-14 hours) - Minimal fix + parallel work
- Higher risk but faster to next WBS

**Not Recommended:**
- ❌ **Option C** - Defer all work
- Too risky for production deployment

---

## Approval Required

**Question for User:** Which option do you prefer?

**Option A:** Fix all P0 issues now (24-35 hours), then move to next WBS
**Option B:** Minimal fix (10-14 hours), move to next WBS, address rest in parallel
**Option C:** Defer all, move to next WBS immediately (not recommended)

Please advise on your preference based on project timeline and risk tolerance.
