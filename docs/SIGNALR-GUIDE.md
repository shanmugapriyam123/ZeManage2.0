# SignalR Implementation Guide — BIManageAPI

## Overview

The BIManageAPI uses **ASP.NET Core SignalR** with a **Redis backplane** for real-time push notifications.
The hub is fully operational and requires no additional server-side setup to start connecting clients.

**Hub Endpoint:**
```
wss://{host}/hubs/notifications
```

All connections require a valid JWT Bearer token passed via the SignalR query string mechanism:
```
GET /hubs/notifications?access_token=<JWT>
```

---

## Architecture Files

| File | Purpose |
|------|---------|
| `Core/NotificationHub.cs` | SignalR Hub (`[Authorize]` — JWT required) |
| `Groups/ISignalRGroupHelper.cs` | Group name interface |
| `Groups/SignalRGroupHelper.cs` | Group name implementation (`prefix:id` format) |
| `Services/ISignalRNotificationService.cs` | Server-push notification interface |
| `Services/SignalRNotificationService.cs` | Server-push notification implementation |
| `Configuration/Extensions/CachingExtensions.cs` | SignalR + Redis registration |
| `Configuration/Extensions/MiddlewarePipelineExtensions.cs` | Hub route (`MapHub`) |

---

## Auto Groups on Connection

When a client connects, the hub reads JWT claims and automatically joins the appropriate groups:

| Token Type | JWT Claims | Auto-joined Groups |
|-----------|-----------|-------------------|
| Provider | `userId` | `user:{userId}` |
| Tenant Admin (Web Admin) | `profileId` + `companyId` | `admin:{companyId}`, `company:{companyId}` |
| Revit Device | `machineId` + `companyId` | `machine:{machineId}`, `company:{companyId}` |

---

## Group Name Reference

All group names use `prefix:id` format:

| Group | Format | Who joins | How |
|-------|--------|-----------|-----|
| Company | `company:{companyId}` | All tenant users | Auto on connect |
| Admin | `admin:{companyId}` | Tenant admins | Auto on connect |
| Machine | `machine:{machineId}` | Revit devices | Auto on connect |
| User | `user:{userId}` | Providers | Auto on connect |
| Project | `project:{projectId}` | Any client | Explicit `SubscribeToProject` |
| Model | `model:{modelGuid}` | Any client | Explicit `SubscribeToModel` |
| Sync | `sync:{modelGuid}` | Any client | Explicit `SubscribeToSync` |
| Conversation | `conversation:{id}` | Any client | Explicit `SubscribeToConversation` |

---

## Client → Server Hub Methods

After connecting, clients can call these hub methods:

| Method | Parameters | Effect |
|--------|-----------|--------|
| `SubscribeToCompany` | `Guid companyId` | Join `company:{companyId}` group |
| `UnsubscribeFromCompany` | `Guid companyId` | Leave `company:{companyId}` group |
| `SubscribeToMachine` | `Guid machineId` | Join `machine:{machineId}` group |
| `UnsubscribeFromMachine` | `Guid machineId` | Leave `machine:{machineId}` group |
| `SubscribeToModel` | `string modelGuid` | Join `model:{modelGuid}` group |
| `UnsubscribeFromModel` | `string modelGuid` | Leave `model:{modelGuid}` group |
| `SubscribeToProject` | `string projectId` | Join `project:{projectId}` group |
| `UnsubscribeFromProject` | `string projectId` | Leave `project:{projectId}` group |
| `SubscribeToSync` | `string modelGuid` | Join `sync:{modelGuid}` group |
| `UnsubscribeFromSync` | `string modelGuid` | Leave `sync:{modelGuid}` group |
| `SubscribeToConversation` | `string conversationId` | Join `conversation:{id}` group |
| `UnsubscribeFromConversation` | `string conversationId` | Leave `conversation:{id}` group |
| `Ping` | _(none)_ | Receive `Pong` response |

---

## Server → Client Events

Events the server pushes to connected clients:

