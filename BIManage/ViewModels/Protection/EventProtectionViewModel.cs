using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Core.Identity;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Protection;
using Microsoft.Win32;

namespace BIManageRevit.BIManage.ViewModels.Protection
{
    public class EventProtectionViewModel : INotifyPropertyChanged
    {
        private readonly string _revitUsername;
        private readonly string? _profileId;
        private readonly EventProtectionRepository? _repository;
        private readonly EventProtectionSyncService? _syncService;
        private readonly string? _modelGuid;
        private readonly ILogger? _logger;
        private readonly int? _projectId;
        private readonly bool _isCompanyAdmin;
        /// <summary>
        /// Current project's GUID (for project-level overrides). Optional — when set,
        /// project-admin overrides POST with this projectId so the server's Nullable&lt;Guid&gt;
        /// validator passes. The dialog populates it from registered_models by modelGuid.
        /// </summary>
        public string? CurrentProjectId { get; set; }
        private ObservableCollection<EventSettingViewModel> _settings;
        private ObservableCollection<EventSettingViewModel> _filteredSettings;
        private EventSettingViewModel? _selectedSetting;
        private bool _isLoading;

        public EventProtectionViewModel(
            string revitUsername,
            EventProtectionRepository? repository = null,
            int? projectId = null,
            ILogger? logger = null,
            string? profileId = null,
            EventProtectionSyncService? syncService = null,
            string? modelGuid = null,
            bool isCompanyAdmin = false)
        {
            _revitUsername = revitUsername;
            _profileId = profileId;
            _repository = repository;
            _syncService = syncService;
            _modelGuid = modelGuid;
            _projectId = projectId;
            _logger = logger;
            _isCompanyAdmin = isCompanyAdmin;
            _settings = new ObservableCollection<EventSettingViewModel>();
            _filteredSettings = new ObservableCollection<EventSettingViewModel>();

            // Ensure current user's profileId→displayName mapping is in cache
            if (!string.IsNullOrEmpty(_profileId))
                UserDisplayNameCache.Set(_profileId, _revitUsername);
        }

        public ObservableCollection<EventSettingViewModel> Settings
        {
            get => _settings;
            set { _settings = value; OnPropertyChanged(); }
        }

        public ObservableCollection<EventSettingViewModel> FilteredSettings
        {
            get => _filteredSettings;
            set { _filteredSettings = value; OnPropertyChanged(); }
        }

