using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using BIManage.AI.Interfaces;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;

namespace BIManage.AI
{
    /// <summary>
    /// Fetches the latest model metrics for the currently open Revit document
    /// from all three API endpoints (manual, periodic, syncsave) and formats
    /// a rich context block that is injected into the AI prompt.
    ///
    /// Flow:
    ///   1. Extract modelGuid from the active Revit Document via ModelGuidHelper
    ///   2. GET /api/v1/Revit/metrics/manual      → filter to this modelGuid, pick latest by capturedAt
    ///   3. GET /api/v1/Revit/metrics/periodic    → filter to this modelGuid, pick latest by capturedAt
    ///   4. GET /api/v1/Revit/metrics/syncsave    → filter to this modelGuid, pick latest by capturedAt
    ///   5. Format all three into a human-readable context block
    /// </summary>
    public class ModelContextService
    {
        private readonly AuthenticatedHttpClient _httpClient;
        private readonly ILogger? _logger;

        // API endpoints (GET - read the stored data)
        private const string ManualEndpoint    = "/api/v1/Revit/metrics/manual";
        private const string PeriodicEndpoint  = "/api/v1/Revit/metrics/periodic";
        private const string SyncSaveEndpoint  = "/api/v1/Revit/metrics/syncsave";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling         = JsonCommentHandling.Skip
        };

        public ModelContextService(AuthenticatedHttpClient httpClient, ILogger? logger = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger;
        }

