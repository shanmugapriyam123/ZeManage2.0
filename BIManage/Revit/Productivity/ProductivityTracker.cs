using System;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.BackgroundSync;

namespace BIManage.Revit.Productivity
{
    /// <summary>
    ///     Lightweight productivity tracker for measuring active vs idle time
    ///     Performance-optimized: minimal allocations, simple calculations
    /// </summary>
    public class ProductivityTracker : IProductivityTracker
    {
        private readonly ILogger? _logger;
        private readonly TimeSpan _idleThreshold;

        private DateTime _sessionStart;
        private DateTime _lastActivityTime;
        private TimeSpan _accumulatedActiveTime;
        private bool _wasIdle;

        public ProductivityTracker(ILogger? logger, TimeSpan? idleThreshold = null)
        {
            _logger = logger;
            _idleThreshold = idleThreshold ?? TimeSpan.FromMinutes(5); // Default: 5 min idle threshold
            ResetSession();
        }

        public void RecordActivity()
        {
            var now = DateTime.UtcNow;

            // If transitioning from idle to active, don't count idle time as active
            if (_wasIdle)
            {
                _wasIdle = false;
                _lastActivityTime = now;
                _logger?.LogDebug($"User returned from idle at {now:HH:mm:ss}");
                return;
            }

            // Accumulate active time since last activity
            var timeSinceLastActivity = now - _lastActivityTime;
            if (timeSinceLastActivity < _idleThreshold)
            {
                _accumulatedActiveTime += timeSinceLastActivity;
            }

            _lastActivityTime = now;
        }

        public TimeSpan GetActiveTime()
        {
            // If currently active, include time since last activity
            if (!IsCurrentlyIdle)
            {
                var currentActiveSegment = DateTime.UtcNow - _lastActivityTime;
                return _accumulatedActiveTime + currentActiveSegment;
            }

            return _accumulatedActiveTime;
        }

        public TimeSpan GetIdleTime()
        {
            var totalTime = GetTotalTime();
            var activeTime = GetActiveTime();
            return totalTime - activeTime;
        }

        public TimeSpan GetTotalTime()
        {
            return DateTime.UtcNow - _sessionStart;
        }

        public bool IsCurrentlyIdle
        {
            get
            {
                var timeSinceLastActivity = DateTime.UtcNow - _lastActivityTime;
                var isIdle = timeSinceLastActivity >= _idleThreshold;

                // Log transition to idle (only once)
                if (isIdle && !_wasIdle)
                {
                    _wasIdle = true;
                    _logger?.LogDebug($"User became idle at {DateTime.UtcNow:HH:mm:ss} (no activity for {_idleThreshold.TotalMinutes:F1} min)");
                }

                return isIdle;
            }
        }

        public void ResetSession()
        {
            _sessionStart = DateTime.UtcNow;
            _lastActivityTime = _sessionStart;
            _accumulatedActiveTime = TimeSpan.Zero;
            _wasIdle = false;
            _logger?.LogInfo($"Productivity tracking session started at {_sessionStart:HH:mm:ss}");
        }

        /// <summary>
        /// Returns the time since last mouse/keyboard input at the OS level using Win32 GetLastInputInfo.
        /// More accurate than the DateTime-based IsCurrentlyIdle which requires explicit RecordActivity() calls.
        /// </summary>
        public TimeSpan GetTimeSinceLastSystemInput()
        {
            return RevitInteropHelper.GetTimeSinceLastInput();
        }
    }
}
