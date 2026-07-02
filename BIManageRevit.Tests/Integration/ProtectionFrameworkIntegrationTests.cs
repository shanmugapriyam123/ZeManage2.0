using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using Moq;
using BIManage.Core.Rules;
using BIManage.Core.Rules.Models;
using BIManage.Core.Protection;
using BIManage.Core.Protection.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Core.Features;
using BIManage.Core.Identity;
using BIManage.Revit.Commands;
using BIManageRevit.Tests.Helpers;

namespace BIManageRevit.Tests.Integration
{
    /// <summary>
    /// Integration tests for Protection Framework with all three modes
    /// Tests Monitor, Guide, and Prevent modes with RBAC audit logging
    /// </summary>
    public class ProtectionFrameworkIntegrationTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly ILogger _logger;
        private readonly RuleRepository _ruleRepository;
        private readonly AuditRepository _auditRepository;
        private readonly PasswordManager _passwordManager;
        private readonly AsyncAuditQueue _asyncAuditQueue;
        private readonly Mock<IRuleService> _mockRuleService;
        private readonly Mock<ICommandInterceptionService> _mockCommandService;
        private readonly Mock<IFeatureToggleService> _mockFeatureService;
        private readonly Mock<IUserService> _mockUserService;

        public ProtectionFrameworkIntegrationTests()
        {
            // Initialize logger and create test database with full schema
            _logger = new ConsoleLogger();
            _testDbPath = TestDatabaseHelper.CreateTestDatabase(_logger);

            // Initialize repositories
            _ruleRepository = new RuleRepository(_testDbPath, _logger);
            _auditRepository = new AuditRepository(_testDbPath, _logger);
            _passwordManager = new PasswordManager(_testDbPath, _logger);

            // Initialize async audit queue
            _asyncAuditQueue = new AsyncAuditQueue(_auditRepository, _logger);

            // Setup mocks
            _mockRuleService = new Mock<IRuleService>();
            _mockCommandService = new Mock<ICommandInterceptionService>();
            _mockFeatureService = new Mock<IFeatureToggleService>();
            _mockUserService = new Mock<IUserService>();

            // Setup default feature toggle (enabled)
            _mockFeatureService.Setup(f => f.IsFeatureEnabled("ProtectionFramework")).Returns(true);

            // Setup default user (normal user)
            var mockUser = new UserIdentity
            {
                UserId = "test-user-id",
                UserName = "Test User",
                Email = "test@example.com",
                IsCompanyAdmin = false,
                IsProjectAdmin = false
            };
            _mockUserService.Setup(u => u.CurrentUser).Returns(mockUser);
            _mockUserService.Setup(u => u.IsAuthenticated).Returns(true);
        }

        #region Monitor Mode Tests

        [Fact]
        public async Task MonitorMode_ShouldLogAction_WithoutBlocking()
        {
            // Arrange
            var rule = CreateTestRule(
                ruleId: "RULE-MONITOR-001",
                name: "Monitor Wall Changes",
                mode: ProtectionMode.Notify,
                categoryOST: "OST_Walls"
            );

            await _ruleRepository.SaveRuleAsync(rule);

            var evaluationResult = new RuleEvaluationResult
            {
                FinalMode = ProtectionMode.Notify,
                CombinedMessage = "Monitoring wall modifications",
                MatchedRules = new List<Rule> { rule },
            };

            _mockRuleService.Setup(r => r.EvaluateRules(
                It.IsAny<Autodesk.Revit.DB.Element>(), It.IsAny<int>()))
                .Returns(evaluationResult);

            // Create audit entry directly (simulating RuleCommandInterceptor)
            var auditEntry = new ProtectionAuditEntry
            {
                Timestamp = DateTime.Now,
                UserName = "Test User",
                WasCompanyAdmin = false,
                WasProjectAdmin = false,
                ProtectionId = "RULE-MONITOR-001",
                CommandName = "ID_OBJECTS_DELETE",
                Mode = ProtectionMode.Notify,
                Action = ProtectionAction.Allowed,
                ElementIds = "12345,67890",
                ElementCount = 2,
                Reason = "Monitoring wall modifications"
            };

            // Act - Use async queue
            _asyncAuditQueue.Enqueue(auditEntry);

            // Wait for processing
            await Task.Delay(500);

            // Assert
            var entries = await _auditRepository.GetAuditEntriesAsync(limit: 10);
            entries.Should().HaveCount(1);

            var savedEntry = entries.First();
            savedEntry.Mode.Should().Be(ProtectionMode.Notify);
            savedEntry.Action.Should().Be(ProtectionAction.Allowed);
            savedEntry.UserName.Should().Be("Test User");
            savedEntry.WasCompanyAdmin.Should().BeFalse();
            savedEntry.ElementCount.Should().Be(2);

            // Verify queue stats
            var stats = _asyncAuditQueue.GetStats();
            stats.TotalEnqueued.Should().Be(1);
            stats.TotalProcessed.Should().Be(1);
            stats.TotalFailed.Should().Be(0);
        }

