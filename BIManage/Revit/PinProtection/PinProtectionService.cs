using System;
using Autodesk.Revit.UI;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.PinProtection
{
    /// <summary>
    /// Main service for Pin Protection functionality
    /// Manages enabled state for Pin Protection feature
    /// Note: Pin/Unpin command bindings are registered by CommandInterceptionService
    /// as part of Option B (full individual-binding pattern) implementation
    /// </summary>
    public class PinProtectionService : IDisposable
    {
        private readonly ILogger _logger;
        private readonly Func<bool> _isAdminCheck;
        private readonly Func<string> _getCurrentUsername;
        private readonly AuditRepository? _auditRepository;

        private bool _isEnabled;
        private bool _disposed;

        public PinProtectionService(
            ILogger logger,
            Func<bool> isAdminCheck,
            Func<string> getCurrentUsername,
            AuditRepository? auditRepository = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _isAdminCheck = isAdminCheck ?? throw new ArgumentNullException(nameof(isAdminCheck));
            _getCurrentUsername = getCurrentUsername ?? throw new ArgumentNullException(nameof(getCurrentUsername));
            _auditRepository = auditRepository;

            // Initialize storage with logger
            PinProtectionStorage.Initialize(_logger);

            _logger.LogInfo("PinProtectionService created");
        }

        /// <summary>
        /// Check if pin protection is currently enabled
        /// </summary>
        public bool IsEnabled => _isEnabled;

        /// <summary>
        /// Enable pin protection state
        /// Note: Pin/Unpin command bindings are registered by CommandInterceptionService
        /// via RegisterIndividualBindings() as part of Option B (full individual-binding pattern).
        /// This service only manages the enabled state flag.
        /// </summary>
        public void Enable(UIApplication uiApp)
        {
            if (_isEnabled)
            {
                _logger.LogWarning("Pin protection is already enabled");
                return;
            }

            if (uiApp == null)
                throw new ArgumentNullException(nameof(uiApp));

            try
            {
                _isEnabled = true;
                _logger.LogInfo("Pin protection enabled (bindings managed by CommandInterceptionService)");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to enable pin protection: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Disable pin protection state
        /// Note: Pin/Unpin command bindings are unregistered by CommandInterceptionService
        /// This service only manages the enabled state flag.
        /// </summary>
        public void Disable()
        {
            if (!_isEnabled)
            {
                _logger.LogWarning("Pin protection is already disabled");
                return;
            }

            try
            {
                _isEnabled = false;
                _logger.LogInfo("Pin protection disabled");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to disable pin protection: {ex.Message}", ex);
                throw;
            }
        }

        /// <summary>
        /// Dispose resources
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            try
            {
                Disable();
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during pin protection disposal: {ex.Message}", ex);
            }

            _disposed = true;
        }
    }
}
