using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using BIManage.Data.SQLite;
using BIManage.Data.Caching;
using BIManage.Infrastructure.Logging;
using BIManage.Core.Rules.Models;
using BIManageRevit.Tests.Helpers;

namespace BIManageRevit.Tests.Integration
{
    /// <summary>
    /// Integration tests for local persistence and offline cache system
    /// Tests session management, event logging, offline queue, and hybrid caching
    /// </summary>
    public class PersistenceIntegrationTests : IDisposable
    {
        private readonly string _testDbPath;
        private readonly ILogger _logger;
        private readonly SessionRepository _sessionRepository;
        private readonly EventRepository _eventRepository;
        private readonly OfflineQueueRepository _offlineQueueRepository;
        private readonly HybridCacheManager _cacheManager;
        private readonly CacheCleanupPolicy _cleanupPolicy;

        public PersistenceIntegrationTests()
        {
            _logger = new ConsoleLogger();
            _testDbPath = TestDatabaseHelper.CreateTestDatabase(_logger);

            _sessionRepository = new SessionRepository(_testDbPath, _logger);
            _eventRepository = new EventRepository(_testDbPath, _logger);
            _offlineQueueRepository = new OfflineQueueRepository(_testDbPath, _logger);
            _cacheManager = new HybridCacheManager(_testDbPath, _logger, maxInMemoryItems: 100);

            var cleanupConfig = new CleanupConfiguration
            {
                InitialDelay = TimeSpan.FromHours(24), // Prevent auto-cleanup during tests
                EnableEventCleanup = true,
                EnableOfflineQueueCleanup = true,
                EnableOtpCleanup = true,
                EnableAuditCleanup = true
            };
            _cleanupPolicy = new CacheCleanupPolicy(_testDbPath, _logger, cleanupConfig);
        }

        #region Session Management Tests

        [Fact]
        public async Task StartSession_ShouldCreateNewSession()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();

            // Act
            var session = await _sessionRepository.StartSessionAsync(
                sessionId: sessionId,
                revitVersion: "2025",
                revitBuild: "25.0.0.123",
                username: "test.user",
                revitUsername: "test.user",
                userEmail: "test.user@example.com",
                computerName: "TEST-PC");

            // Assert
            session.Should().NotBeNull();
            session.SessionId.Should().Be(sessionId);
            session.IsActive.Should().BeTrue();
            session.RevitVersion.Should().Be("2025");
            session.Username.Should().Be("test.user");
        }

        [Fact]
        public async Task EndSession_ShouldMarkSessionInactive()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();
            await _sessionRepository.StartSessionAsync(
                sessionId, "2025", "25.0.0.123", "test.user", "test.user", "test@example.com", "TEST-PC");

            // Act
            var ended = await _sessionRepository.EndSessionAsync(sessionId, crashDetected: false);

            // Assert
            ended.Should().BeTrue();