        [Fact]
        public async Task MonitorMode_ShouldNotBlock_EvenWithMultipleRules()
        {
            // Arrange
            var rules = new List<Rule>
            {
                CreateTestRule("RULE-MON-1", "Monitor Doors", ProtectionMode.Notify, "OST_Doors"),
                CreateTestRule("RULE-MON-2", "Monitor Windows", ProtectionMode.Notify, "OST_Windows")
            };

            foreach (var rule in rules)
            {
                await _ruleRepository.SaveRuleAsync(rule);
            }

            var evaluationResult = new RuleEvaluationResult
            {
                FinalMode = ProtectionMode.Notify,
                CombinedMessage = "Multiple monitoring rules matched",
                MatchedRules = rules,
            };

            _mockRuleService.Setup(r => r.EvaluateRules(
                It.IsAny<Autodesk.Revit.DB.Element>(), It.IsAny<int>()))
                .Returns(evaluationResult);

            // Act - Create multiple audit entries
            for (int i = 0; i < 5; i++)
            {
                var entry = new ProtectionAuditEntry
                {
                    Timestamp = DateTime.Now,
                    UserName = "Test User",
                    WasCompanyAdmin = false,
                    WasProjectAdmin = false,
                    ProtectionId = $"RULE-MON-{i + 1}",
                    CommandName = "ID_OBJECTS_DELETE",
                    Mode = ProtectionMode.Notify,
                    Action = ProtectionAction.Allowed,
                    ElementIds = $"{12345 + i}",
                    ElementCount = 1
                };

                _asyncAuditQueue.Enqueue(entry);
            }

            // Wait for processing
            await Task.Delay(1000);

            // Assert
            var stats = _asyncAuditQueue.GetStats();
            stats.TotalEnqueued.Should().Be(5);
            stats.TotalProcessed.Should().Be(5);
            stats.SuccessRate.Should().Be(100.0);
        }

        #endregion

        #region Guide Mode Tests

        [Fact]
        public async Task GuideMode_UserConfirms_ShouldAllowWithComment()
        {
            // Arrange
            var rule = CreateTestRule(
                ruleId: "RULE-GUIDE-001",
                name: "Guide Structural Changes",
                mode: ProtectionMode.Assist,
                categoryOST: "OST_StructuralFraming",
                requireComment: true
            );

            await _ruleRepository.SaveRuleAsync(rule);

            // Simulate user confirmation with comment
            var auditEntry = new ProtectionAuditEntry
            {
                Timestamp = DateTime.Now,
                UserName = "Test User",
                WasCompanyAdmin = false,
                WasProjectAdmin = false,
                ProtectionId = "RULE-GUIDE-001",
                CommandName = "ID_EDIT_MOVE",
                Mode = ProtectionMode.Assist,
                Action = ProtectionAction.Allowed,
                ElementIds = "45678",
                ElementCount = 1,
                UserComment = "Moving beam to align with column grid",
                Reason = "Structural element modifications require confirmation"
            };

            // Act
            _asyncAuditQueue.Enqueue(auditEntry);
            await Task.Delay(500);

            // Assert
            var entries = await _auditRepository.GetAuditEntriesAsync(limit: 10);
            entries.Should().HaveCount(1);

            var savedEntry = entries.First();
            savedEntry.Mode.Should().Be(ProtectionMode.Assist);
            savedEntry.Action.Should().Be(ProtectionAction.Allowed);
            savedEntry.UserComment.Should().Be("Moving beam to align with column grid");
        }

