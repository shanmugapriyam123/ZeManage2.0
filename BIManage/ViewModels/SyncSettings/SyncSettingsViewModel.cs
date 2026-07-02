using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Core.Features;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Win32;

namespace BIManage.ViewModels.SyncSettings
{
    public class SyncSettingsViewModel : ObservableObject
    {
        private readonly SyncRepository _syncRepository;
        private readonly ILogger? _logger;
        private readonly IFeatureToggleService? _featureToggleService;

        private string _statusMessage = "Ready";
        private bool _isBusy;

        // ── Master ──────────────────────────────────────────────────────────────
        private bool _isBackgroundSyncEnabled = true;

        // ── Sync Mode ───────────────────────────────────────────────────────────
        private bool _syncAllTheTime = false;

        // ── Timing ──────────────────────────────────────────────────────────────
        private int _syncIntervalMinutes = 30;
        private int _relinquishIntervalMinutes = 60;
        private bool _enableIdleSync = true;
        private int _idleTimeoutMinutes = 15;

        // ── Behaviour ───────────────────────────────────────────────────────────
        private bool _enableRelinquish = true;
        private bool _syncOnSave;
        private bool _syncEvenIfNoChanges;

        // ── Schedule ────────────────────────────────────────────────────────────
        private bool _enableSchedule;
        private string _scheduleStartTime = "21:00";
        private string _scheduleEndTime = "09:00";

        // ── Compact Model ────────────────────────────────────────────────────────
        private bool _compactModelOnceADay;
        private bool _compactAtNightOnly;

        // ── Auto-Exit ────────────────────────────────────────────────────────────
        private bool _exitRevitOnIdle;
        private int _exitRevitAfterMinutes = 1440;

        // ── Open Views Behaviour ────────────────────────────────────────────────
        // 0 = Keep them open (default), 1 = Close all views, 2 = Close all views and reopen after sync
        private int _openViewsOnSyncMode = 0;
        // Min 2, default 10
        private int _preventSyncWhenViewsOpenedOver = 10;

        // ── Helper properties ───────────────────────────────────────────────────

        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public bool IsBusy
        {
            get => _isBusy;
            set => SetProperty(ref _isBusy, value);
        }

        // ── Master ──────────────────────────────────────────────────────────────

        public bool IsBackgroundSyncEnabled
        {
            get => _isBackgroundSyncEnabled;
            set => SetProperty(ref _isBackgroundSyncEnabled, value);
        }

        // ── Sync Mode ───────────────────────────────────────────────────────────

        /// <summary>true = Continuous; false = When I take a pause (idle-based)</summary>
        public bool SyncAllTheTime
        {
            get => _syncAllTheTime;
            set
            {
                SetProperty(ref _syncAllTheTime, value);
                OnPropertyChanged(nameof(SyncWhenPaused));
                OnPropertyChanged(nameof(ShowIdleOptions));
            }
        }

        /// <summary>Inverse of SyncAllTheTime — for radio-button binding.</summary>
        public bool SyncWhenPaused
        {
            get => !_syncAllTheTime;
            set => SyncAllTheTime = !value;
        }

        /// <summary>Show idle timeout controls only in When-Paused mode.</summary>
        public bool ShowIdleOptions => !_syncAllTheTime;

        // ── Timing ──────────────────────────────────────────────────────────────

        public int SyncIntervalMinutes
        {
            get => _syncIntervalMinutes;
            set => SetProperty(ref _syncIntervalMinutes, value);
        }

        public int RelinquishIntervalMinutes
        {
            get => _relinquishIntervalMinutes;
            set => SetProperty(ref _relinquishIntervalMinutes, value);
        }

        public bool EnableIdleSync
        {
            get => _enableIdleSync;
            set => SetProperty(ref _enableIdleSync, value);
        }

        public int IdleTimeoutMinutes
        {
            get => _idleTimeoutMinutes;
            set => SetProperty(ref _idleTimeoutMinutes, value);
        }

        // ── Behaviour ───────────────────────────────────────────────────────────

