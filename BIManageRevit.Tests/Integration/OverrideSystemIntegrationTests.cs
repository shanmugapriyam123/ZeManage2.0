using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using BIManage.Core.Protection;
using BIManage.Core.Protection.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManageRevit.Tests.Helpers;

namespace BIManageRevit.Tests.Integration
{
    /// <summary>
    /// Integration tests for structured override system
    /// Tests scope-based, time-bound overrides with metadata and audit trail
    /// </summary>
    public class OverrideSystemIntegrationTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly ILogger _logger;
        private readonly OverrideRepository _overrideRepository;
        private readonly PasswordManager _passwordManager;

        public OverrideSystemIntegrationTests()
        {
            _logger = new ConsoleLogger();
            _testDbPath = TestDatabaseHelper.CreateTestDatabase(_logger);
            _overrideRepository = new OverrideRepository(_testDbPath, _logger);
            _passwordManager = new PasswordManager(_testDbPath, _logger);
        }

        #region Global Override Tests

        [Fact]
        public async Task GlobalOverride_ShouldApplyToAllRulesAndCommands()
        {
            // Arrange
            var globalOverride = ProtectionOverride.Create(
                ruleId: null,
                commandId: null,
                scope: OverrideScope.Global,
                approverEmail: "admin@example.com",
                approverName: "Admin User",
                reason: "Emergency maintenance window",
                duration: TimeSpan.FromHours(2)
            );

            // Act
            var saved = await _overrideRepository.SaveOverrideAsync(globalOverride);
            saved.Should().BeTrue();

            // Assert - Should apply to any context
            var applicable = await _overrideRepository.GetApplicableOverridesAsync(
                ruleId: "RULE-001",
                commandId: "1234",
                documentId: "doc-123"
            );

            applicable.Should().HaveCount(1);
            applicable.First().Scope.Should().Be(OverrideScope.Global);
            applicable.First().IsValid().Should().BeTrue();
        }

        [Fact]
        public async Task GlobalOverride_WhenExpired_ShouldNotApply()
        {
            // Arrange
            var expiredOverride = ProtectionOverride.Create(
                ruleId: null,
                commandId: null,
                scope: OverrideScope.Global,
                approverEmail: "admin@example.com",
                approverName: "Admin User",
                reason: "Test expired override",
                duration: TimeSpan.FromSeconds(-10) // Already expired
            );

            // Act
            await _overrideRepository.SaveOverrideAsync(expiredOverride);

            var activeOverrides = await _overrideRepository.GetActiveOverridesAsync();

            // Assert - Expired override should not be in active list
            activeOverrides.Should().BeEmpty("Expired overrides should be filtered out");
        }

        #endregion

        #region Rule-Scoped Override Tests

        [Fact]
        public async Task RuleScopedOverride_ShouldApplyOnlyToSpecificRule()
        {
            // Arrange
            var ruleOverride = ProtectionOverride.Create(
                ruleId: "RULE-PREVENT-001",
                commandId: null,
                scope: OverrideScope.Rule,
                approverEmail: "pm@example.com",
                approverName: "Project Manager",
                reason: "Approved demolition work",
                duration: TimeSpan.FromDays(1)
            );

            // Act
            await _overrideRepository.SaveOverrideAsync(ruleOverride);

            // Assert - Should apply to matching rule
            var applicable1 = await _overrideRepository.GetApplicableOverridesAsync(
                ruleId: "RULE-PREVENT-001",
                commandId: "any-command",
                documentId: "any-doc"
            );
            applicable1.Should().HaveCount(1);

            // Assert - Should NOT apply to different rule
            var applicable2 = await _overrideRepository.GetApplicableOverridesAsync(
                ruleId: "RULE-PREVENT-002",
                commandId: "any-command",
                documentId: "any-doc"
            );
            applicable2.Should().BeEmpty();
        }

        #endregion

        #region Command-Scoped Override Tests

        [Fact]
        public async Task CommandScopedOverride_ShouldApplyOnlyToSpecificCommand()
        {
            // Arrange
            var commandOverride = ProtectionOverride.Create(
                ruleId: null,
                commandId: "ID_OBJECTS_DELETE",
                scope: OverrideScope.Command,
                approverEmail: "lead@example.com",
                approverName: "Team Lead",
                reason: "Bulk cleanup operation",
                duration: TimeSpan.FromHours(4)
            );

            // Act
            await _overrideRepository.SaveOverrideAsync(commandOverride);

            // Assert - Should apply to matching command
            var applicable1 = await _overrideRepository.GetApplicableOverridesAsync(
                ruleId: "any-rule",
                commandId: "ID_OBJECTS_DELETE",
                documentId: "any-doc"
            );
            applicable1.Should().HaveCount(1);

            // Assert - Should NOT apply to different command
            var applicable2 = await _overrideRepository.GetApplicableOverridesAsync(
                ruleId: "any-rule",
                commandId: "ID_EDIT_MOVE",
                documentId: "any-doc"
            );
            applicable2.Should().BeEmpty();
        }