| Event | Payload Shape | Sent To | Trigger |
|-------|--------------|---------|---------|
| `Connected` | `{ connectionId, companyId, message, timestamp }` | Caller only | On successful connection |
| `Subscribed` | `{ type, id, timestamp }` | Caller only | After any `Subscribe*` call |
| `Unsubscribed` | `{ type, id, timestamp }` | Caller only | After any `Unsubscribe*` call |
| `Pong` | `{ timestamp }` | Caller only | Response to `Ping` |
| `ModelRegistered` | `ModelRegisterResponse` | `company:{companyId}` group | Model registered via API |

---

## Data Flow

```
┌─────────────────────────────────────────────────────────────────┐
│                        SignalR Hub                              │
│                    /hubs/notifications                          │
└───────────────────────────┬─────────────────────────────────────┘
                            │  JWT verified on connect
          ┌─────────────────┴──────────────────┐
          │                                    │
   ┌──────▼──────┐                    ┌────────▼────────┐
   │ Revit Plugin│                    │  Web Admin UI   │
   │  (C# .NET)  │                    │  (TypeScript)   │
   │machineId JWT│                    │profileId JWT    │
   └──────┬──────┘                    └────────┬────────┘
          │                                    │
    Joins groups:                        Joins groups:
    machine:{machineId}                  admin:{companyId}
    company:{companyId}                  company:{companyId}
          │                                    │
          └──────────────┬─────────────────────┘
                         │
              Both receive "ModelRegistered"
              when server sends to company:{companyId}
```

---

## Client 1 — Revit Plugin (C# .NET)

### NuGet Package
```xml
<PackageReference Include="Microsoft.AspNetCore.SignalR.Client" Version="8.0.*" />
```

### Connection Implementation

```csharp
using Microsoft.AspNetCore.SignalR.Client;

public class BIManageSignalRClient : IAsyncDisposable
{
    private HubConnection _connection;
    private readonly string _baseUrl;
    private string _accessToken;

    public BIManageSignalRClient(string baseUrl, string accessToken)
    {
        _baseUrl = baseUrl;
        _accessToken = accessToken;
    }

    public async Task StartAsync()
    {
        _connection = new HubConnectionBuilder()
            .WithUrl($"{_baseUrl}/hubs/notifications", options =>
            {
                // SignalR requires JWT via AccessTokenProvider for WebSocket transport
                options.AccessTokenProvider = () => Task.FromResult(_accessToken);
            })
            .WithAutomaticReconnect(new[]
            {
                TimeSpan.Zero,           // Retry immediately
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        // ─── Register handlers BEFORE calling StartAsync ───

        _connection.On<object>("Connected", info =>
        {
            // Server confirms connection with identity info
        });

        _connection.On<object>("ModelRegistered", response =>
        {
            // A Revit model was registered — update local state if needed
        });

        _connection.On<object>("Subscribed", info =>
        {
            // Subscription confirmation (type + id)
        });

        _connection.On<object>("Pong", pong =>
        {
            // Health check reply
        });

        _connection.Reconnecting += error =>
        {
            // Connection lost — attempting to reconnect
            return Task.CompletedTask;
        };

        _connection.Reconnected += connectionId =>
        {
            // Re-subscribe to model/sync groups here after reconnect
            return Task.CompletedTask;
        };

        _connection.Closed += error =>
        {
            // All retries exhausted — handle gracefully
            return Task.CompletedTask;
        };

        await _connection.StartAsync();
    }

    /// <summary>Subscribe to real-time events for a specific model.</summary>
    public Task SubscribeToModelAsync(string modelGuid)
        => _connection.InvokeAsync("SubscribeToModel", modelGuid);

    /// <summary>Subscribe to sync coordination events for a specific model.</summary>
    public Task SubscribeToSyncAsync(string modelGuid)
        => _connection.InvokeAsync("SubscribeToSync", modelGuid);

    /// <summary>Unsubscribe from model events (e.g., on model close).</summary>
    public Task UnsubscribeFromModelAsync(string modelGuid)
        => _connection.InvokeAsync("UnsubscribeFromModel", modelGuid);

    /// <summary>Health check ping.</summary>
    public Task PingAsync()
        => _connection.InvokeAsync("Ping");

    public async ValueTask DisposeAsync()
    {
        if (_connection is not null)
            await _connection.DisposeAsync();
    }
}
```

