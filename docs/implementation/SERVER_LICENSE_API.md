# Server-Side License API Requirements

> **Purpose**: Defines the new API endpoint and database changes required on the server
> to support BIManage's per-module licensing, passive mode, and seat management.

---

## New Endpoint

### `GET /api/v1/tenant/device/auth/license-status`

**Authentication**: Bearer JWT (access token from device auth or admin login)

**Description**: Returns the complete license state for the authenticated device's company.
Called by the Revit client at startup (after auto-auth) and periodically every 4 hours.

**Response (200 OK)**:
```json
{
    "status": "Valid",
    "isPassiveMode": false,
    "enabledModules": [1, 2, 3, 4, 5],
    "expiration": "2027-03-16T00:00:00Z",
    "companyId": "c7a8b9d0-1234-5678-9abc-def012345678",
    "companyName": "Acme Engineering",
    "totalSeats": 10,
    "activeSeats": 7
}
```

**Response Fields**:

| Field | Type | Description |
|-------|------|-------------|
| `status` | string | `"Valid"`, `"Expired"`, `"Revoked"` |
| `isPassiveMode` | bool | `true` when `activeSeats > totalSeats` |
| `enabledModules` | int[] | Module IDs the company has purchased (see Module IDs below) |
| `expiration` | datetime? | License expiration date (UTC). `null` if perpetual. |
| `companyId` | string | Company UUID |
| `companyName` | string | Company display name |
| `totalSeats` | int | Licensed seat limit for this company |
| `activeSeats` | int | Currently active devices with sessions |

**Error Responses**:
- `401 Unauthorized` — invalid/expired access token
- `403 Forbidden` — device not registered or company suspended
- `404 Not Found` — no license record for this company

---

## Module IDs

| ID | Module Name | Description |
|----|-------------|-------------|
| 1 | **Protection** | Command interception, event protection, rule evaluation, pin protection |
| 2 | **Activity Tracker** | Session tracking, audit logging, crash detection, heartbeats |
| 3 | **Health Monitor** | Model file metrics collection, health dashboard |
| 4 | **Sync Control** | Background sync, sync queue management, sync traffic control |
| 5 | **AI** | AI assistant (Ze AI / OpenAI), model context |

---

## Passive Mode Logic (Server-Side)

The server determines passive mode — the client does NOT calculate it locally.

### Decision Rule

```
isPassiveMode = (activeSeats > totalSeats)
```

### How Active Seats Are Counted

Count distinct devices (by `machineId`) that have:
- An active Revit session (session `status = 'Active'` AND `is_active = 1`)
- A heartbeat within the last 5 minutes (avoids counting crashed/stale sessions)

### Passive Mode Effect on Client

When `isPassiveMode = true`, the client automatically:
- **Keeps active**: Protection (module 1) + Activity Tracker (module 2)
- **Disables**: Health Monitor (3), Sync Control (4), AI (5)
- **Shows yellow icon** in the Revit ribbon

The `enabledModules` array is **ignored** during passive mode — the client hardcodes which modules survive passive mode. The server only needs to set the `isPassiveMode` flag.

---

## Database Schema Changes

### New Table: `company_licenses`

```sql
CREATE TABLE company_licenses (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    company_id UUID NOT NULL REFERENCES companies(id),
    total_seats INT NOT NULL DEFAULT 5,
    enabled_modules INT[] NOT NULL DEFAULT '{1,2,3,4,5}',
    expiration TIMESTAMPTZ,            -- NULL = perpetual
    status VARCHAR(20) NOT NULL DEFAULT 'Valid',  -- Valid, Expired, Revoked
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    modified_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE(company_id)
);
```

### Modify Existing: `devices` or `device_sessions`

Add a way to count active seats:
- Either count active sessions grouped by `machine_id`
- Or maintain a `last_heartbeat_at` column on the device record

### Example Query for Active Seats

```sql
SELECT COUNT(DISTINCT d.machine_id)
FROM device_sessions ds
JOIN devices d ON ds.device_id = d.id
WHERE ds.company_id = :companyId
  AND ds.status = 'Active'
  AND ds.last_heartbeat_at > NOW() - INTERVAL '5 minutes';
```

---

## Endpoint Implementation (Pseudocode)

```csharp
[HttpGet("license-status")]
[Authorize]
public async Task<IActionResult> GetLicenseStatus()
{
    var companyId = GetCompanyIdFromToken();  // Extract from JWT claims

    // 1. Get license record
    var license = await _db.CompanyLicenses
        .FirstOrDefaultAsync(l => l.CompanyId == companyId);

    if (license == null)
        return NotFound("No license found for this company");

    // 2. Check expiration
    if (license.Expiration.HasValue && license.Expiration < DateTime.UtcNow)
        license.Status = "Expired";

    // 3. Count active seats
    var activeSeats = await _db.DeviceSessions
        .Where(s => s.CompanyId == companyId
                  && s.Status == "Active"
                  && s.LastHeartbeatAt > DateTime.UtcNow.AddMinutes(-5))
        .Select(s => s.MachineId)
        .Distinct()
        .CountAsync();

    // 4. Determine passive mode
    var isPassive = activeSeats > license.TotalSeats;

    return Ok(new {
        status = license.Status,
        isPassiveMode = isPassive,
        enabledModules = license.EnabledModules,
        expiration = license.Expiration,
        companyId = companyId.ToString(),
        companyName = license.Company.Name,
        totalSeats = license.TotalSeats,
        activeSeats = activeSeats
    });
}
```

---

## Admin Management Endpoints (Future)

These endpoints allow company admins to view and manage licenses from the web dashboard:

### `GET /api/v1/admin/license`
Returns the company's license details (same data as license-status but with admin-level fields).

### `PUT /api/v1/admin/license/modules`
Update enabled modules for the company.
```json
{ "enabledModules": [1, 2, 3] }
```

### `GET /api/v1/admin/license/seats`
Returns detailed seat usage: which devices are active, last heartbeat time, user info.

---

## SignalR Events (Future Enhancement)

When seat count changes cause a passive mode transition, the server can push:

| Event | Payload | Trigger |
|-------|---------|---------|
| `PassiveModeActivated` | `{ companyId, activeSeats, totalSeats }` | `activeSeats` exceeds `totalSeats` |
| `PassiveModeDeactivated` | `{ companyId, activeSeats, totalSeats }` | `activeSeats` drops to/below `totalSeats` |
| `LicenseExpired` | `{ companyId }` | License expiration date reached |

These are **not required** for the initial implementation — the client polls every 4 hours via `GET /license-status`. SignalR events would provide real-time transitions.

---

## Migration Checklist

1. [ ] Create `company_licenses` table with seed data for existing companies
2. [ ] Add `GET /api/v1/tenant/device/auth/license-status` endpoint
3. [ ] Implement active seat counting logic (distinct `machine_id` with recent heartbeat)
4. [ ] Implement passive mode calculation (`activeSeats > totalSeats`)
5. [ ] Wire `company_id` extraction from JWT claims in the endpoint
6. [ ] Seed default license: all 5 modules enabled, 5 seats, Valid status
7. [ ] Add admin endpoints for license management (future)
8. [ ] Add SignalR passive mode events (future)
