using System;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Interface for aggregating the various event registries under a single facade.
    /// </summary>
    public interface IEventRegistry : IDisposable
    {
        /// <summary>
        ///     Gets the legacy event registry service.
        /// </summary>
        EventRegistryService LegacyRegistry { get; }

        /// <summary>
        ///     Registers all event handlers.
        /// </summary>
        void RegisterAll();

        /// <summary>
        ///     Unregisters all event handlers.
        /// </summary>
        void UnregisterAll();
    }
}
