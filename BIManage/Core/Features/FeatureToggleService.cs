using System.Collections.Generic;
using BIManage.Infrastructure.Logging;

namespace BIManage.Core.Features
{
    /// <summary>
    ///     Service for managing feature toggles and pause states
    ///     Allows enabling/disabling features without code changes
    /// </summary>
    public class FeatureToggleService : IFeatureToggleService
    {
        private readonly ILogger? _logger;
        private readonly Dictionary<string, bool> _featureStates;
        private bool _globalPause;

        public FeatureToggleService(ILogger? logger)
        {
            _logger = logger;
            _featureStates = new Dictionary<string, bool>();
            _globalPause = false;

            // Initialize default feature states
            InitializeDefaultFeatures();
        }

        /// <summary>
        ///     Global pause flag - when true, all monitoring is paused
        /// </summary>
        public bool IsGlobalPaused
        {
            get => _globalPause;
            set
            {
                if (_globalPause != value)
                {
                    _globalPause = value;
                    _logger?.LogInfo($"Global pause state changed: {value}");
                }
            }
        }

        /// <summary>
        ///     Check if a specific feature is enabled
        /// </summary>
        public bool IsFeatureEnabled(string featureName)
        {
            if (_globalPause)
            {
                return false;
            }

            return _featureStates.TryGetValue(featureName, out var enabled) && enabled;
        }

        /// <summary>
        ///     Enable or disable a specific feature
        /// </summary>
        public void SetFeatureEnabled(string featureName, bool enabled)
        {
            _featureStates[featureName] = enabled;
            _logger?.LogInfo($"Feature '{featureName}' {(enabled ? "enabled" : "disabled")}");
        }

        /// <summary>
        ///     Initialize default feature states
        /// </summary>
        private void InitializeDefaultFeatures()
        {
            // Event monitoring features
            SetFeatureEnabled("EventMonitoring", true);
            SetFeatureEnabled("DocumentLifecycleEvents", true);
            SetFeatureEnabled("ViewTracking", true); // Productivity tracking (active/idle time)
            SetFeatureEnabled("ChangeSetExtraction", true); // DocumentChanged event
            SetFeatureEnabled("CommandInterception", true); // Command monitoring with lazy UIApplication initialization
            SetFeatureEnabled("IdlingEventPolling", true); // Hybrid throttle (time + work-based) with lazy UIApplication initialization
            SetFeatureEnabled("UITracking", true); // View activation and selection tracking

            // Developer tools (for testing and diagnostics)
            SetFeatureEnabled("DeveloperTools", false); // Only enabled via BIMANAGE_DEV_MODE env var

            // Rule engine features (Phase 2)
            SetFeatureEnabled("RuleEvaluation", true); // Enable rule evaluation
            SetFeatureEnabled("ProtectionModes", true); // Enable Monitor, Guide, Prevent modes
            SetFeatureEnabled("PasswordProtection", true); // Enable password override for Prevent mode

            // Pin Protection features
            SetFeatureEnabled("PinProtectionBypassDetection", true); // Detect group-based pin protection bypass

            // Event Protection features (event-based protections)
            SetFeatureEnabled("EventProtection", false); // Disabled by default until tested

            // Persistence features (Phase 3)
            SetFeatureEnabled("LocalPersistence", true); // Enable SQLite persistence
            SetFeatureEnabled("EvidenceCapture", false); // Screenshot capture (future)

            // Network security features
            SetFeatureEnabled("StrictSslValidation", true); // Enabled — staging/production use valid Azure TLS certificates

            // SignalR features (real-time communication)
            SetFeatureEnabled("SignalR", true); // Real-time hub connection enabled

            // Sync Control Module features (NOT protections - separate module)
            // These control sync traffic coordination and background sync operations
            SetFeatureEnabled("SyncQueueControl", true); // Sync queue coordination (block if others syncing)
            SetFeatureEnabled("BackgroundSync", true); // Automatic background sync on interval
            SetFeatureEnabled("BackgroundRelinquish", true); // Automatic element relinquish
            SetFeatureEnabled("IdleSync", true); // Sync when user is idle

            _logger?.LogInfo("Default feature states initialized");
        }

        /// <summary>
        ///     Get all feature states (for debugging/admin purposes)
        /// </summary>
        public Dictionary<string, bool> GetAllFeatureStates()
        {
            return new Dictionary<string, bool>(_featureStates);
        }
    }
}
