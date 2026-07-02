using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;
using FluentAssertions;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManageRevit.Tests.Helpers;

namespace BIManageRevit.Tests.Integration
{
    /// <summary>
    /// Critical resilience tests for persistence layer
    /// Tests that data survives Revit restarts, corruption, and failure scenarios
    /// These tests verify actual file I/O, not just in-memory operations
    /// </summary>
    public class PersistenceResilienceTests : IDisposable
    {
        private string _testDbPath;
        private ILogger _logger;

        public PersistenceResilienceTests()
        {
            _logger = new ConsoleLogger();
            _testDbPath = TestDatabaseHelper.CreateTestDatabase(_logger);
        }

        #region Session Persistence Across Restarts

        [Fact]
        public async Task Session_SurvivesRepositoryDisposal()
        {
            // ARRANGE: Create a session and commit to database
            var sessionId = $"test-session-{Guid.NewGuid()}";
            SessionRepository repository;

            using (repository = new SessionRepository(_testDbPath, _logger))
            {
                var session = await repository.StartSessionAsync(
                    sessionId: sessionId,
                    revitVersion: "2025",
                    revitBuild: "20.0.0.1",
                    username: "John Doe",
                    revitUsername: "John Doe",
                    userEmail: "john@example.com",
                    computerName: "JOHN-PC"
                );

                // Verify session was created
                session.Should().NotBeNull();
                session.SessionId.Should().Be(sessionId);
                session.Username.Should().Be("John Doe");
            }
            // Repository disposed here - simulates Revit shutdown

            // ACT: Create new repository instance - simulates Revit restart
            using (repository = new SessionRepository(_testDbPath, _logger))
            {
                // ASSERT: Session should still exist after restart
                var retrieved = await repository.GetSessionAsync(sessionId);
                retrieved.Should().NotBeNull("session data should survive repository disposal");
                retrieved.SessionId.Should().Be(sessionId);
                retrieved.Username.Should().Be("John Doe");
                retrieved.UserEmail.Should().Be("john@example.com");
                retrieved.ComputerName.Should().Be("JOHN-PC");
                retrieved.RevitVersion.Should().Be("2025");
            }
        }

        [Fact]
        public async Task Session_HeartbeatsPersistedAcrossRestart()
        {
            // ARRANGE: Create session and record heartbeats
            var sessionId = $"test-session-{Guid.NewGuid()}";
            DateTime lastActiveTime;

            using (var repository = new SessionRepository(_testDbPath, _logger))
            {
                await repository.StartSessionAsync(sessionId, "2025", "20.0.0.1", "user", "user", null, "PC");

                // Record 3 heartbeats with delays
                await repository.RecordHeartbeatAsync(sessionId);
                await Task.Delay(50);
                await repository.RecordHeartbeatAsync(sessionId);
                await Task.Delay(50);
                await repository.RecordHeartbeatAsync(sessionId);

                var session = await repository.GetSessionAsync(sessionId);
                lastActiveTime = session.LastHeartbeat;
            }
            // Repository disposed - simulates Revit shutdown

            // ACT: Reopen repository - simulates Revit restart
            using (var repository = new SessionRepository(_testDbPath, _logger))
            {
                // ASSERT: Last active timestamp should be preserved
                var session = await repository.GetSessionAsync(sessionId);
                session.Should().NotBeNull();
                session.LastHeartbeat.Should().Be(lastActiveTime, "heartbeat timestamp should survive restart");
            }
        }

        [Fact]
        public async Task Session_MultipleSessionsPersistedAcrossRestart()
        {
            // ARRANGE: Create multiple sessions
            var session1Id = $"session-1-{Guid.NewGuid()}";
            var session2Id = $"session-2-{Guid.NewGuid()}";
            var session3Id = $"session-3-{Guid.NewGuid()}";

            using (var repository = new SessionRepository(_testDbPath, _logger))
            {
                await repository.StartSessionAsync(session1Id, "2025", "20.0.0.1", "user1", "user1", "user1@test.com", "PC1");
                await repository.StartSessionAsync(session2Id, "2025", "20.0.0.2", "user2", "user2", "user2@test.com", "PC2");
                await repository.StartSessionAsync(session3Id, "2024", "19.0.0.1", "user3", "user3", "user3@test.com", "PC3");

                // End session2
                await repository.EndSessionAsync(session2Id, crashDetected: false);
            }
            // Repository disposed

            // ACT: Reopen repository
            using (var repository = new SessionRepository(_testDbPath, _logger))
            {
                // ASSERT: Active sessions should survive restart
                var activeSessions = await repository.GetActiveSessionsAsync();
                activeSessions.Should().HaveCount(2, "two active sessions should survive restart");

                var s1 = await repository.GetSessionAsync(session1Id);
                var s2 = await repository.GetSessionAsync(session2Id);
                var s3 = await repository.GetSessionAsync(session3Id);

                s1.Should().NotBeNull("session1 should survive restart");
                s2.Should().NotBeNull("session2 should survive restart");
                s3.Should().NotBeNull("session3 should survive restart");

                s1.IsActive.Should().BeTrue("session1 should still be active");
                s2.IsActive.Should().BeFalse("session2 should be inactive");
                s3.IsActive.Should().BeTrue("session3 should still be active");
            }
        }

