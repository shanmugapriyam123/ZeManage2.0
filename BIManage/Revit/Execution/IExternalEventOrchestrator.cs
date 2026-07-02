using System;
using Autodesk.Revit.UI;

namespace BIManage.Revit.Execution
{
    /// <summary>
    ///     Interface for orchestrating ExternalEvent execution for safe Revit API calls.
    ///     Ensures all Revit API operations run on the correct thread.
    /// </summary>
    public interface IExternalEventOrchestrator : IDisposable
    {
        /// <summary>
        ///     Register an external event handler.
        /// </summary>
        /// <typeparam name="T">The handler type</typeparam>
        /// <param name="handler">The handler instance</param>
        void RegisterHandler<T>(IExternalEventHandler handler) where T : IExternalEventHandler;

        /// <summary>
        ///     Raise an external event (execute handler on Revit API thread).
        /// </summary>
        /// <typeparam name="T">The handler type</typeparam>
        /// <returns>The external event request result</returns>
        ExternalEventRequest Raise<T>() where T : IExternalEventHandler;

        /// <summary>
        ///     Get the external event instance (for advanced usage).
        /// </summary>
        /// <typeparam name="T">The handler type</typeparam>
        /// <returns>The external event instance or null if not registered</returns>
        ExternalEvent? GetExternalEvent<T>() where T : IExternalEventHandler;
    }
}