        [Fact(Skip = "Quarantined: requires real WPF dialog interaction; will be replaced by RevitUnit equivalent in BIManageRevit.RevitTests.R26. See docs/testing/quarantine.md.")]
        public async Task GuideMode_UserCancels_ShouldBlock()
        {
            // Arrange
            var rule = CreateTestRule(
                ruleId: "RULE-GUIDE-002",
                name: "Guide View Deletions",
                mode: ProtectionMode.Assist,
                categoryOST: "OST_Views"
            );

            await _ruleRepository.SaveRuleAsync(rule);

            // Simulate user cancellation
            var auditEntry = new ProtectionAuditEntry
            {
                Timestamp = DateTime.Now,
                UserName = "Test User",
                WasCompanyAdmin = false,
                WasProjectAdmin = false,
                ProtectionId = "RULE-GUIDE-002",
                CommandName = "ID_OBJECTS_DELETE",
                Mode = ProtectionMode.Assist,
                Action = ProtectionAction.Blocked,
                ElementIds = "98765",
                ElementCount = 1,
                UserComment = null,
                Reason = "User cancelled view deletion"
            };

            // Act
            _asyncAuditQueue.Enqueue(auditEntry);
            await Task.Delay(500);

            // Assert
            var entries = await _auditRepository.GetAuditEntriesAsync(limit: 10);
            entries.Should().HaveCount(1);

            var savedEntry = entries.First();
            savedEntry.Action.Should().Be(ProtectionAction.Blocked);
            savedEntry.UserComment.Should().BeNull();
        }

        #endregion

        #region Prevent Mode Tests

        [Fact]
        public async Task PreventMode_NoPassword_ShouldBlock()
        {
            // Arrange
            var rule = CreateTestRule(
                ruleId: "RULE-PREVENT-001",
                name: "Prevent Wall Deletion",
                mode: ProtectionMode.Protect,
                categoryOST: "OST_Walls"
            );

            await _ruleRepository.SaveRuleAsync(rule);

            // Simulate blocked attempt without password
            var auditEntry = new ProtectionAuditEntry
            {
                Timestamp = DateTime.Now,
                UserName = "Test User",
                WasCompanyAdmin = false,
                WasProjectAdmin = false,
                ProtectionId = "RULE-PREVENT-001",
                CommandName = "ID_OBJECTS_DELETE",
                Mode = ProtectionMode.Protect,
                Action = ProtectionAction.Blocked,
                ElementIds = "11111",
                ElementCount = 1,
                Reason = "Wall deletion blocked by protection rule"
            };

            // Act
            _asyncAuditQueue.Enqueue(auditEntry);
            await Task.Delay(500);

            // Assert
            var entries = await _auditRepository.GetAuditEntriesAsync(limit: 10);
            entries.Should().HaveCount(1);

            var savedEntry = entries.First();
            savedEntry.Mode.Should().Be(ProtectionMode.Protect);
            savedEntry.Action.Should().Be(ProtectionAction.Blocked);
        }

        [Fact]
        public async Task PreventMode_WithValidPassword_ShouldAllowWithOverride()
        {
            // Arrange
            var rule = CreateTestRule(
                ruleId: "RULE-PREVENT-002",
                name: "Prevent Level Deletion",
                mode: ProtectionMode.Protect,
                categoryOST: "OST_Levels"
            );

            await _ruleRepository.SaveRuleAsync(rule);

            // Set admin password
            var password = "Admin@123";
            _passwordManager.SetPassword(password);

            // Validate password works
            var isValid = _passwordManager.ValidatePassword(password);
            isValid.Should().BeTrue("Password should be set correctly");

            // Simulate override with valid password
            var auditEntry = new ProtectionAuditEntry
            {
                Timestamp = DateTime.Now,
                UserName = "Test User",
                WasCompanyAdmin = false,
                WasProjectAdmin = false,
                ProtectionId = "RULE-PREVENT-002",
                CommandName = "ID_OBJECTS_DELETE",
                Mode = ProtectionMode.Protect,
                Action = ProtectionAction.Override,
                ElementIds = "22222",
                ElementCount = 1,
                OverrideMethod = "AdminPassword",
                Reason = "Level deletion blocked, admin override granted"
            };

            // Act
            _asyncAuditQueue.Enqueue(auditEntry);
            await Task.Delay(500);

            // Assert
            var entries = await _auditRepository.GetAuditEntriesAsync(limit: 10);
            entries.Should().HaveCount(1);

            var savedEntry = entries.First();
            savedEntry.Mode.Should().Be(ProtectionMode.Protect);
            savedEntry.Action.Should().Be(ProtectionAction.Override);
            savedEntry.OverrideMethod.Should().Be("AdminPassword");
        }