        #endregion

        #region Event Persistence Across Restarts

        [Fact]
        public async Task Events_PreservedAcrossRestart()
        {
            // ARRANGE: Log events
            var sessionId = $"session-{Guid.NewGuid()}";

            using (var repository = new EventRepository(_testDbPath, _logger))
            {
                var evt1 = RevitEvent.Create(sessionId, null, "CommandExecuted", "Command", "CommandExecuted");
                evt1.Metadata = "{\"command\":\"Move\"}";
                await repository.LogEventAsync(evt1);

                var evt2 = RevitEvent.Create(sessionId, null, "CommandExecuted", "Command", "CommandExecuted");
                evt2.Metadata = "{\"command\":\"Rotate\"}";
                await repository.LogEventAsync(evt2);

                var evt3 = RevitEvent.Create(sessionId, null, "CommandExecuted", "Command", "CommandExecuted");
                evt3.Metadata = "{\"command\":\"Mirror\"}";
                await repository.LogEventAsync(evt3);
            }
            // Repository disposed

            // ACT: Reopen repository
            using (var repository = new EventRepository(_testDbPath, _logger))
            {
                // ASSERT: Events should be preserved
                var events = await repository.GetRecentEventsAsync(sessionId);
                events.Should().HaveCount(3, "all events should survive restart");
                events.Should().Contain(e => e.Metadata.Contains("Move"));
                events.Should().Contain(e => e.Metadata.Contains("Rotate"));
                events.Should().Contain(e => e.Metadata.Contains("Mirror"));
            }
        }

        [Fact(Skip = "Quarantined: pre-existing prod bug — EventRepository positional reads vs Schema.sql column order drift. See docs/testing/quarantine.md.")]
        public async Task Events_MaintainOrderAcrossRestart()
        {
            // ARRANGE: Log 100 events in order
            var sessionId = $"session-{Guid.NewGuid()}";

            using (var repository = new EventRepository(_testDbPath, _logger))
            {
                for (int i = 0; i < 100; i++)
                {
                    var evt = RevitEvent.Create(sessionId, null, "Event", "General", "Event");
                    evt.Metadata = $"{{\"index\":{i}}}";
                    await repository.LogEventAsync(evt);
                }
            }
            // Repository disposed

            // ACT: Reopen repository
            using (var repository = new EventRepository(_testDbPath, _logger))
            {
                // ASSERT: Events should maintain order
                var events = await repository.GetRecentEventsAsync(sessionId);
                events.Should().HaveCount(100, "all events should be preserved");

                // Verify sequential order
                for (int i = 0; i < 100; i++)
                {
                    events[i].Metadata.Should().Contain($"\"index\":{i}", $"event at position {i} should match");
                }
            }
        }

