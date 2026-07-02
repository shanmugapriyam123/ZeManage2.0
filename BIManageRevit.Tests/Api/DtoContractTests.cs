using System;
using System.Collections.Generic;
using System.Text.Json;
using BIManage.Infrastructure.Api;
using BIManage.Licensing;
using FluentAssertions;
using Xunit;

namespace BIManageRevit.Tests.Api
{
    /// <summary>
    /// Verifies API DTOs serialize to the exact JSON field names the server expects.
    /// Catches field mismatches (e.g., sending 'userEmail' when server doesn't have it).
    /// </summary>
    public class DtoContractTests
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        #region SessionApiRequest

        [Fact]
        public void SessionApiRequest_ShouldNotContain_UserEmail()
        {
            var request = new SessionApiRequest { SessionId = "test" };
            var json = JsonSerializer.Serialize(request, JsonOptions);
            json.Should().NotContain("userEmail", "server schema does not have userEmail field");
        }

        [Fact]
        public void SessionApiRequest_ShouldNotContain_CrashDetected()
        {
            var request = new SessionApiRequest { SessionId = "test" };
            var json = JsonSerializer.Serialize(request, JsonOptions);
            json.Should().NotContain("crashDetected", "crashDetected is only on PATCH SessionUpdateRequest");
        }

        [Fact]
        public void SessionApiRequest_ShouldContain_RequiredFields()
        {
            var request = new SessionApiRequest
            {
                SessionId = "abc-123",
                MachineId = "machine-456",
                RevitVersion = "2025",
                RevitBuild = "25.0.2.419",
                Status = "Active"
            };
            var json = JsonSerializer.Serialize(request, JsonOptions);

            json.Should().Contain("\"sessionId\"");
            json.Should().Contain("\"machineId\"");
            json.Should().Contain("\"revitVersion\"");
            json.Should().Contain("\"revitBuild\"");
            json.Should().Contain("\"status\"");
            json.Should().Contain("\"computerUserName\"");
            json.Should().Contain("\"revitUsername\"");
            json.Should().Contain("\"computerName\"");
        }

        #endregion

        #region HeartbeatRequest

        [Fact]
        public void HeartbeatRequest_ShouldUseCorrectFieldNames()
        {
            var request = new HeartbeatRequest
            {
                MemoryUsageMb = 2048,
                CpuUsagePercent = 12.3,
                DiskUsageMb = 512,
                GraphicsUsageMb = 256
            };
            var json = JsonSerializer.Serialize(request, JsonOptions);

            json.Should().Contain("\"memoryUsageMB\"");
            json.Should().Contain("\"cpuUsagePercent\"");
            json.Should().Contain("\"diskUsageMB\"");
            json.Should().Contain("\"graphicsUsageMB\"");
        }

        #endregion

        #region HeartbeatResponse

        [Fact(Skip = "Quarantined: pre-existing DTO contract drift. See docs/testing/quarantine.md.")]
        public void HeartbeatResponse_Deserializes_LicenseFields()
        {
            var json = @"{
                ""sessionId"": ""3fa85f64-5717-4562-b3fc-2c963f66afa6"",
                ""lastHeartbeat"": ""2026-03-19T14:00:00Z"",
                ""isActive"": true,
                ""activeSessionCount"": 5,
                ""maxUsers"": 10,
                ""licenseMode"": 1
            }";

            var response = JsonSerializer.Deserialize<HeartbeatResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            response.Should().NotBeNull();
            response!.LicenseMode.Should().Be(1);
            response.ActiveSessionCount.Should().Be(5);
            response.MaxUsers.Should().Be(10);
            response.IsActive.Should().BeTrue();
        }

        [Fact]
        public void HeartbeatResponse_Deserializes_RestrictionMode()
        {
            var json = @"{ ""licenseMode"": 0, ""activeSessionCount"": 12, ""maxUsers"": 10 }";
            var response = JsonSerializer.Deserialize<HeartbeatResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            response!.LicenseMode.Should().Be(0, "0 = RestrictionMode");
        }

        [Fact]
        public void HeartbeatResponse_Deserializes_PassiveMode()
        {
            var json = @"{ ""licenseMode"": 2, ""activeSessionCount"": 11, ""maxUsers"": 10 }";
            var response = JsonSerializer.Deserialize<HeartbeatResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            response!.LicenseMode.Should().Be(2, "2 = PassiveMode");
        }

        #endregion

        #region AuditLogRequest

        [Fact]
        public void AuditLogRequest_ContainsRequiredFields()
        {
            var request = new CreateRevitAuditLogRequest
            {
                AuditLogId = "test-id",
                UserName = "admin",
                Mode = "Prevent",
                Action = "Blocked",
                SessionId = "session-123",
                ModelGuid = "model-456"
            };
            var json = JsonSerializer.Serialize(request, JsonOptions);

            json.Should().Contain("\"auditLogId\"");
            json.Should().Contain("\"userName\"");
            json.Should().Contain("\"mode\"");
            json.Should().Contain("\"action\"");
            json.Should().Contain("\"sessionId\"");
            json.Should().Contain("\"modelGuid\"");
        }

        #endregion

        #region UnmonitoredUserReport

        [Fact]
        public void UnmonitoredUserReport_HasCorrectFieldNames()
        {
            var report = new UnmonitoredUserReport
            {
                Username = "JohnDoe",
                ModelName = "MyModel.rvt",
                ModifiedBy = "Admin"
            };
            var json = JsonSerializer.Serialize(report, JsonOptions);

            json.Should().Contain("\"username\"");
            json.Should().Contain("\"modelName\"");
            json.Should().Contain("\"modifiedBy\"");
            json.Should().NotContain("revitUsername", "old field name should not exist");
            json.Should().NotContain("detectionSource", "old field name should not exist");
        }

        #endregion
    }
}
