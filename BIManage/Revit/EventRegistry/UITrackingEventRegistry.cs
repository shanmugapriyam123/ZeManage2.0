using System;
using Autodesk.Revit.ApplicationServices;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Context;
using BIManage.Revit.UITracking;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Registry for UI tracking (view and selection) - initializes when UIApplication becomes availablef
    /// </summary>
    public class UITrackingEventRegistry : IUITrackingEventRegistry
    {
        private readonly ControlledApplication _controlledApplication;
        private readonly IEventTogglePolicy _togglePolicy;
        private readonly IUIApplicationProvider _uiApplicationProvider;
        private readonly IViewTrackingService _viewTrackingService;
        private readonly ISelectionTrackingService _selectionTrackingService;
        private readonly ILogger? _logger;
        private bool _disposed;
        private bool _initialized;

        public UITrackingEventRegistry(
            ControlledApplication controlledApplication,
            IEventTogglePolicy togglePolicy,
            IUIApplicationProvider uiApplicationProvider,
            IViewTrackingService viewTrackingService,
            ISelectionTrackingService selectionTrackingService,
            ILogger? logger)
        {
            _controlledApplication = controlledApplication;
            _togglePolicy = togglePolicy;
            _uiApplicationProvider = uiApplicationProvider;
            _viewTrackingService = viewTrackingService;
            _selectionTrackingService = selectionTrackingService;
            _logger = logger;
        }

        public void Register()
        {
            if (!_togglePolicy.ShouldMonitorUITracking()) return;

            // Attempt lazy initialization if UIApplication is available
            TryInitialize();
        }

        public void Unregister()
        {
            if (!_initialized) return;

            try
            {
                _viewTrackingService.Unregister();
                _selectionTrackingService.Unregister();
                _logger?.LogInfo("UI tracking event registry unregistered");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Error unregistering UI tracking: {ex.Message}");
            }
        }

        /// <summary>
        ///     Attempt to initialize UI tracking if UIApplication is available
        /// </summary>
        public void TryInitialize()
        {
            if (_initialized || !_togglePolicy.ShouldMonitorUITracking()) return;

            if (_uiApplicationProvider.IsAvailable && _uiApplicationProvider.UIApplication != null)
            {
                var uiApp = _uiApplicationProvider.UIApplication;

                // Register view tracking
                _viewTrackingService.Register(uiApp);

                // Register selection tracking
                _selectionTrackingService.Register(uiApp);

                _initialized = true;
                _logger?.LogInfo("UI tracking initialized with UIApplication (view + selection tracking active)");
            }
            else
            {
                _logger?.LogDebug("UI tracking event registry waiting for UIApplication");
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
