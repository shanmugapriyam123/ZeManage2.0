using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BIManage.Core.Evidence;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.PinProtection
{
    /// <summary>
    /// Tracks elements that were unpinned via OTP authorization for after-screenshot capture.
    /// When any tracked element is modified (moved, deleted, etc.), captures an "after" screenshot.
    /// </summary>
    public class UnpinnedElementTracker
    {
        private readonly ILogger? _logger;
        private readonly Func<IScreenshotService?>? _screenshotServiceGetter;
        private readonly Func<EvidenceRepository?>? _evidenceRepositoryGetter;

        // Thread-safe dictionary: ElementId -> TrackingInfo
        private readonly ConcurrentDictionary<long, ElementTrackingInfo> _trackedElements = new();

        public UnpinnedElementTracker(
            ILogger? logger,
            Func<IScreenshotService?>? screenshotServiceGetter = null,
            Func<EvidenceRepository?>? evidenceRepositoryGetter = null)
        {
            _logger = logger;
            _screenshotServiceGetter = screenshotServiceGetter;
            _evidenceRepositoryGetter = evidenceRepositoryGetter;
        }

        /// <summary>
        /// Register elements for tracking after OTP-authorized unpin.
        /// Also records the "before" evidence capture to the database.
        /// </summary>
        /// <param name="elementIds">Element IDs to track</param>
        /// <param name="userEventData">UserEventData containing before screenshot and metadata</param>
        public void TrackElements(IEnumerable<long> elementIds, UserEventData userEventData)
        {
            var info = new ElementTrackingInfo
            {
                EventData = userEventData,
                TrackedAt = DateTime.UtcNow
            };

            foreach (var elementId in elementIds)
            {
                if (_trackedElements.TryAdd(elementId, info))
                {
                    _logger?.LogDebug($"UnpinnedElementTracker: Now tracking element {elementId} (auditLogId: {userEventData.AuditLogId})");
                }
            }

            _logger?.LogInfo($"UnpinnedElementTracker: Tracking {elementIds.Count()} elements for after-screenshot");

            // Record "before" evidence capture to database
            RecordEvidenceCapture(userEventData, "before");
        }

        /// <summary>
        /// Check if any tracked elements were modified and capture after screenshot.
        /// Call this from DocumentChanged event handler.
        /// Skips the first modification (unpin itself) and only captures on subsequent actions (move, delete, etc.)
        /// Returns true if screenshot was captured.
        /// </summary>
        public bool CheckForModifications(IEnumerable<long> modifiedElementIds, IEnumerable<long> deletedElementIds)
        {
            if (_trackedElements.IsEmpty)
                return false;

            // Combine modified and deleted into one set
            var changedIds = new HashSet<long>(modifiedElementIds);
            foreach (var id in deletedElementIds)
                changedIds.Add(id);

            if (changedIds.Count == 0)
                return false;

            // Find any tracked elements that were changed
            var matchedElements = new List<(long ElementId, ElementTrackingInfo Info)>();

            foreach (var changedId in changedIds)
            {
                if (_trackedElements.TryGetValue(changedId, out var info))
                {
                    matchedElements.Add((changedId, info));
                }
            }

            if (matchedElements.Count == 0)
                return false;

            // Check if this is the first modification (the unpin itself) - skip it
            var firstMatch = matchedElements.First();
            if (firstMatch.Info.SkipNextModification)
            {
                // Clear the skip flag for all matched elements - next modification will capture
                foreach (var match in matchedElements)
                {
                    match.Info.SkipNextModification = false;
                    _logger?.LogDebug($"UnpinnedElementTracker: Skipping unpin modification for element {match.ElementId}, waiting for subsequent action (move, delete, etc.)");
                }
                return false;
            }

            // This is a subsequent modification (move, delete, rotate, etc.) - capture the after screenshot
            var eventData = firstMatch.Info.EventData;
            if (eventData == null)
            {
                _logger?.LogWarning("UnpinnedElementTracker: EventData is null, cannot capture after screenshot");
                return false;
            }

            _logger?.LogInfo($"UnpinnedElementTracker: Detected subsequent modification (move/delete/etc.) of {matchedElements.Count} tracked element(s), capturing after screenshot");

            // Capture after screenshot and store in UserEventData
            bool captured = CaptureAfterScreenshot(eventData);

            // Remove all matched elements from tracking (only capture first real change after unpin)
            foreach (var match in matchedElements)
            {
                _trackedElements.TryRemove(match.ElementId, out _);
                _logger?.LogDebug($"UnpinnedElementTracker: Stopped tracking element {match.ElementId}");
            }

            return captured;
        }

        /// <summary>
        /// Capture the after screenshot and store in UserEventData.
        /// Also saves to temp file for debugging and records to database.
        /// </summary>
        private bool CaptureAfterScreenshot(UserEventData eventData)
        {
            var screenshotService = _screenshotServiceGetter?.Invoke();
            if (screenshotService == null)
            {
                _logger?.LogWarning("UnpinnedElementTracker: Screenshot service not available");
                return false;
            }

            try
            {
                // Capture as Base64 string (Win32 PrintWindow → PNG → Base64)
                var afterImageBase64 = screenshotService.CaptureRevitWindowAsBase64();

                if (!string.IsNullOrEmpty(afterImageBase64))
                {
                    // Store in UserEventData
                    eventData.AfterImageBytes = afterImageBase64;
                    eventData.CompletedAt = DateTime.UtcNow;
                    eventData.Status = "Completed";

                    _logger?.LogInfo($"UnpinnedElementTracker: Captured after screenshot as Base64 ({afterImageBase64.Length} chars)");
                    _logger?.LogInfo($"UserEventData completed: Before={eventData.BeforeImageBytes?.Length ?? 0} chars, After={afterImageBase64.Length} chars");

                    // Save to temp file for debugging (always save for now, future: check developer mode)
                    SaveToTempForDebugging(eventData, screenshotService);

                    // Record "after" evidence capture to database
                    RecordEvidenceCapture(eventData, "after");

                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"UnpinnedElementTracker: Failed to capture after screenshot: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Record evidence capture to the database.
        /// </summary>
        private void RecordEvidenceCapture(UserEventData eventData, string stage)
        {
            var evidenceRepository = _evidenceRepositoryGetter?.Invoke();
            if (evidenceRepository == null)
            {
                _logger?.LogWarning($"UnpinnedElementTracker: Evidence repository not available, cannot record {stage} evidence");
                return;
            }

            try
            {
                var screenshotService = _screenshotServiceGetter?.Invoke();
                var imageBytes = stage == "before" ? eventData.BeforeImageBytes : eventData.AfterImageBytes;

                // Save Base64 to temp file and compute hash
                string savedPath = null;
                string fileHash = null;
                if (screenshotService != null && !string.IsNullOrEmpty(imageBytes))
                {
                    savedPath = screenshotService.SaveBase64ToTempFile(imageBytes, eventData.SessionId, $"{eventData.AuditLogId}_{stage}");
                    if (!string.IsNullOrEmpty(savedPath))
                    {
                        try
                        {
                            using (var sha256 = System.Security.Cryptography.SHA256.Create())
                            {
                                var fileBytes = System.IO.File.ReadAllBytes(savedPath);
                                var hashBytes = sha256.ComputeHash(fileBytes);
                                fileHash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                            }
                        }
                        catch { /* hash failure is non-fatal */ }
                    }
                }

                var fileSizeBytes = imageBytes != null ? (long?)(imageBytes.Length * 3 / 4) : null; // Approximate decoded size

                var evidence = new EvidenceRepository.EvidenceCapture
                {
                    EvidenceId = Guid.NewGuid().ToString(),
                    AuditLogId = eventData.AuditLogId,
                    SessionId = eventData.SessionId,
                    ProtectionType = "pin",
                    CaptureType = "screenshot",
                    CaptureStage = stage,
                    CapturedAt = stage == "before" ? eventData.CreatedAt : (eventData.CompletedAt ?? DateTime.UtcNow),
                    CommandId = "pin_protection",
                    CommandName = eventData.CommandName,
                    ElementIds = string.Join(",", eventData.ElementIds),
                    ElementCount = eventData.ElementCount,
                    FilePath = savedPath,
                    FileSizeBytes = fileSizeBytes,
                    FileFormat = "png",
                    FileHashSha256 = fileHash,
                    UploadStatus = "pending",
                    Metadata = $"{{\"username\":\"{eventData.Username}\",\"isAdmin\":{eventData.IsAdmin.ToString().ToLower()},\"otpUsed\":\"{eventData.OtpCodeUsed ?? ""}\",\"parentEvidenceId\":\"{eventData.AuditLogId}\"}}"
                };

                // Fire and forget - don't block the UI thread
                _ = evidenceRepository.RecordEvidenceCaptureAsync(evidence).ContinueWith(t =>
                {
                    if (t.IsFaulted)
                    {
                        _logger?.LogWarning($"Failed to record {stage} evidence: {t.Exception?.InnerException?.Message}");
                    }
                    else
                    {
                        _logger?.LogInfo($"Recorded {stage} evidence capture to database: {evidence.EvidenceId}");
                    }
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to record {stage} evidence to database: {ex.Message}");
            }
        }

        /// <summary>
        /// Save before and after screenshots to temp folder for debugging.
        /// </summary>
        private void SaveToTempForDebugging(UserEventData eventData, IScreenshotService screenshotService)
        {
            try
            {
                // Save before screenshot if available
                if (!string.IsNullOrEmpty(eventData.BeforeImageBytes))
                {
                    var beforePath = screenshotService.SaveBase64ToTempFile(
                        eventData.BeforeImageBytes,
                        eventData.SessionId,
                        $"{eventData.AuditLogId}_before");

                    if (beforePath != null)
                    {
                        _logger?.LogDebug($"Saved before screenshot to: {beforePath}");
                    }
                }

                // Save after screenshot if available
                if (!string.IsNullOrEmpty(eventData.AfterImageBytes))
                {
                    var afterPath = screenshotService.SaveBase64ToTempFile(
                        eventData.AfterImageBytes,
                        eventData.SessionId,
                        $"{eventData.AuditLogId}_after");

                    if (afterPath != null)
                    {
                        _logger?.LogDebug($"Saved after screenshot to: {afterPath}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to save screenshots to temp: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear all tracked elements (call on session end).
        /// </summary>
        public void ClearAll()
        {
            var count = _trackedElements.Count;
            _trackedElements.Clear();

            if (count > 0)
            {
                _logger?.LogInfo($"UnpinnedElementTracker: Cleared {count} tracked element(s) on session end");
            }
        }

        /// <summary>
        /// Check if any elements are being tracked.
        /// </summary>
        public bool HasTrackedElements => !_trackedElements.IsEmpty;

        /// <summary>
        /// Get count of tracked elements.
        /// </summary>
        public int TrackedCount => _trackedElements.Count;
    }

    /// <summary>
    /// Information about a tracked unpinned element.
    /// </summary>
    public class ElementTrackingInfo
    {
        /// <summary>
        /// The UserEventData containing screenshots and metadata
        /// </summary>
        public UserEventData? EventData { get; set; }

        public DateTime TrackedAt { get; set; }

        /// <summary>
        /// Flag to skip the first modification (the unpin itself).
        /// Only capture screenshot on subsequent modifications (move, delete, etc.)
        /// </summary>
        public bool SkipNextModification { get; set; } = true;
    }
}