        #endregion

        #region Document-Scoped Override Tests

        [Fact]
        public async Task DocumentScopedOverride_ShouldApplyOnlyToSpecificDocument()
        {
            // Arrange
            var docOverride = ProtectionOverride.Create(
                ruleId: null,
                commandId: null,
                scope: OverrideScope.Document,
                approverEmail: "architect@example.com",
                approverName: "Lead Architect",
                reason: "Design iteration session",
                duration: TimeSpan.FromHours(8)
            );
            docOverride.DocumentId = "central-model-v12";

            // Act
            await _overrideRepository.SaveOverrideAsync(docOverride);

            // Assert - Should apply to matching document
            var applicable1 = await _overrideRepository.GetApplicableOverridesAsync(
                ruleId: "any-rule",
                commandId: "any-command",
                documentId: "central-model-v12"
            );
            applicable1.Should().HaveCount(1);

            // Assert - Should NOT apply to different document
            var applicable2 = await _overrideRepository.GetApplicableOverridesAsync(
                ruleId: "any-rule",
                commandId: "any-command",
                documentId: "different-model"
            );
            applicable2.Should().BeEmpty();
        }

        #endregion

        #region RuleAndCommand-Scoped Override Tests

        [Fact]
        public async Task RuleAndCommandOverride_ShouldRequireBothToMatch()
        {
            // Arrange
            var combinedOverride = ProtectionOverride.Create(
                ruleId: "RULE-PREVENT-WALL-DELETE",
                commandId: "ID_OBJECTS_DELETE",
                scope: OverrideScope.RuleAndCommand,
                approverEmail: "senior@example.com",
                approverName: "Senior Engineer",
                reason: "Approved wall removal for corridor widening",
                duration: TimeSpan.FromDays(2)
            );

            // Act
            await _overrideRepository.SaveOverrideAsync(combinedOverride);

            // Assert - Should apply when BOTH match
            var applicable1 = await _overrideRepository.GetApplicableOverridesAsync(
                ruleId: "RULE-PREVENT-WALL-DELETE",
                commandId: "ID_OBJECTS_DELETE",
                documentId: "any-doc"
            );
            applicable1.Should().HaveCount(1);

            // Assert - Should NOT apply when only rule matches
            var applicable2 = await _overrideRepository.GetApplicableOverridesAsync(
                ruleId: "RULE-PREVENT-WALL-DELETE",
                commandId: "ID_EDIT_MOVE",
                documentId: "any-doc"
            );
            applicable2.Should().BeEmpty();

            // Assert - Should NOT apply when only command matches
            var applicable3 = await _overrideRepository.GetApplicableOverridesAsync(
                ruleId: "DIFFERENT-RULE",
                commandId: "ID_OBJECTS_DELETE",
                documentId: "any-doc"
            );
            applicable3.Should().BeEmpty();
        }

        #endregion

        #region Override Revocation Tests

        [Fact]
        public async Task RevokeOverride_ShouldMarkAsInactive()
        {
            // Arrange
            var tempOverride = ProtectionOverride.Create(
                ruleId: "RULE-TEST",
                commandId: null,
                scope: OverrideScope.Rule,
                approverEmail: "temp@example.com",
                approverName: "Temp Approver",
                reason: "Temporary bypass",
                duration: TimeSpan.FromDays(30)
            );

            await _overrideRepository.SaveOverrideAsync(tempOverride);

            // Act - Revoke the override
            var revoked = await _overrideRepository.RevokeOverrideAsync(
                tempOverride.OverrideId,
                revokedBy: "admin@example.com"
            );

            // Assert
            revoked.Should().BeTrue();

            var retrieved = await _overrideRepository.GetOverrideByIdAsync(tempOverride.OverrideId);
            retrieved.Should().NotBeNull();
            retrieved.IsActive.Should().BeFalse();
            retrieved.RevokedAt.Should().NotBeNull();
            retrieved.RevokedBy.Should().Be("admin@example.com");
            retrieved.IsValid().Should().BeFalse("Revoked override should not be valid");
        }

