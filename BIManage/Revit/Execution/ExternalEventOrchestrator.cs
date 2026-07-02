using System;
using System.Collections.Generic;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Execution
{
    /// <summary>
    ///     Orchestrates ExternalEvent execution for safe Revit API calls
    ///     Ensures all Revit API operations run on the correct thread
    /// </summary>
    public class ExternalEventOrchestrator : IExternalEventOrchestrator
    {
        private readonly ILogger? _logger;
        private readonly Dictionary<Type, ExternalEvent> _externalEvents;
        private bool _disposed;

        public ExternalEventOrchestrator(ILogger? logger)
        {
            _logger = logger;
            _externalEvents = new Dictionary<Type, ExternalEvent>();
        }

        /// <summary>
        ///     Register an external event handler
        /// </summary>
        public void RegisterHandler<T>(IExternalEventHandler handler) where T : IExternalEventHandler
        {
            if (_disposed) return;

            var handlerType = typeof(T);
            if (_externalEvents.ContainsKey(handlerType))
            {
                _logger?.LogWarning($"External event handler {handlerType.Name} is already registered");
                return;
            }

            try
            {
                var externalEvent = ExternalEvent.Create(handler);
                _externalEvents[handlerType] = externalEvent;
                _logger?.LogInfo($"Registered external event handler: {handlerType.Name}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to register external event handler {handlerType.Name}: {ex.Message}", ex);
            }
        }

        /// <summary>
        ///     Raise an external event (execute handler on Revit API thread)
        /// </summary>
        public ExternalEventRequest Raise<T>() where T : IExternalEventHandler
        {
            if (_disposed)
            {
                _logger?.LogWarning("Attempted to raise external event after disposal");
                return default;
            }

            var handlerType = typeof(T);
            if (!_externalEvents.TryGetValue(handlerType, out var externalEvent))
            {
                _logger?.LogError($"External event handler {handlerType.Name} is not registered");
                return default;
            }

            try
            {
                return externalEvent.Raise();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to raise external event {handlerType.Name}: {ex.Message}", ex);
                return default;
            }
        }

        /// <summary>
        ///     Get the external event instance (for advanced usage)
        /// </summary>
        public ExternalEvent? GetExternalEvent<T>() where T : IExternalEventHandler
        {
            var handlerType = typeof(T);
            _externalEvents.TryGetValue(handlerType, out var externalEvent);
            return externalEvent;
        }

        public void Dispose()
        {
            if (_disposed) return;

            _logger?.LogInfo("Disposing external event orchestrator");
            
            // Note: ExternalEvent instances are managed by Revit, we don't dispose them
            _externalEvents.Clear();
            
            _disposed = true;
        }
    }
}
