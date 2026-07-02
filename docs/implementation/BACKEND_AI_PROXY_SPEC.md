# Backend AI Chat Proxy — Implementation Spec

## Purpose

The Revit plugin (`BIManageRevit`) currently calls OpenAI directly with a hardcoded key in
`OpenAIProvider.cs`. Anyone with the DLL can extract the key in five minutes using
ILSpy/dnSpy. We want the key to live only in **Azure Key Vault** (already deployed:
`bimanage-kv-product`, secret name `OpenAI-Apikey`) and have the backend proxy chat
requests on behalf of the plugin.

After this work ships:
- The OpenAI key is read from Key Vault by the backend at startup (same pattern already used
  for `ConnectionStrings--MasterDB`, `Redis--ConnectionString`, etc.).
- The plugin's `AI:UseBackendProxy` config key flips to `true`, the `AI:ApiKey` value is
  blanked, and the previously-leaked key can be revoked in OpenAI's dashboard.
- Future key rotations happen in Key Vault only — no plugin redeploy, no installer rebuild.

## Resources confirmed (do not re-verify)

| Item | Value |
|---|---|
| Vault name | `bimanage-kv-product` |
| Vault URI | `https://bimanage-kv-product.vault.azure.net/` |
| Resource group | `bimanage-production-cus` |
| Subscription ID | `69e4f797-ac65-4dd6-b8cd-5aebdde5870c` |
| Region | Central US |
| Tenant (Directory) ID | `e29937e9-cd6d-44c1-97f1-60af4610e32b` |
| Vault auth model | RBAC (legacy access policies disabled) |
| OpenAI secret name | `OpenAI-Apikey` (exact case) |
| Secret type | Plain OpenAI key (starts with `sk-…`), NOT Azure OpenAI |
| Backend hosting | Azure App Service (`api.zemanage.com` custom domain) |
| Managed Identity on App Service | Enabled (system-assigned), already granted Key Vault Secrets User |
| Backend already pulls from this vault | Yes — `ConnectionStrings--MasterDB`, `Redis--ConnectionString`, etc. |

## Endpoints to expose

Two routes, both under `/api/v1/ai/`. Both require a valid ZeManage user JWT (the same
JWT used for all other backend calls — `AuthenticatedHttpClient.GetCurrentAccessTokenAsync()`
on the plugin side).

### 1. `POST /api/v1/ai/chat` — non-streaming

**Purpose**: forward OpenAI Chat Completions requests with the Key-Vault-sourced key
attached. Returns OpenAI's response **verbatim** (status code, body, headers).

**Request**:
- Method: `POST`
- Headers:
  - `Authorization: Bearer <user JWT>` — validated by existing JWT middleware
  - `Content-Type: application/json`
- Body: the **exact OpenAI Chat Completions request body** the plugin would have sent to
  `https://api.openai.com/v1/chat/completions`. Includes `model`, `messages`, `tools`,
  `tool_choice`, `temperature`, `max_tokens`, etc. The plugin already constructs this body
  via `OpenAIProvider.BuildRequestBody()` — your endpoint does **not** need to validate or
  reshape it. Pass through.

**Response**: the OpenAI response body, status code and `Content-Type` preserved.
- On `200 OK` from OpenAI → return `200 OK` with the JSON body.
- On `4xx` from OpenAI → return the same status and body. Plugin handles these gracefully
  (`429` → quota-exceeded message, `4xx` → friendly error, etc.).
- On `5xx` or transport failure → return `502 Bad Gateway` with body
  `{"error": "Upstream AI service unavailable"}`. The plugin's `AiNetworkUnavailableException`
  catch path will surface the standard "AI is unavailable" bubble.

### 2. `POST /api/v1/ai/chat/stream` — streaming (SSE passthrough)

**Purpose**: same as above, but for the streaming path (`stream: true` in the body). The
plugin reads token-by-token Server-Sent Events. Your endpoint should stream the response
chunks back to the client as they arrive from OpenAI — do **not** buffer the full response.

**Request**: same shape as `/chat`, but the body includes `"stream": true`.

**Response**:
- `Content-Type: text/event-stream`
- Stream OpenAI's SSE chunks back unchanged. Each chunk looks like `data: {...}\n\n`,
  terminated by `data: [DONE]\n\n`.
