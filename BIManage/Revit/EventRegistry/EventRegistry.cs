using System;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Aggregates the various event registries under a single facade.
    /// </summary>
    public class EventRegistry : IEventRegistry
    {
        private readonly IEventRegistryService _legacyRegistry;
        private readonly IDocumentEventRegistry _documentRegistry;
        private readonly ICommandEventRegistry _commandRegistry;
        private readonly IIdlingEventRegistry _idlingRegistry;
        private readonly IUITrackingEventRegistry _uiTrackingRegistry;
        private readonly ILogger? _logger;
        private bool _disposed;

        public EventRegistry(
            IEventRegistryService legacyRegistry,
            IDocumentEventRegistry documentRegistry,
            ICommandEventRegistry commandRegistry,
            IIdlingEventRegistry idlingRegistry,
            IUITrackingEventRegistry uiTrackingRegistry,
            ILogger? logger)
        {
            _legacyRegistry = legacyRegistry;
            _documentRegistry = documentRegistry;
            _commandRegistry = commandRegistry;
            _idlingRegistry = idlingRegistry;
            _uiTrackingRegistry = uiTrackingRegistry;
            _logger = logger;
        }

        public EventRegistryService LegacyRegistry => (EventRegistryService)_legacyRegistry;

        public void RegisterAll()
        {
            _legacyRegistry.RegisterControlledApplicationEvents();
            _documentRegistry.Register();
            _commandRegistry.Register();
            _idlingRegistry.Register();
            _uiTrackingRegistry.Register();
            _logger?.LogInfo("Revit event registry initialized (including UI tracking).");
        }

        public void UnregisterAll()
        {
            _legacyRegistry.UnregisterAllEvents();
            _documentRegistry.Unregister();
            _commandRegistry.Unregister();
            _idlingRegistry.Unregister();
            _uiTrackingRegistry.Unregister();
            _logger?.LogInfo("Revit event registry shut down.");
        }

        public void Dispose()
        {
            if (_disposed) return;

            UnregisterAll();
            _disposed = true;
        }
    }
}
