using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Linq;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;

namespace BIManage.Core.Detection
{
    /// <summary>
    /// Detects Revit users working on workshared models without BIManage installed.
    /// Uses workset ownership and element checkout info to find usernames not in active sessions.
    /// Throttled to run every 5 minutes during Revit idle to minimize performance impact.
    /// </summary>
    public class UnmonitoredUserDetectionService
    {
        private readonly SessionRepository _sessionRepository;
        private readonly string _connectionString;
        private readonly ILogger? _logger;
        private SessionSyncService? _sessionSyncService;

        private DateTime _lastScanTime = DateTime.MinValue;
        private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(5);

        /// <summary>Max elements to sample for WorksharingTooltipInfo per scan.</summary>
        private const int ElementSampleSize = 200;

        /// <summary>Detections older than this are treated as stale and excluded from the UI.</summary>
        private static readonly TimeSpan StaleAfter = TimeSpan.FromHours(72);

        public UnmonitoredUserDetectionService(
            SessionRepository sessionRepository,
            string databasePath,
            ILogger? logger = null)
        {
            _sessionRepository = sessionRepository ?? throw new ArgumentNullException(nameof(sessionRepository));
            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;
        }

        /// <summary>
        /// Sets the sync service for API reporting. Called after API services are registered.
        /// </summary>
        public void SetSyncService(SessionSyncService syncService)
        {
            _sessionSyncService = syncService;
        }