### Usage Pattern

```csharp
// After device authentication — obtain the device JWT
var client = new BIManageSignalRClient("https://api.yourserver.com", deviceAccessToken);
await client.StartAsync();

// When user opens a Revit model
await client.SubscribeToModelAsync(openedModelGuid);
await client.SubscribeToSyncAsync(openedModelGuid);

// When user closes the model
await client.UnsubscribeFromModelAsync(closedModelGuid);

// On plugin shutdown / Revit exit
await client.DisposeAsync();
```

### Token Refresh

When the device access token is refreshed, update the client before the next reconnect:

```csharp
// Store token refresh callback; HubConnectionBuilder re-calls AccessTokenProvider
// on every reconnect, so updating the field is sufficient:
client.UpdateToken(newAccessToken);
```

Add `UpdateToken(string token)` to the class:

```csharp
public void UpdateToken(string newToken) => _accessToken = newToken;
```

---

## Client 2 — Web Admin Dashboard (TypeScript)

### NPM Package

```bash
npm install @microsoft/signalr
```

### Connection Implementation

```typescript
import * as signalR from "@microsoft/signalr";

export class BIManageSignalRService {
  private connection!: signalR.HubConnection;
  private readonly baseUrl: string;

  constructor(baseUrl: string) {
    this.baseUrl = baseUrl;
  }

  /** Connect to the hub using a valid tenant admin JWT. */
  async start(accessToken: string): Promise<void> {
    this.connection = new signalR.HubConnectionBuilder()
      .withUrl(`${this.baseUrl}/hubs/notifications`, {
        accessTokenFactory: () => accessToken,
        // Prefer WebSockets; fall back to Server-Sent Events, then Long Polling
        transport:
          signalR.HttpTransportType.WebSockets |
          signalR.HttpTransportType.ServerSentEvents |
          signalR.HttpTransportType.LongPolling,
      })
      .withAutomaticReconnect([0, 5000, 15000, 30000])
      .configureLogging(signalR.LogLevel.Information)
      .build();

    // ─── Register handlers BEFORE calling start() ───

    this.connection.on("Connected", (info) => {
      // Connected — info contains { connectionId, companyId, timestamp }
    });

    this.connection.on("ModelRegistered", (response) => {
      // A model was registered — update dashboard state
      // e.g., dispatch to store: store.dispatch(modelRegistered(response))
    });

    this.connection.on("Subscribed", (info) => {
      // Subscription confirmed
    });

    this.connection.on("Pong", (pong) => {
      // Health check reply
    });

    this.connection.onreconnecting((error) => {
      // Show reconnecting indicator in UI
    });

    this.connection.onreconnected((_connectionId) => {
      // Hide reconnecting indicator; re-subscribe to any explicit groups
    });

    this.connection.onclose((error) => {
      // Show disconnected state in UI; optionally prompt user to reload
    });

    await this.connection.start();
  }

  /** Subscribe to all events for a specific project. */
  async subscribeToProject(projectId: string): Promise<void> {
    await this.connection.invoke("SubscribeToProject", projectId);
  }

  /** Subscribe to all events for a specific model. */
  async subscribeToModel(modelGuid: string): Promise<void> {
    await this.connection.invoke("SubscribeToModel", modelGuid);
  }

  /** Unsubscribe when leaving the project or model page. */
  async unsubscribeFromProject(projectId: string): Promise<void> {
    await this.connection.invoke("UnsubscribeFromProject", projectId);
  }

  async unsubscribeFromModel(modelGuid: string): Promise<void> {
    await this.connection.invoke("UnsubscribeFromModel", modelGuid);
  }

  /** Health check. */
  async ping(): Promise<void> {
    await this.connection.invoke("Ping");
  }

  /** Call on logout or app teardown. */
  async stop(): Promise<void> {
    await this.connection.stop();
  }
}
```