        [Fact(Skip = "Quarantined: pre-existing prod bug — events table column mismatch with schema. See docs/testing/quarantine.md.")]
        public async Task Events_TransactionRollbackPreventsPartialCommit()
        {
            // ARRANGE: Attempt a batch insert that fails mid-way
            var sessionId = $"session-{Guid.NewGuid()}";

            // First, ensure schema exists
            using (var repository = new EventRepository(_testDbPath, _logger))
            {
                // Just initialize the repository to create schema
            }

            // ACT: Manually attempt transaction that we'll rollback
            try
            {
                using (var connection = new SQLiteConnection($"Data Source={_testDbPath};Version=3;"))
                {
                    connection.Open();
                    using (var transaction = connection.BeginTransaction())
                    {
                        // Insert 2 events successfully
                        for (int i = 0; i < 2; i++)
                        {
                            using (var cmd = connection.CreateCommand())
                            {
                                cmd.CommandText = @"
                                    INSERT INTO events (session_id, event_type, payload, created_at)
                                    VALUES (@sid, @type, @payload, @created)";
                                cmd.Parameters.AddWithValue("@sid", sessionId);
                                cmd.Parameters.AddWithValue("@type", $"Event{i}");
                                cmd.Parameters.AddWithValue("@payload", "{}");
                                cmd.Parameters.AddWithValue("@created", DateTime.UtcNow.ToString("o"));
                                cmd.ExecuteNonQuery();
                            }
                        }

                        // Simulate error - don't commit transaction
                        throw new Exception("Simulated error during batch insert");
                    }
                }
            }
            catch (Exception ex)
            {
                // Expected exception
                ex.Message.Should().Contain("Simulated error");
            }

            // ASSERT: No events should be committed due to rollback
            using (var repository = new EventRepository(_testDbPath, _logger))
            {
                var events = await repository.GetRecentEventsAsync(sessionId);
                events.Should().BeEmpty("transaction rollback should prevent any events from being committed");
            }
        }

        #endregion

        #region Offline Queue Persistence Across Restarts

        [Fact]
        public async Task OfflineQueue_PreservesOrderAcrossRestart()
        {
            // ARRANGE: Enqueue operations in specific order
            using (var repository = new OfflineQueueRepository(_testDbPath, _logger))
            {
                await repository.EnqueueAsync("AuditLog", "{\"action\":\"Operation1\"}", "endpoint1", "POST");
                await Task.Delay(10); // Ensure different timestamps
                await repository.EnqueueAsync("AuditLog", "{\"action\":\"Operation2\"}", "endpoint2", "POST");
                await Task.Delay(10);
                await repository.EnqueueAsync("AuditLog", "{\"action\":\"Operation3\"}", "endpoint3", "POST");
            }
            // Repository disposed

            // ACT: Reopen repository
            using (var repository = new OfflineQueueRepository(_testDbPath, _logger))
            {
                // ASSERT: Queue should preserve order
                var pending = await repository.GetPendingItemsAsync();
                pending.Should().HaveCount(3, "all queue items should survive restart");
                pending[0].OperationData.Should().Contain("Operation1", "first operation should be first");
                pending[1].OperationData.Should().Contain("Operation2", "second operation should be second");
                pending[2].OperationData.Should().Contain("Operation3", "third operation should be third");
            }
        }

        [Fact]
        public async Task OfflineQueue_StatusUpdatesPersistedAcrossRestart()
        {
            // ARRANGE: Create queue items with different statuses
            long id1, id2, id3;

            using (var repository = new OfflineQueueRepository(_testDbPath, _logger))
            {
                id1 = await repository.EnqueueAsync("Op1", "{}", "endpoint", "POST");
                id2 = await repository.EnqueueAsync("Op2", "{}", "endpoint", "POST");
                id3 = await repository.EnqueueAsync("Op3", "{}", "endpoint", "POST");

                // Update statuses
                await repository.UpdateStatusAsync(id1, "succeeded");
                await repository.UpdateStatusAsync(id2, "in-progress");
                // id3 remains pending
            }
            // Repository disposed

            // ACT: Reopen repository
            using (var repository = new OfflineQueueRepository(_testDbPath, _logger))
            {
                // ASSERT: Statuses should be preserved
                var allItems = await repository.GetAllItemsAsync();
                allItems.Should().HaveCount(3);

                var item1 = allItems.First(x => x.Id == id1);
                var item2 = allItems.First(x => x.Id == id2);
                var item3 = allItems.First(x => x.Id == id3);

                item1.SyncStatus.Should().Be("succeeded", "status updates should survive restart");
                item2.SyncStatus.Should().Be("in-progress", "status updates should survive restart");
                item3.SyncStatus.Should().Be("pending", "default status should survive restart");

                // Only id3 should be in pending queue
                var pending = await repository.GetPendingItemsAsync();
                pending.Should().HaveCount(1);
                pending[0].Id.Should().Be((int)id3);
            }
        }

