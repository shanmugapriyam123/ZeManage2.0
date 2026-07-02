using System.Diagnostics;

namespace BIManage.Revit.Timing
{
    /// <summary>
    /// Tracks cumulative time spent on modal dialogs during Revit startup.
    /// Used to subtract user idle time (waiting on plugin warnings, license prompts, etc.)
    /// from the session opening duration, giving a more accurate measure of pure Revit load time.
    ///
    /// Usage:
    ///   OnStartup: subscribe UIControlledApplication.DialogBoxShowing → call OnDialogShowing()
    ///   Idling tick or ApplicationInitialized: call OnDialogDismissed() (dialog is gone)
    ///   UpdateSessionReadyAsync: subtract TotalDialogSeconds from raw opening duration
    /// </summary>
    public static class StartupDialogTracker
    {
        private static readonly Stopwatch _dialogStopwatch = new Stopwatch();
        private static double _totalDialogSeconds;
        private static bool _dialogActive;
        private static bool _trackingComplete;

        /// <summary>
        /// Call when a Revit dialog appears (DialogBoxShowing event).
        /// Starts timing if not already tracking a dialog.
        /// </summary>
        public static void OnDialogShowing()
        {
            if (_trackingComplete) return;

            if (!_dialogActive)
            {
                _dialogActive = true;
                _dialogStopwatch.Restart();
            }
        }

        /// <summary>
        /// Call when a dialog is dismissed (first Idling tick after dialog, or ApplicationInitialized).
        /// Stops timing and accumulates the duration.
        /// </summary>
        public static void OnDialogDismissed()
        {
            if (!_dialogActive) return;

            _dialogStopwatch.Stop();
            _totalDialogSeconds += _dialogStopwatch.Elapsed.TotalSeconds;
            _dialogActive = false;
        }

        /// <summary>
        /// Total seconds spent on modal dialogs during startup.
        /// Includes any currently-active dialog time.
        /// </summary>
        public static double TotalDialogSeconds =>
            _totalDialogSeconds + (_dialogActive ? _dialogStopwatch.Elapsed.TotalSeconds : 0);

        /// <summary>
        /// Call after ApplicationInitialized to stop tracking.
        /// Any further DialogBoxShowing events are ignored (post-startup dialogs are not our concern).
        /// </summary>
        public static void StopTracking()
        {
            OnDialogDismissed(); // Flush any active dialog
            _trackingComplete = true;
        }

        /// <summary>
        /// Reset for next session (if Revit supports session restart without process restart).
        /// </summary>
        public static void Reset()
        {
            _dialogStopwatch.Reset();
            _totalDialogSeconds = 0;
            _dialogActive = false;
            _trackingComplete = false;
        }
    }
}