        // ──────────────────────────────────────────────────────────────────────
        // Public API
        // ──────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Extracts the model GUID from the active Revit document.
        /// Returns null if the document is null or a family document.
        /// </summary>
        public string? GetModelGuid(Document? doc)
        {
            if (doc == null || doc.IsFamilyDocument)
                return null;

            try
            {
                return ModelGuidHelper.GetModelGuid(doc, _logger);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ModelContextService] Could not get model GUID: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Returns true if the user message is asking about why a specific element
        /// is not visible in a view — routes to ElementVisibilityService for diagnosis.
        /// </summary>
        public bool IsVisibilityQuery(string userMessage) =>
            ElementVisibilityService.IsVisibilityQuery(userMessage);

        /// <summary>
        /// Checks whether the user message is asking about model metrics / health.
        /// Used to gate the API calls so we don't hit the backend for every question.
        /// </summary>
        public bool IsModelAnalysisQuery(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return false;

            var lower = userMessage.ToLowerInvariant();

            // ────────────────────────────────────────────────────────────────────
            // GATE 1 — explicit reference to "the open model" / "my model" / "this".
            // If the user is talking about THEIR model, fetch metrics. This is the
            // most reliable signal: catches phrases like "check my model", "what's
            // in this file", "the current project's warnings".
            // ────────────────────────────────────────────────────────────────────
            var modelReferences = new[]
            {
                "my model", "this model", "the model", "current model",
                "this file", "my file", "the file", "this project", "my project",
                "the project", "current project", "active document", "open model",
                "in this", "of this", "my revit"
            };
            if (modelReferences.Any(k => lower.Contains(k))) return true;

            // ────────────────────────────────────────────────────────────────────
            // GATE 2 — numeric/aggregate questions ("how many walls", "the largest
            // duct"). These implicitly need live data.
            // ────────────────────────────────────────────────────────────────────
            var aggregatePhrases = new[]
            {
                "how many", "how much", "what's the count", "whats the count",
                "what is the count", "total count", "largest", "smallest",
                "longest", "shortest", "tallest", "biggest", "most common",
                "average", "summary of", "list all", "list the",
                "show me all", "show me the"
            };
            if (aggregatePhrases.Any(k => lower.Contains(k))) return true;

            // ────────────────────────────────────────────────────────────────────
            // GATE 3 — direct mentions of model-state / governance concepts that
            // only make sense against live data. Generic-knowledge questions like
            // "what is a wall" deliberately don't trigger this — they're answered
            // from BIM knowledge, not from the open file.
            // ────────────────────────────────────────────────────────────────────
            var liveStateKeywords = new[]
            {
                "model health", "health alert", "health alerts", "health check",
                "warnings count", "warning count", "model warnings", "model warning",
                "duplicate elements", "unused families", "purge unused",
                "audit log", "audit trail", "protection rule", "protection rules",
                "pin protection", "active rule", "sync history", "last sync",
                "when synced", "model statistics", "model overview",
                "what's in this", "whats in this", "what is in this"
            };
            if (liveStateKeywords.Any(k => lower.Contains(k))) return true;

            // Everything else (including "what is revit", "how to draw duct",
            // "best practices for sheets") is definitional or how-to. The LLM
            // answers those generically — fetching metrics for them just wastes
            // a backend roundtrip and injects irrelevant context.
            return false;
        }

        /// <summary>
        /// Main entry point: fetches all 3 endpoints in parallel, filters by modelGuid,
        /// picks latest by date, and returns a formatted context block.
        /// Returns null if not authenticated or no data found.
        /// </summary>
        public async Task<ModelContextResult?> GetModelContextAsync(string? modelGuid, string? modelName = null)
        {
            if (string.IsNullOrEmpty(modelGuid))
            {
                AiDiagLog.ContextSkipped("modelGuid is null or empty");
                _logger?.LogDebug("[ModelContextService] No model GUID provided – skipping context fetch");
                return null;
            }

            // ── Follow the same pattern as RulesSyncService.FetchAllRulesFromApiAsync ──
            // Null guard only — do NOT gate on IsAuthenticated for GET calls.
            // AuthenticatedHttpClient.GetAsync() attaches the Bearer token when available.
            // - Endpoint open (now) : succeeds with HTTP 200, no token needed.
            // - Endpoint locked (future): token is auto-attached + retried on 401.
            if (_httpClient == null)
            {
                AiDiagLog.ContextSkipped("AuthenticatedHttpClient is null");
                _logger?.LogWarning("[ModelContextService] HTTP client is null — cannot fetch metrics");
                return null;
            }

            // Log for diagnostics — but this is informational only, NOT a blocker
            AiDiagLog.AuthCheck(_httpClient.IsAuthenticated);
            _logger?.LogInfo($"[ModelContextService] Fetching metrics for GUID: {modelGuid} | IsAuthenticated={_httpClient.IsAuthenticated}");

            // Log the full URLs being called so we can verify the base URL is correct
            AiDiagLog.ApiCallStart($"{ManualEndpoint}  [base from DI AuthenticatedHttpClient]");
            AiDiagLog.ApiCallStart($"{PeriodicEndpoint} [base from DI AuthenticatedHttpClient]");
            AiDiagLog.ApiCallStart($"{SyncSaveEndpoint} [base from DI AuthenticatedHttpClient]");

            var manualTask   = FetchManualMetricsAsync(modelGuid);
            var periodicTask = FetchPeriodicMetricsAsync(modelGuid);
            var syncSaveTask = FetchSyncSaveMetricsAsync(modelGuid);

            await Task.WhenAll(manualTask, periodicTask, syncSaveTask);

            var manual   = await manualTask;
            var periodic = await periodicTask;
            var syncSave = await syncSaveTask;

            AiDiagLog.ContextResult(
                hasManual:   manual   != null,
                hasPeriodic: periodic != null,
                hasSyncSave: syncSave != null);

            if (manual == null && periodic == null && syncSave == null)
            {
                _logger?.LogDebug("[ModelContextService] No metrics data found for this model");
                return null;
            }

            var result = new ModelContextResult
            {
                ModelGuid      = modelGuid,
                ModelName      = modelName ?? manual?.ModelName ?? periodic?.ModelName ?? syncSave?.ModelName ?? "Unknown",
                LatestManual   = manual,
                LatestPeriodic = periodic,
                LatestSyncSave = syncSave
            };

            _logger?.LogInfo($"[ModelContextService] Context ready – manual:{manual != null}, periodic:{periodic != null}, syncsave:{syncSave != null}");
            return result;
        }

        /// <summary>
        /// Formats a compact one-line summary of the model's key health metrics.
        /// Only non-zero / non-null fields are included — saves ~270 tokens vs the full block.
        /// </summary>
        public string FormatSlimContextBlock(ModelContextResult ctx)
        {
            var sb = new StringBuilder();
            sb.Append($"MODEL: {ctx.ModelName}");

            if (ctx.LatestSyncSave != null)
            {
                sb.Append($" | lastSync:{ctx.LatestSyncSave.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
                sb.Append($" | syncType:{ctx.LatestSyncSave.CaptureType ?? "unknown"}");
                sb.Append($" | syncBy:{ctx.LatestSyncSave.CapturedBy ?? "unknown"}");

                if (ctx.LatestSyncSave.FileSizeBytes > 0)
                {
                    var mb = ctx.LatestSyncSave.FileSizeBytes / 1_048_576.0;
                    sb.Append($" | fileSize:{mb:F0}MB");
                }
            }

            if (ctx.LatestSyncSave?.WarningsCount > 0)
                sb.Append($" | warnings:{ctx.LatestSyncSave.WarningsCount}");

            if (ctx.LatestPeriodic?.TotalElementsCount > 0)
                sb.Append($" | elements:{ctx.LatestPeriodic.TotalElementsCount:N0}");

            if (ctx.LatestSyncSave?.TotalFamiliesCount > 0)
                sb.Append($" | families:{ctx.LatestSyncSave.TotalFamiliesCount}");

            if (ctx.LatestPeriodic?.UnplacedRoomsCount > 0)
                sb.Append($" | unplacedRooms:{ctx.LatestPeriodic.UnplacedRoomsCount}");

            if (ctx.LatestPeriodic?.ViewsNotOnSheetsCount > 0)
                sb.Append($" | viewsOffSheets:{ctx.LatestPeriodic.ViewsNotOnSheetsCount}");

            if (ctx.LatestManual?.FamiliesOver5mbCount > 0)
                sb.Append($" | largeFamilies:{ctx.LatestManual.FamiliesOver5mbCount}");

            if (ctx.LatestManual?.PurgeableElementsCount > 0)
                sb.Append($" | purgeable:{ctx.LatestManual.PurgeableElementsCount}");

            if (ctx.LatestSyncSave?.DuplicateElementsCount > 0)
                sb.Append($" | duplicates:{ctx.LatestSyncSave.DuplicateElementsCount}");

            sb.AppendLine();
            sb.AppendLine("Use ONLY these numbers. Do NOT invent values. Highlight anything concerning.");

            return sb.ToString();
        }

        /// <summary>
        /// Formats the ModelContextResult into a string block
        /// that is injected above the user prompt in the AI system message.
        /// </summary>
        public string FormatContextBlock(ModelContextResult ctx)
        {
            var sb = new StringBuilder();
            sb.AppendLine("═══════════════════════════════════════════════════════");
            sb.AppendLine("  LIVE MODEL DATA — USE THIS FOR YOUR ANALYSIS");
            sb.AppendLine("═══════════════════════════════════════════════════════");
            sb.AppendLine($"  Model  : {ctx.ModelName}");
            sb.AppendLine($"  GUID   : {ctx.ModelGuid}");
            sb.AppendLine();

            // ── Manual (Deep Analysis) ──────────────────────────────────────
            if (ctx.LatestManual != null)
            {
                var m = ctx.LatestManual;
                sb.AppendLine("📊 MANUAL ANALYSIS (User-Triggered Deep Scan)");
                sb.AppendLine($"   Captured     : {m.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm}  by {m.CapturedBy}");
                sb.AppendLine($"   Reason       : {m.CaptureReason}");
                sb.AppendLine($"   Large Families (>5 MB) : {m.FamiliesOver5mbCount}");
                sb.AppendLine($"   Purgeable Elements     : {m.PurgeableElementsCount}");
                sb.AppendLine();
            }

            // ── Periodic (Hourly Health Snapshot) ───────────────────────────
            if (ctx.LatestPeriodic != null)
            {
                var p = ctx.LatestPeriodic;
                sb.AppendLine("⏱ PERIODIC SNAPSHOT (24-Hour Health Check)");
                sb.AppendLine($"   Captured     : {p.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm}  by {p.CapturedBy}");
                sb.AppendLine($"   Interval     : {p.CaptureIntervalHours}h  |  Manual trigger: {(p.IsManualTrigger ? "Yes" : "No")}");
                sb.AppendLine();
                sb.AppendLine("   Element Counts:");
                sb.AppendLine($"     Total Elements      : {p.TotalElementsCount:N0}");
                sb.AppendLine($"     Model Elements      : {p.ModelElementsCount:N0}");
                sb.AppendLine($"     Annotative Elements : {p.AnnotativeElementsCount:N0}");
                sb.AppendLine($"     In-Place Families   : {p.InplaceFamiliesCount}");
                sb.AppendLine();
                sb.AppendLine("   Room & View Status:");
                sb.AppendLine($"     Unplaced Rooms      : {p.UnplacedRoomsCount}");
                sb.AppendLine($"     Unenclosed Rooms    : {p.UnenclosedRoomsCount}");
                sb.AppendLine($"     Views Not on Sheets : {p.ViewsNotOnSheetsCount}");
                sb.AppendLine();
                sb.AppendLine("   Connectivity Issues:");
                sb.AppendLine($"     Walls Not Connected : {p.WallsNotConnectedCount}");
                sb.AppendLine($"     Pipes Not Connected : {p.PipesNotConnectedCount}");
                sb.AppendLine($"     Ducts Not Connected : {p.DuctsNotConnectedCount}");
                sb.AppendLine();
            }

            // ── Sync/Save (File Metadata) ───────────────────────────────────
            if (ctx.LatestSyncSave != null)
            {
                var s = ctx.LatestSyncSave;
                var fileSizeMb = s.FileSizeBytes > 0 ? (s.FileSizeBytes / 1_048_576.0).ToString("F1") + " MB" : "N/A";
                sb.AppendLine("💾 SYNC/SAVE METADATA (Latest File State)");
                sb.AppendLine($"   Captured     : {s.CapturedAt.ToLocalTime():yyyy-MM-dd HH:mm}  by {s.CapturedBy}");
                sb.AppendLine($"   Capture Type : {s.CaptureType}");
                sb.AppendLine($"   File Size    : {fileSizeMb}");
                sb.AppendLine();
                sb.AppendLine("   Model Structure:");
                sb.AppendLine($"     Levels              : {s.LevelsCount}");
                sb.AppendLine($"     Grids               : {s.GridsCount}");
                sb.AppendLine($"     Total Views         : {s.TotalViewsCount}");
                sb.AppendLine($"     Total Families      : {s.TotalFamiliesCount}");
                sb.AppendLine($"     Design Options      : {s.DesignOptionsCount}");
                sb.AppendLine($"     Model Groups        : {s.ModelGroupsCount}");
                sb.AppendLine($"     Detail Groups       : {s.DetailGroupsCount}");
                sb.AppendLine();
                sb.AppendLine("   Warnings & Issues:");
                sb.AppendLine($"     Warnings            : {s.WarningsCount}");
                sb.AppendLine($"     Duplicate Elements  : {s.DuplicateElementsCount}");
                sb.AppendLine();
                sb.AppendLine("   Linked & Imported Content:");
                sb.AppendLine($"     Linked Revit Files  : {s.LinkedRevitCount}");
                sb.AppendLine($"     Linked DWGs         : {s.LinkedDwgCount}");
                sb.AppendLine($"     Imported DWGs       : {s.ImportedDwgCount}");
                sb.AppendLine($"     Raster Images       : {s.RasterImagesCount}");
                if (s.SharedCoordNs.HasValue || s.SharedCoordEw.HasValue)
                {
                    sb.AppendLine();
                    sb.AppendLine("   Shared Coordinates:");
                    sb.AppendLine($"     N/S  : {s.SharedCoordNs?.ToString("F4") ?? "N/A"}");
                    sb.AppendLine($"     E/W  : {s.SharedCoordEw?.ToString("F4")  ?? "N/A"}");
                    sb.AppendLine($"     Elev : {s.SharedCoordElevation?.ToString("F4") ?? "N/A"}");
                }
                sb.AppendLine();
            }

            sb.AppendLine("═══════════════════════════════════════════════════════");
            sb.AppendLine("Use ONLY the numbers above when answering. Do NOT invent numbers.");
            sb.AppendLine("If a metric looks concerning, proactively highlight it and suggest a fix.");
            sb.AppendLine("═══════════════════════════════════════════════════════");

            return sb.ToString();
        }

        // ──────────────────────────────────────────────────────────────────────
        // Private helpers — fetch and filter each endpoint
        // ──────────────────────────────────────────────────────────────────────

        private async Task<ManualMetricsRecord?> FetchManualMetricsAsync(string modelGuid)
        {
            try
            {
                var response = await _httpClient.GetAsync(ManualEndpoint);
                var json     = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    AiDiagLog.ApiCallFailed(ManualEndpoint, (int)response.StatusCode, json);
                    _logger?.LogWarning($"[ModelContextService] Manual metrics GET failed: {response.StatusCode}");
                    return null;
                }

                var records = TryDeserializeList<ManualMetricsRecord>(json);
                var matched = records?.Where(r => string.Equals(r.ModelGuid, modelGuid, StringComparison.OrdinalIgnoreCase)).ToList();
                var latest  = matched?.OrderByDescending(r => r.CapturedAt).FirstOrDefault();

                AiDiagLog.ApiCallResult(
                    ManualEndpoint,
                    (int)response.StatusCode,
                    totalRecords:    records?.Count    ?? 0,
                    matchingRecords: matched?.Count    ?? 0,
                    latestDate:      latest?.CapturedAt);

                // If nothing matched — dump first 500 chars of raw JSON so we can inspect the GUID field name
                if ((matched?.Count ?? 0) == 0 && !string.IsNullOrEmpty(json))
                    AiDiagLog.ContextSkipped($"Manual — raw JSON snippet: {(json.Length > 500 ? json.Substring(0, 500) : json)}");

                _logger?.LogDebug($"[ModelContextService] Manual: {records?.Count ?? 0} total, matching={matched?.Count ?? 0}, latest={latest?.CapturedAt}");
                return latest;
            }
            catch (Exception ex)
            {
                AiDiagLog.ApiCallError(ManualEndpoint,
                    $"{ex.GetType().Name}: {ex.Message}" +
                    (ex.InnerException != null ? $" | inner: {ex.InnerException.Message}" : ""));
                _logger?.LogWarning($"[ModelContextService] FetchManualMetrics error: {ex.Message}");
                return null;
            }
        }

        private async Task<PeriodicMetricsRecord?> FetchPeriodicMetricsAsync(string modelGuid)
        {
            try
            {
                var response = await _httpClient.GetAsync(PeriodicEndpoint);
                var json     = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    AiDiagLog.ApiCallFailed(PeriodicEndpoint, (int)response.StatusCode, json);
                    _logger?.LogWarning($"[ModelContextService] Periodic metrics GET failed: {response.StatusCode}");
                    return null;
                }

                // The periodic endpoint returns { "success": true, "message": "...", "data": [...] }
                List<PeriodicMetricsRecord>? records = null;
                try
                {
                    var wrapped = JsonSerializer.Deserialize<PeriodicMetricsApiResponse>(json, JsonOpts);
                    records = wrapped?.Data;
                }
                catch
                {
                    records = TryDeserializeList<PeriodicMetricsRecord>(json);
                }

                var matched = records?.Where(r => string.Equals(r.ModelGuid, modelGuid, StringComparison.OrdinalIgnoreCase)).ToList();
                var latest  = matched?.OrderByDescending(r => r.CapturedAt).FirstOrDefault();

                AiDiagLog.ApiCallResult(
                    PeriodicEndpoint,
                    (int)response.StatusCode,
                    totalRecords:    records?.Count ?? 0,
                    matchingRecords: matched?.Count ?? 0,
                    latestDate:      latest?.CapturedAt);

                if ((matched?.Count ?? 0) == 0 && !string.IsNullOrEmpty(json))
                    AiDiagLog.ContextSkipped($"Periodic — raw JSON snippet: {(json.Length > 500 ? json.Substring(0, 500) : json)}");

                _logger?.LogDebug($"[ModelContextService] Periodic: {records?.Count ?? 0} total, matching={matched?.Count ?? 0}, latest={latest?.CapturedAt}");
                return latest;
            }
            catch (Exception ex)
            {
                AiDiagLog.ApiCallError(PeriodicEndpoint,
                    $"{ex.GetType().Name}: {ex.Message}" +
                    (ex.InnerException != null ? $" | inner: {ex.InnerException.Message}" : ""));
                _logger?.LogWarning($"[ModelContextService] FetchPeriodicMetrics error: {ex.Message}");
                return null;
            }
        }

