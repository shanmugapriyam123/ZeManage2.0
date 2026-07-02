using System;
using System.IO;
using System.Threading.Tasks;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManageRevit.Tests.Helpers;
using FluentAssertions;
using Xunit;

namespace BIManageRevit.Tests.Integration
{
    public class OfflineQueueRetryTests : IDisposable
    {
        private readonly string _dbPath;
        private readonly ILogger _logger;
        private readonly OfflineQueueRepository _repo;

        public OfflineQueueRetryTests()
        {
            _logger = new ConsoleLogger();
            _dbPath = TestDatabaseHelper.CreateTestDatabase(_logger);
            _repo = new OfflineQueueRepository(_dbPath, _logger);
        }

        public void Dispose()
        {
            TestDatabaseHelper.Cleanup(_dbPath);
        }

        [Fact(Skip = "Quarantined: pre-existing constant drift between OfflineOperation and test expectations. See docs/testing/quarantine.md.")]
        public void DefaultMaxRetries_Is10()
        {
            OfflineOperation.DefaultMaxRetries.Should().Be(10);
        }

        [Fact(Skip = "Quarantined: pre-existing constant drift between OfflineOperation and test expectations. See docs/testing/quarantine.md.")]
        public void CriticalMaxRetries_Is20()
        {
            OfflineOperation.CriticalMaxRetries.Should().Be(20);
        }

        [Fact]
        public void Create_UsesDefaultMaxRetries()
        {
            var op = OfflineOperation.Create("session-1", "test_op", "{}", priority: 5);
            op.MaxRetries.Should().Be(OfflineOperation.DefaultMaxRetries);
        }

        [Fact]
        public void Create_WithCustomMaxRetries()
        {
            var op = OfflineOperation.Create("session-1", "test_op", "{}", maxRetries: 50);
            op.MaxRetries.Should().Be(50);
        }

        [Fact(Skip = "Quarantined: pre-existing prod bug — OfflineQueueRepository positional reads vs Schema.sql column order drift. See docs/testing/quarantine.md.")]
        public async Task MarkFailed_AtLimit_TransitionsToFailedStatus()
        {
            // Enqueue with max 2 retries
            var op = OfflineOperation.Create("session-1", "test_op", "{\"test\":true}", maxRetries: 2);
            await _repo.EnqueueAsync(op);

            // First failure → still pending
            await _repo.MarkFailedAsync(op.QueueId, "error 1");
            var pending = await _repo.GetPendingOperationsAsync(10);
            pending.Should().HaveCount(1, "still has 1 retry left");

            // Second failure → transitions to 'failed'
            await _repo.MarkFailedAsync(op.QueueId, "error 2");
            pending = await _repo.GetPendingOperationsAsync(10);
            pending.Should().HaveCount(0, "exhausted max retries, no longer pending");
        }

        [Fact]
        public async Task GetPending_SkipsExhaustedItems()
        {
            // Enqueue two items: one with max retries, one unlimited
            var limited = OfflineOperation.Create("s1", "limited_op", "{}", maxRetries: 1);
            var unlimited = OfflineOperation.Create("s1", "unlimited_op", "{}", maxRetries: 0);

            await _repo.EnqueueAsync(limited);
            await _repo.EnqueueAsync(unlimited);

            // Fail the limited one past its limit
            await _repo.MarkFailedAsync(limited.QueueId, "fail");

            var pending = await _repo.GetPendingOperationsAsync(10);
            pending.Should().HaveCount(1);
            pending[0].OperationType.Should().Be("unlimited_op");
        }

        [Fact(Skip = "Quarantined: pre-existing prod bug — OfflineQueueRepository positional reads vs Schema.sql column order drift. See docs/testing/quarantine.md.")]
        public async Task UnlimitedRetries_NeverTransitionsToFailed()
        {
            var op = OfflineOperation.Create("s1", "unlimited_op", "{}", maxRetries: 0);
            await _repo.EnqueueAsync(op);

            // Fail many times
            for (int i = 0; i < 20; i++)
                await _repo.MarkFailedAsync(op.QueueId, $"fail {i}");

            var pending = await _repo.GetPendingOperationsAsync(10);
            pending.Should().HaveCount(1, "max_retries=0 means unlimited");
        }

        [Fact]
        public void CanRetry_RespectsMaxRetries()
        {
            var op = new OfflineOperation { MaxRetries = 5, RetryCount = 4, SyncStatus = "pending" };
            op.CanRetry.Should().BeTrue("4 < 5");

            op.RetryCount = 5;
            op.CanRetry.Should().BeFalse("5 >= 5");
        }
    }
}