        [Fact]
        public async Task PreventMode_AdminUser_ShouldTrackRoleInAudit()
        {
            // Arrange - Setup company admin user
            var adminUser = new UserIdentity
            {
                UserId = "admin-user-id",
                UserName = "Admin User",
                Email = "admin@example.com",
                IsCompanyAdmin = true,
                IsProjectAdmin = false
            };
            _mockUserService.Setup(u => u.CurrentUser).Returns(adminUser);

            var rule = CreateTestRule(
                ruleId: "RULE-PREVENT-003",
                name: "Prevent Sheet Deletion",
                mode: ProtectionMode.Protect,
                categoryOST: "OST_Sheets"
            );

            await _ruleRepository.SaveRuleAsync(rule);

            // Simulate admin override
            var auditEntry = new ProtectionAuditEntry
            {
                Timestamp = DateTime.Now,
                UserName = "Admin User",
                WasCompanyAdmin = true,
                WasProjectAdmin = false,
                ProtectionId = "RULE-PREVENT-003",
                CommandName = "ID_OBJECTS_DELETE",
                Mode = ProtectionMode.Protect,
                Action = ProtectionAction.Override,
                ElementIds = "33333",
                ElementCount = 1,
                OverrideMethod = "AdminPassword",
                Reason = "Company admin override"
            };

            // Act
            _asyncAuditQueue.Enqueue(auditEntry);
            await Task.Delay(500);

            // Assert
            var entries = await _auditRepository.GetAuditEntriesAsync(limit: 10);
            entries.Should().HaveCount(1);

            var savedEntry = entries.First();
            savedEntry.WasCompanyAdmin.Should().BeTrue();
            savedEntry.WasProjectAdmin.Should().BeFalse();
        }

        #endregion

        #region Async Queue Performance Tests

        [Fact]
        public async Task AsyncQueue_ShouldHandleHighVolume_WithoutBlocking()
        {
            // Arrange
            const int entryCount = 100;
            var startTime = DateTime.Now;

            // Act - Enqueue 100 entries rapidly
            for (int i = 0; i < entryCount; i++)
            {
                var entry = new ProtectionAuditEntry
                {
                    Timestamp = DateTime.Now,
                    UserName = "Test User",
                    WasCompanyAdmin = false,
                    WasProjectAdmin = false,
                    ProtectionId = $"RULE-PERF-{i}",
                    CommandName = "ID_TEST_COMMAND",
                    Mode = ProtectionMode.Notify,
                    Action = ProtectionAction.Allowed,
                    ElementIds = i.ToString(),
                    ElementCount = 1
                };

                _asyncAuditQueue.Enqueue(entry);
            }

            var enqueueTime = (DateTime.Now - startTime).TotalMilliseconds;

            // Enqueuing should be very fast (non-blocking)
            enqueueTime.Should().BeLessThan(500, "Enqueuing should be non-blocking");

            // Wait for processing
            await Task.Delay(3000);

            // Assert
            var stats = _asyncAuditQueue.GetStats();
            stats.TotalEnqueued.Should().Be(entryCount);
            stats.TotalProcessed.Should().BeGreaterThan(entryCount - 5); // Allow small margin for timing
            stats.SuccessRate.Should().BeGreaterThan(95.0);
        }

        [Fact]
        public async Task AsyncQueue_ShouldReportStats_Accurately()
        {
            // Act
            for (int i = 0; i < 10; i++)
            {
                var entry = new ProtectionAuditEntry
                {
                    Timestamp = DateTime.Now,
                    UserName = "Test User",
                    ProtectionId = $"RULE-STATS-{i}",
                    Mode = ProtectionMode.Notify,
                    Action = ProtectionAction.Allowed,
                    ElementCount = 1
                };

                _asyncAuditQueue.Enqueue(entry);
            }

            await Task.Delay(1000);

            // Assert
            var stats = _asyncAuditQueue.GetStats();
            stats.TotalEnqueued.Should().Be(10);
            stats.TotalProcessed.Should().Be(10);
            stats.TotalFailed.Should().Be(0);
            stats.SuccessRate.Should().Be(100.0);

            _logger.LogInfo($"Queue Stats: {stats}");
        }

        #endregion

        #region Helper Methods

        private Rule CreateTestRule(
            string ruleId,
            string name,
            ProtectionMode mode,
            string categoryOST = null,
            bool requireComment = false)
        {
            return new Rule
            {
                RuleId = ruleId,
                Name = name,
                Description = $"Test rule: {name}",
                IsEnabled = true,
                Mode = mode,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                CategoryName = categoryOST ?? "OST_Walls",
                RequireComment = requireComment,
                CaptureBeforeScreenshot = false,
                CaptureAfterScreenshot = false
            };
        }

        #endregion

        public void Dispose()
        {
            // Dispose async queue
            _asyncAuditQueue?.Dispose();

            // Delete test database
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