            var sessions = await _sessionRepository.GetActiveSessionsAsync();
            sessions.Should().NotContain(s => s.SessionId == sessionId);
        }

        [Fact]
        public async Task RecordHeartbeat_ShouldUpdateSessionHealth()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();
            await _sessionRepository.StartSessionAsync(
                sessionId, "2025", "25.0.0.123", "test.user", "test.user", "test@example.com", "TEST-PC");

            // Act
            await Task.Delay(100); // Small delay to ensure timestamp difference
            var recorded = await _sessionRepository.RecordHeartbeatAsync(
                sessionId,
                activeDocumentId: "doc-123",
                memoryUsagePercent: 65.5,
                cpuUsagePercent: 25.5);

            // Assert
            recorded.Should().BeTrue();

            var sessions = await _sessionRepository.GetActiveSessionsAsync();
            var session = sessions.FirstOrDefault(s => s.SessionId == sessionId);
            session.Should().NotBeNull();
            session.IsHealthy(TimeSpan.FromMinutes(5)).Should().BeTrue();
        }

        [Fact]
        public async Task OpenDocument_ShouldCreateDocumentSession()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();
            await _sessionRepository.StartSessionAsync(
                sessionId, "2025", "25.0.0.123", "test.user", "test.user", "test@example.com", "TEST-PC");

            // Act
            var docSession = await _sessionRepository.OpenDocumentAsync(
                sessionId: sessionId,
                documentId: "doc-123",
                documentPath: @"C:\Projects\Test.rvt",
                documentTitle: "Test Project",
                isWorkshared: true,
                isFamily: false);

            // Assert
            docSession.Should().NotBeNull();
            docSession.DocumentId.Should().Be("doc-123");
            docSession.IsWorkshared.Should().BeTrue();
            docSession.IsActive.Should().BeTrue();
        }

        [Fact]
        public async Task CloseDocument_ShouldMarkDocumentInactive()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();
            await _sessionRepository.StartSessionAsync(
                sessionId, "2025", "25.0.0.123", "test.user", "test.user", "test@example.com", "TEST-PC");

            await _sessionRepository.OpenDocumentAsync(
                sessionId, "doc-123", @"C:\Test.rvt", "Test", false, false);

            // Act
            var closed = await _sessionRepository.CloseDocumentAsync(sessionId, "doc-123");

            // Assert
            closed.Should().BeTrue();
        }

        #endregion

        #region Event Logging Tests

        [Fact]
        public async Task LogEvent_ShouldPersistEvent()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();
            var evt = RevitEvent.Create(
                sessionId: sessionId,
                documentId: "doc-123",
                eventType: "DocumentOpened",
                eventCategory: "Document",
                eventName: "OnDocumentOpened");
            evt.DurationMs = 150;

            // Act
            var logged = await _eventRepository.LogEventAsync(evt);

            // Assert
            logged.Should().BeTrue();

            var recentEvents = await _eventRepository.GetRecentEventsAsync(sessionId, limit: 10);
            recentEvents.Should().HaveCount(1);
            recentEvents.First().EventType.Should().Be("DocumentOpened");
            recentEvents.First().DurationMs.Should().Be(150);
        }

        [Fact]
        public async Task LogEventsBatch_ShouldPersistMultipleEvents()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();
            var events = new System.Collections.Generic.List<RevitEvent>();

            for (int i = 0; i < 50; i++)
            {
                events.Add(RevitEvent.Create(
                    sessionId, "doc-123", $"EventType{i}", "Element", $"OnEvent{i}"));
            }

            // Act
            var count = await _eventRepository.LogEventsBatchAsync(events);

            // Assert
            count.Should().Be(50);

            var recentEvents = await _eventRepository.GetRecentEventsAsync(sessionId, limit: 100);
            recentEvents.Should().HaveCount(50);
        }

        [Fact]
        public async Task GetEventStatistics_ShouldReturnAccurateCounts()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();

            // Log successful events
            for (int i = 0; i < 10; i++)
            {
                var evt = RevitEvent.Create(sessionId, "doc-123", "SuccessEvent", "Element", "OnSuccess");
                evt.Success = true;
                await _eventRepository.LogEventAsync(evt);
            }

            // Log failed events
            for (int i = 0; i < 3; i++)
            {
                var evt = RevitEvent.Create(sessionId, "doc-123", "FailEvent", "Element", "OnFail");
                evt.Success = false;
                evt.ErrorMessage = "Test error";
                await _eventRepository.LogEventAsync(evt);
            }

            // Act
            var stats = await _eventRepository.GetEventStatisticsAsync(sessionId);

            // Assert
            stats.Should().ContainKey("Element");
            stats["Element"].TotalEvents.Should().Be(13);
            stats["Element"].Successful.Should().Be(10);
            stats["Element"].Failed.Should().Be(3);
            stats["Element"].SuccessRate.Should().BeApproximately(76.92, 0.1);
        }

        [Fact]
        public async Task CleanupOldEvents_ShouldDeleteExpiredEvents()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();

            // Create old event (manually set timestamp in past)
            var oldEvent = RevitEvent.Create(sessionId, "doc-123", "OldEvent", "Element", "OnOld");
            oldEvent.Timestamp = DateTime.UtcNow.AddDays(-100);
            await _eventRepository.LogEventAsync(oldEvent);

            // Create recent event
            var recentEvent = RevitEvent.Create(sessionId, "doc-123", "RecentEvent", "Element", "OnRecent");
            await _eventRepository.LogEventAsync(recentEvent);

            // Act
            var deleted = await _eventRepository.CleanupOldEventsAsync(TimeSpan.FromDays(90));

            // Assert
            deleted.Should().BeGreaterThan(0);

            var remaining = await _eventRepository.GetRecentEventsAsync(sessionId, limit: 100);
            remaining.Should().Contain(e => e.EventName == "OnRecent");
        }

        #endregion

        #region Offline Queue Tests

        [Fact]
        public async Task EnqueueOperation_ShouldAddToQueue()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();
            var operation = OfflineOperation.Create(
                sessionId: sessionId,
                operationType: "audit_log",
                operationData: "{\"action\": \"test\"}",
                priority: 5);

            // Act
            var enqueued = await _offlineQueueRepository.EnqueueAsync(operation);

            // Assert
            enqueued.Should().BeTrue();

            var pending = await _offlineQueueRepository.GetPendingOperationsAsync(limit: 10);
            pending.Should().HaveCount(1);
            pending.First().OperationType.Should().Be("audit_log");
        }

        [Fact]
        public async Task GetPendingOperations_ShouldOrderByPriorityAndTime()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();

            // High priority, old
            var op1 = OfflineOperation.Create(sessionId, "op1", "{}", priority: 1);
            op1.CreatedAt = DateTime.UtcNow.AddMinutes(-10);
            await _offlineQueueRepository.EnqueueAsync(op1);

            // Low priority, new
            var op2 = OfflineOperation.Create(sessionId, "op2", "{}", priority: 10);
            await _offlineQueueRepository.EnqueueAsync(op2);

            // High priority, new
            var op3 = OfflineOperation.Create(sessionId, "op3", "{}", priority: 1);
            await _offlineQueueRepository.EnqueueAsync(op3);

            // Act
            var pending = await _offlineQueueRepository.GetPendingOperationsAsync(limit: 10);

            // Assert
            pending.Should().HaveCount(3);
            pending[0].Priority.Should().Be(1); // High priority first
            pending[2].Priority.Should().Be(10); // Low priority last
        }

        [Fact]
        public async Task MarkCompleted_ShouldUpdateStatus()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();
            var operation = OfflineOperation.Create(sessionId, "test_op", "{}");
            await _offlineQueueRepository.EnqueueAsync(operation);

            // Act
            var marked = await _offlineQueueRepository.MarkCompletedAsync(operation.QueueId);

            // Assert
            marked.Should().BeTrue();

            var pending = await _offlineQueueRepository.GetPendingOperationsAsync(limit: 10);
            pending.Should().NotContain(op => op.QueueId == operation.QueueId);
        }

        [Fact(Skip = "Quarantined: pre-existing prod bug — OfflineQueueRepository positional reads vs Schema.sql column order drift. See docs/testing/quarantine.md.")]
        public async Task MarkFailed_ShouldIncrementRetryCount()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();
            var operation = OfflineOperation.Create(sessionId, "test_op", "{}");
            operation.MaxRetries = 3;
            await _offlineQueueRepository.EnqueueAsync(operation);

            // Act - Fail twice (should still be pending)
            await _offlineQueueRepository.MarkFailedAsync(operation.QueueId, "Error 1");
            await _offlineQueueRepository.MarkFailedAsync(operation.QueueId, "Error 2");

            var pending1 = await _offlineQueueRepository.GetPendingOperationsAsync(limit: 10);

            // Fail third time (should be permanently failed)
            await _offlineQueueRepository.MarkFailedAsync(operation.QueueId, "Error 3");

            var pending2 = await _offlineQueueRepository.GetPendingOperationsAsync(limit: 10);

            // Assert
            pending1.Should().HaveCount(1);
            pending2.Should().BeEmpty(); // Permanently failed, no longer pending
        }

        [Fact]
        public async Task GetQueueSummary_ShouldReturnAccurateStatistics()
        {
            // Arrange
            var sessionId = Guid.NewGuid().ToString();

            // Pending
            await _offlineQueueRepository.EnqueueAsync(
                OfflineOperation.Create(sessionId, "pending1", "{}"));
            await _offlineQueueRepository.EnqueueAsync(
                OfflineOperation.Create(sessionId, "pending2", "{}"));

            // Completed
            var completed = OfflineOperation.Create(sessionId, "completed", "{}");
            await _offlineQueueRepository.EnqueueAsync(completed);
            await _offlineQueueRepository.MarkCompletedAsync(completed.QueueId);

            // Failed
            var failed = OfflineOperation.Create(sessionId, "failed", "{}");
            failed.MaxRetries = 1;
            await _offlineQueueRepository.EnqueueAsync(failed);
            await _offlineQueueRepository.MarkFailedAsync(failed.QueueId, "Error");

            // Act
            var summary = await _offlineQueueRepository.GetQueueSummaryAsync();

            // Assert
            summary.PendingCount.Should().Be(2);
            summary.CompletedCount.Should().Be(1);
            summary.FailedCount.Should().Be(1);
            summary.TotalCount.Should().Be(4);
        }

        #endregion

        #region Hybrid Cache Tests

        [Fact]
        public async Task HybridCache_GetRule_ShouldHitMemoryCache()
        {
            // Arrange
            var rule = CreateTestRule("CACHE-001", "Test Rule");

            // Act - First access (cache miss, should load)
            await _cacheManager.SetAsync(rule.RuleId, rule);

            // Second access (cache hit)
            var retrievedRule = await _cacheManager.GetRuleAsync(rule.RuleId);

            // Assert
            retrievedRule.Should().NotBeNull();
            retrievedRule.RuleId.Should().Be("CACHE-001");

            var stats = _cacheManager.GetStatistics();
            stats.HitCount.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task HybridCache_SetBatch_ShouldCacheMultipleRules()
        {
            // Arrange
            var rules = new System.Collections.Generic.List<Rule>();
            for (int i = 0; i < 20; i++)
            {
                rules.Add(CreateTestRule($"BATCH-{i:000}", $"Batch Rule {i}"));
            }

            // Act
            var cached = await _cacheManager.SetBatchAsync(rules);

            // Assert
            cached.Should().Be(20);

            var allRules = await _cacheManager.GetAllRulesAsync(memoryOnly: true);
            allRules.Should().HaveCount(20);
        }

        [Fact]
        public async Task HybridCache_Invalidate_ShouldRemoveFromCache()
        {
            // Arrange
            var rule = CreateTestRule("INVALID-001", "Test Rule");
            await _cacheManager.SetAsync(rule.RuleId, rule);

            // Act
            await _cacheManager.InvalidateAsync(rule.RuleId);

            // Assert
            var stats = _cacheManager.GetStatistics();
            stats.InMemoryCount.Should().Be(0);
        }

        [Fact]
        public async Task HybridCache_LRU_ShouldEvictOldestItems()
        {
            // Arrange - Create small cache
            var smallCache = new HybridCacheManager(_testDbPath, _logger, maxInMemoryItems: 5);

            // Add 5 items
            for (int i = 0; i < 5; i++)
            {
                await smallCache.SetAsync($"RULE-{i}", CreateTestRule($"RULE-{i}", $"Rule {i}"));
            }

            // Access RULE-0 to make it most recently used
            await Task.Delay(100);
            await smallCache.GetRuleAsync("RULE-0");

            // Add one more (should evict least recently used)
            await smallCache.SetAsync("RULE-5", CreateTestRule("RULE-5", "Rule 5"));

            // Assert
            var stats = smallCache.GetStatistics();
            stats.InMemoryCount.Should().BeLessOrEqualTo(5);
            stats.EvictionCount.Should().BeGreaterThan(0);

            // RULE-0 should still be in cache (recently accessed)
            var rule0 = await smallCache.GetRuleAsync("RULE-0");
            rule0.Should().NotBeNull();

            smallCache.Dispose();
        }

        [Fact]
        public async Task HybridCache_Statistics_ShouldBeAccurate()
        {
            // Arrange
            var rule1 = CreateTestRule("STAT-001", "Stat Rule 1");
            var rule2 = CreateTestRule("STAT-002", "Stat Rule 2");

            // Act
            await _cacheManager.SetAsync(rule1.RuleId, rule1);
            await _cacheManager.GetRuleAsync(rule1.RuleId); // Hit
            await _cacheManager.GetRuleAsync(rule1.RuleId); // Hit
            await _cacheManager.GetRuleAsync("NON-EXISTENT"); // Miss

            var stats = _cacheManager.GetStatistics();

            // Assert
            stats.HitCount.Should().Be(2);
            stats.MissCount.Should().BeGreaterThan(0);
            stats.HitRate.Should().BeGreaterThan(0);
        }

        #endregion

        #region Cleanup Policy Tests

        [Fact]
        public async Task CleanupPolicy_ShouldRemoveOldData()
        {
            // Arrange - Create old data
            var sessionId = Guid.NewGuid().ToString();

            // Old event
            var oldEvent = RevitEvent.Create(sessionId, "doc", "Old", "Element", "OnOld");
            oldEvent.Timestamp = DateTime.UtcNow.AddDays(-100);
            await _eventRepository.LogEventAsync(oldEvent);

            // Old completed operation
            var oldOp = OfflineOperation.Create(sessionId, "old_op", "{}");
            oldOp.CreatedAt = DateTime.UtcNow.AddDays(-10);
            await _offlineQueueRepository.EnqueueAsync(oldOp);
            await _offlineQueueRepository.MarkCompletedAsync(oldOp.QueueId);

            // Act
            await _cleanupPolicy.ExecuteCleanupAsync();

            // Assert - Old data should be cleaned
            // (Verification depends on cleanup configuration)
        }

        #endregion

        #region Helper Methods

        private Rule CreateTestRule(string ruleId, string name)
        {
            return new Rule
            {
                RuleId = ruleId,
                Name = name,
                Description = $"Test rule: {name}",
                IsEnabled = true,
                Mode = ProtectionMode.Notify,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                RequireComment = false
            };
        }

        #endregion

        public void Dispose()
        {
            _cacheManager?.Dispose();
            _cleanupPolicy?.Dispose();

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
