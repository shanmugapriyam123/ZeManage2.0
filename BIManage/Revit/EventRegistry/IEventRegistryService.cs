using System;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Interface for registering and managing Revit application-level events.
    ///     Handles ControlledApplication event subscriptions.
    /// </summary>
    public interface IEventRegistryService : IDisposable
    {
        /// <summary>
        ///     Register all ControlledApplication events.
        /// </summary>
        void RegisterControlledApplicationEvents();

        /// <summary>
        ///     Unregister all events.
        /// </summary>
        void UnregisterAllEvents();
    }
}
