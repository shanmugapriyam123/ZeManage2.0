using System;
using System.Collections.Generic;
using System.Linq;

namespace BIManage.Core.Evidence
{
    /// <summary>
    /// Screenshot throttling cache - prevents duplicate screenshots within time window
    /// Key: "{ruleId}_{commandId}_{elementIdsHash}_{stage}"
    /// Value: Timestamp of last capture
    /// </summary>
    public class ScreenshotThrottleCache
    {
        private readonly Dictionary<string, DateTime> _lastCaptureTime = new Dictionary<string, DateTime>();
        private readonly TimeSpan _throttleWindow;
        private readonly object _lock = new object();

        /// <summary>
        /// Create throttle cache with specified time window
        /// </summary>
        /// <param name="throttleWindowSeconds">Throttle window in seconds (default: 5)</param>
        public ScreenshotThrottleCache(int throttleWindowSeconds = 5)
        {
            _throttleWindow = TimeSpan.FromSeconds(throttleWindowSeconds);
        }

        /// <summary>
        /// Check if screenshot should be captured based on throttle rules
        /// </summary>
        /// <param name="ruleId">Rule ID that triggered capture</param>
        /// <param name="commandId">Command ID that was executed</param>
        /// <param name="elementIds">Comma-separated element IDs</param>
        /// <param name="stage">Capture stage (before/after)</param>
        /// <returns>True if screenshot should be captured, false if throttled</returns>
        public bool ShouldCapture(string ruleId, int commandId, string elementIds, string stage)
        {
            lock (_lock)
            {
                // Create cache key
                var elementHash = GetStableHash(elementIds ?? string.Empty);
                var cacheKey = $"{ruleId}_{commandId}_{elementHash}_{stage}";

                // Check if we captured this recently
                if (_lastCaptureTime.TryGetValue(cacheKey, out var lastCapture))
                {
                    var elapsed = DateTime.Now - lastCapture;
                    if (elapsed < _throttleWindow)
                    {
                        return false; // Throttled - too recent
                    }
                }

                // Update cache
                _lastCaptureTime[cacheKey] = DateTime.Now;

                // Cleanup old entries (older than 1 minute)
                CleanupOldEntries();

                return true;
            }
        }

        /// <summary>
        /// Get a stable hash code for element IDs (simple hash for cache key, not cryptographic)
        /// </summary>
        private string GetStableHash(string input)
        {
            // Simple hash for cache key (not cryptographic)
            return input.GetHashCode().ToString("X8");
        }

        /// <summary>
        /// Cleanup old cache entries to prevent memory leaks
        /// </summary>
        private void CleanupOldEntries()
        {
            var cutoff = DateTime.Now - TimeSpan.FromMinutes(1);
            var keysToRemove = _lastCaptureTime
                .Where(kvp => kvp.Value < cutoff)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _lastCaptureTime.Remove(key);
            }
        }

        /// <summary>
        /// Clear all cache entries (for testing or session reset)
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _lastCaptureTime.Clear();
            }
        }

        /// <summary>
        /// Get current cache size (for diagnostics)
        /// </summary>
        public int GetCacheSize()
        {
            lock (_lock)
            {
                return _lastCaptureTime.Count;
            }
        }
    }
}