        public bool EnableRelinquish
        {
            get => _enableRelinquish;
            set => SetProperty(ref _enableRelinquish, value);
        }

        public bool SyncOnSave
        {
            get => _syncOnSave;
            set => SetProperty(ref _syncOnSave, value);
        }

        public bool SyncEvenIfNoChanges
        {
            get => _syncEvenIfNoChanges;
            set => SetProperty(ref _syncEvenIfNoChanges, value);
        }

        // ── Schedule ────────────────────────────────────────────────────────────

        public bool EnableSchedule
        {
            get => _enableSchedule;
            set => SetProperty(ref _enableSchedule, value);
        }

        public string ScheduleStartTime
        {
            get => _scheduleStartTime;
            set => SetProperty(ref _scheduleStartTime, value);
        }

        public string ScheduleEndTime
        {
            get => _scheduleEndTime;
            set => SetProperty(ref _scheduleEndTime, value);
        }

        // ── Compact Model ────────────────────────────────────────────────────────

        public bool CompactModelOnceADay
        {
            get => _compactModelOnceADay;
            set
            {
                SetProperty(ref _compactModelOnceADay, value);
                if (!value) CompactAtNightOnly = false;
            }
        }

        public bool CompactAtNightOnly
        {
            get => _compactAtNightOnly;
            set => SetProperty(ref _compactAtNightOnly, value);
        }

        // ── Auto-Exit ────────────────────────────────────────────────────────────

        public bool ExitRevitOnIdle
        {
            get => _exitRevitOnIdle;
            set => SetProperty(ref _exitRevitOnIdle, value);
        }

        public int ExitRevitAfterMinutes
        {
            get => _exitRevitAfterMinutes;
            set
            {
                if (SetProperty(ref _exitRevitAfterMinutes, value))
                {
                    OnPropertyChanged(nameof(ExitAfterHours));
                    OnPropertyChanged(nameof(ExitAfterMins));
                }
            }
        }

        public int ExitAfterHours
        {
            get => _exitRevitAfterMinutes / 60;
            set
            {
                var mins = ExitAfterMins;
                ExitRevitAfterMinutes = (value * 60) + mins;
            }
        }

        public int ExitAfterMins
        {
            get => _exitRevitAfterMinutes % 60;
            set
            {
                var hours = ExitAfterHours;
                ExitRevitAfterMinutes = (hours * 60) + value;
            }
        }

        // ── Open Views Behaviour ────────────────────────────────────────────────

        /// <summary>
        /// 0 = Keep them open (default), 1 = Close all views, 2 = Close all views and reopen after sync.
        /// </summary>
        public int OpenViewsOnSyncMode
        {
            get => _openViewsOnSyncMode;
            set => SetProperty(ref _openViewsOnSyncMode, Math.Max(0, Math.Min(2, value)));
        }

        /// <summary>
        /// Prevent background sync when more than this many views are open. Minimum 2, default 10.
        /// </summary>
        public int PreventSyncWhenViewsOpenedOver
        {
            get => _preventSyncWhenViewsOpenedOver;
            set => SetProperty(ref _preventSyncWhenViewsOpenedOver, Math.Max(2, value));
        }

        // ── Constructor ─────────────────────────────────────────────────────────

        public SyncSettingsViewModel(
            SyncRepository syncRepository,
            ILogger? logger = null,
            IFeatureToggleService? featureToggleService = null)
        {
            _syncRepository = syncRepository;
            _logger = logger;
            _featureToggleService = featureToggleService;
        }

        // ── Load / Save ─────────────────────────────────────────────────────────

