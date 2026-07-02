using System;

namespace BIManage.Revit.Productivity
{
    /// <summary>
    ///     Tracks user productivity metrics (active time, idle time)
    /// </summary>
    public interface IProductivityTracker
    {
        /// <summary>
        ///     Record user activity (view change, command execution, etc.)
        /// </summary>
        void RecordActivity();

        /// <summary>
        ///     Get total active time in current session
        /// </summary>
        TimeSpan GetActiveTime();

        /// <summary>
        ///     Get total idle time in current session
        /// </summary>
        TimeSpan GetIdleTime();

        /// <summary>
        ///     Get total session time
        /// </summary>
        TimeSpan GetTotalTime();

        /// <summary>
        ///     Check if user is currently idle (no activity for threshold duration)
        /// </summary>
        bool IsCurrentlyIdle { get; }

        /// <summary>
        ///     Reset session tracking
        /// </summary>
        void ResetSession();

        /// <summary>
        ///     Get time since last mouse/keyboard input at the OS level (Win32 GetLastInputInfo).
        /// </summary>
        TimeSpan GetTimeSinceLastSystemInput();
    }
}
