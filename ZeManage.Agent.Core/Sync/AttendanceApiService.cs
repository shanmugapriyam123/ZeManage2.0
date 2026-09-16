using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZeManage.Agent.Core.Services;

namespace ZeManage.Agent.Core.Sync;

/// <summary>Fetches today's Active/Idle/Break/Total-Work-Time tile from the backend's self-service
/// GET /api/v1/agentdb/employee-machine-dashboard/today-summary endpoint — UserId/MachineId are
/// resolved server-side from this agent's own JWT, no query params needed. Returns the exact same
/// EmployeeTimeSummary shape (Productive/Unproductive/Idle/Break/WorkHoursSeconds) the admin's own
/// Employee Machine Dashboard is built from, so these numbers always match what an admin sees for
/// this employee's today. "Active" for this tile = ProductiveSeconds + UnproductiveSeconds (any
/// classified app-usage time, not just the productive half) — WorkHoursSeconds is used directly for
/// Total Work Time rather than re-derived client-side, since it also folds in unclassified
/// (Neutral) focus time that Productive+Unproductive alone would miss.</summary>
public sealed class AttendanceApiService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly TokenProvider _tokens;
    private readonly AgentOptions _opts;
    private readonly ILogger<AttendanceApiService> _log;

    public AttendanceApiService(
        IHttpClientFactory httpFactory, TokenProvider tokens,
        IOptions<AgentOptions> opts, ILogger<AttendanceApiService> log)
    {
        _httpFactory = httpFactory;
        _tokens = tokens;
        _opts = opts.Value;
        _log = log;
    }

    public async Task<(long ActiveSeconds, long IdleSeconds, long BreakSeconds, long TotalWorkSeconds)?> GetTodaySummaryAsync(CancellationToken ct = default)
    {
        try
        {
            var token = await _tokens.GetAsync(ct);
            if (token is null) return null;

            var client = _httpFactory.CreateClient("backend");
            client.BaseAddress = new Uri(_opts.BackendBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await client.GetAsync("/api/v1/agentdb/employee-machine-dashboard/today-summary", ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("GetTodaySummaryAsync: {Status}", (int)resp.StatusCode);
                return null;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var data)) return null;

            var productive   = GetLong(data, "productiveSeconds");
            var unproductive = GetLong(data, "unproductiveSeconds");
            var idle         = GetLong(data, "idleSeconds");
            var brk          = GetLong(data, "breakSeconds");
            var workHours    = GetLong(data, "workHoursSeconds");

            return (productive + unproductive, idle, brk, workHours);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "GetTodaySummaryAsync failed");
            return null;
        }
    }

    private static long GetLong(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
}