        /// <summary>
        /// Runs the detection scan if the throttle interval has elapsed.
        /// Must be called from the Revit main thread (accesses Document API).
        /// </summary>
        /// <returns>True if a scan was performed, false if throttled.</returns>
        public async Task<bool> TryScanAsync(Document doc)
        {
            if (doc == null || !doc.IsWorkshared)
                return false;

            if (DateTime.UtcNow - _lastScanTime < ScanInterval)
                return false;

            _lastScanTime = DateTime.UtcNow;

            try
            {
                var modelGuid = ModelGuidHelper.GetModelGuid(doc, _logger);
                if (string.IsNullOrEmpty(modelGuid))
                    return false;

                // Get the current Revit username (our own user)
                var currentRevitUser = doc.Application?.Username;

                // Step 1: Collect all Revit usernames visible in worksharing data
                var detectedUsers = new Dictionary<string, string>(); // username -> detection_source

                ScanWorksetOwners(doc, detectedUsers);
                ScanElementCheckouts(doc, detectedUsers);

                if (detectedUsers.Count == 0)
                    return true; // No external users detected

                // Step 2: Get known BIManage users for this model
                var knownUsers = await _sessionRepository.GetActiveUsersByModelGuidAsync(modelGuid);
                var knownRevitUsernames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var user in knownUsers)
                {
                    if (!string.IsNullOrEmpty(user.RevitUsername))
                        knownRevitUsernames.Add(user.RevitUsername);
                    if (!string.IsNullOrEmpty(user.Username))
                        knownRevitUsernames.Add(user.Username);
                }

                // Also exclude our own username
                if (!string.IsNullOrEmpty(currentRevitUser))
                    knownRevitUsernames.Add(currentRevitUser);

                // Step 3: Filter to unmonitored users
                var unmonitoredUsers = detectedUsers
                    .Where(kv => !knownRevitUsernames.Contains(kv.Key))
                    .ToList();

                if (unmonitoredUsers.Count > 0)
                {
                    _logger?.LogWarning($"Detected {unmonitoredUsers.Count} unmonitored user(s) on model {modelGuid}: " +
                        string.Join(", ", unmonitoredUsers.Select(u => u.Key)));

                    // Step 4: Store detections locally
                    await SaveDetectionsAsync(modelGuid, unmonitoredUsers);

                    // Step 5: Report to API
                    if (_sessionSyncService != null)
                    {
                        var modelName = System.IO.Path.GetFileNameWithoutExtension(doc.Title ?? doc.PathName ?? modelGuid);
                        var modifiedBy = currentRevitUser ?? Environment.UserName;

                        var reports = unmonitoredUsers.Select(u => new UnmonitoredUserReport
                        {
                            Username = u.Key,
                            ModelName = modelName,
                            ModifiedBy = modifiedBy
                        }).ToList();

                        await _sessionSyncService.ReportUnmonitoredUsersAsync(reports);
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Unmonitored user detection scan failed: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Scans all user worksets for their Owner property.
        /// </summary>
        private void ScanWorksetOwners(Document doc, Dictionary<string, string> detectedUsers)
        {
            try
            {
                var collector = new FilteredWorksetCollector(doc);
                foreach (Workset ws in collector.OfKind(WorksetKind.UserWorkset))
                {
                    if (!string.IsNullOrEmpty(ws.Owner) && !detectedUsers.ContainsKey(ws.Owner))
                    {
                        detectedUsers[ws.Owner] = "workset_owner";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Workset owner scan failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Samples recently modified elements for checkout/ownership info.
        /// Uses a FilteredElementCollector with WhereElementIsNotElementType for efficiency.
        /// </summary>
        private void ScanElementCheckouts(Document doc, Dictionary<string, string> detectedUsers)
        {
            try
            {
                // Sample elements: get a subset of model elements (not types, not views)
                var collector = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .WhereElementIsViewIndependent();

                int scanned = 0;
                foreach (var element in collector)
                {
                    if (scanned >= ElementSampleSize) break;

                    try
                    {
                        var status = WorksharingUtils.GetCheckoutStatus(doc, element.Id);
                        if (status == CheckoutStatus.OwnedByOtherUser)
                        {
                            var info = WorksharingUtils.GetWorksharingTooltipInfo(doc, element.Id);

                            if (!string.IsNullOrEmpty(info.Owner) && !detectedUsers.ContainsKey(info.Owner))
                            {
                                detectedUsers[info.Owner] = "element_checkout";
                            }

                            if (!string.IsNullOrEmpty(info.LastChangedBy) && !detectedUsers.ContainsKey(info.LastChangedBy))
                            {
                                detectedUsers[info.LastChangedBy] = "last_changed_by";
                            }
                        }
                    }
                    catch
                    {
                        // Skip elements that can't be queried
                    }

                    scanned++;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Element checkout scan failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves unmonitored user detections to the local SQLite database.
        /// Uses INSERT OR REPLACE to update the detected_at timestamp on subsequent detections.
        /// </summary>
        private async Task SaveDetectionsAsync(string modelGuid, List<KeyValuePair<string, string>> unmonitoredUsers)
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                foreach (var user in unmonitoredUsers)
                {
                    using var cmd = new SQLiteCommand(@"
                        INSERT INTO unmonitored_user_detections
                            (model_guid, revit_username, detection_source, detected_at, synced)
                        VALUES (@modelGuid, @username, @source, @detectedAt, 0)
                        ON CONFLICT(model_guid, revit_username, detection_source)
                        DO UPDATE SET detected_at = @detectedAt, synced = 0",
                        connection);

                    cmd.Parameters.AddWithValue("@modelGuid", modelGuid);
                    cmd.Parameters.AddWithValue("@username", user.Key);
                    cmd.Parameters.AddWithValue("@source", user.Value);
                    cmd.Parameters.AddWithValue("@detectedAt", DateTime.UtcNow.ToString("o"));

                    await cmd.ExecuteNonQueryAsync();
                }

                _logger?.LogInfo($"Saved {unmonitoredUsers.Count} unmonitored user detection(s) for model {modelGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to save unmonitored user detections: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets all unsynced detections for API reporting.
        /// </summary>
        public async Task<List<UnmonitoredUserDetection>> GetUnsyncedDetectionsAsync()
        {
            var detections = new List<UnmonitoredUserDetection>();

            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                using var cmd = new SQLiteCommand(
                    "SELECT id, model_guid, revit_username, detection_source, detected_at FROM unmonitored_user_detections WHERE synced = 0",
                    connection);

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    detections.Add(new UnmonitoredUserDetection
                    {
                        Id = reader.GetInt32(0),
                        ModelGuid = reader.GetString(1),
                        RevitUsername = reader.GetString(2),
                        DetectionSource = reader.GetString(3),
                        DetectedAt = reader.GetString(4)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get unsynced detections: {ex.Message}", ex);
            }

            return detections;
        }

        /// <summary>
        /// Gets distinct unmonitored users detected for a specific model (all, not just unsynced).
        /// Returns the latest detection per username.
        /// </summary>
        public async Task<List<UnmonitoredUserDetection>> GetDetectionsByModelAsync(string modelGuid)
        {
            var detections = new List<UnmonitoredUserDetection>();
            if (string.IsNullOrEmpty(modelGuid)) return detections;

            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                using var cmd = new SQLiteCommand(@"
                    SELECT id, model_guid, revit_username, detection_source, MAX(detected_at) as detected_at
                    FROM unmonitored_user_detections
                    WHERE model_guid = @modelGuid AND detected_at >= @cutoff
                    GROUP BY revit_username
                    ORDER BY detected_at DESC", connection);
                cmd.Parameters.AddWithValue("@modelGuid", modelGuid);
                cmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.Subtract(StaleAfter).ToString("o"));

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    detections.Add(new UnmonitoredUserDetection
                    {
                        Id = reader.GetInt32(0),
                        ModelGuid = reader.GetString(1),
                        RevitUsername = reader.GetString(2),
                        DetectionSource = reader.GetString(3),
                        DetectedAt = reader.GetString(4)
                    });
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get detections for model {modelGuid}: {ex.Message}", ex);
            }

            return detections;
        }

        /// <summary>
        /// Marks detections as synced after successful API upload.
        /// </summary>
        public async Task MarkSyncedAsync(List<int> ids)
        {
            if (ids == null || ids.Count == 0) return;

            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                await connection.OpenAsync();

                var idList = string.Join(",", ids);
                using var cmd = new SQLiteCommand(
                    $"UPDATE unmonitored_user_detections SET synced = 1 WHERE id IN ({idList})",
                    connection);

                await cmd.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to mark detections as synced: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Represents a detected unmonitored user working without BIManage.
    /// </summary>
    public class UnmonitoredUserDetection
    {
        public int Id { get; set; }
        public string ModelGuid { get; set; } = "";
        public string RevitUsername { get; set; } = "";
        public string DetectionSource { get; set; } = "";
        public string DetectedAt { get; set; } = "";
    }
}