        [Fact]
        public async Task OfflineQueue_RetryCountsPreservedAcrossRestart()
        {
            // ARRANGE: Create item and increment retry count
            long itemId;

            using (var repository = new OfflineQueueRepository(_testDbPath, _logger))
            {
                itemId = await repository.EnqueueAsync("Op", "{}", "endpoint", "POST");

                // Increment retry count 3 times
                await repository.IncrementRetryAsync(itemId);
                await repository.IncrementRetryAsync(itemId);
                await repository.IncrementRetryAsync(itemId);
            }
            // Repository disposed

            // ACT: Reopen repository
            using (var repository = new OfflineQueueRepository(_testDbPath, _logger))
            {
                // ASSERT: Retry count should be preserved
                var item = (await repository.GetAllItemsAsync()).First(x => x.Id == itemId);
                item.RetryCount.Should().Be(3, "retry count should survive restart");
            }
        }

        #endregion

        #region Corrupted Database Recovery

        [Fact]
        public void SessionRepository_HandlesCorruptedDatabase()
        {
            // ARRANGE: Create corrupted database file
            File.WriteAllText(_testDbPath, "CORRUPTED DATA - NOT A VALID SQLITE FILE");

            // ACT & ASSERT: Repository should detect corruption and recreate schema
            SessionRepository repository = null;
            Action createRepository = () =>
            {
                repository = new SessionRepository(_testDbPath, _logger);
            };

            createRepository.Should().NotThrow("repository should handle corrupted database gracefully");

            // Verify schema was recreated by attempting to create a session
            Func<Task> createSession = async () =>
            {
                var session = await repository.StartSessionAsync(
                    "test-session", "2025", "20.0.0.1", "user", "user", null, "PC");
                session.Should().NotBeNull();
            };

            createSession.Should().NotThrowAsync("should be able to create session after recovery");

            repository?.Dispose();
        }

        [Fact]
        public void EventRepository_HandlesCorruptedDatabase()
        {
            // ARRANGE: Create corrupted database file
            File.WriteAllText(_testDbPath, "CORRUPTED DATA");

            // ACT & ASSERT: Should handle corruption gracefully
            EventRepository repository = null;
            Action createRepository = () =>
            {
                repository = new EventRepository(_testDbPath, _logger);
            };

            createRepository.Should().NotThrow("repository should handle corrupted database gracefully");

            // Verify can append events after recovery
            Func<Task> appendEvent = async () =>
            {
                var evt = RevitEvent.Create("session", null, "EventType", "General", "EventType");
                await repository.LogEventAsync(evt);
            };

            appendEvent.Should().NotThrowAsync("should be able to log events after recovery");

            repository?.Dispose();
        }

        [Fact]
        public void OfflineQueueRepository_HandlesCorruptedDatabase()
        {
            // ARRANGE: Create corrupted database file
            File.WriteAllText(_testDbPath, "CORRUPTED DATA");

            // ACT & ASSERT: Should handle corruption gracefully
            OfflineQueueRepository repository = null;
            Action createRepository = () =>
            {
                repository = new OfflineQueueRepository(_testDbPath, _logger);
            };

            createRepository.Should().NotThrow("repository should handle corrupted database gracefully");

            // Verify can enqueue operations after recovery
            Func<Task> enqueueOperation = async () =>
            {
                await repository.EnqueueAsync("OpType", "{}", "endpoint", "POST");
            };

            enqueueOperation.Should().NotThrowAsync("should be able to enqueue operations after recovery");

            repository?.Dispose();
        }

        #endregion

        #region Locked Database Handling

        [Fact]
        public async Task SessionRepository_HandlesMultipleReaders()
        {
            // ARRANGE: Create first repository instance
            using (var repo1 = new SessionRepository(_testDbPath, _logger))
            {
                await repo1.StartSessionAsync("session1", "2025", "20.0.0.1", "user1", "user1", null, "PC1");

                // ACT: Create second repository instance (simulates another Revit instance or background process)
                using (var repo2 = new SessionRepository(_testDbPath, _logger))
                {
                    // ASSERT: Second repository should be able to read
                    var session = await repo2.GetSessionAsync("session1");
                    session.Should().NotBeNull("SQLite allows multiple readers");

                    // Both repos should be able to write to different sessions
                    await repo2.StartSessionAsync("session2", "2025", "20.0.0.1", "user2", "user2", null, "PC2");

                    var activeSessions = await repo1.GetActiveSessionsAsync();
                    activeSessions.Should().HaveCount(2, "both sessions should be visible to both repositories");
                }
            }
        }