        [Fact]
        public async Task RevokedOverride_ShouldNotAppearInActiveList()
        {
            // Arrange
            var override1 = ProtectionOverride.Create(
                ruleId: "RULE-001",
                commandId: null,
                scope: OverrideScope.Rule,
                approverEmail: "admin@example.com",
                approverName: "Admin",
                reason: "Test 1",
                duration: TimeSpan.FromDays(1)
            );

            var override2 = ProtectionOverride.Create(
                ruleId: "RULE-002",
                commandId: null,
                scope: OverrideScope.Rule,
                approverEmail: "admin@example.com",
                approverName: "Admin",
                reason: "Test 2",
                duration: TimeSpan.FromDays(1)
            );

            await _overrideRepository.SaveOverrideAsync(override1);
            await _overrideRepository.SaveOverrideAsync(override2);

            // Act - Revoke one
            await _overrideRepository.RevokeOverrideAsync(override1.OverrideId, "admin");

            var activeOverrides = await _overrideRepository.GetActiveOverridesAsync();

            // Assert - Only non-revoked should be active
            activeOverrides.Should().HaveCount(1);
            activeOverrides.First().OverrideId.Should().Be(override2.OverrideId);
        }

        #endregion

        #region Expiration and Cleanup Tests

        [Fact]
        public async Task CleanupExpiredOverrides_ShouldMarkAsInactive()
        {
            // Arrange - Create already-expired override
            var expiredOverride = new ProtectionOverride
            {
                OverrideId = Guid.NewGuid().ToString(),
                RuleId = "RULE-EXPIRED",
                Scope = OverrideScope.Rule,
                CreatedAt = DateTime.UtcNow.AddDays(-2),
                ExpiresAt = DateTime.UtcNow.AddDays(-1), // Expired yesterday
                ApproverEmail = "test@example.com",
                ApproverName = "Test",
                Reason = "Expired test",
                IsActive = true
            };

            await _overrideRepository.SaveOverrideAsync(expiredOverride);

            // Act
            var cleaned = await _overrideRepository.CleanupExpiredOverridesAsync();

            // Assert
            cleaned.Should().BeGreaterThan(0, "Should have cleaned up expired override");

            var retrieved = await _overrideRepository.GetOverrideByIdAsync(expiredOverride.OverrideId);
            retrieved.IsActive.Should().BeFalse("Expired override should be marked inactive");
        }

        [Fact]
        public async Task PermanentOverride_ShouldNotExpire()
        {
            // Arrange - Create permanent override (no expiration)
            var permanentOverride = ProtectionOverride.Create(
                ruleId: "RULE-PERMANENT",
                commandId: null,
                scope: OverrideScope.Rule,
                approverEmail: "admin@example.com",
                approverName: "Admin",
                reason: "Permanent exception",
                duration: null // No expiration
            );

            // Act
            await _overrideRepository.SaveOverrideAsync(permanentOverride);

            // Wait and cleanup
            await Task.Delay(100);
            await _overrideRepository.CleanupExpiredOverridesAsync();

            // Assert - Should still be active
            var activeOverrides = await _overrideRepository.GetActiveOverridesAsync();
            activeOverrides.Should().Contain(o => o.OverrideId == permanentOverride.OverrideId);
            activeOverrides.First().IsValid().Should().BeTrue();
        }

        #endregion

        #region PasswordManager Integration Tests

        [Fact]
        public async Task GetActiveOverride_ShouldReturnMostSpecificScope()
        {
            // Arrange - Create overlapping overrides with different scopes
            var globalOverride = ProtectionOverride.Create(
                ruleId: null,
                commandId: null,
                scope: OverrideScope.Global,
                approverEmail: "admin@example.com",
                approverName: "Admin",
                reason: "Global maintenance",
                duration: TimeSpan.FromDays(1)
            );

            var ruleOverride = ProtectionOverride.Create(
                ruleId: "RULE-001",
                commandId: null,
                scope: OverrideScope.Rule,
                approverEmail: "pm@example.com",
                approverName: "PM",
                reason: "Specific rule override",
                duration: TimeSpan.FromDays(1)
            );

            await _overrideRepository.SaveOverrideAsync(globalOverride);
            await _overrideRepository.SaveOverrideAsync(ruleOverride);

            // Act - Get active override via PasswordManager
            var activeOverride = await _passwordManager.GetActiveOverrideAsync(
                ruleId: "RULE-001",
                commandId: "any-command",
                documentId: "any-doc"
            );

            // Assert - Should return most specific (Rule scope = 1, Global scope = 0)
            activeOverride.Should().NotBeNull();
            activeOverride.Scope.Should().Be(OverrideScope.Global, "Should prioritize most specific scope (lower enum value)");
        }

