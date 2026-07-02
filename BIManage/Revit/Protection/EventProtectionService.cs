using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.Protection
{
    /// <summary>
    /// Service for managing event protection settings
    /// Caches settings for efficient lookup during event handling
    /// </summary>
    public class EventProtectionService : IEventProtectionService
    {
        private readonly ILogger? _logger;
        private readonly EventProtectionRepository? _repository;
        private int? _currentProjectId;
        private string? _currentModelGuid;
        private bool _isEnabled = true;

        // Cache settings by dummy command ID for fast lookup
        private readonly ConcurrentDictionary<string, EventProtectionSettings> _settingsCache = new();

        // Cache settings by event type for efficient event handling
        private readonly ConcurrentDictionary<RevitEventType, List<EventProtectionSettings>> _eventCache = new();

        public EventProtectionService(ILogger? logger, EventProtectionRepository? repository = null)
        {
            _logger = logger;
            _repository = repository;
        }

        /// <summary>
        /// Check if event protection is globally enabled
        /// </summary>
        public bool IsProtectionEnabled => _isEnabled;

        /// <summary>
        /// Check if a specific protection is enabled by dummy command ID
        /// </summary>
        public bool IsProtectionEnabledFor(string dummyCommandId)
        {
            if (!_isEnabled) return false;

            return _settingsCache.TryGetValue(dummyCommandId, out var setting) && setting.Enabled;
        }

        /// <summary>
        /// Get all protections for a specific event type
        /// Returns only enabled protections for efficient event handling
        /// </summary>
        public IReadOnlyList<EventProtectionSettings> GetProtectionsForEvent(RevitEventType eventType)
        {
            if (!_isEnabled) return Array.Empty<EventProtectionSettings>();

            if (_eventCache.TryGetValue(eventType, out var settings))
            {
                return settings.Where(s => s.Enabled).ToList();
            }

            return Array.Empty<EventProtectionSettings>();
        }

        /// <summary>
        /// Get a specific protection by dummy command ID
        /// </summary>
        public EventProtectionSettings? GetProtectionByDummyCommandId(string dummyCommandId)
        {
            _settingsCache.TryGetValue(dummyCommandId, out var setting);
            return setting;
        }

        /// <summary>
        /// Get a protection by dummy command ID, falling back to a direct DB query if the
        /// cache is empty (e.g. called from OnDocumentOpening before LoadSettingsFromDatabase).
        /// Does not modify the cache.
        /// </summary>
        public EventProtectionSettings? GetProtectionByDummyCommandIdDirect(string dummyCommandId)
        {
            if (_settingsCache.TryGetValue(dummyCommandId, out var cached))
                return cached;

            return _repository?.GetProtectionByDummyCommandId(dummyCommandId, null);
        }

        /// <summary>
        /// Get configuration for a specific protection
        /// </summary>
        public T? GetConfiguration<T>(string dummyCommandId) where T : EventProtectionConfig
        {
            if (!_settingsCache.TryGetValue(dummyCommandId, out var setting))
                return null;

            if (string.IsNullOrEmpty(setting.ConfigurationJson))
                return null;

            try
            {
                return JsonSerializer.Deserialize<T>(setting.ConfigurationJson, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to deserialize config for {dummyCommandId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Load settings from database
        /// </summary>
        public void LoadSettingsFromDatabase(int? projectId, string? modelGuid = null)
        {
            try
            {
                _currentProjectId = projectId;
                _currentModelGuid = modelGuid;
                _settingsCache.Clear();
                _eventCache.Clear();

                // Seed code defaults first so every known protection is always in the cache.
                // DB rows loaded below override these via ShouldPreferNewOverExisting.
                LoadDefaultSettings();

                if (_repository == null)
                {
                    _logger?.LogWarning("EventProtectionRepository not available, using default settings");
                    return;
                }

                var settings = _repository.LoadEventProtectionSettings(projectId, modelGuid);

                foreach (var setting in settings)
                {
                    // Cache by DummyCommandId. When multiple rows share the same id (e.g.
                    // company-level seed + a project-level override created by a Project
                    // Admin toggle), apply scope precedence so the override wins:
                    //   model-specific > project-level > company-level
                    // Without this, dictionary overwrite ordering was non-deterministic and
                    // a just-enabled project override could be hidden behind the disabled
                    // company-level seed → enforcement saw Enabled=false → didn't fire.
                    if (_settingsCache.TryGetValue(setting.DummyCommandId, out var existing))
                    {
                        if (ShouldPreferNewOverExisting(setting, existing))
                            _settingsCache[setting.DummyCommandId] = setting;
                    }
                    else
                    {
                        _settingsCache[setting.DummyCommandId] = setting;
                    }

                    // Add to event cache
                    if (!_eventCache.TryGetValue(setting.EventType, out var eventList))
                    {
                        eventList = new List<EventProtectionSettings>();
                        _eventCache[setting.EventType] = eventList;
                    }
                    eventList.Add(setting);
                }

                _logger?.LogInfo($"Loaded {settings.Count} event protections from database " +
                    $"({_eventCache.Count} event types, {_settingsCache.Values.Count(s => s.Enabled)} enabled)");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load event protection settings: {ex.Message}", ex);
                LoadDefaultSettings();
            }
        }

        /// <summary>
        /// Reload settings from database
        /// </summary>
        public void RefreshSettings()
        {
            LoadSettingsFromDatabase(_currentProjectId, _currentModelGuid);
        }

        /// <summary>
        /// Returns true if the new setting should replace the existing cached one for the
        /// same DummyCommandId. Precedence: model-specific (most specific) wins over
        /// project-level, which wins over company-level. Within the same scope tier, the
        /// most-recently-modified row wins so a just-toggled value isn't masked by an
        /// older copy with the same scope.
        /// </summary>
        private static bool ShouldPreferNewOverExisting(EventProtectionSettings candidate, EventProtectionSettings existing)
        {
            int CandidateRank = ScopeRank(candidate);
            int ExistingRank = ScopeRank(existing);
            if (CandidateRank != ExistingRank)
                return CandidateRank > ExistingRank;

            // Same scope tier — prefer the row whose ModifiedAt is newer.
            var candTs = candidate.ModifiedAt ?? DateTime.MinValue;
            var existTs = existing.ModifiedAt ?? DateTime.MinValue;
            return candTs > existTs;
        }

        private static int ScopeRank(EventProtectionSettings s)
        {
            // 3 = model-specific (most specific), 2 = project-level, 1 = company-level (least specific)
            if (!string.IsNullOrWhiteSpace(s.ModelGuid)) return 3;
            if (!s.IsCompanyLevel || !string.IsNullOrWhiteSpace(s.ProjectId)) return 2;
            return 1;
        }

        /// <summary>
        /// Update a protection setting (runtime)
        /// </summary>
        public void UpdateProtection(EventProtectionSettings setting)
        {
            _settingsCache[setting.DummyCommandId] = setting;

            // Update event cache
            if (_eventCache.TryGetValue(setting.EventType, out var eventList))
            {
                var index = eventList.FindIndex(s => s.DummyCommandId == setting.DummyCommandId);
                if (index >= 0)
                {
                    eventList[index] = setting;
                }
                else
                {
                    eventList.Add(setting);
                }
            }
            else
            {
                _eventCache[setting.EventType] = new List<EventProtectionSettings> { setting };
            }

            _logger?.LogInfo($"Updated event protection: {setting.DummyCommandId} " +
                $"(enabled: {setting.Enabled}, mode: {setting.Mode})");
        }

        /// <summary>
        /// Enable/disable all protections globally
        /// </summary>
        public void SetGlobalEnabled(bool enabled)
        {
            _isEnabled = enabled;
            _logger?.LogInfo($"Event protection globally {(enabled ? "enabled" : "disabled")}");
        }

        /// <summary>
        /// Load default settings (when database not available)
        /// All protections disabled by default
        /// </summary>
        private void LoadDefaultSettings()
        {
            _settingsCache.Clear();
            _eventCache.Clear();

            // Define default protections (all disabled), grouped by user-facing category
            // eventType: 1=DocumentOpening, 2=DocumentSaving, 3=FamilyLoading, 4=DocumentPrinting, 5=DocumentChanged, 6=CommandProtection
            var defaults = new[]
            {
                // --- Model Access & Security (DocumentOpening = 1) ---
                EventProtectionSettings.CreateDefault(RevitEventType.DocumentOpening,
                    "Ze_DuplicateUserSessionProtection", "Duplicate Username in Same Model"),
                EventProtectionSettings.CreateDefault(RevitEventType.DocumentOpening,
                    "Ze_OpenCentralFileProtection", "Open Central File Directly"),
                EventProtectionSettings.CreateDefault(RevitEventType.DocumentOpening,
                    "Ze_ModelUpgradeProtection", "Model Upgrade Protection"),

                // --- File Operations ---
                EventProtectionSettings.CreateDefault(RevitEventType.DocumentSaving,
                    "Ze_SaveOverEarlierFileVersionProtection", "Save Over Earlier File Version"),
                EventProtectionSettings.CreateDefault(RevitEventType.DocumentPrinting,
                    "Ze_DocumentPrintingProtection", "Document Printing Protection"),
                EventProtectionSettings.CreateDefault(RevitEventType.CommandProtection,
                    "Ze_DocumentSaveAsProtection", "Document Save As Protection"),
                EventProtectionSettings.CreateDefault(RevitEventType.CommandProtection,
                    "Ze_DocumentExportingProtection", "Document Exporting Protection"),
                EventProtectionSettings.CreateDefault(RevitEventType.CommandProtection,
                    "Ze_TransferProjectStandardsProtection", "Transfer Project Standards Protection"),

                // --- Family Management (FamilyLoadingIntoDocument = 3) ---
                EventProtectionSettings.CreateDefault(RevitEventType.FamilyLoadingIntoDocument,
                    "Ze_FamilyLibrarySettings", "Load Family from Non-Approved Location"),

                // --- Import & Link Protection ---
                EventProtectionSettings.CreateDefault(RevitEventType.CommandProtection,
                    "Ze_CADImportProtection", "CAD Import Protection"),
                EventProtectionSettings.CreateDefault(RevitEventType.CommandProtection,
                    "Ze_CADExplodeProtection", "CAD Explode Protection"),
                EventProtectionSettings.CreateDefault(RevitEventType.CommandProtection,
                    "Ze_EquipmentMirrorProtection", "Equipment Mirror Protection"),
                EventProtectionSettings.CreateDefault(RevitEventType.DocumentChanged,
                    "Ze_RVTLinkPinPrompt", "Revit Link Pin Prompt"),

                // NOTE: Sync Control (SyncQueueControl, BackgroundSync, BackgroundRelinquish, IdleSync)
                // are NOT protections. They are a separate module with feature flags in FeatureToggleService.
            };

            foreach (var setting in defaults)
            {
                _settingsCache[setting.DummyCommandId] = setting;

                if (!_eventCache.TryGetValue(setting.EventType, out var eventList))
                {
                    eventList = new List<EventProtectionSettings>();
                    _eventCache[setting.EventType] = eventList;
                }
                eventList.Add(setting);
            }

            _logger?.LogInfo($"Loaded {defaults.Length} default event protections (all disabled)");
        }
    }
}