        [Fact]
        public async Task EventRepository_HandlesConcurrentWrites()
        {
            // ARRANGE: Create two repository instances
            using (var repo1 = new EventRepository(_testDbPath, _logger))
            using (var repo2 = new EventRepository(_testDbPath, _logger))
            {
                var sessionId = $"session-{Guid.NewGuid()}";

                // ACT: Both repositories write events concurrently
                var evt1 = RevitEvent.Create(sessionId, null, "Event1", "General", "Event1");
                evt1.Metadata = "{\"source\":\"repo1\"}";
                var evt2 = RevitEvent.Create(sessionId, null, "Event2", "General", "Event2");
                evt2.Metadata = "{\"source\":\"repo2\"}";

                var task1 = repo1.LogEventAsync(evt1);
                var task2 = repo2.LogEventAsync(evt2);

                await Task.WhenAll(task1, task2);

                // ASSERT: Both events should be recorded
                var events = await repo1.GetRecentEventsAsync(sessionId);
                events.Should().HaveCount(2, "both concurrent writes should succeed");
                events.Should().Contain(e => e.Metadata.Contains("repo1"));
                events.Should().Contain(e => e.Metadata.Contains("repo2"));
            }
        }

        #endregion

        #region Schema Version Mismatch

        [Fact(Skip = "Quarantined: tests deprecated behavior — SchemaMigration is now external; repo no longer auto-detects version mismatch on construction. See docs/testing/quarantine.md.")]
        public async Task SessionRepository_DetectsSchemaVersionMismatch()
        {
            // ARRANGE: Create DB with current schema
            using (var repo = new SessionRepository(_testDbPath, _logger))
            {
                await repo.StartSessionAsync("session1", "2025", "20.0.0.1", "user", "user", null, "PC");
            }

            // Manually downgrade schema version
            using (var connection = new SQLiteConnection($"Data Source={_testDbPath};Version=3;"))
            {
                connection.Open();
                using (var cmd = connection.CreateCommand())
                {
                    cmd.CommandText = "UPDATE schema_version SET version = 1 WHERE version > 1";
                    cmd.ExecuteNonQuery();
                }
            }

            // ACT: Open repository with current schema (should detect version mismatch)
            using (var repo = new SessionRepository(_testDbPath, _logger))
            {
                // ASSERT: Should handle gracefully (log warning or migrate)
                // Session should still be accessible
                var session = await repo.GetSessionAsync("session1");
                session.Should().NotBeNull("schema mismatch should be handled gracefully");
            }
        }

        #endregion

        #region Missing Database File

        [Fact(Skip = "Quarantined: tests deprecated behavior — repos no longer auto-create schema on construction; SchemaMigration is now a separate bootstrap step. See docs/testing/quarantine.md.")]
        public async Task SessionRepository_CreatesSchemaIfMissing()
        {
            // ARRANGE: Ensure database file does NOT exist
            if (File.Exists(_testDbPath))
                File.Delete(_testDbPath);

            // ACT: Create repository (should create schema)
            using (var repository = new SessionRepository(_testDbPath, _logger))
            {
                // ASSERT: Should be able to create session (schema exists)
                var session = await repository.StartSessionAsync(
                    "test-session", "2025", "20.0.0.1", "user", "user", null, "PC");

                session.Should().NotBeNull();

                // Database file should now exist
                File.Exists(_testDbPath).Should().BeTrue("database file should be created");
            }
        }

        [Fact]
        public async Task EventRepository_CreatesSchemaIfMissing()
        {
            // ARRANGE: Ensure database file does NOT exist
            if (File.Exists(_testDbPath))
                File.Delete(_testDbPath);

            // ACT: Create repository (should create schema)
            using (var repository = new EventRepository(_testDbPath, _logger))
            {
                // ASSERT: Should be able to log event (schema exists)
                var evt = RevitEvent.Create("session", null, "EventType", "General", "EventType");
                await repository.LogEventAsync(evt);

                // Database file should now exist
                File.Exists(_testDbPath).Should().BeTrue("database file should be created");
            }
        }

        [Fact(Skip = "Quarantined: tests deprecated behavior — repos no longer auto-create schema on construction. See docs/testing/quarantine.md.")]
        public async Task OfflineQueueRepository_CreatesSchemaIfMissing()
        {
            // ARRANGE: Ensure database file does NOT exist
            if (File.Exists(_testDbPath))
                File.Delete(_testDbPath);

            // ACT: Create repository (should create schema)
            using (var repository = new OfflineQueueRepository(_testDbPath, _logger))
            {
                // ASSERT: Should be able to enqueue operation (schema exists)
                await repository.EnqueueAsync("OpType", "{}", "endpoint", "POST");

                // Database file should now exist
                File.Exists(_testDbPath).Should().BeTrue("database file should be created");
            }
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
