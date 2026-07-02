using System;
using Autodesk.Revit.ApplicationServices;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Commands;
using BIManage.Revit.Context;

namespace BIManage.Revit.EventRegistry
{
    /// <summary>
    ///     Registry for command interception - initializes when UIApplication becomes available
    /// </summary>
    public class CommandEventRegistry : ICommandEventRegistry
    {
        private readonly ControlledApplication _controlledApplication;
        private readonly IEventTogglePolicy _togglePolicy;
        private readonly IUIApplicationProvider _uiApplicationProvider;
        private readonly ICommandInterceptionService _commandInterceptionService;
        private readonly ILogger? _logger;
        private bool _disposed;
        private bool _initialized;

        public CommandEventRegistry(
            ControlledApplication controlledApplication,
            IEventTogglePolicy togglePolicy,
            IUIApplicationProvider uiApplicationProvider,
            ICommandInterceptionService commandInterceptionService,
            ILogger? logger)
        {
            _controlledApplication = controlledApplication;
            _togglePolicy = togglePolicy;
            _uiApplicationProvider = uiApplicationProvider;
            _commandInterceptionService = commandInterceptionService;
            _logger = logger;
        }

        public void Register()
        {
            if (!_togglePolicy.ShouldMonitorCommands()) return;

            // Attempt lazy initialization if UIApplication is available
            TryInitialize();
        }

        public void Unregister()
        {
            if (!_togglePolicy.ShouldMonitorCommands()) return;
            if (_initialized)
            {
                _commandInterceptionService.UnregisterCommandBindings();
            }
            _logger?.LogDebug("Command event registry is shutting down.");
        }

        /// <summary>
        ///     Attempt to initialize command bindings if UIApplication is available
        /// </summary>
        public void TryInitialize()
        {
            if (_initialized || !_togglePolicy.ShouldMonitorCommands()) return;

            if (_uiApplicationProvider.IsAvailable && _uiApplicationProvider.UIApplication != null)
            {
                _commandInterceptionService.RegisterCommandBindings(_uiApplicationProvider.UIApplication);
                _initialized = true;
                _logger?.LogInfo("Command interception initialized with UIApplication");
            }
            else
            {
                _logger?.LogDebug("Command event registry waiting for UIApplication");
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