        [Fact]
        public async Task CreateOverrideAsync_ViaPM_ShouldPersist()
        {
            // Act
            var newOverride = await _passwordManager.CreateOverrideAsync(
                ruleId: "RULE-PM-TEST",
                commandId: "ID_TEST",
                scope: OverrideScope.RuleAndCommand,
                approverEmail: "approver@example.com",
                approverName: "Test Approver",
                reason: "Created via PasswordManager",
                duration: TimeSpan.FromHours(12)
            );

            // Assert
            newOverride.Should().NotBeNull();
            newOverride.OverrideId.Should().NotBeNullOrEmpty();
            newOverride.IsValid().Should().BeTrue();

            // Verify it's persisted
            var retrieved = await _overrideRepository.GetOverrideByIdAsync(newOverride.OverrideId);
            retrieved.Should().NotBeNull();
            retrieved.RuleId.Should().Be("RULE-PM-TEST");
            retrieved.Reason.Should().Be("Created via PasswordManager");
        }

        [Fact]
        public async Task GetAllActiveOverridesAsync_ShouldReturnOnlyValid()
        {
            // Arrange
            var active1 = ProtectionOverride.Create("RULE-1", null, OverrideScope.Rule, "a@x.com", "A", "Reason 1", TimeSpan.FromDays(1));
            var active2 = ProtectionOverride.Create("RULE-2", null, OverrideScope.Rule, "b@x.com", "B", "Reason 2", TimeSpan.FromDays(1));
            var expired = ProtectionOverride.Create("RULE-3", null, OverrideScope.Rule, "c@x.com", "C", "Reason 3", TimeSpan.FromSeconds(-10));

            await _overrideRepository.SaveOverrideAsync(active1);
            await _overrideRepository.SaveOverrideAsync(active2);
            await _overrideRepository.SaveOverrideAsync(expired);

            // Revoke one active
            await _overrideRepository.RevokeOverrideAsync(active1.OverrideId, "admin");

            // Act
            var allActive = await _passwordManager.GetAllActiveOverridesAsync();

            // Assert - Should only return non-revoked, non-expired
            allActive.Should().HaveCount(1);
            allActive.First().OverrideId.Should().Be(active2.OverrideId);
        }

        #endregion

        #region Metadata and Audit Trail Tests

        [Fact]
        public async Task Override_ShouldStoreCompleteMetadata()
        {
            // Arrange
            var detailedOverride = ProtectionOverride.Create(
                ruleId: "RULE-METADATA-TEST",
                commandId: "ID_METADATA_CMD",
                scope: OverrideScope.RuleAndCommand,
                approverEmail: "senior.engineer@company.com",
                approverName: "Jane Smith",
                reason: "Emergency fix for production issue #12345 - approved by CTO",
                duration: TimeSpan.FromHours(6)
            );
            detailedOverride.DocumentId = "CentralModel_2024_v15";

            // Act
            await _overrideRepository.SaveOverrideAsync(detailedOverride);

            var retrieved = await _overrideRepository.GetOverrideByIdAsync(detailedOverride.OverrideId);

            // Assert - All metadata should be preserved
            retrieved.Should().NotBeNull();
            retrieved.OverrideId.Should().Be(detailedOverride.OverrideId);
            retrieved.RuleId.Should().Be("RULE-METADATA-TEST");
            retrieved.CommandId.Should().Be("ID_METADATA_CMD");
            retrieved.DocumentId.Should().Be("CentralModel_2024_v15");
            retrieved.Scope.Should().Be(OverrideScope.RuleAndCommand);
            retrieved.ApproverEmail.Should().Be("senior.engineer@company.com");
            retrieved.ApproverName.Should().Be("Jane Smith");
            retrieved.Reason.Should().Contain("Emergency fix");
            retrieved.CreatedAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
            retrieved.ExpiresAt.Should().NotBeNull();
            retrieved.ExpiresAt.Value.Should().BeCloseTo(DateTime.UtcNow.AddHours(6), TimeSpan.FromSeconds(5));
            retrieved.IsActive.Should().BeTrue();
            retrieved.RevokedAt.Should().BeNull();
            retrieved.RevokedBy.Should().BeNull();
        }

        #endregion

        public void Dispose()
        {
            if (File.Exists(_testDbPath))
            {
                try
                {
                    File.Delete(_testDbPath);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Could not delete test database: {ex.Message}");
                }
            }
        }
    }
}