        private async Task<SyncSaveMetricsRecord?> FetchSyncSaveMetricsAsync(string modelGuid)
        {
            try
            {
                var response = await _httpClient.GetAsync(SyncSaveEndpoint);
                var json     = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    AiDiagLog.ApiCallFailed(SyncSaveEndpoint, (int)response.StatusCode, json);
                    _logger?.LogWarning($"[ModelContextService] SyncSave metrics GET failed: {response.StatusCode}");
                    return null;
                }

                var records = TryDeserializeList<SyncSaveMetricsRecord>(json);
                var matched = records?.Where(r => string.Equals(r.ModelGuid, modelGuid, StringComparison.OrdinalIgnoreCase)).ToList();
                var latest  = matched?.OrderByDescending(r => r.CapturedAt).FirstOrDefault();

                AiDiagLog.ApiCallResult(
                    SyncSaveEndpoint,
                    (int)response.StatusCode,
                    totalRecords:    records?.Count ?? 0,
                    matchingRecords: matched?.Count ?? 0,
                    latestDate:      latest?.CapturedAt);

                if ((matched?.Count ?? 0) == 0 && !string.IsNullOrEmpty(json))
                    AiDiagLog.ContextSkipped($"SyncSave — raw JSON snippet: {(json.Length > 500 ? json.Substring(0, 500) : json)}");

                _logger?.LogDebug($"[ModelContextService] SyncSave: {records?.Count ?? 0} total, matching={matched?.Count ?? 0}, latest={latest?.CapturedAt}");
                return latest;
            }
            catch (Exception ex)
            {
                AiDiagLog.ApiCallError(SyncSaveEndpoint,
                    $"{ex.GetType().Name}: {ex.Message}" +
                    (ex.InnerException != null ? $" | inner: {ex.InnerException.Message}" : ""));
                _logger?.LogWarning($"[ModelContextService] FetchSyncSaveMetrics error: {ex.Message}");
                return null;
            }
        }

        /// <summary>Tries to deserialize JSON as a list, handles wrapped { data: [] } format too.</summary>
        private static List<T>? TryDeserializeList<T>(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            // Try direct array first
            try
            {
                var list = JsonSerializer.Deserialize<List<T>>(json, JsonOpts);
                if (list != null && list.Count > 0) return list;
            }
            catch { /* try next */ }

            // Try wrapped { "data": [...] }
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var dataProp))
                {
                    var inner = dataProp.GetRawText();
                    return JsonSerializer.Deserialize<List<T>>(inner, JsonOpts);
                }
            }
            catch { /* give up */ }

            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Result container
    // ══════════════════════════════════════════════════════════════════════════

    /// <summary>Holds the three latest metric snapshots for a single model.</summary>
    public class ModelContextResult
    {
        public string ModelGuid    { get; set; } = string.Empty;
        public string ModelName    { get; set; } = string.Empty;
        public ManualMetricsRecord?   LatestManual   { get; set; }
        public PeriodicMetricsRecord? LatestPeriodic { get; set; }
        public SyncSaveMetricsRecord? LatestSyncSave { get; set; }

        public bool HasAnyData =>
            LatestManual != null || LatestPeriodic != null || LatestSyncSave != null;
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Response DTOs — modelled from Swagger screenshots
    // ══════════════════════════════════════════════════════════════════════════

    #region Manual Metrics Record

    public class ManualMetricsRecord
    {
        [JsonPropertyName("captureId")]         public string?   CaptureId          { get; set; }
        [JsonPropertyName("sessionId")]         public string?   SessionId          { get; set; }
        [JsonPropertyName("documentId")]        public string?   DocumentId         { get; set; }
        [JsonPropertyName("modelGuid")]         public string?   ModelGuid          { get; set; }
        [JsonPropertyName("modelPath")]         public string?   ModelPath          { get; set; }
        [JsonPropertyName("modelName")]         public string?   ModelName          { get; set; }
        [JsonPropertyName("capturedAt")]        public DateTime  CapturedAt         { get; set; }
        [JsonPropertyName("capturedBy")]        public string?   CapturedBy         { get; set; }
        [JsonPropertyName("commandSource")]     public string?   CommandSource      { get; set; }
        [JsonPropertyName("captureReason")]     public string?   CaptureReason      { get; set; }
        [JsonPropertyName("familiesOver5mbCount")]    public int FamiliesOver5mbCount    { get; set; }
        [JsonPropertyName("purgeableElementsCount")]  public int PurgeableElementsCount  { get; set; }
        [JsonPropertyName("createdAt")]         public DateTime  CreatedAt          { get; set; }
        [JsonPropertyName("companyId")]         public string?   CompanyId          { get; set; }
    }

    #endregion

    #region Periodic Metrics Record

    /// <summary>Root response shape for /api/v1/Revit/metrics/periodic</summary>
    public class PeriodicMetricsApiResponse
    {
        [JsonPropertyName("success")]  public bool                    Success  { get; set; }
        [JsonPropertyName("message")]  public string?                 Message  { get; set; }
        [JsonPropertyName("data")]     public List<PeriodicMetricsRecord>? Data { get; set; }
    }

    public class PeriodicMetricsRecord
    {
        [JsonPropertyName("captureId")]               public string?   CaptureId               { get; set; }
        [JsonPropertyName("sessionId")]               public string?   SessionId               { get; set; }
        [JsonPropertyName("documentId")]              public string?   DocumentId              { get; set; }
        [JsonPropertyName("modelGuid")]               public string?   ModelGuid               { get; set; }
        [JsonPropertyName("modelPath")]               public string?   ModelPath               { get; set; }
        [JsonPropertyName("modelName")]               public string?   ModelName               { get; set; }
        [JsonPropertyName("capturedAt")]              public DateTime  CapturedAt              { get; set; }
        [JsonPropertyName("capturedBy")]              public string?   CapturedBy              { get; set; }
        [JsonPropertyName("captureIntervalHours")]    public int       CaptureIntervalHours    { get; set; }
        [JsonPropertyName("isManualTrigger")]         public bool      IsManualTrigger         { get; set; }
        [JsonPropertyName("totalElementsCount")]      public int       TotalElementsCount      { get; set; }
        [JsonPropertyName("modelElementsCount")]      public int       ModelElementsCount      { get; set; }
        [JsonPropertyName("annotativeElementsCount")] public int       AnnotativeElementsCount { get; set; }
        [JsonPropertyName("inplaceFamiliesCount")]    public int       InplaceFamiliesCount    { get; set; }
        [JsonPropertyName("unplacedRoomsCount")]      public int       UnplacedRoomsCount      { get; set; }
        [JsonPropertyName("viewsNotOnSheetsCount")]   public int       ViewsNotOnSheetsCount   { get; set; }
        [JsonPropertyName("unenclosedRoomsCount")]    public int       UnenclosedRoomsCount    { get; set; }
        [JsonPropertyName("wallsNotConnectedCount")]  public int       WallsNotConnectedCount  { get; set; }
        [JsonPropertyName("pipesNotConnectedCount")]  public int       PipesNotConnectedCount  { get; set; }
        [JsonPropertyName("ductsNotConnectedCount")]  public int       DuctsNotConnectedCount  { get; set; }
        [JsonPropertyName("createdAt")]               public DateTime  CreatedAt               { get; set; }
    }

    #endregion

    #region SyncSave Metrics Record

    public class SyncSaveMetricsRecord
    {
        [JsonPropertyName("captureId")]             public string?   CaptureId             { get; set; }
        [JsonPropertyName("sessionId")]             public string?   SessionId             { get; set; }
        [JsonPropertyName("documentId")]            public string?   DocumentId            { get; set; }
        [JsonPropertyName("modelGuid")]             public string?   ModelGuid             { get; set; }
        [JsonPropertyName("modelPath")]             public string?   ModelPath             { get; set; }
        [JsonPropertyName("modelName")]             public string?   ModelName             { get; set; }
        [JsonPropertyName("captureType")]           public string?   CaptureType           { get; set; }
        [JsonPropertyName("syncGuid")]              public string?   SyncGuid              { get; set; }
        [JsonPropertyName("capturedAt")]            public DateTime  CapturedAt            { get; set; }
        [JsonPropertyName("capturedBy")]            public string?   CapturedBy            { get; set; }
        [JsonPropertyName("fileSizeBytes")]         public long      FileSizeBytes         { get; set; }
        [JsonPropertyName("levelsCount")]           public int       LevelsCount           { get; set; }
        [JsonPropertyName("gridsCount")]            public int       GridsCount            { get; set; }
        [JsonPropertyName("designOptionsCount")]    public int       DesignOptionsCount    { get; set; }
        [JsonPropertyName("linkedDwgCount")]        public int       LinkedDwgCount        { get; set; }
        [JsonPropertyName("importedDwgCount")]      public int       ImportedDwgCount      { get; set; }
        [JsonPropertyName("linkedRevitCount")]      public int       LinkedRevitCount      { get; set; }
        [JsonPropertyName("rasterImagesCount")]     public int       RasterImagesCount     { get; set; }
        [JsonPropertyName("warningsCount")]         public int       WarningsCount         { get; set; }
        [JsonPropertyName("duplicateElementsCount")]public int       DuplicateElementsCount{ get; set; }
        [JsonPropertyName("modelGroupsCount")]      public int       ModelGroupsCount      { get; set; }
        [JsonPropertyName("detailGroupsCount")]     public int       DetailGroupsCount     { get; set; }
        [JsonPropertyName("totalViewsCount")]       public int       TotalViewsCount       { get; set; }
        [JsonPropertyName("totalFamiliesCount")]    public int       TotalFamiliesCount    { get; set; }
        [JsonPropertyName("sharedCoordNs")]         public double?   SharedCoordNs         { get; set; }
        [JsonPropertyName("sharedCoordEw")]         public double?   SharedCoordEw         { get; set; }
        [JsonPropertyName("sharedCoordElevation")]  public double?   SharedCoordElevation  { get; set; }
        [JsonPropertyName("createdAt")]             public DateTime  CreatedAt             { get; set; }
        [JsonPropertyName("companyId")]             public string?   CompanyId             { get; set; }
    }

    #endregion
}
