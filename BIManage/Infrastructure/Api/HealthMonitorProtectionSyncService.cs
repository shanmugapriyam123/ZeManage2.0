using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.Api
{
    /// <summary>
    /// Fetches health monitor protection thresholds from the backend API.
    /// Read-only — CRUD is managed by the admin portal.
    /// Thresholds are UI/visual only (displayed in ModelHealthDashboard as reference targets).
    /// They do NOT affect protection logic, rule evaluation, or command blocking.
    /// Endpoint: GET /api/v1/Revit/health-monitor-protections/by-model/{modelGuid}
    /// </summary>
    public class HealthMonitorProtectionSyncService
    {
        private readonly AuthenticatedHttpClient? _httpClient;
        private readonly ILogger? _logger;

        private const string Endpoint = "/api/v1/Revit/health-monitor-protections/by-model";

        // In-memory cache — thresholds are small and rarely change per session
        private HealthMonitorProtection? _cachedProtection;
        private string? _cachedModelGuid;

        public HealthMonitorProtectionSyncService(
            AuthenticatedHttpClient? httpClient,
            ILogger? logger = null)
        {
            _httpClient = httpClient;
            _logger = logger;

            _logger?.LogInfo($"HealthMonitorProtectionSyncService initialized (HTTP: {(_httpClient != null ? "enabled" : "disabled")})");
        }

        /// <summary>
        /// Returns the cached protection if the model matches; fetches fresh from API otherwise.
        /// </summary>
        public async Task<HealthMonitorProtection?> FetchProtectionAsync(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return null;

            // Return cached if same model
            if (_cachedProtection != null && _cachedModelGuid == modelGuid)
            {
                return _cachedProtection;
            }

            try
            {
                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogDebug("Not authenticated, skipping health monitor protection fetch");
                    return null;
                }

                var endpoint = $"{Endpoint}/{modelGuid}";
                var response = await _httpClient.GetAsync(endpoint);

                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"Fetch health monitor protections failed: {response.StatusCode} - {body}");
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Health monitor protection response (model {modelGuid}):\n{json}");

                var items = ParseResponse(json);

                if (items == null || items.Count == 0)
                {
                    _logger?.LogInfo($"No health monitor protections returned from API for model {modelGuid}");
                    _cachedProtection = null;
                    _cachedModelGuid = modelGuid;
                    return null;
                }

                // Take the first/effective entry (server returns company → project → model priority)
                _cachedProtection = items[0];
                _cachedModelGuid = modelGuid;

                _logger?.LogInfo($"Fetched health monitor protection for model {modelGuid}: " +
                    $"maxModelSize={_cachedProtection.MaxModelSize}, maxWarnings={_cachedProtection.MaximumWarningCount}, " +
                    $"maxInPlaceFamily={_cachedProtection.MaxInPlaceFamilyCount}, " +
                    $"maxGrids={_cachedProtection.MaxGridsCount}, maxLevels={_cachedProtection.MaxLevelsCount}, " +
                    $"maxImportedDwg={_cachedProtection.MaxImportedDwgCount}, maxNonNativeStyles={_cachedProtection.MaxNonNativeObjectStylesCount}, " +
                    $"maxPurgeable={_cachedProtection.MaxPurgeableElementsCount}, maxTotalViews={_cachedProtection.MaxTotalViewsCount}");

                return _cachedProtection;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"FetchHealthMonitorProtection failed (non-critical): {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Invalidate cached thresholds (e.g. on model switch or SignalR push).
        /// </summary>
        public void InvalidateCache()
        {
            _cachedProtection = null;
            _cachedModelGuid = null;
        }

        /// <summary>
        /// Parse the API response in multiple formats (same pattern as other sync services).
        /// Handles: { "data": [...] }, { "$values": [...] }, direct array
        /// </summary>
        private List<HealthMonitorProtection>? ParseResponse(string json)
        {
            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                List<HealthMonitorParameter>? parameters = null;

                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    parameters = JsonSerializer.Deserialize<List<HealthMonitorParameter>>(dataElement.GetRawText(), jsonOptions);
                else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("$values", out var valuesElement))
                    parameters = JsonSerializer.Deserialize<List<HealthMonitorParameter>>(valuesElement.GetRawText(), jsonOptions);
                else if (root.ValueKind == JsonValueKind.Array)
                    parameters = JsonSerializer.Deserialize<List<HealthMonitorParameter>>(json, jsonOptions);

                // Pass the logger so each parameter's mapping decision (which DTO field
                // it lands in, or "(unmatched)" if none of the patterns hit) is recorded —
                // this is the diagnostic gap that previously made it impossible to tell
                // WHY a threshold the admin configured wasn't surfacing in the dashboard.
                var mapped = HealthMonitorProtection.MapFromParameters(parameters, _logger);
                if (mapped != null)
                {
                    _logger?.LogInfo($"[HealthMonitor] Mapped goals: " +
                        $"MaxModelSize={mapped.MaxModelSize}, MaximumWarningCount={mapped.MaximumWarningCount}, " +
                        $"MaxDuplicateElementsCount={mapped.MaxDuplicateElementsCount}, " +
                        $"ViewsNotOnSheet={mapped.ViewsNotOnSheet}, MaxImportedDwgCount={mapped.MaxImportedDwgCount}, " +
                        $"MaxNonNativeObjectStylesCount={mapped.MaxNonNativeObjectStylesCount}, " +
                        $"MaxTotalWorksetsCount={mapped.MaxTotalWorksetsCount}, " +
                        $"MaxTotalFamiliesCount={mapped.MaxTotalFamiliesCount}, " +
                        $"MaxTotalViewsCount={mapped.MaxTotalViewsCount}, MaxPurgeableElementsCount={mapped.MaxPurgeableElementsCount}");
                }
                return mapped != null ? new List<HealthMonitorProtection> { mapped } : null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to deserialize health monitor protection response: {ex.Message}", ex);
            }

            return null;
        }
    }

    /// <summary>
    /// Single parameter item returned by the new API format.
    /// GET /api/v1/Revit/health-monitor-protections/by-model/{modelGuid}
    /// returns an array of these, one per metric.
    /// </summary>
    public class HealthMonitorParameter
    {
        [JsonPropertyName("healthMonitorParameterId")]
        public string? HealthMonitorParameterId { get; set; }

        [JsonPropertyName("parameterCode")]
        public string? ParameterCode { get; set; }

        [JsonPropertyName("parameterName")]
        public string? ParameterName { get; set; }

        [JsonPropertyName("companyId")]
        public string? CompanyId { get; set; }

        [JsonPropertyName("isDefault")]
        public bool IsDefault { get; set; }

        [JsonPropertyName("isEnabled")]
        public bool IsEnabled { get; set; }

        [JsonPropertyName("unitType")]
        public string? UnitType { get; set; }

        [JsonPropertyName("warningThreshold")]
        public double WarningThreshold { get; set; }

        [JsonPropertyName("breachedThreshold")]
        public double BreachedThreshold { get; set; }

        // The StaticModelInfo endpoint (/api/v1/Revit/models/StaticModelInfo/{guid})
        // returns each parameter with a single "threshold" field — not the
        // warningThreshold/breachedThreshold pair the /health-monitor-protections
        // endpoint uses. Deserializing both lets the same MapFromParameters method
        // handle the shape from EITHER endpoint without separate classes.
        [JsonPropertyName("threshold")]
        public double Threshold { get; set; }

        [JsonPropertyName("sendMail")]
        public bool SendMail { get; set; }

        [JsonPropertyName("scope")]
        public int Scope { get; set; }

        [JsonPropertyName("projectId")]
        public string? ProjectId { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }
    }

    /// <summary>
    /// Aggregated view of all health metric goals, mapped from the API parameter array.
    /// Used for UI/visual display only — does not affect protection logic.
    /// </summary>
    public class HealthMonitorProtection
    {
        [JsonPropertyName("healthMonitorProtectionId")]
        public string? HealthMonitorProtectionId { get; set; }

        [JsonPropertyName("healthMonitorProtectionName")]
        public string? HealthMonitorProtectionName { get; set; }

        [JsonPropertyName("companyId")]
        public string? CompanyId { get; set; }

        [JsonPropertyName("maxModelSize")]
        public long? MaxModelSize { get; set; }

        [JsonPropertyName("maxFamilySize")]
        public long? MaxFamilySize { get; set; }

        [JsonPropertyName("maxDuplicateElementsCount")]
        public int? MaxDuplicateElementsCount { get; set; }

        [JsonPropertyName("maximumWarningCount")]
        public int? MaximumWarningCount { get; set; }

        [JsonPropertyName("maxInPlaceFamilyCount")]
        public int? MaxInPlaceFamilyCount { get; set; }

        [JsonPropertyName("viewsNotOnSheet")]
        public double? ViewsNotOnSheet { get; set; }

        // Additional API-configurable goals (each maps from a separate parameterCode in the
        // /api/v1/Revit/health-monitor-protections/by-model response). The dashboard only
        // surfaces a "Needs Attention" tile when the matching field is non-null and > 0.
        [JsonPropertyName("maxLevelsCount")]
        public int? MaxLevelsCount { get; set; }

        [JsonPropertyName("maxGridsCount")]
        public int? MaxGridsCount { get; set; }

        [JsonPropertyName("maxLinkedRevitCount")]
        public int? MaxLinkedRevitCount { get; set; }

        [JsonPropertyName("maxLinkedDwgCount")]
        public int? MaxLinkedDwgCount { get; set; }

        [JsonPropertyName("maxImportedDwgCount")]
        public int? MaxImportedDwgCount { get; set; }

        [JsonPropertyName("maxRasterImagesCount")]
        public int? MaxRasterImagesCount { get; set; }

        [JsonPropertyName("maxNonNativeObjectStylesCount")]
        public int? MaxNonNativeObjectStylesCount { get; set; }

        // Goals reported as separate health-monitor-protection parameters on the web
        // side but previously had no mapping here — so the dashboard's "Needs Attention"
        // pill and the matching tile border colour never reflected the configured limits.
        // PurgeableElements: typical web parameterCode = "PURGEABLE_ELEMENTS"
        // TotalViews:        typical web parameterCode = "TOTAL_VIEWS"
        [JsonPropertyName("maxPurgeableElementsCount")]
        public int? MaxPurgeableElementsCount { get; set; }

        [JsonPropertyName("maxTotalViewsCount")]
        public int? MaxTotalViewsCount { get; set; }

        // Web parameters that were missing from this DTO — observed 2026-05-28 on
        // a project where the Health Monitor page had 6 enabled thresholds but the
        // plugin's "Needs Attention" pill only counted 4 of them. Total Worksets
        // and Total Families are both surfaced as separate parameterCodes on the
        // server (TOTAL_WORKSETS / TOTAL_FAMILIES typically) and the plugin already
        // captures the live counts in SnapshotSyncSave; they just had no goal field
        // to compare against, so the dashboard never added them to the metric set.
        [JsonPropertyName("maxTotalWorksetsCount")]
        public int? MaxTotalWorksetsCount { get; set; }

        [JsonPropertyName("maxTotalFamiliesCount")]
        public int? MaxTotalFamiliesCount { get; set; }

        // Server "Families Over SMB" (i.e. families exceeding 5 MB) threshold — drives
        // the Oversized Families dashboard tile. Without this field the parameter
        // canonicalises to "FAMILIESOVERSMB" and falls into the broad FAMILIES branch,
        // silently overwriting MaxTotalFamiliesCount. Added 2026-05-29.
        [JsonPropertyName("maxFamiliesOver5MbCount")]
        public int? MaxFamiliesOver5MbCount { get; set; }

        // Additional optimisation / quality / view-management goals the web Health
        // Monitor page now exposes. Previously the parameterCodes for these arrived
        // unmatched in MapFromParameters and were silently dropped, so the
        // dashboard's Model Groups / Detail Groups / View Templates / Design
        // Options / Unplaced Rooms / Unenclosed Rooms / Sheets tiles never picked
        // up the admin's configured threshold (no pill, no red colour change).
        [JsonPropertyName("maxModelGroupsCount")]     public int? MaxModelGroupsCount     { get; set; }
        [JsonPropertyName("maxDetailGroupsCount")]    public int? MaxDetailGroupsCount    { get; set; }
        [JsonPropertyName("maxViewTemplatesCount")]   public int? MaxViewTemplatesCount   { get; set; }
        [JsonPropertyName("maxDesignOptionsCount")]   public int? MaxDesignOptionsCount   { get; set; }
        [JsonPropertyName("maxUnplacedRoomsCount")]   public int? MaxUnplacedRoomsCount   { get; set; }
        [JsonPropertyName("maxUnenclosedRoomsCount")] public int? MaxUnenclosedRoomsCount { get; set; }
        [JsonPropertyName("maxSheetsCount")]          public int? MaxSheetsCount          { get; set; }

        // Disconnected-elements goals (Walls / Pipes / Ducts) and the Element
        // Composition total goal. Added 2026-06-05 so the dashboard's Disconnected
        // Elements card and Element Composition donut card can pick up an
        // admin-configured threshold and flip to red when the live count meets
        // or exceeds it.
        [JsonPropertyName("maxWallsNotConnectedCount")] public int? MaxWallsNotConnectedCount { get; set; }
        [JsonPropertyName("maxPipesNotConnectedCount")] public int? MaxPipesNotConnectedCount { get; set; }
        [JsonPropertyName("maxDuctsNotConnectedCount")] public int? MaxDuctsNotConnectedCount { get; set; }
        [JsonPropertyName("maxTotalElementsCount")]     public int? MaxTotalElementsCount     { get; set; }

        // Per-chip Element Composition goals (Model / Annotative / Other). The
        // overall MaxTotalElementsCount above covers the donut total; these three
        // drive the individual chip thresh-pills and red-colour rule on each
        // breakdown row.
        [JsonPropertyName("maxModelElementsCount")]      public int? MaxModelElementsCount      { get; set; }
        [JsonPropertyName("maxAnnotativeElementsCount")] public int? MaxAnnotativeElementsCount { get; set; }
        [JsonPropertyName("maxOtherElementsCount")]      public int? MaxOtherElementsCount      { get; set; }

        [JsonPropertyName("projectId")]
        public string? ProjectId { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("isDefault")]
        public bool IsDefault { get; set; }

        [JsonPropertyName("levelScope")]
        public int LevelScope { get; set; }

        /// <summary>
        /// Maps the new API parameter array into the existing HealthMonitorProtection goal fields.
        /// Uses breachedThreshold as the goal value (hard limit) for each metric.
        /// parameterCode matching is case-insensitive. Normalizes both the API code and the
        /// match patterns by stripping non-letter/digit characters so "TOTAL_WORKSETS",
        /// "totalWorksets", "Total Worksets", "TOTAL-WORKSETS" etc. all match the same way.
        /// </summary>
        public static HealthMonitorProtection? MapFromParameters(List<HealthMonitorParameter>? parameters, ILogger? logger = null)
        {
            if (parameters == null || parameters.Count == 0) return null;

            var result = new HealthMonitorProtection();

            // Strip non-alphanumerics + uppercase so "Total Worksets", "TOTAL_WORKSETS",
            // "totalWorksetsCount", "total-worksets" all canonicalize to "TOTALWORKSETS".
            // The previous matcher relied on substring contains() with mixed underscore
            // patterns, which silently failed on any code that used a different delimiter
            // (whitespace, hyphen, mixed case). The user reported a 0/0 pill after the
            // refactor because of exactly that — the company's parameterCodes don't match
            // the underscore form we were checking for.
            static string Canon(string? s)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;
                var sb = new StringBuilder(s.Length);
                foreach (var ch in s)
                {
                    if (char.IsLetterOrDigit(ch)) sb.Append(char.ToUpperInvariant(ch));
                }
                return sb.ToString();
            }

            foreach (var p in parameters)
            {
                if (!p.IsEnabled) continue;
                var rawCode = p.ParameterCode ?? p.ParameterName ?? string.Empty;
                var code = Canon(rawCode);
                string mappedField = "(unmatched)";

                // PICK THE EFFECTIVE THRESHOLD — the backend exposes the same value
                // under three different JSON field names depending on which endpoint
                // returned it:
                //   • /health-monitor-protections/by-model  → breachedThreshold + warningThreshold
                //   • /models/StaticModelInfo/{guid}        → threshold (single)
                // Whichever endpoint is the source, only one of the three is non-zero
                // for a given parameter. Take BreachedThreshold first (stricter), then
                // WarningThreshold, then Threshold. This is the single behaviour change
                // that makes the StaticModelInfo array-shape response work without any
                // call-site changes.
                double effective = p.BreachedThreshold > 0 ? p.BreachedThreshold
                                 : p.WarningThreshold > 0 ? p.WarningThreshold
                                 : p.Threshold;

                // Order matters — more-specific codes must be checked before broader contains() matches.
                if (code.Contains("FILESIZE") || code.Contains("MODELSIZE"))
                { result.MaxModelSize = (long)effective; mappedField = nameof(MaxModelSize); }
                else if (code.Contains("FAMILYSIZE"))
                { result.MaxFamilySize = (long)effective; mappedField = nameof(MaxFamilySize); }
                else if (code.Contains("WARNING"))
                { result.MaximumWarningCount = (int)effective; mappedField = nameof(MaximumWarningCount); }
                else if (code.Contains("DUPLICATE"))
                { result.MaxDuplicateElementsCount = (int)effective; mappedField = nameof(MaxDuplicateElementsCount); }
                else if (code.Contains("INPLACE"))
                { result.MaxInPlaceFamilyCount = (int)effective; mappedField = nameof(MaxInPlaceFamilyCount); }
                else if (code.Contains("VIEWSNOTONSHEET") || code.Contains("VIEWNOTONSHEET") || code.Contains("VIEWSNOT"))
                { result.ViewsNotOnSheet = effective; mappedField = nameof(ViewsNotOnSheet); }
                // TOTALVIEWS must come AFTER VIEWSNOTONSHEET so the latter wins for codes
                // that contain both words. Dashboard treats total-views and views-not-on-sheet
                // as separate metrics.
                else if (code.Contains("TOTALVIEW"))
                { result.MaxTotalViewsCount = (int)effective; mappedField = nameof(MaxTotalViewsCount); }
                else if (code.Contains("PURGEABLE"))
                { result.MaxPurgeableElementsCount = (int)effective; mappedField = nameof(MaxPurgeableElementsCount); }
                // LINKEDREVIT re-added 2026-06-05 after the web's Health Monitor
                // exposed an explicit "Linked Revit Files" parameter. The
                // compound substring "LINKEDREVIT" is specific enough not to
                // collide with other Revit codes (LINKEDDWG matches below, not
                // here), and the earlier false-positive scenario was caused by
                // bare LINKED matching, which we never reintroduced.
                else if (code.Contains("LINKEDREVIT"))
                { result.MaxLinkedRevitCount = (int)effective; mappedField = nameof(MaxLinkedRevitCount); }
                else if (code.Contains("LINKEDDWG"))
                { result.MaxLinkedDwgCount = (int)effective; mappedField = nameof(MaxLinkedDwgCount); }
                else if (code.Contains("IMPORTEDDWG"))
                { result.MaxImportedDwgCount = (int)effective; mappedField = nameof(MaxImportedDwgCount); }
                else if (code.Contains("RASTER"))
                { result.MaxRasterImagesCount = (int)effective; mappedField = nameof(MaxRasterImagesCount); }
                else if (code.Contains("NONNATIVE") || code.Contains("OBJECTSTYLE"))
                { result.MaxNonNativeObjectStylesCount = (int)effective; mappedField = nameof(MaxNonNativeObjectStylesCount); }
                // Total Worksets / Total Families — must come BEFORE the broader LEVELS /
                // GRIDS / FAMILIES single-word matches. Added 2026-05-28 after the user's
                // 6-threshold web config was producing a 0/0 Revit pill because these two
                // codes were silently unmatched.
                else if (code.Contains("TOTALWORKSETS") || code.Contains("WORKSETS"))
                { result.MaxTotalWorksetsCount = (int)effective; mappedField = nameof(MaxTotalWorksetsCount); }
                // "FAMILIES_OVER_5MB" / "FAMILIES_OVER_SMB" / "OVERSIZED_FAMILIES" — must
                // come BEFORE the broad FAMILIES catch-all so it doesn't get swallowed
                // into MaxTotalFamiliesCount (which previously hid the Oversized Families
                // goal from the dashboard).
                else if (code.Contains("FAMILIESOVER") || code.Contains("OVERSIZED"))
                { result.MaxFamiliesOver5MbCount = (int)effective; mappedField = nameof(MaxFamiliesOver5MbCount); }
                else if (code.Contains("TOTALFAMILIES") || code.Contains("FAMILIES"))
                { result.MaxTotalFamiliesCount = (int)effective; mappedField = nameof(MaxTotalFamiliesCount); }
                // LEVELS / GRIDS re-added 2026-06-05 after the web exposed
                // explicit "Levels" and "Grids" parameters. Restricted to the
                // trailing-S form ONLY — the bare LEVEL / GRID fallback that
                // was here before is deliberately omitted so codes like
                // WARNING_LEVEL, LEVELOFDETAIL, REFERENCE_GRID, GRIDLINE etc.
                // can't false-positive into MaxLevelsCount / MaxGridsCount.
                else if (code.Contains("LEVELS"))
                { result.MaxLevelsCount = (int)effective; mappedField = nameof(MaxLevelsCount); }
                else if (code.Contains("GRIDS"))
                { result.MaxGridsCount = (int)effective; mappedField = nameof(MaxGridsCount); }
                // Optimisation Indicators tiles — must come BEFORE the broader
                // single-word fallbacks. MODELGROUPS/DETAILGROUPS specifically
                // match before any bare "GROUP" catch-all would, and the order
                // here mirrors the dashboard's tile order so future readers can
                // trace tile ↔ field at a glance.
                else if (code.Contains("MODELGROUPS") || code.Contains("MODELGROUP"))
                { result.MaxModelGroupsCount = (int)effective; mappedField = nameof(MaxModelGroupsCount); }
                else if (code.Contains("DETAILGROUPS") || code.Contains("DETAILGROUP"))
                { result.MaxDetailGroupsCount = (int)effective; mappedField = nameof(MaxDetailGroupsCount); }
                else if (code.Contains("VIEWTEMPLATE"))
                { result.MaxViewTemplatesCount = (int)effective; mappedField = nameof(MaxViewTemplatesCount); }
                else if (code.Contains("DESIGNOPTION"))
                { result.MaxDesignOptionsCount = (int)effective; mappedField = nameof(MaxDesignOptionsCount); }
                else if (code.Contains("UNPLACEDROOM"))
                { result.MaxUnplacedRoomsCount = (int)effective; mappedField = nameof(MaxUnplacedRoomsCount); }
                else if (code.Contains("UNENCLOSEDROOM"))
                { result.MaxUnenclosedRoomsCount = (int)effective; mappedField = nameof(MaxUnenclosedRoomsCount); }
                // SHEETS must come AFTER VIEWSNOTONSHEET (already handled above
                // with explicit code.Contains("VIEWSNOTONSHEET")) so we don't
                // accidentally swallow that parameter into MaxSheetsCount.
                else if (code.Contains("TOTALSHEETS") || code.Contains("SHEETS") || code.Contains("SHEET"))
                { result.MaxSheetsCount = (int)effective; mappedField = nameof(MaxSheetsCount); }
                // Disconnected Elements tiles — explicit compound matchers only
                // (WALLSNOTCONNECTED / PIPESNOTCONNECTED / DUCTSNOTCONNECTED). Do
                // NOT fall back to bare WALL/PIPE/DUCT contains() — those would
                // overlap with countless other Revit parameter codes and produce
                // false-positive thresholds, the same kind of misfire that just
                // forced the Levels/Grids/LinkedRevit matchers to be removed.
                else if (code.Contains("WALLSNOTCONNECTED") || code.Contains("WALLNOTCONNECTED") || code.Contains("WALLDISCONNECT"))
                { result.MaxWallsNotConnectedCount = (int)effective; mappedField = nameof(MaxWallsNotConnectedCount); }
                else if (code.Contains("PIPESNOTCONNECTED") || code.Contains("PIPENOTCONNECTED") || code.Contains("PIPEDISCONNECT"))
                { result.MaxPipesNotConnectedCount = (int)effective; mappedField = nameof(MaxPipesNotConnectedCount); }
                else if (code.Contains("DUCTSNOTCONNECTED") || code.Contains("DUCTNOTCONNECTED") || code.Contains("DUCTDISCONNECT"))
                { result.MaxDuctsNotConnectedCount = (int)effective; mappedField = nameof(MaxDuctsNotConnectedCount); }
                // Element Composition total goal — covers the "Threshold: 1,000"
                // pill the web Element Composition card displays. Explicit
                // TOTAL_ELEMENTS match keeps it from colliding with the more
                // specific MODEL_ELEMENTS / ANNOTATIVE_ELEMENTS codes below.
                else if (code.Contains("TOTALELEMENTS") || code.Contains("TOTALELEMENT"))
                { result.MaxTotalElementsCount = (int)effective; mappedField = nameof(MaxTotalElementsCount); }
                // Per-chip Element Composition goals. These MUST come AFTER the
                // TOTALELEMENT branch so a parameter named "TOTAL_ELEMENTS"
                // doesn't accidentally fall into MaxModelElementsCount via the
                // generic ELEMENT contains() — TOTAL is the more specific match
                // and wins above. Order within this group is irrelevant because
                // each pattern targets a distinct prefix.
                else if (code.Contains("MODELELEMENT"))
                { result.MaxModelElementsCount = (int)effective; mappedField = nameof(MaxModelElementsCount); }
                else if (code.Contains("ANNOTATIVEELEMENT") || code.Contains("ANNOTATIONELEMENT"))
                { result.MaxAnnotativeElementsCount = (int)effective; mappedField = nameof(MaxAnnotativeElementsCount); }
                else if (code.Contains("OTHERELEMENT"))
                { result.MaxOtherElementsCount = (int)effective; mappedField = nameof(MaxOtherElementsCount); }

                logger?.LogInfo($"[HealthMonitor] parameter '{rawCode}' (enabled={p.IsEnabled}, warning={p.WarningThreshold}, breached={p.BreachedThreshold}, effective={effective}) → {mappedField}");
            }

            return result;
        }
    }
}
