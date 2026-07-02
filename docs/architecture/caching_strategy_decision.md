# Local Caching Strategy Decision: the legacy product vs SQLite

## Executive Summary

**Recommendation**: **Hybrid Approach** - Use in-memory caching (the legacy product's method) for active configurations + SQLite for audit trails and offline resilience.

---

## Comparison Matrix

| Criteria | the legacy product (In-Memory) | SQLite | Winner |
|----------|---------------------|---------|---------|
| **Initial load speed** | Medium (API call) | Fast (local read) | 🏆 SQLite |
| **Runtime performance** | 🏆 Ultra-fast (RAM) | Fast (disk I/O) | In-Memory |
| **Offline capability** | ❌ None | ✅ Full | 🏆 SQLite |
| **Real-time updates** | ✅ SignalR | Requires sync mechanism | 🏆 In-Memory |
| **Development complexity** | Simple | Medium (schema, migrations) | 🏆 In-Memory |
| **Memory footprint** | High (all in RAM) | Low (only active data) | 🏆 SQLite |
| **Audit trail** | ❌ None (server only) | ✅ Local evidence | 🏆 SQLite |
| **Data consistency** | Always fresh (API) | Can be stale | 🏆 In-Memory |
| **Crash resilience** | Lost on crash | ✅ Persisted | 🏆 SQLite |
| **Multi-session support** | Session-scoped | Cross-session | 🏆 SQLite |

---

## Detailed Analysis

### the legacy product's In-Memory Approach

#### ✅ Strengths
1. **Simplicity**: No database schema, no migrations, no corruption issues
2. **Performance**: RAM-speed access to all cached data
3. **Real-time sync**: SignalR keeps data instantly synchronized with server
4. **Clean slate**: Every Revit session starts fresh (no stale data)
5. **Low complexity**: Fewer moving parts, easier to debug

#### ❌ Weaknesses
1. **No offline mode**: Dead in the water without internet
2. **No local audit**: Can't review history when offline
3. **Slow startup**: Must fetch everything from API on every Revit launch
4. **Memory overhead**: Large rulesets consume significant RAM
5. **No evidence storage**: Screenshots/audit logs must go to server immediately
6. **Lost on crash**: Any unsent data is gone

#### 🎯 Best For
- **Cloud-first applications** with guaranteed internet
- **Small to medium** configuration datasets
- **Collaborative environments** where real-time sync is critical
- Teams with **good internet connectivity**

---

### SQLite Approach

#### ✅ Strengths
1. **Offline resilience**: Full functionality without internet
2. **Fast startup**: Read local cache, sync in background
3. **Audit trail**: Complete local history of all actions
4. **Evidence storage**: Screenshots, before/after states stored locally
5. **Crash recovery**: Queue unsent operations, retry on restart
6. **Low memory**: Only active data in RAM
7. **Cross-session**: User can review audit logs from previous sessions

#### ❌ Weaknesses
1. **Complexity**: Schema design, migrations, index optimization
2. **Stale data risk**: Local cache can be out of sync with server
3. **Sync conflicts**: Must handle merge conflicts when reconnecting
4. **Database corruption**: SQLite files can corrupt (rare but possible)
5. **Disk I/O overhead**: Slower than pure in-memory
6. **Testing complexity**: More test scenarios (offline, sync, conflicts)

#### 🎯 Best For
- **Field work** or unreliable internet environments
- **Compliance-heavy** industries requiring local audit trails
- **Large configuration datasets** (thousands of rules)
- Applications needing **evidence capture** (screenshots, logs)
- **Offline-first** workflows

---

## Decision Framework

### Choose **the legacy product's In-Memory** If:
- ✅ Users always have reliable internet
- ✅ Configuration datasets are small (<1000 rules)
- ✅ Real-time collaboration is critical
- ✅ You want minimal development complexity
- ✅ Audit trails can live server-side only
- ✅ Offline mode is not required

### Choose **SQLite** If:
- ✅ Users work in offline environments (construction sites, remote areas)
- ✅ You need local audit trails for compliance
- ✅ Configuration datasets are large (>5000 rules)
- ✅ Evidence capture (screenshots) is required locally
- ✅ You need cross-session data (review yesterday's violations)
- ✅ Crash recovery with operation replay is important

### Choose **Hybrid (Recommended)** If:
- ✅ You need the best of both worlds
- ✅ You want fast runtime + offline resilience
- ✅ Audit trails and evidence are critical
- ✅ You can handle the extra complexity
- ✅ You're building a production-grade SaaS product

---

## Recommended Hybrid Architecture

```
┌────────────────────────────────────────────────────┐
│              In-Memory Cache Layer                 │
│  Purpose: Fast runtime access to active configs    │
│  - Current workspace settings                      │
│  - Active project rules (being evaluated)          │
│  - User session state                              │
│  - Real-time SignalR updates                       │
│  Scope: Session-scoped (cleared on Revit close)    │
└────────────────────────────────────────────────────┘
                          ↕
┌────────────────────────────────────────────────────┐
│              SQLite Persistence Layer              │
│  Purpose: Offline resilience + audit trail         │
│  - Rule cache (fallback when offline)              │
│  - Audit logs (all user actions)                   │
│  - Evidence (screenshots, before/after states)     │
│  - Failed operation queue (retry when online)      │
│  - Session history (cross-session analytics)       │
│  Scope: Persistent across sessions                 │
└────────────────────────────────────────────────────┘
                          ↕
┌────────────────────────────────────────────────────┐
│          Backend API + Cloud Storage               │
│  Purpose: Source of truth + centralized audit      │
│  - Master rule repository                          │
│  - Centralized audit aggregation                   │
│  - Multi-tenant company/project data               │
│  - SignalR hub for real-time updates               │
└────────────────────────────────────────────────────┘
```

### Hybrid Data Flow

**Startup:**
```
1. Load cached rules from SQLite (instant startup)
2. Display "Offline Mode" indicator if no internet
3. Background: Call API to get latest rules
4. Compare versions, update cache if newer available
5. Transition to "Online Mode" when synced
```

**Runtime (Online):**
```
1. Rule evaluation uses in-memory cache (fast)
2. User action → Log to SQLite immediately (crash-safe)
3. Evidence captured → Save to SQLite + upload to server
4. SignalR update → Update both in-memory + SQLite
```

**Runtime (Offline):**
```
1. Rule evaluation uses SQLite cache (still fast)
2. User action → Log to SQLite + queue for upload
3. Evidence captured → Save to SQLite only
4. When reconnected → Batch upload queued operations
```

**Evidence Capture:**
```
Action detected (e.g., Delete)
  ↓
Capture before state + screenshot → SQLite
  ↓
Execute (or block) action
  ↓
Capture after state + screenshot → SQLite
  ↓
Package evidence → Upload to server (async)
  ↓
Mark as uploaded in SQLite (keep for 7 days locally)
```

---

## Complexity vs Capability

```
Capability ↑
          │
       ⭐ │        [Hybrid]
          │       /
          │      /
          │     /  [SQLite Only]
          │    /  /
          │   /  /
          │  /  /
          │ /  /  [the legacy product In-Memory]
          │/  /  /
          └──────────────→ Complexity
```

---

## Real-World Scenarios

### Scenario 1: BIM Manager in Office (Good Internet)
**Best Choice**: the legacy product's In-Memory  
**Why**: Always online, needs real-time updates, values simplicity

### Scenario 2: Site Engineer (Spotty Internet)
**Best Choice**: Hybrid with SQLite  
**Why**: Works offline, needs audit trail for compliance, uploads when connected

### Scenario 3: Enterprise with Compliance Requirements
**Best Choice**: Hybrid with SQLite  
**Why**: Must retain local audit logs, evidence capture, disaster recovery

### Scenario 4: Small Firm with 50 Rules
**Best Choice**: the legacy product's In-Memory  
**Why**: Simple setup, low memory overhead, always in office with WiFi

### Scenario 5: Large Firm with 5000+ Complex Rules
**Best Choice**: SQLite or Hybrid  
**Why**: Memory overhead too high for pure in-memory, needs offline caching

---

## Implementation Effort Estimate

| Approach | Development Time | Maintenance | Risk |
|----------|-----------------|-------------|------|
| **the legacy product In-Memory** | 2-3 weeks | Low | Low |
| **SQLite Only** | 4-6 weeks | Medium | Medium |
| **Hybrid** | 6-8 weeks | Medium-High | Medium |

---

## Final Recommendation for CBOX Manage

### 🎯 **Use the Hybrid Approach**

**Reasoning:**
1. Your WBS includes **offline operation** and **evidence capture** → Requires SQLite
2. Your WBS includes **real-time updates** → Suggests SignalR + in-memory
3. You're building a **production SaaS** → Needs robustness
4. **Compliance/audit** is a selling feature → Local evidence storage critical

### Implementation Priority:

**Phase 1 (MVP)**:
- In-memory cache for rules (the legacy product's approach)
- SQLite for audit logs only
- API as source of truth

**Phase 2 (Offline Support)**:
- SQLite rule cache (fallback)
- Offline mode detection
- Queue for failed operations

**Phase 3 (Evidence)**:
- Screenshot capture to SQLite
- Before/after state storage
- Async upload to server

**Phase 4 (Optimization)**:
- Hybrid cache invalidation
- Intelligent prefetching
- Cache compression for large rulesets

---

## Code Architecture Pattern

```csharp
public interface ICacheManager
{
    // Fast path: In-memory
    Task<RuleSet> GetActiveRules(string projectId);
    
    // Persistence: SQLite
    Task SaveAuditLog(AuditEntry entry);
    Task SaveEvidence(Evidence evidence);
    Task QueueFailedOperation(Operation op);
    
    // Sync: Bidirectional
    Task SyncWithServer();
    Task<bool> IsOnline();
}

public class HybridCacheManager : ICacheManager
{
    private readonly InMemoryCache _memoryCache;
    private readonly SqliteCache _sqliteCache;
    private readonly ApiClient _apiClient;
    
    // On startup: Load from SQLite to memory
    // On runtime: Read from memory, write to both
    // On shutdown: Flush pending to SQLite
}
```

---

## Conclusion

**Neither is "best" in absolute terms.** The right choice depends on your:
- Target users (office vs field workers)
- Internet reliability
- Compliance requirements
- Development timeline
- Budget

For **CBOX Manage**, based on your WBS emphasizing offline resilience, audit trails, and evidence capture, the **Hybrid approach** is the optimal choice despite higher complexity.

Start with the legacy product's in-memory for speed, add SQLite for persistence and offline support.