- Use `HttpResponseMessage` with `ResponseHeadersRead` semantics. In ASP.NET Core minimal
  implementation:

  ```csharp
  // After validating JWT and fetching the OpenAI key from Key Vault:
  using var upstream = await _openAiHttp.PostAsync(
      "https://api.openai.com/v1/chat/completions",
      new StringContent(body, Encoding.UTF8, "application/json"),
      HttpCompletionOption.ResponseHeadersRead);

  Response.StatusCode = (int)upstream.StatusCode;
  Response.ContentType = "text/event-stream";
  await upstream.Content.CopyToAsync(Response.Body);
  ```

## Implementation pieces

### A. Key Vault wiring (likely already done)

Verify `Program.cs` already calls `AddAzureKeyVault(...)`. If yes, the secret is reachable
as `_configuration["OpenAI--Apikey"]` (Azure config provider rewrites `--` → `:`).

If for some reason it's not wired, add at the top of `Program.cs`:

```csharp
builder.Configuration.AddAzureKeyVault(
    new Uri("https://bimanage-kv-product.vault.azure.net/"),
    new DefaultAzureCredential());
```

`DefaultAzureCredential` automatically uses the App Service's Managed Identity in production
and developer credentials (`az login` / Visual Studio sign-in) locally.

Register a typed HttpClient for OpenAI in `Program.cs`:

```csharp
builder.Services.AddHttpClient("OpenAI", c =>
{
    c.BaseAddress = new Uri("https://api.openai.com/v1/");
    c.DefaultRequestHeaders.Authorization =
        new AuthenticationHeaderValue("Bearer",
            builder.Configuration["OpenAI--Apikey"]
            ?? throw new InvalidOperationException("OpenAI key not found in Key Vault"));
    c.Timeout = TimeSpan.FromSeconds(60);
});
```

If you want key rotation to take effect without redeploy, read the key per-request from
`IConfiguration` inside the controller instead of baking it into the HttpClient.

### B. Controller

```csharp
[ApiController]
[Route("api/v1/ai")]
[Authorize] // existing JWT middleware
public class AiChatController : ControllerBase
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger<AiChatController> _logger;

    public AiChatController(IHttpClientFactory httpFactory, ILogger<AiChatController> logger)
    {
        _httpFactory = httpFactory;
        _logger = logger;
    }

    [HttpPost("chat")]
    public async Task<IActionResult> Chat([FromBody] JsonElement body)
    {
        var client = _httpFactory.CreateClient("OpenAI");
        var content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");

        try
        {
            using var upstream = await client.PostAsync("chat/completions", content);
            var text = await upstream.Content.ReadAsStringAsync();

            // Usage logging (optional but recommended — see Section C)
            LogUsage(User, body, text, upstream.StatusCode);

            return new ContentResult
            {
                StatusCode = (int)upstream.StatusCode,
                ContentType = "application/json",
                Content = text
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "OpenAI upstream call failed");
            return StatusCode(502, new { error = "Upstream AI service unavailable" });
        }
    }

    [HttpPost("chat/stream")]
    public async Task ChatStream([FromBody] JsonElement body)
    {
        var client = _httpFactory.CreateClient("OpenAI");
        var content = new StringContent(body.GetRawText(), Encoding.UTF8, "application/json");

        using var upstream = await client.PostAsync(
            "chat/completions", content, HttpCompletionOption.ResponseHeadersRead);

        Response.StatusCode = (int)upstream.StatusCode;
        Response.ContentType = "text/event-stream";
        Response.Headers["Cache-Control"] = "no-cache";

        await upstream.Content.CopyToAsync(Response.Body);
    }
}
```

### C. Usage logging (recommended)