        public async Task LoadAsync()
        {
            IsBusy = true;
            StatusMessage = "Loading...";

            try
            {
                if (_syncRepository != null)
                {
                    var s = await _syncRepository.GetBackgroundSyncSettingsAsync();
                    if (s != null)
                    {
                        IsBackgroundSyncEnabled   = s.IsEnabled;
                        SyncAllTheTime            = s.SyncAllTheTime;
                        SyncIntervalMinutes       = s.SyncIntervalMinutes;
                        RelinquishIntervalMinutes = s.RelinquishIntervalMinutes;
                        EnableIdleSync            = s.EnableIdleSync;
                        IdleTimeoutMinutes        = s.IdleTimeoutMinutes;
                        EnableRelinquish          = s.EnableRelinquish;
                        SyncOnSave                = s.SyncOnSave;
                        SyncEvenIfNoChanges       = s.SyncEvenIfNoChanges;
                        EnableSchedule            = s.EnableSchedule;
                        ScheduleStartTime         = s.ScheduleStartTime ?? "21:00";
                        ScheduleEndTime           = s.ScheduleEndTime   ?? "09:00";
                        CompactModelOnceADay      = s.CompactModelOnceADay;
                        CompactAtNightOnly        = s.CompactAtNightOnly;
                        ExitRevitOnIdle           = s.ExitRevitOnIdle;
                        ExitRevitAfterMinutes     = s.ExitRevitAfterMinutes;
                        OpenViewsOnSyncMode       = s.OpenViewsOnSyncMode;
                        PreventSyncWhenViewsOpenedOver = s.PreventSyncWhenViewsOpenedOver;
                    }
                }

                StatusMessage = "Settings loaded";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                _logger?.LogError($"SyncSettingsViewModel.LoadAsync failed: {ex.Message}", ex);
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task<bool> SaveBackgroundSyncSettingsAsync()
        {
            if (_syncRepository == null)
            {
                StatusMessage = "Repository not available";
                return false;
            }

            IsBusy = true;
            StatusMessage = "Saving settings...";

            try
            {
                var settings = new BackgroundSyncSettings
                {
                    IsEnabled                = IsBackgroundSyncEnabled,
                    SyncAllTheTime           = SyncAllTheTime,
                    SyncIntervalMinutes      = SyncIntervalMinutes,
                    RelinquishIntervalMinutes = RelinquishIntervalMinutes,
                    EnableIdleSync           = EnableIdleSync,
                    IdleTimeoutMinutes       = IdleTimeoutMinutes,
                    EnableRelinquish         = EnableRelinquish,
                    SyncOnSave               = SyncOnSave,
                    SyncEvenIfNoChanges      = SyncEvenIfNoChanges,
                    EnableSchedule           = EnableSchedule,
                    ScheduleStartTime        = ScheduleStartTime,
                    ScheduleEndTime          = ScheduleEndTime,
                    CompactModelOnceADay     = CompactModelOnceADay,
                    CompactAtNightOnly       = CompactAtNightOnly,
                    ExitRevitOnIdle          = ExitRevitOnIdle,
                    ExitRevitAfterMinutes    = ExitRevitAfterMinutes,
                    OpenViewsOnSyncMode      = OpenViewsOnSyncMode,
                    PreventSyncWhenViewsOpenedOver = PreventSyncWhenViewsOpenedOver
                };

                var success = await _syncRepository.SaveBackgroundSyncSettingsAsync(settings);

                // Mirror the saved values into in-memory feature toggles so the BackgroundSyncEngine
                // tick gate (which reads BackgroundSync / BackgroundRelinquish) picks up the change
                // immediately, not just at next app startup. Without this, toggling Relinquish off
                // in the UI would have no effect until restart.
                if (success && _featureToggleService != null)
                {
                    _featureToggleService.SetFeatureEnabled("BackgroundSync", IsBackgroundSyncEnabled);
                    _featureToggleService.SetFeatureEnabled("BackgroundRelinquish", EnableRelinquish);
                    _featureToggleService.SetFeatureEnabled("IdleSync", EnableIdleSync);
                    _featureToggleService.SetFeatureEnabled("SyncQueueControl", IsBackgroundSyncEnabled);
                }

                StatusMessage = success ? "Settings saved" : "Failed to save settings";
                return success;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error: {ex.Message}";
                _logger?.LogError($"SaveBackgroundSyncSettingsAsync failed: {ex.Message}", ex);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        // ── Export / Import ───────────────────────────────────────────────────────

        public async Task<bool> ExportToFileAsync()
        {
            try
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "ZE Files (*.ze)|*.ze|All Files (*.*)|*.*",
                    Title = "Export Sync Settings",
                    FileName = $"SyncSettings_Export_{DateTime.Now:yyyyMMdd_HHmmss}.ze"
                };

                if (saveFileDialog.ShowDialog() != true) return false;

                IsBusy = true;
                StatusMessage = "Exporting settings...";
                _logger?.LogInfo($"Exporting sync settings to: {saveFileDialog.FileName}");

                var exportModel = new SyncSettingsExportModel
                {
                    IsEnabled                = IsBackgroundSyncEnabled,
                    SyncAllTheTime           = SyncAllTheTime,
                    SyncIntervalMinutes      = SyncIntervalMinutes,
                    RelinquishIntervalMinutes = RelinquishIntervalMinutes,
                    EnableIdleSync           = EnableIdleSync,
                    IdleTimeoutMinutes       = IdleTimeoutMinutes,
                    EnableRelinquish         = EnableRelinquish,
                    SyncOnSave               = SyncOnSave,
                    SyncEvenIfNoChanges      = SyncEvenIfNoChanges,
                    EnableSchedule           = EnableSchedule,
                    ScheduleStartTime        = ScheduleStartTime,
                    ScheduleEndTime          = ScheduleEndTime,
                    CompactModelOnceADay     = CompactModelOnceADay,
                    CompactAtNightOnly       = CompactAtNightOnly,
                    ExitRevitOnIdle          = ExitRevitOnIdle,
                    ExitRevitAfterMinutes    = ExitRevitAfterMinutes,
                    OpenViewsOnSyncMode      = OpenViewsOnSyncMode,
                    PreventSyncWhenViewsOpenedOver = PreventSyncWhenViewsOpenedOver
                };

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var jsonContent = JsonSerializer.Serialize(exportModel, options);
                await Task.Run(() => File.WriteAllText(saveFileDialog.FileName, jsonContent, Encoding.UTF8));

                StatusMessage = $"Exported to {Path.GetFileName(saveFileDialog.FileName)}";
                _logger?.LogInfo($"Sync settings exported successfully");
                return true;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Export failed: {ex.Message}";
                _logger?.LogError($"ExportToFileAsync failed: {ex.Message}", ex);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }

        public async Task<bool> ImportFromFileAsync()
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "ZE Files (*.ze)|*.ze|All Files (*.*)|*.*",
                    Title = "Import Sync Settings"
                };

                if (openFileDialog.ShowDialog() != true) return false;

                IsBusy = true;
                StatusMessage = "Importing settings...";
                _logger?.LogInfo($"Importing sync settings from: {openFileDialog.FileName}");

                var jsonContent = await Task.Run(() => File.ReadAllText(openFileDialog.FileName, Encoding.UTF8));

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };

                var imported = JsonSerializer.Deserialize<SyncSettingsExportModel>(jsonContent, options);

                if (imported == null)
                {
                    StatusMessage = "No settings found in file";
                    return false;
                }

                // Apply imported values to ViewModel (UI updates via bindings)
                IsBackgroundSyncEnabled   = imported.IsEnabled ?? IsBackgroundSyncEnabled;
                SyncAllTheTime            = imported.SyncAllTheTime ?? SyncAllTheTime;
                SyncIntervalMinutes       = imported.SyncIntervalMinutes ?? SyncIntervalMinutes;
                RelinquishIntervalMinutes = imported.RelinquishIntervalMinutes ?? RelinquishIntervalMinutes;
                EnableIdleSync            = imported.EnableIdleSync ?? EnableIdleSync;
                IdleTimeoutMinutes        = imported.IdleTimeoutMinutes ?? IdleTimeoutMinutes;
                EnableRelinquish          = imported.EnableRelinquish ?? EnableRelinquish;
                SyncOnSave                = imported.SyncOnSave ?? SyncOnSave;
                SyncEvenIfNoChanges       = imported.SyncEvenIfNoChanges ?? SyncEvenIfNoChanges;
                EnableSchedule            = imported.EnableSchedule ?? EnableSchedule;
                ScheduleStartTime         = imported.ScheduleStartTime ?? ScheduleStartTime;
                ScheduleEndTime           = imported.ScheduleEndTime ?? ScheduleEndTime;
                CompactModelOnceADay      = imported.CompactModelOnceADay ?? CompactModelOnceADay;
                CompactAtNightOnly        = imported.CompactAtNightOnly ?? CompactAtNightOnly;
                ExitRevitOnIdle           = imported.ExitRevitOnIdle ?? ExitRevitOnIdle;
                ExitRevitAfterMinutes     = imported.ExitRevitAfterMinutes ?? ExitRevitAfterMinutes;
                OpenViewsOnSyncMode       = imported.OpenViewsOnSyncMode ?? OpenViewsOnSyncMode;
                PreventSyncWhenViewsOpenedOver = imported.PreventSyncWhenViewsOpenedOver ?? PreventSyncWhenViewsOpenedOver;

                StatusMessage = $"Imported from {Path.GetFileName(openFileDialog.FileName)} — click Save to persist";
                _logger?.LogInfo("Sync settings imported successfully (not yet saved)");
                return true;
            }
            catch (JsonException jsonEx)
            {
                StatusMessage = "Invalid file format";
                _logger?.LogError($"ImportFromFileAsync JSON error: {jsonEx.Message}", jsonEx);
                return false;
            }
            catch (Exception ex)
            {
                StatusMessage = $"Import failed: {ex.Message}";
                _logger?.LogError($"ImportFromFileAsync failed: {ex.Message}", ex);
                return false;
            }
            finally
            {
                IsBusy = false;
            }
        }
    }

    // ── Export Model ─────────────────────────────────────────────────────────────

    public class SyncSettingsExportModel
    {
        [JsonPropertyName("isEnabled")]
        public bool? IsEnabled { get; set; }

        [JsonPropertyName("syncAllTheTime")]
        public bool? SyncAllTheTime { get; set; }

        [JsonPropertyName("syncIntervalMinutes")]
        public int? SyncIntervalMinutes { get; set; }

        [JsonPropertyName("relinquishIntervalMinutes")]
        public int? RelinquishIntervalMinutes { get; set; }

        [JsonPropertyName("enableIdleSync")]
        public bool? EnableIdleSync { get; set; }

        [JsonPropertyName("idleTimeoutMinutes")]
        public int? IdleTimeoutMinutes { get; set; }

        [JsonPropertyName("enableRelinquish")]
        public bool? EnableRelinquish { get; set; }

        [JsonPropertyName("syncOnSave")]
        public bool? SyncOnSave { get; set; }

        [JsonPropertyName("syncEvenIfNoChanges")]
        public bool? SyncEvenIfNoChanges { get; set; }

        [JsonPropertyName("enableSchedule")]
        public bool? EnableSchedule { get; set; }

        [JsonPropertyName("scheduleStartTime")]
        public string? ScheduleStartTime { get; set; }

        [JsonPropertyName("scheduleEndTime")]
        public string? ScheduleEndTime { get; set; }

        [JsonPropertyName("compactModelOnceADay")]
        public bool? CompactModelOnceADay { get; set; }

        [JsonPropertyName("compactAtNightOnly")]
        public bool? CompactAtNightOnly { get; set; }

        [JsonPropertyName("exitRevitOnIdle")]
        public bool? ExitRevitOnIdle { get; set; }

        [JsonPropertyName("exitRevitAfterMinutes")]
        public int? ExitRevitAfterMinutes { get; set; }

        [JsonPropertyName("openViewsOnSyncMode")]
        public int? OpenViewsOnSyncMode { get; set; }

        [JsonPropertyName("preventSyncWhenViewsOpenedOver")]
        public int? PreventSyncWhenViewsOpenedOver { get; set; }
    }
}