        public EventSettingViewModel? SelectedSetting
        {
            get => _selectedSetting;
            set { _selectedSetting = value; OnPropertyChanged(); }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public void LoadSettings()
        {
            IsLoading = true;
            Settings.Clear();

            if (_repository != null)
            {
                var dbSettings = _repository.LoadEventProtectionSettings(_projectId, _modelGuid);
                _logger?.LogInfo($"Loaded {dbSettings.Count} event protection settings from database");

                foreach (var setting in dbSettings)
                {
                    var vm = EventSettingViewModel.FromModel(setting);
                    // Map profileId → displayName for UI (resolves all cached users, not just current)
                    if (!string.IsNullOrWhiteSpace(vm.ModifiedBy))
                        vm.ModifiedBy = UserDisplayNameCache.GetDisplayName(vm.ModifiedBy);
                    if (!string.IsNullOrWhiteSpace(vm.CreatedBy))
                        vm.CreatedBy = UserDisplayNameCache.GetDisplayName(vm.CreatedBy);
                    Settings.Add(vm);
                }
            }
            else
            {
                _logger?.LogWarning("EventProtectionRepository is null - no data to load");
            }

            UpdateRowNumbers();
            IsLoading = false;
        }

        /// <summary>
        /// Loads event protection settings, preferring fresh data from the API but falling
        /// back to the local DB so the dialog never gets stuck on "Loading...".
        ///
        /// Why the local-DB-first pattern: the previous implementation awaited the API
        /// fetch BEFORE clearing the Loading overlay. When auth was mid-refresh, the
        /// network was slow, or HttpClient hadn't received the response yet, the await
        /// could sit for the full 30-second client timeout — during which the dialog
        /// showed only "Loading...". Users reported having to sign out/sign in (or
        /// reinstall) to clear that state. Now the local DB renders instantly, so the
        /// dialog is always usable; the API call refreshes in the background and merges
        /// its result when it returns. If the API fails or times out, the local data
        /// stays on screen — no broken state, no need to bounce the session.
        /// </summary>
        public async Task FetchFromApiAsync()
        {
            // Step 1 — paint local data immediately. LoadSettings is synchronous against
            // SQLite and clears IsLoading itself, so the Loading overlay disappears within
            // milliseconds of opening the dialog.
            try { LoadSettings(); }
            catch (Exception loadEx)
            {
                _logger?.LogWarning($"LoadSettings (initial render) failed: {loadEx.Message}");
                // Ensure the overlay clears even if the local load throws.
                IsLoading = false;
            }

            // Step 2 — refresh from the API in the background. If anything goes wrong
            // (auth in mid-refresh, network blip, server 5xx, 30 s HTTP timeout), the
            // local-DB data from Step 1 stays on screen.
            if (_syncService == null || string.IsNullOrEmpty(_modelGuid))
                return;

            try
            {
                var settings = await _syncService.FetchByModelGuidFromApiAsync(_modelGuid);
                _logger?.LogInfo($"Fetched {settings.Count} event protections from API for model {_modelGuid}");

                // Only replace the rendered list if the API returned something. An empty
                // list usually means "auth not ready yet" (FetchByModelGuidFromApiAsync
                // returns [] when !_httpClient.IsAuthenticated) — keep the local data in
                // that case rather than blanking the dialog.
                if (settings.Count == 0)
                {
                    _logger?.LogInfo("API returned 0 event protections — keeping local-DB data on screen.");
                    return;
                }

                Settings.Clear();
                int rowNum = 1;
                foreach (var setting in settings)
                {
                    var vm = EventSettingViewModel.FromModel(setting);
                    if (!string.IsNullOrWhiteSpace(vm.ModifiedBy))
                        vm.ModifiedBy = UserDisplayNameCache.GetDisplayName(vm.ModifiedBy);
                    if (!string.IsNullOrWhiteSpace(vm.CreatedBy))
                        vm.CreatedBy = UserDisplayNameCache.GetDisplayName(vm.CreatedBy);
                    vm.RowNumber = rowNum++;
                    Settings.Add(vm);
                }
            }
            catch (Exception ex)
            {
                // Local data is already on screen from Step 1 — just log and move on.
                _logger?.LogWarning($"FetchFromApiAsync (background refresh) failed: {ex.Message}");
            }
            finally
            {
                // Defensive — LoadSettings already cleared this, but a second clear is
                // harmless and guarantees the overlay never sticks even if Step 1 was
                // bypassed for some reason.
                IsLoading = false;
            }
        }

        public async Task<bool> SaveSettingAsync(EventSettingViewModel settingVm)
        {
            if (_repository == null)
            {
                _logger?.LogWarning("Cannot save event protection - repository is null");
                return false;
            }

            try
            {
                var model = settingVm.ToModel();
                var profileId = _profileId ?? _revitUsername;
                var displayName = UserDisplayNameCache.GetDisplayName(profileId);
                // Store profile ID in database; frontend resolves to display name
                model.ModifiedBy = profileId;
                model.ModifiedAt = DateTime.UtcNow;

                // Set CreatedBy for new records
                bool isNew = string.IsNullOrEmpty(model.Id);
                if (isNew)
                    model.CreatedBy = profileId;

                // Project admin editing a company-level protection → reuse or create a project-level override
                // (server rejects PUT/PATCH on company-level rows from non-CompanyAdmins).
                bool isProjectAdminOverride = !_isCompanyAdmin && !isNew && model.IsCompanyLevel;
                bool isOverrideUpdate = false;
                if (isProjectAdminOverride)
                {
                    var existingOverride = FindExistingProjectOverride(model);
                    if (existingOverride != null)
                    {
                        model.Id = existingOverride.Id;
                        model.IsCompanyLevel = false;
                        model.ProjectId = !string.IsNullOrWhiteSpace(existingOverride.ProjectId) ? existingOverride.ProjectId : CurrentProjectId;
                        model.ModelGuid = null;
                        model.CreatedBy = existingOverride.CreatedBy;
                        isOverrideUpdate = true;
                        _logger?.LogInfo($"Project admin override (event dialog): updating existing project-level override {model.Id} for '{model.ProtectionName}'");
                    }
                    else
                    {
                        model.Id = string.Empty; // force INSERT in repo (new override row)
                        model.IsCompanyLevel = false;
                        model.ProjectId = CurrentProjectId;
                        model.ModelGuid = null;
                        model.CreatedBy = profileId;
                        isNew = true;
                        _logger?.LogInfo($"Project admin override (event dialog): creating new project-level override for '{model.ProtectionName}' (projectId={CurrentProjectId ?? "<null>"})");
                    }
                    // Reflect new scope in the UI VM
                    settingVm.IsCompanyLevel = false;
                }
                // Company admin editing what the local cache surfaced as a project-level override
                // → redirect the save to the COMPANY-LEVEL row for the same DummyCommandId.
                //
                // Without this redirect, the user's edit lands on a stale project-level override
                // (created at some earlier point, possibly when they had a different role).
                // The server's GET endpoint then keeps returning the company-level seed for the
                // model's project, and the user's edit is "stuck" — local Revit shows it (cache
                // precedence picks the override) but other clients and the actual enforcement
                // see the unchanged company-level value.
                else if (_isCompanyAdmin && !model.IsCompanyLevel && !isNew)
                {
                    var companyRow = FindCompanyLevelRow(model.DummyCommandId);
                    if (companyRow != null)
                    {
                        _logger?.LogInfo($"Company admin redirect (event dialog): replacing edit on project-level override {model.Id} with edit on company-level row {companyRow.Id} for '{model.ProtectionName}'.");
                        model.Id = companyRow.Id;
                        model.IsCompanyLevel = true;
                        model.ProjectId = null;
                        model.ModelGuid = null;
                        // Preserve the server's CreatedBy so we don't accidentally rewrite history.
                        if (!string.IsNullOrWhiteSpace(companyRow.CreatedBy))
                            model.CreatedBy = companyRow.CreatedBy;
                        // Reflect new scope in the UI VM so the row stops looking like an override.
                        settingVm.Id = companyRow.Id;
                        settingVm.IsCompanyLevel = true;
                    }
                    else
                    {
                        _logger?.LogWarning($"Company admin edited project-level override for '{model.ProtectionName}' but no company-level row exists locally — saving as-is. The edit may not propagate to other clients.");
                    }
                }

                // Generic fallback: any non-Company row needs a valid projectId or the
                // server rejects the POST/PUT (LevelScope=2 with projectId=null → 400).
                // The override branches above already set this for Project-Admin overrides;
                // this catches Company-Admin creating a new project-scoped event directly,
                // which is the cause of "project-wide event from Revit doesn't reflect on web".
                if (!model.IsCompanyLevel
                    && string.IsNullOrWhiteSpace(model.ProjectId)
                    && !string.IsNullOrWhiteSpace(CurrentProjectId))
                {
                    model.ProjectId = CurrentProjectId;
                    _logger?.LogInfo($"Event protection save: resolved ProjectId from CurrentProjectId for project-scoped row '{model.ProtectionName}' (projectId={CurrentProjectId})");
                }

                var newId = await _repository.SaveEventProtectionAsync(model, _projectId);
                if (!string.IsNullOrEmpty(newId))
                {
                    if (isNew)
                    {
                        settingVm.Id = newId;
                        model.Id = newId;
                    }
                    else if (isOverrideUpdate)
                    {
                        settingVm.Id = model.Id;
                    }

                    // Restore display name for UI
                    settingVm.ModifiedBy = displayName;
                    settingVm.ModifiedAt = DateTime.UtcNow;
                    _logger?.LogInfo($"Saved event protection: {settingVm.ProtectionName} (id: {newId})");

                    // Push to server (non-blocking, non-critical)
                    if (_syncService != null)
                    {
                        try
                        {
                            if (isProjectAdminOverride && isOverrideUpdate)
                                await _syncService.UpdateEventProtectionAsync(model.Id, model, _modelGuid);
                            else if (isProjectAdminOverride)
                                await _syncService.CreateEventProtectionAsync(model);
                            else if (isNew)
                                await _syncService.CreateEventProtectionAsync(model);
                            else
                                await _syncService.UpdateEventProtectionAsync(model.Id, model, _modelGuid);
                        }
                        catch (Exception syncEx)
                        {
                            // Promoted to Warning so the failure is visible at standard log level
                            // — silent Debug here meant the user thought a Save succeeded when the
                            // server never received the change, and the next refresh overwrote the
                            // local edit with stale server data.
                            _logger?.LogWarning($"Event protection API sync FAILED (server did NOT persist this edit) for '{settingVm.ProtectionName}': {syncEx.GetType().Name}: {syncEx.Message}");
                        }
                    }

                    return true;
                }

                _logger?.LogWarning($"Failed to save event protection: {settingVm.ProtectionName}");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error saving event protection {settingVm.ProtectionName}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Finds an existing project-level override for the given company-level protection
        /// (matched by DummyCommandId) so toggles/edits update it instead of creating duplicates.
        /// </summary>
        private EventProtectionSettings? FindExistingProjectOverride(EventProtectionSettings companyProtection)
        {
            if (companyProtection == null) return null;
            var dummyId = companyProtection.DummyCommandId?.Trim();
            if (string.IsNullOrEmpty(dummyId)) return null;

            foreach (var vm in _settings)
            {
                if (vm.IsCompanyLevel) continue;
                if (string.Equals(vm.Id, companyProtection.Id, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(vm.DummyCommandId?.Trim(), dummyId, StringComparison.OrdinalIgnoreCase)) continue;
                return vm.ToModel();
            }
            return null;
        }

        /// <summary>
        /// Inverse of FindExistingProjectOverride: locates the company-level row for the given
        /// DummyCommandId. Used to redirect CompanyAdmin saves away from stale project-level
        /// overrides so their edits actually propagate company-wide.
        /// </summary>
        private EventProtectionSettings? FindCompanyLevelRow(string? dummyCommandId)
        {
            if (string.IsNullOrWhiteSpace(dummyCommandId)) return null;
            var dummyId = dummyCommandId.Trim();
            foreach (var vm in _settings)
            {
                if (!vm.IsCompanyLevel) continue;
                if (!string.Equals(vm.DummyCommandId?.Trim(), dummyId, StringComparison.OrdinalIgnoreCase)) continue;
                return vm.ToModel();
            }
            return null;
        }

        /// <summary>
        /// Deletes an event protection setting from local DB and pushes the delete to the server.
        /// </summary>
        public async Task<bool> DeleteSettingAsync(string settingId, string? dummyCommandId = null)
        {
            if (_repository == null || string.IsNullOrEmpty(settingId))
            {
                _logger?.LogWarning("Cannot delete event protection - repository is null or settingId is empty");
                return false;
            }

            try
            {
                await _repository.DeleteEventProtectionAsync(settingId);
                _logger?.LogInfo($"Deleted event protection from local DB: {dummyCommandId ?? settingId}");

                // Remove from UI collection
                var toRemove = Settings.FirstOrDefault(s => s.Id == settingId);
                if (toRemove != null)
                    Settings.Remove(toRemove);

                // Push delete to server (non-blocking, non-critical)
                if (_syncService != null)
                {
                    try
                    {
                        await _syncService.DeleteEventProtectionAsync(settingId, dummyCommandId);
                    }
                    catch (Exception syncEx)
                    {
                        _logger?.LogWarning($"Event protection delete API sync FAILED (server did NOT persist this delete) for {dummyCommandId ?? settingId}: {syncEx.GetType().Name}: {syncEx.Message}");
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error deleting event protection {settingId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Toggles the enabled state of an event protection and syncs via the dedicated PATCH endpoint.
        /// </summary>
        public async Task<bool> ToggleSettingEnabledAsync(EventSettingViewModel settingVm)
        {
            if (_repository == null)
            {
                _logger?.LogWarning("Cannot toggle event protection - repository is null");
                return false;
            }

            try
            {
                var model = settingVm.ToModel();
                var profileId = _profileId ?? _revitUsername;
                model.ModifiedBy = profileId;
                model.ModifiedAt = DateTime.UtcNow;

                // Pristine seeded rows (System defaults) have no server Id yet. Capture this
                // BEFORE the local save mints a local GUID — otherwise the standard branch
                // PATCHes with that local id and the server returns 404 because the row was
                // never created. The toggle then "saves" locally but silently fails to sync,
                // and the next refresh reverts the row to its un-toggled server state.
                bool isNew = string.IsNullOrEmpty(model.Id);
                if (isNew)
                    model.CreatedBy = profileId;

                // Project admin toggling a company-level protection → reuse or create a project-level override
                // (server rejects PATCH on company-level rows from non-CompanyAdmins with 404
                // "Only company admins (RLID001) can toggle company-level default protections.")
                bool isProjectAdminOverride = !_isCompanyAdmin && model.IsCompanyLevel && !string.IsNullOrEmpty(model.Id);
                bool isOverrideUpdate = false;
                if (isProjectAdminOverride)
                {
                    var existingOverride = FindExistingProjectOverride(model);
                    if (existingOverride != null)
                    {
                        model.Id = existingOverride.Id;
                        model.IsCompanyLevel = false;
                        model.ProjectId = !string.IsNullOrWhiteSpace(existingOverride.ProjectId) ? existingOverride.ProjectId : CurrentProjectId;
                        model.ModelGuid = null;
                        model.CreatedBy = existingOverride.CreatedBy;
                        isOverrideUpdate = true;
                        _logger?.LogInfo($"Project admin override (event toggle): updating existing project-level override {model.Id} for '{model.ProtectionName}'");
                    }
                    else
                    {
                        model.Id = string.Empty; // force INSERT in repo
                        model.IsCompanyLevel = false;
                        model.ProjectId = CurrentProjectId;
                        model.ModelGuid = null;
                        model.CreatedBy = profileId;
                        _logger?.LogInfo($"Project admin override (event toggle): creating new project-level override for '{model.ProtectionName}' (projectId={CurrentProjectId ?? "<null>"})");
                    }
                    settingVm.IsCompanyLevel = false;
                }

                var newId = await _repository.SaveEventProtectionAsync(model, _projectId);
                if (!string.IsNullOrEmpty(newId))
                {
                    settingVm.Id = newId;
                    settingVm.ModifiedBy = UserDisplayNameCache.GetDisplayName(profileId);
                    settingVm.ModifiedAt = DateTime.UtcNow;

                    // Push to server (non-critical)
                    if (_syncService != null)
                    {
                        try
                        {
                            if (isProjectAdminOverride && isOverrideUpdate)
                            {
                                // Existing project-level override → PUT the full row.
                                // The backend's PATCH /is-enabled sub-resource handler does
                                // NOT broadcast SignalR to web clients, so toggles via that
                                // path leave the web UI stale until next refresh. PUT /for-Model/{id}
                                // is the same endpoint the Save button uses, which the backend
                                // does broadcast on.
                                model.Id = newId;
                                await _syncService.UpdateEventProtectionAsync(newId, model, _modelGuid);
                            }
                            else if (isProjectAdminOverride)
                            {
                                // First-time override → POST a new project-level row
                                model.Id = newId;
                                await _syncService.CreateEventProtectionAsync(model);
                            }
                            else if (isNew)
                            {
                                // First-time toggle on a pristine seeded row → POST creates the
                                // server row. PATCH would 404 because the server has no record
                                // of this protection yet, and the user's toggle would silently
                                // drop on the next refresh.
                                model.Id = newId;
                                await _syncService.CreateEventProtectionAsync(model);
                            }
                            else if (!string.IsNullOrEmpty(settingVm.Id))
                            {
                                // Standard path: PUT the existing row (full update).
                                // We deliberately use PUT /for-Model/{id} instead of the lighter
                                // PATCH /is-enabled sub-resource because the backend's PUT
                                // handler broadcasts the ProtectionSettingsChange SignalR event
                                // to subscribed web clients, while the PATCH /is-enabled handler
                                // does not — leaving the web UI stale until the next manual
                                // refresh. Routing the toggle through PUT keeps Revit and web
                                // perfectly in sync in real time.
                                //
                                // Wire-format rule (handled inside MapToUpdateForModelRequest):
                                //   Company-level row → body omits modelGuid (stays company-scoped).
                                //   Project / Model-level row → body includes the row's own GUID,
                                //   or falls back to the current Revit model GUID.
                                var toggleModelGuid = settingVm.IsCompanyLevel ? null : _modelGuid;
                                var toggleResult = await _syncService.UpdateEventProtectionWithResultAsync(
                                    settingVm.Id, model, toggleModelGuid);
                                if (toggleResult == EventToggleResult.PermissionDenied)
                                {
                                    _logger?.LogWarning($"Standard PUT denied by server (permission). Falling back to project-level override for '{settingVm.ProtectionName}'.");
                                    await CreateProjectOverrideFallbackAsync(settingVm, model);
                                }
                            }
                        }
                        catch (Exception syncEx)
                        {
                            _logger?.LogWarning($"Event protection toggle API sync FAILED (server did NOT persist this toggle) for '{settingVm.ProtectionName}': {syncEx.GetType().Name}: {syncEx.Message}");
                        }
                    }

                    return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error toggling event protection {settingVm.ProtectionName}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Fallback path when a standard PATCH on a company-level row was permission-denied
        /// by the server. Creates (or updates) a project-level override locally and POSTs it,
        /// so the user's toggle intent reaches the server even if their cached role
        /// classification (CompanyAdmin) didn't match the server's actual role (ProjectAdmin).
        /// </summary>
        private async Task CreateProjectOverrideFallbackAsync(EventSettingViewModel settingVm, EventProtectionSettings originalModel)
        {
            try
            {
                if (_repository == null || _syncService == null) return;
                var profileId = _profileId ?? _revitUsername;

                // Build an override model from the user's current toggle state
                var overrideModel = settingVm.ToModel();
                overrideModel.ModifiedBy = profileId;
                overrideModel.ModifiedAt = DateTime.UtcNow;

                var existingOverride = FindExistingProjectOverride(overrideModel);
                bool isOverrideUpdate;
                if (existingOverride != null)
                {
                    overrideModel.Id = existingOverride.Id;
                    overrideModel.IsCompanyLevel = false;
                    overrideModel.ProjectId = !string.IsNullOrWhiteSpace(existingOverride.ProjectId)
                        ? existingOverride.ProjectId : CurrentProjectId;
                    overrideModel.ModelGuid = null;
                    overrideModel.CreatedBy = existingOverride.CreatedBy;
                    isOverrideUpdate = true;
                    _logger?.LogInfo($"Override fallback: updating existing project-level override {overrideModel.Id} for '{overrideModel.ProtectionName}'");
                }
                else
                {
                    overrideModel.Id = string.Empty; // force INSERT
                    overrideModel.IsCompanyLevel = false;
                    overrideModel.ProjectId = CurrentProjectId;
                    overrideModel.ModelGuid = null;
                    overrideModel.CreatedBy = profileId;
                    isOverrideUpdate = false;
                    _logger?.LogInfo($"Override fallback: creating new project-level override for '{overrideModel.ProtectionName}' (projectId={CurrentProjectId ?? "<null>"})");
                }
                settingVm.IsCompanyLevel = false;

                var newOverrideId = await _repository.SaveEventProtectionAsync(overrideModel, _projectId);
                if (string.IsNullOrEmpty(newOverrideId)) return;
                settingVm.Id = newOverrideId;
                overrideModel.Id = newOverrideId;

                if (isOverrideUpdate)
                    await _syncService.ToggleEventProtectionAsync(newOverrideId, settingVm.Enabled, _modelGuid);
                else
                    await _syncService.CreateEventProtectionAsync(overrideModel);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Override fallback failed for '{settingVm.ProtectionName}': {ex.Message}");
            }
        }

        public void ApplyFilters(string searchText, string statusFilter, string modeFilter, string eventTypeFilter = "")
        {
            var filtered = Settings.AsEnumerable();

            if (!string.IsNullOrWhiteSpace(searchText))
            {
                filtered = filtered.Where(s =>
                    s.ProtectionName.ToLower().Contains(searchText) ||
                    s.DummyCommandId.ToLower().Contains(searchText) ||
                    s.EventTypeDisplay.ToLower().Contains(searchText));
            }

            if (!string.IsNullOrWhiteSpace(statusFilter))
            {
                var isEnabled = statusFilter == "Active";
                filtered = filtered.Where(s => s.Enabled == isEnabled);
            }

            if (!string.IsNullOrWhiteSpace(modeFilter))
            {
                var mode = modeFilter switch
                {
                    "Notify" => InterventionMode.Notify,
                    "Assist" => InterventionMode.Assist,
                    "Protect" => InterventionMode.Protect,
                    _ => (InterventionMode?)null
                };

                if (mode.HasValue)
                {
                    filtered = filtered.Where(s => s.Mode == mode.Value);
                }
            }

            if (!string.IsNullOrWhiteSpace(eventTypeFilter))
            {
                if (eventTypeFilter == "DC")
                {
                    // Document Changed group: FileImporting(20), CADExploding(21), CopyMonitor(22), TransferProjectStandards(23), CADImportCompleted(24)
                    filtered = filtered.Where(s => (int)s.EventType >= 20 && (int)s.EventType <= 24);
                }
                else if (int.TryParse(eventTypeFilter, out var eventTypeInt))
                {
                    var eventType = (RevitEventType)eventTypeInt;
                    filtered = filtered.Where(s => s.EventType == eventType);
                }
            }

            FilteredSettings.Clear();
            var rowNum = 1;
            foreach (var setting in filtered)
            {
                setting.RowNumber = rowNum++;
                FilteredSettings.Add(setting);
            }
        }

        private void UpdateRowNumbers()
        {
            var rowNum = 1;
            foreach (var setting in Settings)
            {
                setting.RowNumber = rowNum++;
            }
            FilteredSettings = new ObservableCollection<EventSettingViewModel>(Settings);
        }

        #region Import / Export

        public async Task<(bool Success, string Message)> ImportFromJsonAsync()
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "ZE Files (*.ze)|*.ze|All Files (*.*)|*.*",
                    Title = "Import Event Protection"
                };

                if (openFileDialog.ShowDialog() != true)
                    return (false, "Import cancelled");

                IsLoading = true;
                _logger?.LogInfo($"Importing event protection from: {openFileDialog.FileName}");

                var jsonContent = await Task.Run(() => File.ReadAllText(openFileDialog.FileName, Encoding.UTF8));

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };

                var importedSettings = JsonSerializer.Deserialize<List<EventProtectionExportModel>>(jsonContent, options);

                if (importedSettings == null || importedSettings.Count == 0)
                    return (false, "No event protection settings found in file");

                var importedCount = 0;

                foreach (var imported in importedSettings)
                {
                    var existing = Settings.FirstOrDefault(s =>
                        s.DummyCommandId.Equals(imported.DummyCommandId ?? "", StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        existing.Mode = ParseInterventionMode(imported.Mode);
                        existing.Enabled = imported.IsEnabled ?? existing.Enabled;
                        existing.IsCompanyLevel = imported.IsCompanyLevel ?? existing.IsCompanyLevel;
                        existing.CustomMessage = imported.CustomMessage;
                        existing.CaptureBeforeScreenshot = imported.CaptureBeforeScreenshot ?? false;
                        existing.CaptureAfterScreenshot = imported.CaptureAfterScreenshot ?? false;
                        existing.RequireComment = imported.RequireComment ?? false;
                        existing.AllowAdminOverride = imported.AllowAdminOverride ?? true;
                        existing.SendEmail = imported.SendEmail ?? false;
                        existing.ModifiedBy = _revitUsername;
                        existing.ModifiedAt = DateTime.UtcNow;

                        var saved = await SaveSettingAsync(existing);
                        if (saved) importedCount++;
                    }
                }

                LoadSettings();
                _logger?.LogInfo($"Imported {importedCount} event protection settings");

                if (importedCount == 0)
                    return (false, "No matching event protections found to update");

                return (true, $"Imported {importedCount} event protection settings successfully");
            }
            catch (JsonException jsonEx)
            {
                _logger?.LogError($"JSON parsing error: {jsonEx.Message}", jsonEx);
                return (false, $"Invalid JSON format: {jsonEx.Message}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error importing event protection: {ex.Message}", ex);
                return (false, $"Error importing: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task<(bool Success, string Message)> ExportToJsonAsync()
        {
            try
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "ZE Files (*.ze)|*.ze|All Files (*.*)|*.*",
                    Title = "Export Event Protection",
                    FileName = $"EventProtection_Export_{DateTime.Now:yyyyMMdd_HHmmss}.ze"
                };

                if (saveFileDialog.ShowDialog() != true)
                    return (false, "Export cancelled");

                IsLoading = true;
                _logger?.LogInfo($"Exporting event protection to: {saveFileDialog.FileName}");

                var exportModels = Settings.Select(s => new EventProtectionExportModel
                {
                    DummyCommandId = s.DummyCommandId,
                    ProtectionName = s.ProtectionName,
                    EventType = s.EventType.ToString(),
                    Mode = s.Mode.ToString(),
                    IsEnabled = s.Enabled,
                    IsCompanyLevel = s.IsCompanyLevel,
                    Scope = s.Scope,
                    CustomMessage = s.CustomMessage,
                    CaptureBeforeScreenshot = s.CaptureBeforeScreenshot,
                    CaptureAfterScreenshot = s.CaptureAfterScreenshot,
                    RequireComment = s.RequireComment,
                    AllowAdminOverride = s.AllowAdminOverride,
                    SendEmail = s.SendEmail,
                    ModifiedBy = s.ModifiedBy,
                    ModifiedAt = s.ModifiedAt?.ToString("yyyy-MM-dd HH:mm:ss")
                }).ToList();

                var jsonOptions = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var json = JsonSerializer.Serialize(exportModels, jsonOptions);
                await Task.Run(() => File.WriteAllText(saveFileDialog.FileName, json, Encoding.UTF8));

                _logger?.LogInfo($"Exported {exportModels.Count} event protection settings");
                return (true, $"Exported {exportModels.Count} settings to {Path.GetFileName(saveFileDialog.FileName)}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error exporting event protection: {ex.Message}", ex);
                return (false, $"Error exporting: {ex.Message}");
            }
            finally
            {
                IsLoading = false;
            }
        }

        private static InterventionMode ParseInterventionMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return InterventionMode.Notify;
            return value.ToLowerInvariant() switch
            {
                "notify" or "0" => InterventionMode.Notify,
                "assist" or "1" => InterventionMode.Assist,
                "protect" or "2" => InterventionMode.Protect,
                _ => InterventionMode.Notify
            };
        }

        #endregion

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class EventProtectionExportModel
    {
        [JsonPropertyName("dummyCommandId")]
        public string? DummyCommandId { get; set; }

        [JsonPropertyName("protectionName")]
        public string? ProtectionName { get; set; }

        [JsonPropertyName("eventType")]
        public string? EventType { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("isEnabled")]
        public bool? IsEnabled { get; set; }

        [JsonPropertyName("isCompanyLevel")]
        public bool? IsCompanyLevel { get; set; }

        [JsonPropertyName("scope")]
        public string? Scope { get; set; }

        [JsonPropertyName("customMessage")]
        public string? CustomMessage { get; set; }

        [JsonPropertyName("captureBeforeScreenshot")]
        public bool? CaptureBeforeScreenshot { get; set; }

        [JsonPropertyName("captureAfterScreenshot")]
        public bool? CaptureAfterScreenshot { get; set; }

        [JsonPropertyName("requireComment")]
        public bool? RequireComment { get; set; }

        [JsonPropertyName("allowAdminOverride")]
        public bool? AllowAdminOverride { get; set; }

        [JsonPropertyName("sendEmail")]
        public bool? SendEmail { get; set; }

        [JsonPropertyName("modifiedBy")]
        public string? ModifiedBy { get; set; }

        [JsonPropertyName("modifiedAt")]
        public string? ModifiedAt { get; set; }
    }

    public class EventSettingViewModel : INotifyPropertyChanged
    {
        private string _id = string.Empty;
        private int _rowNumber;
        private string _protectionName = "";
        private string _dummyCommandId = "";
        private RevitEventType _eventType;
        private InterventionMode _mode;
        private bool _enabled;
        private bool _isCompanyLevel;
        private string? _customMessage;
        private string? _configurationJson;
        private bool _captureBeforeScreenshot;
        private bool _captureAfterScreenshot;
        private bool _requireComment;
        private bool _allowAdminOverride;
        private bool _sendEmail;
        private string? _customMessageImagePath;
        private string? _projectId;
        private string? _companyId;
        private string? _modelGuid;
        private string? _createdBy;
        private string? _modifiedBy;
        private DateTime? _modifiedAt;

        public string Id
        {
            get => _id;
            set { _id = value; OnPropertyChanged(); }
        }

        public int RowNumber
        {
            get => _rowNumber;
            set { _rowNumber = value; OnPropertyChanged(); }
        }

        public string ProtectionName
        {
            get => _protectionName;
            set { _protectionName = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProtectionNameTooltip)); }
        }

        public string DummyCommandId
        {
            get => _dummyCommandId;
            set { _dummyCommandId = value; OnPropertyChanged(); OnPropertyChanged(nameof(ProtectionNameTooltip)); }
        }

        public RevitEventType EventType
        {
            get => _eventType;
            set { _eventType = value; OnPropertyChanged(); OnPropertyChanged(nameof(EventTypeDisplay)); }
        }

        public InterventionMode Mode
        {
            get => _mode;
            set { _mode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeDisplay)); OnPropertyChanged(nameof(ModeTooltip)); }
        }

        public bool Enabled
        {
            get => _enabled;
            set { _enabled = value; OnPropertyChanged(); }
        }

        public bool IsCompanyLevel
        {
            get => _isCompanyLevel;
            set { _isCompanyLevel = value; OnPropertyChanged(); OnPropertyChanged(nameof(Scope)); OnPropertyChanged(nameof(IsProjectLevel)); }
        }

        /// <summary>
        /// True when the protection's scope is Project (or any non-Company scope).
        /// Drives the visibility of the row-level Delete button — Project-scoped
        /// rows are user-deletable, Company-scoped rows are not.
        /// </summary>
        public bool IsProjectLevel => !_isCompanyLevel;

        public string? CustomMessage
        {
            get => _customMessage;
            set { _customMessage = value; OnPropertyChanged(); }
        }

        public string? ConfigurationJson
        {
            get => _configurationJson;
            set { _configurationJson = value; OnPropertyChanged(); }
        }

        public bool CaptureBeforeScreenshot
        {
            get => _captureBeforeScreenshot;
            set { _captureBeforeScreenshot = value; OnPropertyChanged(); }
        }

        public bool CaptureAfterScreenshot
        {
            get => _captureAfterScreenshot;
            set { _captureAfterScreenshot = value; OnPropertyChanged(); }
        }

        public bool RequireComment
        {
            get => _requireComment;
            set { _requireComment = value; OnPropertyChanged(); }
        }

        public bool AllowAdminOverride
        {
            get => _allowAdminOverride;
            set { _allowAdminOverride = value; OnPropertyChanged(); }
        }

        public bool SendEmail
        {
            get => _sendEmail;
            set { _sendEmail = value; OnPropertyChanged(); }
        }

        public string? CustomMessageImagePath
        {
            get => _customMessageImagePath;
            set { _customMessageImagePath = value; OnPropertyChanged(); }
        }

        public string? ProjectId
        {
            get => _projectId;
            set { _projectId = value; OnPropertyChanged(); }
        }

        public string? CompanyId
        {
            get => _companyId;
            set { _companyId = value; OnPropertyChanged(); }
        }

        public string? ModelGuid
        {
            get => _modelGuid;
            set { _modelGuid = value; OnPropertyChanged(); }
        }

        public string? CreatedBy
        {
            get => _createdBy;
            set { _createdBy = value; OnPropertyChanged(); }
        }

        public string? ModifiedBy
        {
            get => _modifiedBy;
            set { _modifiedBy = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModifiedByTooltip)); }
        }

        public DateTime? ModifiedAt
        {
            get => _modifiedAt;
            set { _modifiedAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModifiedByTooltip)); OnPropertyChanged(nameof(UpdatedAtDisplay)); }
        }

        // Computed properties
        public string Scope => IsCompanyLevel ? "Company" : "Project";

        public string ModeDisplay => Mode switch
        {
            InterventionMode.Notify => "Notify",
            InterventionMode.Assist => "Assist",
            InterventionMode.Protect => "Protect",
            _ => "Unknown"
        };

        public string ModeTooltip => Mode switch
        {
            InterventionMode.Notify => "Notify: Log event execution without blocking",
            InterventionMode.Assist => "Assist: Show confirmation dialog before proceeding",
            InterventionMode.Protect => "Protect: Require password/OTP to proceed",
            _ => ""
        };

        private string? _toolTips;
        public string? ToolTips
        {
            get => _toolTips;
            set { _toolTips = value; OnPropertyChanged(); OnPropertyChanged(nameof(Description)); OnPropertyChanged(nameof(ProtectionNameTooltip)); }
        }

        /// <summary>
        /// True when a Company-scoped row exists for this protection. Project Admins
        /// must not be allowed to disable rows where this is true. Set from API.
        /// </summary>
        private bool _hasCompanyScope;
        public bool HasCompanyScope
        {
            get => _hasCompanyScope;
            set { _hasCompanyScope = value; OnPropertyChanged(); }
        }

        public string Description => !string.IsNullOrWhiteSpace(_toolTips) ? _toolTips! : GetDescription(DummyCommandId);

        public string? ProtectionNameTooltip => !string.IsNullOrWhiteSpace(_toolTips)
            ? _toolTips
            : !string.IsNullOrWhiteSpace(GetDescription(DummyCommandId))
                ? GetDescription(DummyCommandId)
                : null;

        private static string GetDescription(string dummyCommandId) => dummyCommandId switch
        {
            "Ze_CADExplodeProtection" => "Prevents exploding imported CAD files which creates unmanaged elements",
            "Ze_CADImportProtection" => "Controls importing CAD files that bloat file size and add non-native geometry",
            "Ze_DocumentExportingProtection" => "Restricts exporting the model to external formats to prevent unauthorized sharing",
            "Ze_DocumentPrintingProtection" => "Controls printing and PDF export to prevent unauthorized document distribution",
            "Ze_DuplicateUserSessionProtection" => "Detects same user opening the model on multiple machines causing sync conflicts",
            "Ze_FamilyLibrarySettings" => "Restricts loading families from unauthorized folders to enforce standards",
            "Ze_ModelUpgradeProtection" => "Prevents accidental model upgrade making it incompatible with older Revit versions",
            "Ze_OpenCentralFileProtection" => "Blocks opening the central file directly instead of creating a local copy",
            "Ze_RVTLinkPinPrompt" => "Prompts users to pin Revit links after inserting to prevent accidental movement",
            "Ze_SaveOverEarlierFileVersionProtection" => "Prevents saving over an earlier version file which forces irreversible upgrade",
            "Ze_TransferProjectStandardsProtection" => "Controls Transfer Project Standards to prevent overwriting project templates",
            _ => ""
        };

        public string ModifiedByTooltip => ModifiedAt.HasValue
            ? $"Modified by: {ModifiedBy}\nDate: {ModifiedAt:yyyy-MM-dd HH:mm}"
            : $"Modified by: {ModifiedBy}";

        public string EventTypeDisplay => EventType switch
        {
            RevitEventType.DocumentOpening => "Document Opening",
            RevitEventType.DocumentSaving => "Document Saving",
            RevitEventType.FamilyLoadingIntoDocument => "Family Loading",
            RevitEventType.DocumentPrinting => "Document Printing",
            RevitEventType.DocumentChanged => "Document Changed",
            RevitEventType.CommandProtection => "Command Restriction",
            // Tracking events
            RevitEventType.ApplicationInitialized => "App Initialized",
            RevitEventType.ApplicationClosing => "App Closing",
            RevitEventType.DocumentOpened => "Document Opened",
            RevitEventType.DocumentClosed => "Document Closed",
            RevitEventType.DocumentCreated => "Document Created",
            RevitEventType.DocumentSaved => "Document Saved",
            RevitEventType.DocumentSynchronizedWithCentral => "Synced with Central",
            // Legacy (removed protections)
            RevitEventType.DocumentSynchronizingWithCentral => "Sync with Central",
            RevitEventType.FileImporting => "File Importing",
            RevitEventType.CADExploding => "CAD Exploding",
            RevitEventType.CopyMonitor => "Copy Monitor",
            RevitEventType.CADImportCompleted => "CAD Import Completed",
            _ => EventType.ToString()
        };

        public string UpdatedAtDisplay => ModifiedAt.HasValue
            ? ModifiedAt.Value.ToString("yyyy-MM-dd HH:mm")
            : "-";

        public static EventSettingViewModel FromModel(EventProtectionSettings model)
        {
            return new EventSettingViewModel
            {
                Id = model.Id,
                ProtectionName = model.ProtectionName,
                DummyCommandId = model.DummyCommandId,
                EventType = model.EventType,
                Mode = model.Mode,
                Enabled = model.Enabled,
                IsCompanyLevel = model.IsCompanyLevel,
                CustomMessage = model.CustomMessage,
                ConfigurationJson = model.ConfigurationJson,
                CaptureBeforeScreenshot = model.CaptureBeforeScreenshot,
                CaptureAfterScreenshot = model.CaptureAfterScreenshot,
                RequireComment = model.RequireComment,
                AllowAdminOverride = model.AllowAdminOverride,
                SendEmail = model.SendEmail,
                CustomMessageImagePath = model.CustomMessageImagePath,
                ProjectId = model.ProjectId,
                CompanyId = model.CompanyId,
                ModelGuid = model.ModelGuid,
                CreatedBy = model.CreatedBy,
                ModifiedBy = model.ModifiedBy,
                ModifiedAt = model.ModifiedAt,
                ToolTips = model.ToolTips,
                HasCompanyScope = model.HasCompanyScope
            };
        }

        public EventProtectionSettings ToModel()
        {
            return new EventProtectionSettings
            {
                Id = Id,
                ProtectionName = ProtectionName,
                DummyCommandId = DummyCommandId,
                EventType = EventType,
                Mode = Mode,
                Enabled = Enabled,
                IsCompanyLevel = IsCompanyLevel,
                CustomMessage = CustomMessage,
                ConfigurationJson = ConfigurationJson,
                CaptureBeforeScreenshot = CaptureBeforeScreenshot,
                CaptureAfterScreenshot = CaptureAfterScreenshot,
                RequireComment = RequireComment,
                AllowAdminOverride = AllowAdminOverride,
                SendEmail = SendEmail,
                CustomMessageImagePath = CustomMessageImagePath,
                ProjectId = ProjectId,
                CompanyId = CompanyId,
                ModelGuid = ModelGuid,
                CreatedBy = CreatedBy,
                ModifiedBy = ModifiedBy,
                ModifiedAt = ModifiedAt
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
