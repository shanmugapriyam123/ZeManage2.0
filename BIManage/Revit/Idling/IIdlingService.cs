using System;

namespace BIManage.Revit.Idling
{
    /// <summary>
    ///     Service for managing Idling event with throttle and work-based execution
    /// </summary>
    public interface IIdlingService
    {
        /// <summary>
        ///     Register idling event handler
        /// </summary>
        void RegisterIdlingEvent();

        /// <summary>
        ///     Unregister idling event handler
        /// </summary>
        void UnregisterIdlingEvent();

        /// <summary>
        ///     Queue work to be executed during next idling cycle
        /// </summary>
        void QueueWork(Action workItem);

        /// <summary>
        ///     Check if work is pending
        /// </summary>
        bool HasPendingWork { get; }

        /// <summary>
        ///     Get time since last idling execution
        /// </summary>
        TimeSpan TimeSinceLastExecution { get; }
    }
}
