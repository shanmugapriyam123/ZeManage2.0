using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using BIManage.Core.Protection.Models;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// API-based OTP (One-Time Password) service.
    /// All OTP operations (generate, validate, consume) are handled by the backend API.
    /// No local database storage — the server is the single source of truth.
    /// </summary>
    public class OtpRepository
    {
        private AuthenticatedHttpClient? _httpClient;
        private readonly ILogger _logger;

        private const string ValidateEndpoint = "/api/v1/Revit/otp/validate";

        public OtpRepository(ILogger logger)
        {
            _logger = logger;
            _logger?.LogInfo("OtpRepository initialized (API-only mode — no local DB)");
        }

        /// <summary>
        /// Inject the authenticated HTTP client after API services are registered.
        /// Called from RegisterApiServices in RevitBootstrapper.
        /// </summary>
        public void SetHttpClient(AuthenticatedHttpClient httpClient)
        {
            _httpClient = httpClient;
            _logger?.LogInfo("OtpRepository: HTTP client injected for API-based OTP validation");
        }

        /// <summary>
        /// Validate and consume an OTP code via API.
        /// Returns the OTP if valid, null otherwise.
        /// POST /api/v1/Revit/otp/validate
        /// </summary>
        public async Task<OtpCode> ValidateAndConsumeOtpAsync(string code, string userName, string ruleId = null, string commandId = null)
        {
            try
            {
                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("OTP validation skipped — not authenticated (register device first)");
                    return null;
                }

                var request = new
                {
                    code = code,
                    userName = userName,
                    ruleId = ruleId,
                    commandId = commandId
                };

                _logger?.LogInfo($"Validating OTP via API: code={code}, user={userName}, command={commandId}");

                var response = await _httpClient.PostAsync(ValidateEndpoint, request);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    _logger?.LogInfo($"OTP validated successfully via API: {code}");

                    // Parse response to extract OTP details
                    try
                    {
                        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                        // Unwrap "data" wrapper if present
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        var data = root;
                        if (root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Object)
                            data = dataElement;

                        var otpResponse = JsonSerializer.Deserialize<OtpApiResponse>(data.GetRawText(), jsonOptions);

                        if (otpResponse != null)
                        {
                            return new OtpCode
                            {
                                OtpId = otpResponse.OtpId ?? Guid.NewGuid().ToString("N").Substring(0, 8),
                                Code = code,
                                GeneratedAt = otpResponse.GeneratedAt ?? DateTime.UtcNow,
                                ExpiresAt = otpResponse.ExpiresAt ?? DateTime.UtcNow.AddHours(24),
                                GeneratedBy = otpResponse.GeneratedBy ?? userName,
                                Reason = otpResponse.Reason ?? "",
                                IsUsed = true,
                                UsedAt = DateTime.UtcNow,
                                UsedBy = userName,
                                ProjectId = otpResponse.ProjectId,
                                Scope = OverrideScope.Global,
                                MaxUses = 1,
                                UseCount = 1
                            };
                        }
                    }
                    catch (Exception parseEx)
                    {
                        _logger?.LogWarning($"Could not parse OTP response, returning basic OTP: {parseEx.Message}");
                    }

                    // Fallback: return basic valid OTP
                    return new OtpCode
                    {
                        OtpId = Guid.NewGuid().ToString("N").Substring(0, 8),
                        Code = code,
                        GeneratedAt = DateTime.UtcNow,
                        ExpiresAt = DateTime.UtcNow.AddHours(24),
                        GeneratedBy = userName,
                        Reason = "",
                        IsUsed = true,
                        UsedAt = DateTime.UtcNow,
                        UsedBy = userName,
                        Scope = OverrideScope.Global,
                        MaxUses = 1,
                        UseCount = 1
                    };
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"OTP validation failed via API: {response.StatusCode} - {responseBody}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"OTP API validation error: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Validate and consume an OTP code for a specific project via API.
        /// </summary>
        public async Task<OtpCode> ValidateAndConsumeOtpForProjectAsync(string code, string userName, string projectId)
        {
            try
            {
                if (_httpClient == null || !_httpClient.IsAuthenticated)
                {
                    _logger?.LogWarning("OTP validation skipped — not authenticated");
                    return null;
                }

                var request = new
                {
                    code = code,
                    userName = userName,
                    projectId = projectId
                };

                _logger?.LogInfo($"Validating OTP via API for project {projectId}: code={code}, user={userName}");

                var response = await _httpClient.PostAsync(ValidateEndpoint, request);

                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo($"OTP validated for project {projectId}: {code}");

                    return new OtpCode
                    {
                        OtpId = Guid.NewGuid().ToString("N").Substring(0, 8),
                        Code = code,
                        GeneratedAt = DateTime.UtcNow,
                        ExpiresAt = DateTime.UtcNow.AddHours(24),
                        GeneratedBy = userName,
                        Reason = "",
                        IsUsed = true,
                        UsedAt = DateTime.UtcNow,
                        UsedBy = userName,
                        ProjectId = projectId,
                        Scope = OverrideScope.Global,
                        MaxUses = 1,
                        UseCount = 1
                    };
                }
                else
                {
                    var responseBody = await response.Content.ReadAsStringAsync();
                    _logger?.LogWarning($"OTP validation failed for project {projectId}: {response.StatusCode} - {responseBody}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"OTP API validation error for project: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// No-op: OTP codes are saved on the server via POST /api/v1/Revit/otp/generate.
        /// Local save is no longer needed.
        /// </summary>
        public Task<bool> SaveOtpAsync(OtpCode otp)
        {
            _logger?.LogDebug("SaveOtpAsync: no-op (OTP managed by API)");
            return Task.FromResult(true);
        }

        /// <summary>
        /// No-op: active OTPs are managed by the server.
        /// </summary>
        public Task<List<OtpCode>> GetActiveOtpsAsync()
        {
            _logger?.LogDebug("GetActiveOtpsAsync: no-op (OTP managed by API)");
            return Task.FromResult(new List<OtpCode>());
        }

        /// <summary>
        /// No-op: OTP by ID is managed by the server.
        /// </summary>
        public Task<OtpCode> GetOtpByIdAsync(string otpId)
        {
            _logger?.LogDebug("GetOtpByIdAsync: no-op (OTP managed by API)");
            return Task.FromResult<OtpCode>(null);
        }

        /// <summary>
        /// No-op: cleanup is managed by the server.
        /// </summary>
        public Task<int> CleanupOldOtpsAsync(TimeSpan retentionPeriod)
        {
            _logger?.LogDebug("CleanupOldOtpsAsync: no-op (OTP managed by API)");
            return Task.FromResult(0);
        }

        /// <summary>
        /// No-op: statistics are managed by the server.
        /// </summary>
        public Task<OtpStatistics> GetStatisticsAsync()
        {
            _logger?.LogDebug("GetStatisticsAsync: no-op (OTP managed by API)");
            return Task.FromResult(new OtpStatistics());
        }
    }

    /// <summary>
    /// API response DTO for OTP validation.
    /// </summary>
    public class OtpApiResponse
    {
        public string OtpId { get; set; }
        public string Code { get; set; }
        public DateTime? GeneratedAt { get; set; }
        public DateTime? ExpiresAt { get; set; }
        public string GeneratedBy { get; set; }
        public string Reason { get; set; }
        public string ProjectId { get; set; }
    }

    /// <summary>
    /// Statistics for OTP usage and lifecycle
    /// </summary>
    public class OtpStatistics
    {
        public int TotalGenerated { get; set; }
        public int ActiveCount { get; set; }
        public int UsedCount { get; set; }
        public int ExpiredCount { get; set; }

        public double UsageRate => TotalGenerated > 0
            ? (double)UsedCount / TotalGenerated * 100
            : 0;

        public override string ToString()
        {
            return $"OTP Stats - Total: {TotalGenerated} | Active: {ActiveCount} | Used: {UsedCount} | Expired: {ExpiredCount} | Usage Rate: {UsageRate:F1}%";
        }
    }
}