### Usage Pattern (React example)

```typescript
// services/signalr.ts — singleton instance
export const signalRService = new BIManageSignalRService(
  import.meta.env.VITE_API_BASE_URL
);

// In root App component or auth context — after login
useEffect(() => {
  if (accessToken) {
    signalRService.start(accessToken);
  }
  return () => {
    signalRService.stop();
  };
}, [accessToken]);

// In ProjectDetail page component
useEffect(() => {
  signalRService.subscribeToProject(projectId);
  return () => {
    signalRService.unsubscribeFromProject(projectId);
  };
}, [projectId]);
```

---

## How to Add a New Server→Client Event

Follow this pattern to push new real-time events from any service:

### Step 1 — Add to the interface

**`Services/ISignalRNotificationService.cs`**
```csharp
Task NotifySessionStartedAsync(Guid companyId, Guid machineId, object sessionInfo);
```

### Step 2 — Implement in the service

**`Services/SignalRNotificationService.cs`**
```csharp
public async Task NotifySessionStartedAsync(Guid companyId, Guid machineId, object sessionInfo)
{
    try
    {
        var group = _groupHelper.GetCompanyGroup(companyId.ToString());
        await _hubContext.Clients.Group(group).SendAsync("SessionStarted", sessionInfo);
        _logger.LogDebug("SignalR: SessionStarted sent to company {CompanyId}", companyId);
    }
    catch (Exception ex)
    {
        _logger.LogWarning(ex, "SignalR: Failed to send SessionStarted for company {CompanyId}", companyId);
    }
}
```

### Step 3 — Call from the business service

```csharp
// In RevitSessionService.cs — after session is persisted
await _notificationService.NotifySessionStartedAsync(companyId, machineId, session);
```

### Step 4 — Register handler on clients

**Revit Plugin (C#)**
```csharp
_connection.On<RevitSessionDto>("SessionStarted", session =>
{
    // Handle real-time session start
});
```

**Web Admin (TypeScript)**
```typescript
this.connection.on("SessionStarted", (session: RevitSessionDto) => {
  // Update dashboard — e.g., add session to active sessions list
});
```

---

## Verification

### 1. Browser DevTools — Quick Smoke Test

Open the browser console while logged in as a tenant admin and run:

```javascript
import("/path/to/@microsoft/signalr/dist/browser/signalr.min.js").then(async (signalR) => {
  const conn = new signalR.HubConnectionBuilder()
    .withUrl("/hubs/notifications", { accessTokenFactory: () => "<YOUR_JWT>" })
    .build();

  conn.on("Connected", console.log);
  conn.on("Pong", console.log);

  await conn.start();
  console.log("State:", conn.state); // Should print "Connected"
  await conn.invoke("Ping");         // Should log Pong response
});
```

### 2. Check Active Connections

```
GET /metrics
```

Look for `signalr_connected_clients` — it should increment with each new connection.

### 3. Redis Backplane Activity

```bash
redis-cli monitor
# Fire a model registration API call and watch for SignalR channel messages
```

### 4. Health Check

```
GET /health
```

Both `redis` and `postgresql` checks should return `Healthy`.

---

## Configuration Reference (`appsettings.json`)

```json
"SignalR": {
  "EnableDetailedErrors": false,      // Set true in Development for verbose errors
  "KeepAliveInterval": 15,            // Seconds between keep-alive pings
  "ClientTimeoutInterval": 30,        // Seconds before client is considered timed out
  "MaximumReceiveMessageSize": 32768  // Max message size in bytes (32 KB)
},
"Redis": {
  "ConnectionString": "localhost:6379,abortConnect=false",
  "InstanceName": "BIManageAPI:"
}
```
