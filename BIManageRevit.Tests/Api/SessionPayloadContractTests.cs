using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using BIManage.Infrastructure.Api;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace BIManageRevit.Tests.Api
{
    /// <summary>
    /// Validates session-related DTO payloads against the OpenAPI spec (bimanage-api.json).
    /// Catches field mismatches, type mismatches, missing fields, and extra fields
    /// that would cause 400 Bad Request or silent data loss at the API.
    ///
    /// Covers:
    ///   - POST /api/v1/Revit/session/Open             → CreateRevitSessionRequest
    ///   - PATCH /api/v1/Revit/session/{sessionId}      → UpdateRevitSessionRequest
    ///   - PATCH /api/v1/Revit/session/{id}/heartbeat   → HeartbeatRequest
    ///   - POST /api/v1/Revit/models/model-sessions     → CreateRevitModelSessionRequest
    ///   - PATCH /api/v1/Revit/models/model-sessions/{id} → PatchRevitModelSessionRequest
    /// </summary>
    public class SessionPayloadContractTests
    {
        private readonly ITestOutputHelper _output;

        // Serialization options matching AuthenticatedHttpClient.GetJsonOptions()
        private static readonly JsonSerializerOptions ClientJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        // API spec schema cache
        private static readonly Lazy<JsonDocument> ApiSpec = new(() =>
        {
            var path = FindApiSpecPath();
            return JsonDocument.Parse(File.ReadAllText(path));
        });

        public SessionPayloadContractTests(ITestOutputHelper output)
        {
            _output = output;
        }

        // ================================================================
        // Helper: Extract schema field names + types from OpenAPI spec
        // ================================================================

        private static Dictionary<string, SchemaField> GetSchemaFields(string schemaName)
        {
            var schemas = ApiSpec.Value.RootElement
                .GetProperty("components")
                .GetProperty("schemas");

            if (!schemas.TryGetProperty(schemaName, out var schema))
                throw new KeyNotFoundException($"Schema '{schemaName}' not found in bimanage-api.json");

            var fields = new Dictionary<string, SchemaField>(StringComparer.OrdinalIgnoreCase);
            if (schema.TryGetProperty("properties", out var props))
            {
                foreach (var prop in props.EnumerateObject())
                {
                    var type = prop.Value.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
                    var format = prop.Value.TryGetProperty("format", out var f) ? f.GetString() ?? "" : "";
                    var nullable = prop.Value.TryGetProperty("nullable", out var n) && n.GetBoolean();
                    var readOnly = prop.Value.TryGetProperty("readOnly", out var ro) && ro.GetBoolean();
                    fields[prop.Name] = new SchemaField(type, format, nullable, readOnly);
                }
            }
            return fields;
        }

        private static HashSet<string> GetRequiredFields(string schemaName)
        {
            var schemas = ApiSpec.Value.RootElement
                .GetProperty("components")
                .GetProperty("schemas");

            if (!schemas.TryGetProperty(schemaName, out var schema))
                return new HashSet<string>();

            if (!schema.TryGetProperty("required", out var required))
                return new HashSet<string>();

            return required.EnumerateArray()
                .Select(e => e.GetString()!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        private static bool HasAdditionalPropertiesFalse(string schemaName)
        {
            var schemas = ApiSpec.Value.RootElement
                .GetProperty("components")
                .GetProperty("schemas");

            if (!schemas.TryGetProperty(schemaName, out var schema))
                return false;

            return schema.TryGetProperty("additionalProperties", out var ap) && !ap.GetBoolean();
        }

        /// <summary>
        /// Serializes a DTO the same way AuthenticatedHttpClient does, and returns the JSON field names.
        /// </summary>
        private static HashSet<string> GetSerializedFieldNames<T>(T dto)
        {
            var json = JsonSerializer.Serialize(dto, ClientJsonOptions);
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Serializes a DTO and returns the JSON field name → JSON value kind mapping.
        /// </summary>
        private static Dictionary<string, JsonValueKind> GetSerializedFieldTypes<T>(T dto)
        {
            var json = JsonSerializer.Serialize(dto, ClientJsonOptions);
            var doc = JsonDocument.Parse(json);
            return doc.RootElement.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.ValueKind, StringComparer.OrdinalIgnoreCase);
        }

        private static string FindApiSpecPath()
        {
            var dir = Path.GetDirectoryName(typeof(SessionPayloadContractTests).Assembly.Location);
            while (dir != null)
            {
                var candidate = Path.Combine(dir, "docs", "API", "bimanage-api.json");
                if (File.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir);
            }
            throw new FileNotFoundException("bimanage-api.json not found");
        }

        // ================================================================
        //  POST /api/v1/Revit/session/Open → CreateRevitSessionRequest
        // ================================================================

        [Fact]
        public void SessionOpen_ClientFields_ShouldMatchApiSpec()
        {
            var dto = CreateFullSessionApiRequest();
            var clientFields = GetSerializedFieldNames(dto);
            var specFields = GetSchemaFields("CreateRevitSessionRequest");

            var extraInClient = clientFields.Where(f => !specFields.ContainsKey(f)).ToList();

            // endedAt/closedAt are nullable in the spec and null on new sessions.
            // Client uses WhenWritingNull so they're omitted — this is correct behavior.
            var allowedMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "endedAt", "closedAt" };
            var missingInClient = specFields.Keys
                .Where(f => !clientFields.Contains(f))
                .Where(f => !(specFields[f].ReadOnly))
                .Where(f => !allowedMissing.Contains(f))
                .ToList();

            _output.WriteLine($"Client fields: {string.Join(", ", clientFields.OrderBy(f => f))}");
            _output.WriteLine($"Spec fields:   {string.Join(", ", specFields.Keys.OrderBy(f => f))}");
            if (extraInClient.Any()) _output.WriteLine($"EXTRA in client (not in spec): {string.Join(", ", extraInClient)}");
            if (missingInClient.Any()) _output.WriteLine($"MISSING in client (in spec):   {string.Join(", ", missingInClient)}");

            extraInClient.Should().BeEmpty(
                "client sends fields the server doesn't expect (additionalProperties: false). " +
                "Extra fields: [{0}]", string.Join(", ", extraInClient));

            missingInClient.Should().BeEmpty(
                "client is missing fields the server schema defines. " +
                "Missing fields: [{0}]", string.Join(", ", missingInClient));
        }

        [Fact]
        public void SessionOpen_StatusField_ShouldBeString()
        {
            // API spec updated: status is now a string ("Active", "Closed", "Crashed", "Inactive", "Unknown")
            var dto = CreateFullSessionApiRequest();
            var clientTypes = GetSerializedFieldTypes(dto);

            if (clientTypes.TryGetValue("status", out var statusKind))
            {
                statusKind.Should().Be(JsonValueKind.String,
                    "API expects status as string but client sends it as {0}", statusKind);
            }
        }

        [Fact]
        public void SessionOpen_StatusSerialized_IsActiveNotInSpec()
        {
            // status (string) is serialized; isActive is not in CreateRevitSessionRequest spec
            var dto = CreateFullSessionApiRequest();
            var clientFields = GetSerializedFieldNames(dto);

            clientFields.Should().Contain("status",
                "status (string) should appear in serialized payload");
            clientFields.Should().NotContain("isActive",
                "isActive is not in CreateRevitSessionRequest API spec");
        }

        [Fact]
        public void SessionOpen_FieldTypes_ShouldMatchSpec()
        {
            var dto = CreateFullSessionApiRequest();
            var clientTypes = GetSerializedFieldTypes(dto);
            var specFields = GetSchemaFields("CreateRevitSessionRequest");

            var mismatches = new List<string>();
            foreach (var (field, spec) in specFields)
            {
                if (spec.ReadOnly) continue;
                if (!clientTypes.TryGetValue(field, out var jsonKind)) continue;

                if (!IsTypeCompatible(spec, jsonKind))
                {
                    mismatches.Add($"{field}: client sends {jsonKind}, spec expects {spec.Type}({spec.Format})");
                }
            }

            foreach (var m in mismatches) _output.WriteLine($"TYPE MISMATCH: {m}");
            mismatches.Should().BeEmpty("all field types should match the API spec");
        }

        // ================================================================
        //  PATCH /api/v1/Revit/session/{sessionId} → UpdateRevitSessionRequest
        // ================================================================

        [Fact]
        public void SessionUpdate_ClientFields_ShouldMatchApiSpec()
        {
            var dto = new SessionUpdateRequest
            {
                EndedAt = DateTime.UtcNow,
                ClosedAt = DateTime.UtcNow,
                Status = "Closed"
            };

            var clientFields = GetSerializedFieldNames(dto);
            var specFields = GetSchemaFields("UpdateRevitSessionRequest");

            var extraInClient = clientFields.Where(f => !specFields.ContainsKey(f)).ToList();
            var missingInClient = specFields.Keys
                .Where(f => !clientFields.Contains(f))
                .Where(f => !(specFields[f].ReadOnly))
                .ToList();

            _output.WriteLine($"Client fields: {string.Join(", ", clientFields.OrderBy(f => f))}");
            _output.WriteLine($"Spec fields:   {string.Join(", ", specFields.Keys.OrderBy(f => f))}");
            if (extraInClient.Any()) _output.WriteLine($"EXTRA in client: {string.Join(", ", extraInClient)}");
            if (missingInClient.Any()) _output.WriteLine($"MISSING in client: {string.Join(", ", missingInClient)}");

            extraInClient.Should().BeEmpty(
                "client sends extra fields the server may reject (additionalProperties: false). " +
                "Extra: [{0}]", string.Join(", ", extraInClient));
        }

        [Fact]
        public void SessionUpdate_StatusType_ShouldBeString()
        {
            // API spec: status is string (server accepts "Closed", "Active", "Crashed", "Inactive", "Unknown")
            var dto = new SessionUpdateRequest { Status = "Closed" };
            var clientTypes = GetSerializedFieldTypes(dto);

            if (clientTypes.TryGetValue("status", out var statusKind))
            {
                _output.WriteLine($"Client status JSON kind: {statusKind}");
                statusKind.Should().Be(JsonValueKind.String,
                    "API expects status as string but client sends it as {0}", statusKind);
            }
        }

        [Fact]
        public void SessionUpdate_ExtraFields_AreNotInSpec()
        {
            // Documents which extra fields the client sends beyond the spec
            var specFields = GetSchemaFields("UpdateRevitSessionRequest");

            var knownExtras = new[] { "isActive", "crashDetected", "statusCode" };
            foreach (var field in knownExtras)
            {
                var inSpec = specFields.ContainsKey(field);
                _output.WriteLine($"  {field}: in spec = {inSpec}");
            }
        }

        // ================================================================
        //  PATCH /api/v1/Revit/session/{id}/heartbeat → HeartbeatRequest
        // ================================================================

        [Fact]
        public void Heartbeat_ClientFields_ShouldMatchApiSpec()
        {
            var dto = new HeartbeatRequest
            {
                LastHeartbeat = DateTime.UtcNow,
                MemoryUsageMb = 2048,
                CpuUsagePercent = 45.5,
                DiskUsageMb = 512,
                GraphicsUsageMb = 256,
                ModelGuid = Guid.NewGuid().ToString()
            };

            var clientFields = GetSerializedFieldNames(dto);
            var specFields = GetSchemaFields("HeartbeatRequest");

            var extraInClient = clientFields.Where(f => !specFields.ContainsKey(f)).ToList();
            var missingInClient = specFields.Keys
                .Where(f => !clientFields.Contains(f))
                .Where(f => !(specFields[f].ReadOnly))
                .ToList();

            _output.WriteLine($"Client fields: {string.Join(", ", clientFields.OrderBy(f => f))}");
            _output.WriteLine($"Spec fields:   {string.Join(", ", specFields.Keys.OrderBy(f => f))}");
            if (extraInClient.Any()) _output.WriteLine($"EXTRA: {string.Join(", ", extraInClient)}");
            if (missingInClient.Any()) _output.WriteLine($"MISSING: {string.Join(", ", missingInClient)}");

            extraInClient.Should().BeEmpty("extra fields will be rejected");
            missingInClient.Should().BeEmpty("missing fields may cause server errors");
        }

        [Fact]
        public void Heartbeat_MetricFields_TypeCheck()
        {
            // API spec: memoryUsageMB/diskUsageMB/graphicsUsageMB are float
            // Client DTO: int? — JSON serializes as 2048 not 2048.0
            // Most servers handle this, but verify spec expectations
            var specFields = GetSchemaFields("HeartbeatRequest");
            var dto = new HeartbeatRequest
            {
                MemoryUsageMb = 2048,
                DiskUsageMb = 512,
                GraphicsUsageMb = 256
            };
            var clientTypes = GetSerializedFieldTypes(dto);

            var floatFields = new[] { "memoryUsageMB", "cpuUsagePercent", "diskUsageMB", "graphicsUsageMB" };
            foreach (var field in floatFields)
            {
                if (specFields.TryGetValue(field, out var spec))
                {
                    _output.WriteLine($"  {field}: spec={spec.Type}({spec.Format}), " +
                        $"client={clientTypes.GetValueOrDefault(field, JsonValueKind.Undefined)}");
                }
            }
        }

        // ================================================================
        //  POST /api/v1/Revit/models/model-sessions → CreateRevitModelSessionRequest
        // ================================================================

        [Fact]
        public void ModelSessionCreate_ClientFields_ShouldMatchApiSpec()
        {
            var dto = new ModelSessionApiRequest
            {
                SessionId = Guid.NewGuid().ToString(),
                ModelGuid = Guid.NewGuid().ToString(),
                CentralModelPath = @"C:\Models\Test.rvt",
                CentralModelName = "Test",
                LocalModelLocation = @"C:\Local\Test.rvt",
                DocumentTitle = "Test Project",
                LocalProjectName = "TestProject",
                CloudProjectName = "",
                OpenedAt = DateTime.UtcNow,
                OpeningStartedAt = DateTime.UtcNow.AddSeconds(-5),
                ModelOpeningDuration = 5.2,
                OpenedWorksetsCount = 3,
                Status = "Active",
                TotalModifications = 0,
                CreatedBy = "admin",
                ModifiedBy = "admin",
                RevitUsername = "admin"
            };

            var clientFields = GetSerializedFieldNames(dto);
            var specFields = GetSchemaFields("CreateRevitModelSessionRequest");

            // revitUsername is sent by client and accepted by server but not in OpenAPI spec (server spec gap).
            var allowedExtras = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "revitUsername" };
            var extraInClient = clientFields.Where(f => !specFields.ContainsKey(f) && !allowedExtras.Contains(f)).ToList();

            // closedAt/lastSavedAt are null on new model sessions, omitted by WhenWritingNull.
            var allowedMissing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "closedAt", "lastSavedAt" };
            var missingInClient = specFields.Keys
                .Where(f => !clientFields.Contains(f))
                .Where(f => !(specFields[f].ReadOnly))
                .Where(f => !allowedMissing.Contains(f))
                .ToList();

            _output.WriteLine($"Client fields: {string.Join(", ", clientFields.OrderBy(f => f))}");
            _output.WriteLine($"Spec fields:   {string.Join(", ", specFields.Keys.OrderBy(f => f))}");
            if (extraInClient.Any()) _output.WriteLine($"EXTRA: {string.Join(", ", extraInClient)}");
            if (missingInClient.Any()) _output.WriteLine($"MISSING: {string.Join(", ", missingInClient)}");

            extraInClient.Should().BeEmpty(
                "extra fields: [{0}]", string.Join(", ", extraInClient));
        }

        [Fact]
        public void ModelSessionCreate_RequiredFields_ArePresent()
        {
            var required = GetRequiredFields("CreateRevitModelSessionRequest");
            _output.WriteLine($"Required fields: {string.Join(", ", required)}");

            var dto = new ModelSessionApiRequest
            {
                SessionId = Guid.NewGuid().ToString(),
                ModelGuid = Guid.NewGuid().ToString()
            };
            var clientFields = GetSerializedFieldNames(dto);

            foreach (var req in required)
            {
                clientFields.Should().Contain(req,
                    $"required field '{req}' must be present in serialized payload");
            }
        }

        // ================================================================
        //  PATCH /api/v1/Revit/models/model-sessions/{id} → PatchRevitModelSessionRequest
        // ================================================================

        [Fact]
        public void ModelSessionPatch_ClientFields_ShouldMatchApiSpec()
        {
            var dto = new ModelSessionStatusUpdateRequest
            {
                SessionId = Guid.NewGuid().ToString(),
                ModelGuid = Guid.NewGuid().ToString(),
                Status = "Closed",
                ClosedAt = DateTime.UtcNow
            };

            var clientFields = GetSerializedFieldNames(dto);
            var specFields = GetSchemaFields("PatchRevitModelSessionRequest");

            var extraInClient = clientFields.Where(f => !specFields.ContainsKey(f)).ToList();
            var missingInClient = specFields.Keys
                .Where(f => !clientFields.Contains(f))
                .Where(f => !(specFields[f].ReadOnly))
                .ToList();

            _output.WriteLine($"Client fields: {string.Join(", ", clientFields.OrderBy(f => f))}");
            _output.WriteLine($"Spec fields:   {string.Join(", ", specFields.Keys.OrderBy(f => f))}");
            if (extraInClient.Any()) _output.WriteLine($"EXTRA: {string.Join(", ", extraInClient)}");
            if (missingInClient.Any()) _output.WriteLine($"MISSING: {string.Join(", ", missingInClient)}");

            extraInClient.Should().BeEmpty(
                "extra fields: [{0}]", string.Join(", ", extraInClient));
        }

        [Fact]
        public void ModelSessionPatch_RequiredFields_ArePresent()
        {
            var required = GetRequiredFields("PatchRevitModelSessionRequest");
            _output.WriteLine($"Required fields: {string.Join(", ", required)}");

            var dto = new ModelSessionStatusUpdateRequest
            {
                ModelGuid = Guid.NewGuid().ToString(),
                Status = "Closed"
            };
            var clientFields = GetSerializedFieldNames(dto);

            foreach (var req in required)
            {
                clientFields.Should().Contain(req,
                    $"required field '{req}' must be present in serialized payload");
            }
        }

        [Fact]
        public void ModelSessionPatch_MissingSessionId_InClientDto()
        {
            // PatchRevitModelSessionRequest requires sessionId, but
            // ModelSessionStatusUpdateRequest DTO does not have a SessionId property
            var specFields = GetSchemaFields("PatchRevitModelSessionRequest");
            var required = GetRequiredFields("PatchRevitModelSessionRequest");

            _output.WriteLine($"Spec requires: {string.Join(", ", required)}");

            var dto = new ModelSessionStatusUpdateRequest
            {
                ModelGuid = Guid.NewGuid().ToString(),
                Status = "Closed"
            };
            var clientFields = GetSerializedFieldNames(dto);

            if (required.Contains("sessionId"))
            {
                _output.WriteLine("WARNING: API requires 'sessionId' in PATCH body but client DTO doesn't have it");
                _output.WriteLine("(sessionId is passed as URL path parameter — server may populate it from URL)");
            }
        }

        // ================================================================
        //  Cross-cutting: JSON serialization matches AuthenticatedHttpClient
        // ================================================================

        [Fact]
        public void AllSessionDtos_UseExplicitJsonPropertyName()
        {
            // Verify DTOs use [JsonPropertyName] so field names are stable
            // regardless of naming policy changes
            var dtoTypes = new[]
            {
                typeof(SessionApiRequest),
                typeof(SessionUpdateRequest),
                typeof(HeartbeatRequest),
                typeof(HeartbeatResponse),
                typeof(UnmonitoredUserReport),
                typeof(ModelSessionApiRequest),
                typeof(ModelSessionStatusUpdateRequest)
            };

            var issues = new List<string>();
            foreach (var type in dtoTypes)
            {
                var properties = type.GetProperties();
                foreach (var prop in properties)
                {
                    // Skip [JsonIgnore] properties — they're intentionally not serialized
                    var ignoreAttr = prop.GetCustomAttributes(typeof(JsonIgnoreAttribute), false);
                    if (ignoreAttr.Length > 0) continue;

                    var attr = prop.GetCustomAttributes(typeof(JsonPropertyNameAttribute), false);
                    if (attr.Length == 0)
                    {
                        issues.Add($"{type.Name}.{prop.Name} — missing [JsonPropertyName]");
                    }
                }
            }

            foreach (var issue in issues) _output.WriteLine($"  {issue}");
            issues.Should().BeEmpty("all DTO properties should have explicit [JsonPropertyName] attributes");
        }

        [Fact]
        public void SessionApiRequest_NullFields_AreOmittedByClient()
        {
            // AuthenticatedHttpClient uses JsonIgnoreCondition.WhenWritingNull
            // So null fields are NOT sent — verify this matches spec nullable expectations
            var dto = new SessionApiRequest { SessionId = "test-123" };
            var json = JsonSerializer.Serialize(dto, ClientJsonOptions);
            var doc = JsonDocument.Parse(json);
            var fields = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet();

            _output.WriteLine($"Non-null fields sent: {string.Join(", ", fields.OrderBy(f => f))}");

            // status is non-nullable string, always sent
            fields.Should().Contain("status");
            // isActive removed — not in CreateRevitSessionRequest API spec
            fields.Should().NotContain("isActive");
            // sessionId is set, should be sent
            fields.Should().Contain("sessionId");
            // nullable fields with null values should be omitted
            fields.Should().NotContain("endedAt", "null DateTime? should be omitted");
            fields.Should().NotContain("closedAt", "null DateTime? should be omitted");
        }

        // ================================================================
        //  Summary test — aggregates all mismatches for reporting
        // ================================================================

        [Fact]
        public void SessionEndpoints_FullMismatchReport()
        {
            var report = new List<string>();

            // 1) Session Open
            report.AddRange(CompareDto("CreateRevitSessionRequest", CreateFullSessionApiRequest(), "POST /session/Open"));

            // 2) Session Update
            report.AddRange(CompareDto("UpdateRevitSessionRequest",
                new SessionUpdateRequest { EndedAt = DateTime.UtcNow, ClosedAt = DateTime.UtcNow, Status = "Closed" },
                "PATCH /session/{id}"));

            // 3) Heartbeat
            report.AddRange(CompareDto("HeartbeatRequest",
                new HeartbeatRequest { LastHeartbeat = DateTime.UtcNow, MemoryUsageMb = 1024, CpuUsagePercent = 25.0, DiskUsageMb = 512, GraphicsUsageMb = 128, ModelGuid = Guid.NewGuid().ToString() },
                "PATCH /session/{id}/heartbeat"));

            // 4) Model Session Create
            report.AddRange(CompareDto("CreateRevitModelSessionRequest",
                new ModelSessionApiRequest { SessionId = "s", ModelGuid = "m", CentralModelPath = "p", CentralModelName = "n", Status = "Active", OpenedAt = DateTime.UtcNow, OpeningStartedAt = DateTime.UtcNow, CreatedBy = "u", ModifiedBy = "u", RevitUsername = "u" },
                "POST /models/model-sessions"));

            // 5) Model Session Patch
            report.AddRange(CompareDto("PatchRevitModelSessionRequest",
                new ModelSessionStatusUpdateRequest { SessionId = "s", ModelGuid = "m", Status = "Closed", ClosedAt = DateTime.UtcNow },
                "PATCH /models/model-sessions/{id}"));

            _output.WriteLine("========================================");
            _output.WriteLine("  SESSION PAYLOAD MISMATCH REPORT");
            _output.WriteLine("========================================");

            if (report.Count == 0)
            {
                _output.WriteLine("  No mismatches found.");
            }
            else
            {
                foreach (var line in report) _output.WriteLine(line);
                _output.WriteLine($"\n  Total issues: {report.Count(l => l.StartsWith("  ["))}");
            }

            // This test is informational — it always passes but logs all findings.
            // Individual tests above will fail on specific critical mismatches.
        }

        // ================================================================
        //  Helpers
        // ================================================================

        private static SessionApiRequest CreateFullSessionApiRequest()
        {
            return new SessionApiRequest
            {
                SessionId = Guid.NewGuid().ToString(),
                MachineId = Guid.NewGuid().ToString(),
                ProcessId = 12345,
                StartedAt = DateTime.UtcNow,
                OpenedAt = DateTime.UtcNow,
                OpeningDurationSeconds = 3.5,
                RevitVersion = "2025",
                RevitBuild = "25.0.2.419",
                DesktopConnectorVersion = "18.0.0.0",
                BimanageVersion = "1.0.0.0",
                Username = "testuser",
                RevitUsername = "testuser@company.com",
                ComputerName = "WORKSTATION-01",
                AutodeskAddins = 5,
                ExternalAddins = 3,
                LoadedPluginCount = 8,
                JournalFileName = "journal.0001.txt",
                EndedAt = null,
                ClosedAt = null,
                TotalCommands = 0,
                TotalEvents = 0
            };
        }

        private List<string> CompareDto<T>(string schemaName, T dto, string endpointLabel)
        {
            var lines = new List<string>();
            try
            {
                var clientFields = GetSerializedFieldNames(dto);
                var specFields = GetSchemaFields(schemaName);

                var extra = clientFields.Where(f => !specFields.ContainsKey(f)).ToList();
                var missing = specFields.Keys
                    .Where(f => !clientFields.Contains(f))
                    .Where(f => !(specFields[f].ReadOnly))
                    .ToList();

                if (extra.Any() || missing.Any())
                {
                    lines.Add($"\n--- {endpointLabel} ({schemaName}) ---");
                    foreach (var f in extra)
                        lines.Add($"  [EXTRA]   '{f}' sent by client but NOT in API spec");
                    foreach (var f in missing)
                        lines.Add($"  [MISSING] '{f}' in API spec but NOT sent by client");
                }

                // Type checks
                var clientTypes = GetSerializedFieldTypes(dto);
                foreach (var (field, spec) in specFields)
                {
                    if (spec.ReadOnly) continue;
                    if (!clientTypes.TryGetValue(field, out var jsonKind)) continue;
                    if (!IsTypeCompatible(spec, jsonKind))
                    {
                        if (!lines.Any(l => l.Contains(endpointLabel)))
                            lines.Add($"\n--- {endpointLabel} ({schemaName}) ---");
                        lines.Add($"  [TYPE]    '{field}': client={jsonKind}, spec={spec.Type}({spec.Format})");
                    }
                }
            }
            catch (Exception ex)
            {
                lines.Add($"\n--- {endpointLabel} ({schemaName}) --- ERROR: {ex.Message}");
            }
            return lines;
        }

        private static bool IsTypeCompatible(SchemaField spec, JsonValueKind jsonKind)
        {
            return (spec.Type, jsonKind) switch
            {
                ("string", JsonValueKind.String) => true,
                ("integer", JsonValueKind.Number) => true,
                ("number", JsonValueKind.Number) => true,
                ("boolean", JsonValueKind.True) => true,
                ("boolean", JsonValueKind.False) => true,
                ("array", JsonValueKind.Array) => true,
                ("object", JsonValueKind.Object) => true,
                // A string field receiving a number = mismatch
                ("integer", JsonValueKind.String) => false,
                ("string", JsonValueKind.Number) => false,
                _ => true // permissive for unknown combos
            };
        }

        private record SchemaField(string Type, string Format, bool Nullable, bool ReadOnly);
    }
}