Each call should log:
- `userId` (from JWT claims)
- `model` (from request body)
- `prompt_tokens`, `completion_tokens`, `total_tokens` (from response body's `usage` object)
- `latency_ms`, `status_code`, `timestamp`

Store in a `AiUsage` table. Two reasons:
1. **Quota defence** — one runaway loop in the plugin could burn your monthly OpenAI budget.
   With usage logged per user, a per-user daily cap can be enforced later.
2. **Cost attribution** — know which testers/customers are heaviest before they complain.

Schema suggestion:
```sql
CREATE TABLE AiUsage (
    Id              BIGSERIAL PRIMARY KEY,
    UserId          TEXT NOT NULL,
    ModelGuid       TEXT,              -- if plugin sends it
    Model           TEXT NOT NULL,
    PromptTokens    INTEGER NOT NULL,
    CompletionTokens INTEGER NOT NULL,
    TotalTokens     INTEGER NOT NULL,
    LatencyMs       INTEGER NOT NULL,
    StatusCode      INTEGER NOT NULL,
    Timestamp       TIMESTAMPTZ NOT NULL DEFAULT NOW()
);
CREATE INDEX IX_AiUsage_UserId_Timestamp ON AiUsage (UserId, Timestamp DESC);
```

### D. Rate limiting (later, not v1)

ASP.NET Core's built-in `RateLimiter` middleware can cap per-user calls. v1 should ship
without it — observe real usage from the `AiUsage` table for a week, then set a sensible
cap (likely 200–500 calls per user per day for normal workflows).

## Cutover plan

1. **Backend developer**: implement the two endpoints, deploy to staging. Verify with
   `curl` that a hardcoded JWT can hit `/api/v1/ai/chat` and get a real OpenAI response.
2. **Plugin side (already done)**: the `AI:UseBackendProxy` flag and the routing logic in
   `OpenAIProvider.cs` are already in place. No further plugin code changes needed.
3. **Switch staging plugin config**: set `AI:UseBackendProxy=true` in the staging `.config`
   files. Test in Revit — confirm chat still works, confirm `AiUsage` rows appear.
4. **Switch production**: flip the flag in the production `.config` files. Ship the next
   plugin build.
5. **Revoke the old key**: once production is on the proxy and stable for ~48 hours, revoke
   the leaked key in OpenAI's dashboard. The plugin will keep working because it no longer
   knows or uses the key.
6. **Clean up the plugin code**: in a follow-up PR, delete the `HardcodedApiKey` constant
   in `OpenAIProvider.cs`, the `OPENAI_API_KEY` env-var fallback, and blank the `AI:ApiKey`
   value in every `.config` file. The fallbacks are now dead weight.

## Plugin-side contract (for backend developer reference)

The plugin will start sending requests with:
- `Authorization: Bearer <user JWT>` — the same JWT all other backend endpoints already
  accept. Use existing auth middleware.
- `Content-Type: application/json`
- Body shape: vanilla OpenAI Chat Completions request. Examples below.

### Minimal non-streaming example

```json
POST /api/v1/ai/chat
{
  "model": "gpt-4o-mini",
  "messages": [
    { "role": "system", "content": "You are 
    , a Revit assistant." },
    { "role": "user", "content": "How do I purge unused families?" }
  ],
  "temperature": 0.7,
  "max_tokens": 1500
}
```

### Tool-calling example (real plugin traffic)

```json
POST /api/v1/ai/chat
{
  "model": "gpt-4o-mini",
  "messages": [...],
  "tools": [
    { "type": "function", "function": { "name": "get_view_range", "parameters": {...} } },
    { "type": "function", "function": { "name": "query_audit_log", "parameters": {...} } }
  ],
  "tool_choice": "auto",
  "temperature": 0.7,
  "max_tokens": 1500
}
```

The backend should NOT inspect or alter `tools` / `tool_choice` — forward verbatim.

### Streaming example

```json
POST /api/v1/ai/chat/stream
{
  "model": "gpt-4o-mini",
  "messages": [...],
  "stream": true,
  "max_tokens": 1500
}
```

Backend must stream chunks back. Buffering the full response defeats the live-typing UX.

## Out of scope for v1

- Embedding-based RAG on the server (plugin does its own BM25 retrieval).
- Conversation persistence (plugin owns this via its local SQLite `ChatRepository`).
- Tool execution (tool dispatch lives in the plugin — backend just forwards the model's
  `tool_calls` response back to the plugin, which executes them and re-asks).
- Multi-tenancy of the OpenAI key (one key in Key Vault serves all users).

## Acceptance criteria

- [ ] `POST /api/v1/ai/chat` with a valid JWT and a sane OpenAI body returns a 200 with
      OpenAI's response within ~3 seconds for short prompts.
- [ ] `POST /api/v1/ai/chat/stream` streams SSE chunks back without buffering (verify with
      `curl -N` — first chunk should arrive within 1 second).
- [ ] Request without `Authorization` header returns 401.
- [ ] Request with expired JWT returns 401.
- [ ] Key Vault key rotation is picked up within 24 hours without redeploy (test by
      changing the secret, waiting for `IConfiguration` to refresh, and verifying calls
      use the new key).
- [ ] `AiUsage` rows are written for every successful call.
- [ ] Plugin with `AI:UseBackendProxy=true` works end-to-end in staging.
