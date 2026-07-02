using System;
using System.Collections.Generic;
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
    public class CommandProtectionViewModel : INotifyPropertyChanged
    {
        private readonly string _revitUsername;
        private readonly string? _profileId;
        private readonly CommandProtectionRepository? _repository;
        private readonly CommandProtectionSyncService? _syncService;
        private readonly ILogger? _logger;
        private readonly int? _projectId;
        private string? _currentModelGuid;
        private ObservableCollection<CommandSettingViewModel> _commands;
        private ObservableCollection<CommandSettingViewModel> _filteredCommands;
        private CommandSettingViewModel? _selectedCommand;
        private bool _isLoading;

        public CommandProtectionViewModel(
            string revitUsername,
            CommandProtectionRepository? repository = null,
            int? projectId = null,
            ILogger? logger = null,
            CommandProtectionSyncService? syncService = null,
            string? profileId = null)
        {
            _revitUsername = revitUsername;
            _profileId = profileId;
            _repository = repository;
            _projectId = projectId;
            _logger = logger;
            _syncService = syncService;
            _commands = new ObservableCollection<CommandSettingViewModel>();
            _filteredCommands = new ObservableCollection<CommandSettingViewModel>();

            // Ensure current user's profileId→displayName mapping is in cache
            if (!string.IsNullOrEmpty(_profileId))
                UserDisplayNameCache.Set(_profileId, _revitUsername);

            _logger?.LogDebug($"CommandProtectionViewModel created (repository: {(repository != null ? "connected" : "null")}, syncService: {(syncService != null ? "enabled" : "null")}, projectId: {projectId})");
        }

        public ObservableCollection<CommandSettingViewModel> Commands
        {
            get => _commands;
            set { _commands = value; OnPropertyChanged(); }
        }

        public ObservableCollection<CommandSettingViewModel> FilteredCommands
        {
            get => _filteredCommands;
            set { _filteredCommands = value; OnPropertyChanged(); }
        }

        public CommandSettingViewModel? SelectedCommand
        {
            get => _selectedCommand;
            set { _selectedCommand = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Set by the dialog after construction so the per-row delete-icon DataTrigger can
        /// hide the delete button for Company-scope rows when a non-Company-admin is logged in.
        /// </summary>
        public bool IsCompanyAdmin { get; set; }

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Returns the first value in (primary, fallback, signed-in-user) that's an
        /// actual person's name. Anything that looks like an internal identifier —
        /// "System", a raw GUID, the "User &lt;hex&gt;" placeholder emitted by
        /// UserDisplayNameCache for unresolved GUIDs, or empty — is skipped over.
        /// Per user spec: "only name show, don't show System or any id".
        /// </summary>
        private string ResolveDisplayName(string? primary, string? fallback)
        {
            if (IsRealName(primary))   return primary!;
            if (IsRealName(fallback))  return fallback!;

            var currentDisplay = !string.IsNullOrWhiteSpace(_profileId)
                ? UserDisplayNameCache.GetDisplayName(_profileId)
                : null;
            if (IsRealName(currentDisplay)) return currentDisplay!;

            if (!string.IsNullOrWhiteSpace(_revitUsername)
                && !string.Equals(_revitUsername, "System", StringComparison.OrdinalIgnoreCase))
                return _revitUsername;

            return string.Empty;
        }

        private static bool IsRealName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            if (string.Equals(v, "System", StringComparison.OrdinalIgnoreCase)) return false;

            if (v.Length == 36 && v[8] == '-' && v[13] == '-' && v[18] == '-' && v[23] == '-')
                return false;
            if (v.Length == 32 && IsHex(v)) return false;

            // "User <8hex>" placeholder for unresolved GUIDs.
            if (v.StartsWith("User ", StringComparison.OrdinalIgnoreCase)
                && v.Length == 13
                && IsHex(v.Substring(5)))
                return false;

            return true;
        }

        private static bool IsHex(string s)
        {
            foreach (var c in s)
            {
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            }
            return true;
        }

        public void LoadCommands()
        {
            IsLoading = true;
            Commands.Clear();

            _logger?.LogDebug($"LoadCommands called (repository: {(_repository != null ? "connected" : "null")})");

            if (_repository != null)
            {
                // Load from database
                _logger?.LogDebug("Loading commands from database...");
                var dbCommands = _repository.LoadAllCommandSettings(_projectId);
                _logger?.LogInfo($"Loaded {dbCommands.Count} commands from database");

                foreach (var cmd in dbCommands)
                {
                    // Convert profileId → displayName using cache. The DB stores profile
                    // IDs; the cache (persisted to user_display_names.json) maps them to
                    // the friendly names the web dashboard shows ("Admin Zestine",
                    // "Sujith Kumar", etc.).
                    if (!string.IsNullOrWhiteSpace(cmd.ModifiedBy))
                    {
                        cmd.ModifiedBy = UserDisplayNameCache.GetDisplayName(cmd.ModifiedBy);
                    }
                    if (!string.IsNullOrWhiteSpace(cmd.CreatedBy))
                    {
                        cmd.CreatedBy = UserDisplayNameCache.GetDisplayName(cmd.CreatedBy);
                    }

                    // Per user spec: "only name show, don't show System or any id".
                    // ResolveDisplayName picks the first value in the priority chain
                    // that is an actual person's name — never "System", never a raw
                    // GUID, never the "User <hex>" placeholder UserDisplayNameCache
                    // emits for unresolved GUIDs. Final fallback is the signed-in
                    // user's name so the column always renders something meaningful.
                    cmd.ModifiedBy = ResolveDisplayName(cmd.ModifiedBy, cmd.CreatedBy);
                    cmd.CreatedBy  = ResolveDisplayName(cmd.CreatedBy, null);

                    Commands.Add(cmd);
                }
            }
            else
            {
                // No repository available - commands will be loaded from API
                _logger?.LogWarning("Repository is null - no local commands available (will sync from API)");
            }

            UpdateRowNumbers();
            IsLoading = false;
        }

        /// <summary>
        /// Fetches command settings from the server API, saves to local DB, and reloads UI.
        /// Falls back to local DB if API is unavailable.
        /// </summary>
        /// <summary>Set the current model GUID for model-specific API fetching</summary>
        public void SetModelGuid(string? modelGuid) => _currentModelGuid = modelGuid;

        /// <summary>
        /// Project GUID for the current model — resolved by the dialog at load time
        /// (via local registered_models or live API). Used as the fallback ProjectId
        /// when a command is saved with Project or Model scope: command.ProjectId is
        /// not populated by the edit dialog, so without this fallback the API POST
        /// sends LevelScope=2 with ProjectId=null and the server rejects with 400 —
        /// the cause of "project-scoped commands don't reflect on web".
        /// </summary>
        public string? CurrentProjectIdGuid { get; set; }

        /// <summary>Public read-only access to the current model GUID.</summary>
        public string? CurrentModelGuid => _currentModelGuid;

        public async Task FetchFromApiAsync()
        {
            if (_syncService == null)
            {
                _logger?.LogWarning("FetchFromApiAsync: SyncService is not initialized");
                LoadCommands();
                return;
            }

            try
            {
                IsLoading = true;

                // Use model-specific endpoint when model GUID is available
                if (!string.IsNullOrEmpty(_currentModelGuid))
                {
                    _logger?.LogInfo($"Fetching command protections from API for model {_currentModelGuid}...");
                    var apiCommands = await _syncService.FetchByModelGuidFromApiAsync(_currentModelGuid, _profileId ?? _revitUsername);
                    _logger?.LogInfo($"Fetched {apiCommands.Count} command protections from server (model-specific)");
                    LoadCommands();
                    return;
                }

                // Fallback to fetch all
                _logger?.LogInfo("Fetching all command settings from API...");
                var allCommands = await _syncService.FetchAllFromApiAsync(_profileId ?? _revitUsername);

                if (allCommands.Count == 0)
                {
                    _logger?.LogWarning("No command settings returned from API");
                    LoadCommands();
                    return;
                }

                LoadCommands();
                _logger?.LogInfo($"Fetched {allCommands.Count} command settings from server");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error fetching command settings from API: {ex.Message}", ex);
                LoadCommands();
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Save command setting to database and sync to API
        /// </summary>
        public async Task<bool> SaveCommandAsync(CommandSettingViewModel command)
        {
            if (_repository == null)
            {
                _logger?.LogWarning($"Cannot save command '{command.CommandCode}' - repository is null");
                return false;
            }

            try
            {
                bool isNewCommand = string.IsNullOrEmpty(command.Id);

                // Store profile ID in database; frontend resolves to display name
                var profileId = _profileId ?? _revitUsername;
                var displayName = UserDisplayNameCache.GetDisplayName(profileId);
                command.ModifiedBy = profileId;

                if (isNewCommand)
                {
                    command.CreatedBy = profileId;

                    // New command - insert
                    _logger?.LogDebug($"Inserting new command: {command.CommandCode}");
                    var newId = await _repository.SaveCommandSettingAsync(command, _projectId);

                    if (!string.IsNullOrEmpty(newId))
                    {
                        command.Id = newId;
                        // Restore display name for UI
                        command.ModifiedBy = displayName;
                        command.CreatedBy = displayName;
                        _logger?.LogInfo($"Saved new command: {command.CommandCode} (id: {newId})");

                        // Sync to API (offline queue fallback) — MapToApiRequest uses _profileId
                        await SyncCommandToApiAsync(command, isNew: true);
                        return true;
                    }
                    _logger?.LogWarning($"Failed to save new command: {command.CommandCode}");
                    return false;
                }
                else
                {
                    // Existing command - update
                    _logger?.LogDebug($"Updating command: {command.CommandCode} (id: {command.Id})");
                    var success = await _repository.UpdateCommandSettingAsync(command);

                    // Restore display name for UI
                    command.ModifiedBy = displayName;

                    if (success)
                    {
                        _logger?.LogInfo($"Updated command: {command.CommandCode} (id: {command.Id})");

                        // Sync to API (offline queue fallback) — MapToApiRequest uses _profileId
                        await SyncCommandToApiAsync(command, isNew: false);
                    }
                    else
                    {
                        _logger?.LogWarning($"Failed to update command: {command.CommandCode} (id: {command.Id})");
                    }
                    return success;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error saving command {command.CommandCode}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Delete command setting from database and sync to API
        /// </summary>
        public async Task<bool> DeleteCommandAsync(string commandId, string? commandCode = null)
        {
            if (_repository == null || string.IsNullOrEmpty(commandId)) return false;

            try
            {
                var success = await _repository.DeleteCommandSettingAsync(commandId);

                // Safety net: also remove any other local rows matching this command_code.
                // If the client-side id has drifted from the server id, Refresh would otherwise
                // re-upsert the same command under a new id — this prevents that.
                if (!string.IsNullOrWhiteSpace(commandCode))
                {
                    try { await _repository.DeleteCommandSettingsByCodeAsync(commandCode); }
                    catch (Exception cleanupEx) { _logger?.LogWarning($"Cleanup by code failed: {cleanupEx.Message}"); }
                }

                if (_syncService != null)
                {
                    // Sync delete to API — pass current modelGuid so the sync service can use the
                    // working by-model GET endpoint to find the server's real commandProtectionId
                    // (the plain-list GET returns 405 MethodNotAllowed on the server).
                    await _syncService.SyncDeleteCommandAsync(commandId, commandCode ?? $"id_{commandId}", _currentModelGuid);
                }
                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error deleting command {commandId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Sync command protection setting to API (or queue for offline sync)
        /// Uses POST for new, PUT for full updates (edit panel), PATCH for partial updates (inline toggle)
        /// </summary>
        private async Task SyncCommandToApiAsync(CommandSettingViewModel command, bool isNew)
        {
            if (_syncService == null)
            {
                _logger?.LogDebug($"Sync service not available - skipping API sync for {command.CommandCode}");
                return;
            }

            try
            {
                var apiRequest = MapToApiRequest(command);

                if (isNew)
                {
                    // POST for new commands
                    var synced = await _syncService.SyncNewCommandAsync(apiRequest);
                    _logger?.LogInfo($"Command protection API POST (new): {command.CommandCode} - {(synced ? "Success/Queued" : "Failed")}");
                }
                else
                {
                    // PUT for full updates from edit panel. Pass _currentModelGuid so the sync
                    // service can resolve the server's real commandProtectionId via the by-model
                    // GET (works for Project/Company-scope edits where apiRequest.ModelGuid is null).
                    var synced = await _syncService.SyncPutCommandAsync(command.Id, apiRequest, _currentModelGuid);
                    _logger?.LogInfo($"Command protection API PUT (update): {command.CommandCode} - {(synced ? "Success/Queued" : "Failed")}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error syncing command {command.CommandCode} to API: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Sync a partial (inline) update to API via PATCH
        /// Used for toggle switches and single-field changes
        /// </summary>
        public async Task SyncPatchToApiAsync(CommandSettingViewModel command)
        {
            if (_syncService == null)
            {
                _logger?.LogDebug($"Sync service not available - skipping PATCH for {command.CommandCode}");
                return;
            }

            try
            {
                var apiRequest = MapToApiRequest(command);
                var synced = await _syncService.SyncUpdateCommandAsync(command.Id, apiRequest, _currentModelGuid);
                _logger?.LogInfo($"Command protection API PATCH (partial): {command.CommandCode} - {(synced ? "Success/Queued" : "Failed")}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error patching command {command.CommandCode} to API: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Maps a CommandSettingViewModel to the API request DTO
        /// </summary>
        private CommandProtectionApiRequest MapToApiRequest(CommandSettingViewModel command)
        {
            // Server scheme: 1=Company, 2=Project, 3=Model
            int levelScope = command.IsCompanyLevel ? 1 : (command.ModelGuid != null ? 3 : 2);

            // Fall back to the dialog-resolved project GUID when scope requires it but
            // the row didn't carry one. The edit panel only sets IsCompanyLevel — it does
            // NOT populate command.ProjectId, so a brand-new Project-scoped command would
            // otherwise POST with ProjectId=null and the server rejects (400). This
            // fallback is what makes Revit→Web push work for Project and Model scopes.
            string? projectId = !string.IsNullOrWhiteSpace(command.ProjectId)
                ? command.ProjectId
                : (levelScope != 1 ? CurrentProjectIdGuid : null);

            return new CommandProtectionApiRequest
            {
                CommandProtectionId = string.IsNullOrEmpty(command.Id) ? Guid.NewGuid().ToString() : command.Id,
                CommandCode = command.CommandCode,
                CommandName = command.CommandName,
                // Wire format aligned with Rule Management: 0-indexed (Notify=0, Assist=1, Protect=2).
                // The working Swagger PUT for /command-protections/for-Model accepts and persists
                // 0-indexed values (the example shows interventionMode: 0). Rules has always
                // sent (int)Mode directly with no offset and its edits round-trip correctly,
                // so we mirror that exact pattern here.
                InterventionMode = (int)command.Mode,
                Mode = command.Mode.ToString(), // Kept for backward compat
                IsEnabled = command.IsEnabled,
                LevelScope = levelScope,
                ProjectId = projectId,
                ModelGuid = command.ModelGuid,
                CustomMessage = command.CustomMessage,
                CaptureBeforeScreenshot = command.CaptureBeforeScreenshot,
                CaptureAfterScreenshot = command.CaptureAfterScreenshot,
                RequireComment = command.RequireComment,
                AllowAdminOverride = command.AllowAdminOverride,
                ModifiedBy = _profileId ?? _revitUsername,
                ModifiedByDisplayName = UserDisplayNameCache.GetDisplayName(_profileId ?? _revitUsername),
                ModifiedAt = command.ModifiedAt,
                SendEmail = command.SendEmail,
                CreatedAt = DateTime.UtcNow,
                CustomMessageImagePath = command.CustomMessageImagePath
            };
        }

        #region Import / Export

        public async Task<(bool Success, string Message)> ImportFromJsonAsync()
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "ZE Files (*.ze)|*.ze|All Files (*.*)|*.*",
                    Title = "Import Command Restriction"
                };

                if (openFileDialog.ShowDialog() != true)
                    return (false, "Import cancelled");

                IsLoading = true;
                _logger?.LogInfo($"Importing command protection from: {openFileDialog.FileName}");

                var jsonContent = await Task.Run(() => File.ReadAllText(openFileDialog.FileName, Encoding.UTF8));

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };

                var importedCommands = JsonSerializer.Deserialize<List<CommandProtectionExportModel>>(jsonContent, options);

                if (importedCommands == null || importedCommands.Count == 0)
                    return (false, "No commands found in JSON file");

                var importedCount = 0;

                foreach (var imported in importedCommands)
                {
                    var command = new CommandSettingViewModel
                    {
                        CommandCode = imported.CommandCode ?? "",
                        CommandName = imported.CommandName ?? imported.CommandCode ?? "",
                        Mode = ParseInterventionMode(imported.Mode),
                        IsEnabled = imported.IsEnabled ?? true,
                        IsCompanyLevel = imported.IsCompanyLevel ?? true,
                        CustomMessage = imported.CustomMessage,
                        CaptureBeforeScreenshot = imported.CaptureBeforeScreenshot ?? false,
                        CaptureAfterScreenshot = imported.CaptureAfterScreenshot ?? false,
                        RequireComment = imported.RequireComment ?? false,
                        AllowAdminOverride = imported.AllowAdminOverride ?? true,
                        ModifiedBy = _revitUsername,
                        ModifiedAt = DateTime.UtcNow
                    };

                    // Check for duplicate command codes
                    var existing = Commands.FirstOrDefault(c =>
                        c.CommandCode.Equals(command.CommandCode, StringComparison.OrdinalIgnoreCase));

                    if (existing != null)
                    {
                        // Update existing command
                        existing.Mode = command.Mode;
                        existing.IsEnabled = command.IsEnabled;
                        existing.IsCompanyLevel = command.IsCompanyLevel;
                        existing.CustomMessage = command.CustomMessage;
                        existing.CaptureBeforeScreenshot = command.CaptureBeforeScreenshot;
                        existing.CaptureAfterScreenshot = command.CaptureAfterScreenshot;
                        existing.RequireComment = command.RequireComment;
                        existing.AllowAdminOverride = command.AllowAdminOverride;
                        existing.ModifiedBy = _revitUsername;
                        existing.ModifiedAt = DateTime.UtcNow;

                        var saved = await SaveCommandAsync(existing);
                        if (saved) importedCount++;
                    }
                    else
                    {
                        // Save to DB first, then add to collection only if saved
                        var saved = await SaveCommandAsync(command);
                        if (saved)
                        {
                            Commands.Add(command);
                            importedCount++;
                        }
                    }
                }

                // Reload from database to ensure we show persisted data
                LoadCommands();
                _logger?.LogInfo($"Imported {importedCount} command protection settings");

                if (importedCount == 0)
                    return (false, "Failed to save commands - check database connection");

                return (true, $"Imported {importedCount} commands successfully");
            }
            catch (JsonException jsonEx)
            {
                _logger?.LogError($"JSON parsing error: {jsonEx.Message}", jsonEx);
                return (false, $"Invalid JSON format: {jsonEx.Message}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error importing command protection: {ex.Message}", ex);
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
                    Title = "Export Command Restriction",
                    FileName = $"CommandProtection_Export_{DateTime.Now:yyyyMMdd_HHmmss}.ze"
                };

                if (saveFileDialog.ShowDialog() != true)
                    return (false, "Export cancelled");

                IsLoading = true;
                _logger?.LogInfo($"Exporting command protection to: {saveFileDialog.FileName}");

                var exportModels = Commands.Select(cmd => new CommandProtectionExportModel
                {
                    CommandCode = cmd.CommandCode,
                    CommandName = cmd.CommandName,
                    Mode = cmd.Mode.ToString(),
                    IsEnabled = cmd.IsEnabled,
                    IsCompanyLevel = cmd.IsCompanyLevel,
                    Scope = cmd.Scope,
                    CustomMessage = cmd.CustomMessage,
                    CaptureBeforeScreenshot = cmd.CaptureBeforeScreenshot,
                    CaptureAfterScreenshot = cmd.CaptureAfterScreenshot,
                    RequireComment = cmd.RequireComment,
                    AllowAdminOverride = cmd.AllowAdminOverride,
                    ModifiedBy = cmd.ModifiedBy,
                    ModifiedAt = cmd.ModifiedAt?.ToString("yyyy-MM-dd HH:mm:ss")
                }).ToList();

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var jsonContent = JsonSerializer.Serialize(exportModels, options);
                await Task.Run(() => File.WriteAllText(saveFileDialog.FileName, jsonContent, Encoding.UTF8));

                _logger?.LogInfo($"Exported {exportModels.Count} command protection settings to JSON");
                return (true, $"Exported {exportModels.Count} commands to {Path.GetFileName(saveFileDialog.FileName)}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error exporting command protection: {ex.Message}", ex);
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

        public void ApplyFilters(string searchText, string statusFilter, string modeFilter)
        {
            var filtered = Commands.AsEnumerable();

            // Apply search filter
            if (!string.IsNullOrWhiteSpace(searchText))
            {
                filtered = filtered.Where(c =>
                    c.CommandName.ToLower().Contains(searchText) ||
                    c.CommandCode.ToLower().Contains(searchText));
            }

            // Apply status filter
            if (!string.IsNullOrWhiteSpace(statusFilter))
            {
                var isEnabled = statusFilter == "Active";
                filtered = filtered.Where(c => c.IsEnabled == isEnabled);
            }

            // Apply mode filter
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
                    filtered = filtered.Where(c => c.Mode == mode.Value);
                }
            }

            FilteredCommands.Clear();
            var rowNum = 1;
            foreach (var cmd in filtered)
            {
                cmd.RowNumber = rowNum++;
                FilteredCommands.Add(cmd);
            }
        }

        private void UpdateRowNumbers()
        {
            var rowNum = 1;
            foreach (var cmd in Commands)
            {
                cmd.RowNumber = rowNum++;
            }
            FilteredCommands = new ObservableCollection<CommandSettingViewModel>(Commands);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class CommandSettingViewModel : INotifyPropertyChanged
    {
        private string _id = string.Empty;
        private int _rowNumber;
        private string _commandCode = "";
        private string _commandName = "";
        private string? _description;
        private InterventionMode _mode;
        private bool _isEnabled;
        private bool _isCompanyLevel;
        private string? _customMessage;
        private bool _captureBeforeScreenshot;
        private bool _captureAfterScreenshot;
        private bool _requireComment;
        private bool _allowAdminOverride;
        private string? _createdBy;
        private string? _modifiedBy;
        private DateTime? _modifiedAt;
        private bool _sendEmail;
        private string? _customMessageImagePath;
        private string? _projectId;
        private string? _companyId;
        private string? _modelGuid;

        /// <summary>
        /// Database ID (GUID, empty = new/unsaved)
        /// </summary>
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

        public string CommandCode
        {
            get => _commandCode;
            set { _commandCode = value; OnPropertyChanged(); OnPropertyChanged(nameof(CommandNameTooltip)); }
        }

        public string CommandName
        {
            get => _commandName;
            set { _commandName = value; OnPropertyChanged(); OnPropertyChanged(nameof(CommandNameTooltip)); }
        }

        public string? Description
        {
            get => _description;
            set { _description = value; OnPropertyChanged(); OnPropertyChanged(nameof(CommandNameTooltip)); }
        }

        public InterventionMode Mode
        {
            get => _mode;
            set { _mode = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModeDisplay)); OnPropertyChanged(nameof(ModeTooltip)); }
        }

        public bool IsEnabled
        {
            get => _isEnabled;
            set { _isEnabled = value; OnPropertyChanged(); OnPropertyChanged(nameof(StatusTooltip)); }
        }

        public bool IsCompanyLevel
        {
            get => _isCompanyLevel;
            set { _isCompanyLevel = value; OnPropertyChanged(); OnPropertyChanged(nameof(Scope)); }
        }

        public string? CustomMessage
        {
            get => _customMessage;
            set { _customMessage = value; OnPropertyChanged(); }
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
            set { _modifiedAt = value; OnPropertyChanged(); OnPropertyChanged(nameof(ModifiedByTooltip)); }
        }

        /// <summary>
        /// Whether to send email notification when this command is executed
        /// </summary>
        public bool SendEmail
        {
            get => _sendEmail;
            set { _sendEmail = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Optional file path for image attachment with custom message
        /// </summary>
        public string? CustomMessageImagePath
        {
            get => _customMessageImagePath;
            set { _customMessageImagePath = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Project ID for project-level assignment
        /// </summary>
        public string? ProjectId
        {
            get => _projectId;
            set { _projectId = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Company ID for company-level assignment
        /// </summary>
        public string? CompanyId
        {
            get => _companyId;
            set { _companyId = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// Model GUID for model-specific assignment
        /// </summary>
        public string? ModelGuid
        {
            get => _modelGuid;
            set { _modelGuid = value; OnPropertyChanged(); }
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
            InterventionMode.Notify => "Notify: Log command execution without blocking",
            InterventionMode.Assist => "Assist: Show confirmation dialog before proceeding",
            InterventionMode.Protect => "Protect: Require password/OTP to proceed",
            _ => ""
        };

        public string StatusTooltip => IsEnabled ? "Click to disable" : "Click to enable";

        // Return null when there's no real description so WPF hides the tooltip entirely
        // instead of showing an empty/fallback popup.
        public string? CommandNameTooltip => !string.IsNullOrWhiteSpace(_description)
            ? _description
            : null;

        public string ModifiedByTooltip => ModifiedAt.HasValue
            ? $"Modified by: {ModifiedBy}\nDate: {ModifiedAt:yyyy-MM-dd HH:mm}"
            : $"Modified by: {ModifiedBy}";

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class CommandProtectionExportModel
    {
        [JsonPropertyName("commandCode")]
        public string? CommandCode { get; set; }

        [JsonPropertyName("commandName")]
        public string? CommandName { get; set; }

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

        [JsonPropertyName("modifiedBy")]
        public string? ModifiedBy { get; set; }

        [JsonPropertyName("modifiedAt")]
        public string? ModifiedAt { get; set; }
    }
}
