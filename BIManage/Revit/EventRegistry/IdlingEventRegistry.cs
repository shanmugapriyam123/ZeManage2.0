using System;
using Autodesk.Revit.ApplicationServices;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.Idling;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Registry for idling events - initializes when UIApplication becomes available
    /// </summary>
    public class IdlingEventRegistry : IIdlingEventRegistry
    {
        private readonly ControlledApplication _controlledApplication;
        private readonly IEventTogglePolicy _togglePolicy;
        private readonly IUIApplicationProvider _uiApplicationProvider;
        private readonly IIdlingService _idlingService;
        private readonly ILogger? _logger;
        private bool _disposed;
        private bool _initialized;

        public IdlingEventRegistry(
            ControlledApplication controlledApplication,
            IEventTogglePolicy togglePolicy,
            IUIApplicationProvider uiApplicationProvider,
            IIdlingService idlingService,
            ILogger? logger)
        {
            _controlledApplication = controlledApplication;
            _togglePolicy = togglePolicy;
            _uiApplicationProvider = uiApplicationProvider;
            _idlingService = idlingService;
            _logger = logger;
        }

        public void Register()
        {
            // Always register idling event for core functionality (heartbeat, session health)
            // Other optional idling features may still be controlled by feature flags within the handler
            TryInitialize();
        }

        public void Unregister()
        {
            if (_initialized)
            {
                _idlingService.UnregisterIdlingEvent();
            }
            _logger?.LogDebug("Idling event registry is shutting down.");
        }

        /// <summary>
        ///     Attempt to initialize idling event if UIApplication is available.
        ///     Always registers for core session health (heartbeat) monitoring.
        /// </summary>
        public void TryInitialize()
        {
            if (_initialized) return;

            if (_uiApplicationProvider.IsAvailable && _uiApplicationProvider.UIApplication != null)
            {
                _idlingService.RegisterIdlingEvent();
                _initialized = true;
                _logger?.LogInfo("Idling event monitoring initialized with UIApplication");
            }
            else
            {
                _logger?.LogDebug("Idling event registry waiting for UIApplication");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;

            Unregister();
            _disposed = true;
        }
    }
}
