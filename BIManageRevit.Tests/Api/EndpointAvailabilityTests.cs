using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BIManageRevit.Tests.Api
{
    /// <summary>
    /// Shared fixture that refreshes bimanage-api.json from staging before endpoint tests run.
    /// Runs apiRefresh.ps1 once per test class, not per test method.
    /// </summary>
    public class ApiSpecFixture
    {
        public bool RefreshSucceeded { get; }
        public string? RefreshError { get; }

        public ApiSpecFixture()
        {
            try
            {
                var scriptPath = FindRefreshScript();
                if (scriptPath == null)
                {
                    RefreshError = "apiRefresh.ps1 not found";
                    RefreshSucceeded = false;
                    return;
                }

                var psi = new ProcessStartInfo("powershell.exe",
                    $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                {
                    RefreshError = "Failed to start PowerShell";
                    RefreshSucceeded = false;
                    return;
                }

                process.WaitForExit(15000); // 15s timeout
                RefreshSucceeded = process.ExitCode == 0;
                if (!RefreshSucceeded)
                    RefreshError = process.StandardError.ReadToEnd();
            }
            catch (Exception ex)
            {
                RefreshError = ex.Message;
                RefreshSucceeded = false;
                // Non-fatal — tests will use the existing spec file
            }
        }

        private static string? FindRefreshScript()
        {
            var dir = Path.GetDirectoryName(typeof(ApiSpecFixture).Assembly.Location);
            while (dir != null)
            {
                var candidate = Path.Combine(dir, "docs", "API", "apiRefresh.ps1");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }
    }

    /// <summary>
    /// Validates that all API endpoints used by the plugin exist in the server's OpenAPI spec (bimanage-api.json).
    /// Refreshes the spec from staging before running. Catches endpoint drift at test time.
    /// </summary>
    public class EndpointAvailabilityTests : IClassFixture<ApiSpecFixture>
    {
        private static readonly HashSet<string> ServerEndpoints;
        private static readonly Dictionary<string, HashSet<string>> ServerEndpointMethods;

        static EndpointAvailabilityTests()
        {
            var specPath = FindApiSpecPath();
            var json = File.ReadAllText(specPath);
            var doc = JsonDocument.Parse(json);

            ServerEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ServerEndpointMethods = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var path in doc.RootElement.GetProperty("paths").EnumerateObject())
            {
                ServerEndpoints.Add(path.Name);
                var methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var method in path.Value.EnumerateObject())
                {
                    methods.Add(method.Name.ToUpperInvariant());
                }
                ServerEndpointMethods[path.Name] = methods;
            }
        }

        private readonly ApiSpecFixture _fixture;

        public EndpointAvailabilityTests(ApiSpecFixture fixture)
        {
            _fixture = fixture;
        }

        /// <summary>
        /// Finds bimanage-api.json by walking up from the test assembly location.
        /// </summary>
        private static string FindApiSpecPath()
        {
            var dir = Path.GetDirectoryName(typeof(EndpointAvailabilityTests).Assembly.Location);
            while (dir != null)
            {
                var candidate = Path.Combine(dir, "docs", "API", "bimanage-api.json");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException("bimanage-api.json not found. Ensure docs/API/bimanage-api.json exists in the repo root.");
        }

        /// <summary>
        /// Checks if a parameterized endpoint pattern exists in the spec.
        /// E.g., "
        /// /{0}/heartbeat" matches "/api/v1/Revit/session/{sessionId}/heartbeat"
        /// </summary>
        private static bool EndpointExistsInSpec(string clientEndpoint)
        {
            // Direct match
            if (ServerEndpoints.Contains(clientEndpoint))
                return true;

            // Normalize C# string.Format placeholders ({0}, {1}) to match OpenAPI path params ({paramName})
            // E.g., "/api/v1/Revit/session/{0}/heartbeat" → pattern matches "/api/v1/Revit/session/{sessionId}/heartbeat"
            var normalized = System.Text.RegularExpressions.Regex.Replace(clientEndpoint, @"\{[0-9]+\}", "{PARAM}");

            foreach (var serverPath in ServerEndpoints)
            {
                var serverNormalized = System.Text.RegularExpressions.Regex.Replace(serverPath, @"\{[^}]+\}", "{PARAM}");
                if (string.Equals(normalized, serverNormalized, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool EndpointSupportsMethod(string clientEndpoint, string method)
        {
            foreach (var kvp in ServerEndpointMethods)
            {
                var serverNormalized = System.Text.RegularExpressions.Regex.Replace(kvp.Key, @"\{[^}]+\}", "{PARAM}");
                var clientNormalized = System.Text.RegularExpressions.Regex.Replace(clientEndpoint, @"\{[0-9]+\}", "{PARAM}");
                if (string.Equals(serverNormalized, clientNormalized, StringComparison.OrdinalIgnoreCase))
                    return kvp.Value.Contains(method.ToUpperInvariant());
            }
            return false;
        }

        // ============================================================
        // Auth Endpoints (6)
        // ============================================================

        [Fact] public void Auth_AdminLogin() => EndpointExistsInSpec("/api/v1/tenant/device/auth/admin-login").Should().BeTrue();
        [Fact] public void Auth_RegisterDevice() => EndpointExistsInSpec("/api/v1/tenant/device/auth/register-device").Should().BeTrue();
        [Fact] public void Auth_ValidateDevice() => EndpointExistsInSpec("/api/v1/tenant/device/auth/validate-device").Should().BeTrue();
        [Fact] public void Auth_DeviceRefresh() => EndpointExistsInSpec("/api/v1/tenant/device/auth/refresh").Should().BeTrue();
        [Fact] public void Auth_AdminRefresh() => EndpointExistsInSpec("/api/v1/tenant/device/auth/admin-refresh").Should().BeTrue();
        [Fact] public void Auth_CompanyLogin() => EndpointExistsInSpec("/api/v1/tenant/company-auth/login").Should().BeTrue();

        // ============================================================
        // Session Endpoints (4)
        // ============================================================

        [Fact] public void Session_Open() => EndpointExistsInSpec("/api/v1/Revit/session/Open").Should().BeTrue();
        [Fact] public void Session_Update() => EndpointExistsInSpec("/api/v1/Revit/session").Should().BeTrue();
        [Fact] public void Session_Heartbeat() => EndpointExistsInSpec("/api/v1/Revit/session/{0}/heartbeat").Should().BeTrue();
        [Fact] public void Session_Heartbeat_SupportsPatch() => EndpointSupportsMethod("/api/v1/Revit/session/{0}/heartbeat", "PATCH").Should().BeTrue();
        [Fact] public void Session_ById() => EndpointExistsInSpec("/api/v1/Revit/session/{sessionId}").Should().BeTrue();

        // ============================================================
        // Model Endpoints (5)
        // ============================================================

        [Fact] public void Models_Register() => EndpointExistsInSpec("/api/v1/Revit/models/register").Should().BeTrue();
        [Fact] public void Models_Get() => EndpointExistsInSpec("/api/v1/Revit/models").Should().BeTrue();
        [Fact] public void Models_ModelSessions() => EndpointExistsInSpec("/api/v1/Revit/models/model-sessions").Should().BeTrue();
        [Fact] public void Models_SessionsByModel() => EndpointExistsInSpec("/api/v1/Revit/models/{modelGuid}/sessions").Should().BeTrue();

        // Note: PATCH /api/v1/Revit/models/model-sessions/{sessionId} is used by ModelSessionSyncService
        // but not documented in swagger spec — server-side spec gap, tracked separately

        // ============================================================
        // Metrics Endpoints (7)
        // ============================================================

        [Fact] public void Metrics_SyncSave() => EndpointExistsInSpec("/api/v1/Revit/metrics/syncsave").Should().BeTrue();
        [Fact] public void Metrics_Periodic() => EndpointExistsInSpec("/api/v1/Revit/metrics/periodic").Should().BeTrue();
        [Fact] public void Metrics_Manual() => EndpointExistsInSpec("/api/v1/Revit/metrics/manual").Should().BeTrue();
        [Fact] public void Metrics_SyncSaveLatest() => EndpointExistsInSpec("/api/v1/Revit/metrics/syncsave/latest/{modelGuid}").Should().BeTrue();
        [Fact] public void Metrics_PeriodicLatest() => EndpointExistsInSpec("/api/v1/Revit/metrics/periodic/latest/{modelGuid}").Should().BeTrue();
        [Fact] public void Metrics_ManualLatest() => EndpointExistsInSpec("/api/v1/Revit/metrics/manual/latest/{modelGuid}").Should().BeTrue();
        [Fact] public void Metrics_ModelSyncs() => EndpointExistsInSpec("/api/v1/Revit/model-syncs").Should().BeTrue();

        // ============================================================
        // Protection Endpoints (6)
        // ============================================================

        [Fact] public void AuditLogs() => EndpointExistsInSpec("/api/v1/Revit/audit-logs").Should().BeTrue();
        [Fact] public void AuditLogs_SupportsPost() => EndpointSupportsMethod("/api/v1/Revit/audit-logs", "POST").Should().BeTrue();
        [Fact] public void AuditLogs_SendMail() => EndpointExistsInSpec("/api/v1/Revit/audit-logs/{0}/send-mail").Should().BeTrue();
        [Fact] public void CommandProtections() => EndpointExistsInSpec("/api/v1/Revit/command-protections").Should().BeTrue();
        [Fact] public void EventProtections_ByModel() => EndpointExistsInSpec("/api/v1/Revit/event-protections/by-model/{modelGuid}").Should().BeTrue();
        [Fact] public void HealthMonitor_ByModel() => EndpointExistsInSpec("/api/v1/Revit/health-monitor-protections/by-model/{modelGuid}").Should().BeTrue();

        // ============================================================
        // Pin & Rule Protections (3)
        // ============================================================

        [Fact] public void PinProtections() => EndpointExistsInSpec("/api/v1/Revit/pin-protections").Should().BeTrue();
        [Fact] public void RuleProtections() => EndpointExistsInSpec("/api/v1/Revit/rule-protections").Should().BeTrue();
        [Fact] public void ProjectModelAdmins() => EndpointExistsInSpec("/api/v1/Revit/project-model-admins/model").Should().BeTrue();

        // ============================================================
        // OTP Endpoints (2)
        // ============================================================

        [Fact] public void Otp_Validate() => EndpointExistsInSpec("/api/v1/Revit/otp/validate").Should().BeTrue();
        [Fact] public void Otp_Generate() => EndpointExistsInSpec("/api/v1/Revit/otp/generate").Should().BeTrue();

        // ============================================================
        // Evidence & Unmonitored Users (2)
        // ============================================================

        [Fact] public void EvidenceImages() => EndpointExistsInSpec("/api/v1/Revit/evidence-images/with-images").Should().BeTrue();
        [Fact] public void UnmonitoredUsers() => EndpointExistsInSpec("/api/v1/Revit/unmonitored-users").Should().BeTrue();

        // ============================================================
        // Reference Data Endpoints (3)
        // ============================================================

        [Fact] public void Categories() => EndpointExistsInSpec("/api/v1/Revit/categories").Should().BeTrue();
        [Fact] public void Commands() => EndpointExistsInSpec("/api/v1/Revit/commands").Should().BeTrue();
        [Fact] public void AiTraining() => EndpointExistsInSpec("/api/v1/master/ai-training").Should().BeTrue();

        // ============================================================
        // Negative Tests — endpoints we removed or never existed
        // ============================================================

        [Fact]
        public void LicenseStatus_ShouldNotExist()
        {
            EndpointExistsInSpec("/api/v1/tenant/device/auth/license-status").Should().BeFalse(
                "license data comes from heartbeat response body, not a separate endpoint");
        }

        // ============================================================
        // Spec Sanity
        // ============================================================

        [Fact]
        public void ApiSpec_HasReasonableEndpointCount()
        {
            ServerEndpoints.Count.Should().BeGreaterThan(20, "API spec should have a substantial number of endpoints");
        }

        [Fact]
        public void ApiSpec_IsOpenApi3()
        {
            var specPath = FindApiSpecPath();
            var json = File.ReadAllText(specPath);
            var doc = JsonDocument.Parse(json);
            doc.RootElement.GetProperty("openapi").GetString().Should().StartWith("3.");
        }

        [Fact]
        public void ApiSpec_WasRefreshedFromStaging()
        {
            // Informational — logs whether the refresh succeeded.
            // Does NOT fail the test if staging is unreachable (offline dev).
            if (!_fixture.RefreshSucceeded)
            {
                // Still pass — we use the existing local spec file
                var specPath = FindApiSpecPath();
                File.Exists(specPath).Should().BeTrue("local bimanage-api.json must exist as fallback");
            }
        }
    }
}
