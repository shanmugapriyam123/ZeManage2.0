using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Revit.Commands.Bindings;
using BIManage.Revit.Protection;
using BIManage.Core.Identity;
using BIManageRevit.BIManage.ViewModels.Protection;

// Resolve ambiguous types with Revit API
using WpfColor = System.Windows.Media.Color;
using WpfVisibility = System.Windows.Visibility;


namespace BIManageRevit.BIManage.Views.Protection
{
    public partial class CommandProtectionDialog : Window
    {
        private readonly CommandProtectionViewModel _viewModel;
        private readonly CommandProtectionRepository? _repository;
        private readonly CommandProtectionBinding? _commandProtectionBinding;
        private readonly ILogger? _logger;
        private readonly CacheRepository? _cacheRepository;
        private readonly RevitApiService? _revitApiService;
        private CommandSettingViewModel? _editingCommand;
        private CommandSettingViewModel? _commandToDelete;
        private readonly string _revitUsername;
        private readonly string? _profileId;
        private List<AvailableCommand> _allAvailableCommands;
        private List<AvailableCommand> _filteredAvailableCommands;
        private bool _isNewCommand;
        private readonly bool _isCompanyAdmin;
        private readonly ISignalREventBus? _eventBus;
        private Action<ProtectionChangedEvent>? _protectionChangedHandler;

        public CommandProtectionDialog(
            string revitUsername = null,
            CommandProtectionRepository? repository = null,
            int? projectId = null,
            ILogger? logger = null,
            CommandProtectionBinding? commandProtectionBinding = null,
            CacheRepository? cacheRepository = null,
            CommandProtectionSyncService? syncService = null,
            bool isCompanyAdmin = false,
            string? profileId = null,
            ISignalREventBus? eventBus = null,
            string? modelGuid = null,
            RevitApiService? revitApiService = null)
        {
            _revitUsername = revitUsername ?? Environment.UserName;
            _profileId = profileId;
            _repository = repository;
            _cacheRepository = cacheRepository;
            _revitApiService = revitApiService;
            _commandProtectionBinding = commandProtectionBinding;
            _logger = logger;
            _isCompanyAdmin = isCompanyAdmin;
            _eventBus = eventBus;
            _viewModel = new CommandProtectionViewModel(_revitUsername, repository, projectId, logger, syncService, _profileId);
            _viewModel.IsCompanyAdmin = _isCompanyAdmin;
            _viewModel.SetModelGuid(modelGuid);
            _allAvailableCommands = new List<AvailableCommand>();
            _filteredAvailableCommands = new List<AvailableCommand>();

            _logger?.LogDebug($"CommandProtectionDialog created (repository: {(repository != null ? "connected" : "null")}, binding: {(commandProtectionBinding != null ? "connected" : "null")}, syncService: {(syncService != null ? "enabled" : "null")})");

            DataContext = _viewModel;
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            // Hide "Company-wide" scope option for non-company-admins
            if (!_isCompanyAdmin)
            {
                foreach (ComboBoxItem item in EditScope.Items)
                {
                    if (item.Tag?.ToString() == "Company")
                    {
                        item.Visibility = WpfVisibility.Collapsed;
                        break;
                    }
                }
            }

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_protectionChangedHandler != null)
                _eventBus?.Unsubscribe<ProtectionChangedEvent>(_protectionChangedHandler);
        }

        /// <summary>
        /// Resolves the current model's project GUID via the API and caches it on the
        /// ViewModel so MapToApiRequest can populate ProjectId on Project/Model-scoped
        /// command POSTs. Best-effort: a network failure leaves the cache null and
        /// company-scoped commands still sync as before.
        /// </summary>
        private async System.Threading.Tasks.Task ResolveCurrentProjectIdGuidAsync()
        {
            try
            {
                var modelGuid = _viewModel.CurrentModelGuid;
                if (string.IsNullOrEmpty(modelGuid)) return;

                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;
                var modelSync = services?.GetService<global::BIManage.Infrastructure.Api.ModelSyncService>();
                if (modelSync == null) return;

                var apiModel = await modelSync.FetchModelByGuidAsync(modelGuid);
                _viewModel.CurrentProjectIdGuid = apiModel?.ZemanageProjectId ?? apiModel?.ProjectIdAlternate;
                _logger?.LogInfo($"[CmdProt] Resolved project GUID for model {modelGuid}: {_viewModel.CurrentProjectIdGuid ?? "<null>"}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"ResolveCurrentProjectIdGuidAsync failed: {ex.Message}");
            }
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Auto-sync command settings from server (falls back to local DB if API unavailable)
            await _viewModel.FetchFromApiAsync();
            await LoadAvailableCommandsAsync();

            // Resolve and cache the current model's project GUID — needed so that
            // POST /api/v1/Revit/command-protections sends a valid Nullable<Guid>
            // for Project-scoped commands. Without this the edit panel saves a
            // command with command.ProjectId=null and the server rejects (400),
            // so the rule never reflects on the web side.
            await ResolveCurrentProjectIdGuidAsync();

            ApplyFilters();
            UpdateStatusBar();

            // Subscribe to real-time protection changes via SignalR event bus
            _protectionChangedHandler = (evt) =>
            {
                if (evt.ProtectionType == "Command")
                    Dispatcher.InvokeAsync(async () => { await _viewModel.FetchFromApiAsync(); ApplyFilters(); UpdateStatusBar(); });
            };
            _eventBus?.Subscribe<ProtectionChangedEvent>(_protectionChangedHandler);
        }

        private async System.Threading.Tasks.Task LoadAvailableCommandsAsync()
        {
            // Refresh the local command_cache from the server every time the dialog opens,
            // so admin-side toggles of needBinding on /api/v1/Revit/commands are reflected
            // immediately. The previous behaviour populated the cache only on first run
            // (RevitBootstrapper.AutoLoadCommandCacheAsync skips when count > 0), so flipping
            // a command to needBinding=false on the server didn't remove it from the picker
            // until the user manually cleared the cache or reinstalled. Best-effort: if the
            // refresh fails (offline, auth issue, etc.) the existing cache is reused.
            if (_revitApiService != null && _cacheRepository != null)
            {
                try
                {
                    var apiCommands = await _revitApiService.GetCommandsAsync();
                    if (apiCommands != null && apiCommands.Count > 0)
                    {
                        await _cacheRepository.ClearCommandCacheAsync();
                        var batch = apiCommands
                            .Where(c => !string.IsNullOrEmpty(c.CommandName))
                            .Select(c => (c.CommandName, c.MemberName, c.Description, c.CanHaveBinding, c.NeedBinding, c.CanWorkWithSelection, c.Code))
                            .ToList();
                        var savedCount = await _cacheRepository.UpsertCommandCacheBatchAsync(batch);
                        _logger?.LogInfo($"CommandProtectionDialog: refreshed command_cache with {savedCount} commands (needBinding={apiCommands.Count(c => c.NeedBinding)})");
                    }
                    else
                    {
                        _logger?.LogWarning("CommandProtectionDialog: API returned 0 commands — keeping existing cache");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"CommandProtectionDialog: cache refresh from API failed, falling back to existing cache: {ex.Message}");
                }
            }

            // Load commands from command_cache — only commands where need_binding = true.
            // No hard-coded fallback merge: the picker shows exactly what the server returned
            // with needBinding=true. If the server hides a command (needBinding=false), it
            // disappears from the picker on the next dialog open.
            _allAvailableCommands = new List<AvailableCommand>();
            if (_cacheRepository != null)
            {
                var cachedCommands = await _cacheRepository.GetCommandsWithNeedBindingAsync();
                _allAvailableCommands = cachedCommands
                    .Select(c => new AvailableCommand
                    {
                        CommandCode = c.MemberName ?? c.CommandName, // Use member_name_24 as code, fallback to command_name
                        CommandName = c.CommandName,
                        Description = c.Description
                    })
                    .ToList();
            }

            _filteredAvailableCommands = new List<AvailableCommand>(_allAvailableCommands);

            // Propagate descriptions to the already-loaded saved commands so tooltips work
            try
            {
                var byCode = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                var byName = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var ac in _allAvailableCommands)
                {
                    if (!string.IsNullOrEmpty(ac.CommandCode) && !byCode.ContainsKey(ac.CommandCode))
                        byCode[ac.CommandCode] = ac.Description;
                    if (!string.IsNullOrEmpty(ac.CommandName) && !byName.ContainsKey(ac.CommandName))
                        byName[ac.CommandName] = ac.Description;
                }
                foreach (var cmd in _viewModel.Commands)
                {
                    if (string.IsNullOrWhiteSpace(cmd.Description))
                    {
                        if (!string.IsNullOrEmpty(cmd.CommandCode) && byCode.TryGetValue(cmd.CommandCode, out var d1) && !string.IsNullOrWhiteSpace(d1))
                            cmd.Description = d1;
                        else if (!string.IsNullOrEmpty(cmd.CommandName) && byName.TryGetValue(cmd.CommandName, out var d2))
                            cmd.Description = d2;
                    }
                }
            }
            catch { /* best-effort tooltip hydration */ }

            // Lazy-fetch from the per-command detail endpoint for anything still missing.
            // The list endpoint often returns null descriptions; detail endpoint has them.
            _ = FetchMissingDescriptionsFromDetailEndpointAsync();
        }

        private async System.Threading.Tasks.Task FetchMissingDescriptionsFromDetailEndpointAsync()
        {
            if (_revitApiService == null) return;
            try
            {
                // Step 1: Refresh cache from the list endpoint (one API call, fast) so next open is fresh too
                var allCommands = await _revitApiService.GetCommandsAsync();
                if (allCommands != null && allCommands.Count > 0)
                {
                    var byName = allCommands
                        .Where(c => !string.IsNullOrEmpty(c.CommandName))
                        .GroupBy(c => c.CommandName, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                    var byMember = allCommands
                        .Where(c => !string.IsNullOrEmpty(c.MemberName))
                        .GroupBy(c => c.MemberName!, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

                    // Push descriptions from the fresh list into the saved rows
                    Dispatcher.Invoke(() =>
                    {
                        foreach (var cmd in _viewModel.Commands)
                        {
                            RevitCommand? match = null;
                            if (!string.IsNullOrEmpty(cmd.CommandName)) byName.TryGetValue(cmd.CommandName, out match);
                            if (match == null && !string.IsNullOrEmpty(cmd.CommandCode)) byMember.TryGetValue(cmd.CommandCode, out match);
                            if (match != null && !string.IsNullOrWhiteSpace(match.Description))
                                cmd.Description = match.Description;
                        }
                        foreach (var ac in _allAvailableCommands)
                        {
                            RevitCommand? match = null;
                            if (!string.IsNullOrEmpty(ac.CommandName)) byName.TryGetValue(ac.CommandName, out match);
                            if (match == null && !string.IsNullOrEmpty(ac.CommandCode)) byMember.TryGetValue(ac.CommandCode, out match);
                            if (match != null && !string.IsNullOrWhiteSpace(match.Description))
                                ac.Description = match.Description;
                        }
                    });

                    // Refresh the on-disk cache so descriptions persist for next open
                    if (_cacheRepository != null)
                    {
                        var commandList = allCommands
                            .Where(c => !string.IsNullOrEmpty(c.CommandName))
                            .Select(c => (c.CommandName, c.MemberName, c.Description, c.CanHaveBinding, c.NeedBinding, c.CanWorkWithSelection, c.Code))
                            .ToList();
                        await _cacheRepository.UpsertCommandCacheBatchAsync(commandList);
                        _logger?.LogInfo($"Refreshed command_cache with {commandList.Count} commands (descriptions included)");
                    }
                }

                // Step 2: For commands STILL missing a description, use the detail endpoint
                var stillMissing = _viewModel.Commands
                    .Where(c => string.IsNullOrWhiteSpace(c.Description) && !string.IsNullOrWhiteSpace(c.CommandName))
                    .Select(c => c.CommandName)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                foreach (var name in stillMissing)
                {
                    try
                    {
                        var details = await _revitApiService.GetCommandDetailsAsync(name);
                        if (details == null || string.IsNullOrWhiteSpace(details.Description)) continue;

                        Dispatcher.Invoke(() =>
                        {
                            foreach (var cmd in _viewModel.Commands.Where(c => string.Equals(c.CommandName, name, StringComparison.OrdinalIgnoreCase)))
                                cmd.Description = details.Description;
                            foreach (var ac in _allAvailableCommands.Where(a => string.Equals(a.CommandName, name, StringComparison.OrdinalIgnoreCase)))
                                ac.Description = details.Description;
                        });
                    }
                    catch { /* skip */ }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"FetchMissingDescriptionsFromDetailEndpointAsync: {ex.Message}");
            }
        }

        /// <summary>
        /// Supplementary Revit commands that MUST always appear in the picker, regardless
        /// of what the server-driven command_cache contains. The server's command list has
        /// gaps for certain commands (Create Assembly (Ribbon), Hide Element, etc.) — they
        /// either come back with need_binding=0 or aren't returned at all — which leaves
        /// admins unable to protect them. This list is merged into the cache-loaded list
        /// by <see cref="LoadAvailableCommandsAsync"/> so the picker is the union of both
        /// sources. Code values mirror revit_commands.csv (PostableCommandName column) so
        /// the protection binding lookup in CommandProtectionBinding still resolves them.
        /// </summary>
#region Success Popup

        private System.Windows.Threading.DispatcherTimer? _popupTimer;

        private void ShowSuccessPopup(string title, string message)
        {
            SuccessTitle.Text = title;
            SuccessMessage.Text = message;
            SuccessPopup.Visibility = WpfVisibility.Visible;

            _popupTimer?.Stop();
            _popupTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _popupTimer.Tick += (s, e) =>
            {
                _popupTimer.Stop();
                SuccessPopup.Visibility = WpfVisibility.Collapsed;
            };
            _popupTimer.Start();
        }

        private void CloseSuccessPopup_Click(object sender, RoutedEventArgs e)
        {
            _popupTimer?.Stop();
            SuccessPopup.Visibility = WpfVisibility.Collapsed;
        }

        #endregion

        #region Button Click Handlers

        private void AddCommand_Click(object sender, RoutedEventArgs e)
        {
            // Remove any existing unsaved new command(s) to prevent extra rows
            RemoveUnsavedNewCommand();

            // Create a new command with placeholder values
            var newCommand = new CommandSettingViewModel
            {
                CommandCode = "",
                CommandName = "(Select Command)",
                Mode = InterventionMode.Notify,
                IsEnabled = true,
                IsCompanyLevel = _isCompanyAdmin,
                CustomMessage = "",
                CaptureBeforeScreenshot = false,
                CaptureAfterScreenshot = false,
                RequireComment = false,
                AllowAdminOverride = false,
                ModifiedBy = UserDisplayNameCache.GetDisplayName(_profileId ?? _revitUsername),
                ModifiedAt = DateTime.UtcNow
            };

            // Add to the grid
            _viewModel.Commands.Add(newCommand);
            ApplyFilters();
            UpdateStatusBar();

            // Select and scroll to the new command
            CommandsDataGrid.SelectedItem = newCommand;
            CommandsDataGrid.ScrollIntoView(newCommand);

            // Open edit panel for the new command
            _editingCommand = newCommand;
            _isNewCommand = true;
            OpenEditPanel(newCommand);
        }

        /// <summary>
        /// Remove any unsaved placeholder commands (empty CommandCode) to prevent extra rows
        /// </summary>
        private void RemoveUnsavedNewCommand()
        {
            // Remove tracked unsaved command
            if (_isNewCommand && _editingCommand != null)
            {
                _viewModel.Commands.Remove(_editingCommand);
            }

            // Also remove any orphaned placeholder rows with empty CommandCode
            var placeholders = _viewModel.Commands
                .Where(c => string.IsNullOrEmpty(c.CommandCode))
                .ToList();
            foreach (var placeholder in placeholders)
            {
                _viewModel.Commands.Remove(placeholder);
            }

            _editingCommand = null;
            _isNewCommand = false;
        }

        private void CloseAddCommandPopup_Click(object sender, RoutedEventArgs e)
        {
            AddCommandPopup.Visibility = WpfVisibility.Collapsed;
        }

        private void CommandSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var searchText = CommandSearchBox.Text?.ToLower() ?? "";
            var existingCodes = _viewModel.Commands.Select(c => c.CommandCode).ToHashSet();

            var filtered = _allAvailableCommands
                .Where(c => !existingCodes.Contains(c.CommandCode))
                .Where(c => string.IsNullOrEmpty(searchText) ||
                           c.CommandName.ToLower().Contains(searchText) ||
                           c.CommandCode.ToLower().Contains(searchText))
                .ToList();

            AvailableCommandsList.ItemsSource = filtered;
        }

        private void AvailableCommandsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AddSelectedCommandButton.IsEnabled = AvailableCommandsList.SelectedItem != null;
        }

        private void AddSelectedCommand_Click(object sender, RoutedEventArgs e)
        {
            if (AvailableCommandsList.SelectedItem is AvailableCommand selectedCommand)
            {
                var newCommand = new CommandSettingViewModel
                {
                    CommandCode = selectedCommand.CommandCode,
                    CommandName = selectedCommand.CommandName,
                    Description = selectedCommand.Description,
                    Mode = InterventionMode.Notify,
                    IsEnabled = true,
                    IsCompanyLevel = _isCompanyAdmin,
                    CustomMessage = "",
                    CaptureBeforeScreenshot = false,
                    CaptureAfterScreenshot = false,
                    RequireComment = false,
                    AllowAdminOverride = false,
                    ModifiedBy = UserDisplayNameCache.GetDisplayName(_profileId ?? _revitUsername),
                    ModifiedAt = DateTime.UtcNow
                };

                _viewModel.Commands.Add(newCommand);
                ApplyFilters();
                UpdateStatusBar();

                AddCommandPopup.Visibility = WpfVisibility.Collapsed;
                ShowSuccessPopup("Command Added!", $"'{selectedCommand.CommandName}' has been added for protection");
            }
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            var result = await _viewModel.ImportFromJsonAsync();
            if (result.Success)
            {
                ApplyFilters();
                UpdateStatusBar();
                ShowSuccessPopup("Import Complete", result.Message);
                RefreshCommandProtectionBindings();
            }
            else
            {
                StatusMessage.Text = result.Message;
            }
        }

        private async void Export_Click(object sender, RoutedEventArgs e)
        {
            var result = await _viewModel.ExportToJsonAsync();
            if (result.Success)
            {
                ShowSuccessPopup("Export Complete", result.Message);
            }
            else
            {
                StatusMessage.Text = result.Message;
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.FetchFromApiAsync();
            ApplyFilters();
            UpdateStatusBar();
            ShowSuccessPopup("Refreshed", "Command list has been refreshed");
        }

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is CommandSettingViewModel commandViewModel)
            {
                _viewModel.SelectedCommand = commandViewModel;
                _editingCommand = commandViewModel;
                _isNewCommand = false;
                OpenEditPanel(commandViewModel);
            }
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is CommandSettingViewModel commandViewModel)
            {
                // Project Admins cannot delete Company-wide commands
                if (!_isCompanyAdmin && commandViewModel.IsCompanyLevel)
                {
                    MessageBox.Show(
                        "You do not have permission to delete company-wide commands.\n\nOnly Company Administrators can delete company-wide scope items.",
                        "Permission Denied",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    e.Handled = true;
                    return;
                }

                _commandToDelete = commandViewModel;
                DeleteCommandName.Text = $"'{commandViewModel.CommandName}'";
                DeletePopup.Visibility = WpfVisibility.Visible;
                e.Handled = true;
            }
        }

        private void CancelDelete_Click(object sender, RoutedEventArgs e)
        {
            DeletePopup.Visibility = WpfVisibility.Collapsed;
            _commandToDelete = null;
        }

        private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_commandToDelete != null)
            {
                var commandName = _commandToDelete.CommandName;
                var commandCode = _commandToDelete.CommandCode;
                var commandId = _commandToDelete.Id;

                // Delete from database if it has an ID
                if (!string.IsNullOrEmpty(commandId))
                {
                    var success = await _viewModel.DeleteCommandAsync(commandId, commandCode);
                    if (!success)
                    {
                        StatusMessage.Text = $"Failed to delete '{commandName}' from database";
                        DeletePopup.Visibility = WpfVisibility.Collapsed;
                        _commandToDelete = null;
                        return;
                    }
                }

                // Remove from UI
                _viewModel.Commands.Remove(_commandToDelete);
                ApplyFilters();
                UpdateStatusBar();
                ShowSuccessPopup("Command Deleted!", $"'{commandName}' has been removed");

                // Refresh command protection bindings to unregister the command
                RefreshCommandProtectionBindings();
            }
            DeletePopup.Visibility = WpfVisibility.Collapsed;
            _commandToDelete = null;
        }

        private AvailableCommand? _selectedEditCommand;

        private void OpenEditPanel(CommandSettingViewModel command)
        {
            // Set panel title based on whether it's a new or existing command
            EditPanelTitle.Text = _isNewCommand ? "Add Command" : "Edit Command";

            // Show dropdown for new commands, textbox for existing
            if (_isNewCommand)
            {
                // Filter out already added commands and populate dropdown
                var existingCodes = _viewModel.Commands
                    .Where(c => c != command) // Exclude the new placeholder command
                    .Select(c => c.CommandCode)
                    .ToHashSet();

                var availableCommands = _allAvailableCommands
                    .Where(c => !existingCodes.Contains(c.CommandCode))
                    .ToList();

                // Populate the searchable dropdown
                EditCommandList.ItemsSource = availableCommands;
                EditCommandSearchBox.Text = "";
                EditCommandDisplayText.Text = "Select command...";
                EditCommandDisplayText.Foreground = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#94A3B8"));
                _selectedEditCommand = null;

                EditCommandDropdownBorder.Visibility = WpfVisibility.Visible;
                EditCommandDropdownBorder.BorderBrush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#E2E8F0"));
                EditCommandError.Visibility = WpfVisibility.Collapsed;
                EditCommandName.Visibility = WpfVisibility.Collapsed;
            }
            else
            {
                // Editing existing command — command name is locked (read-only)
                _selectedEditCommand = _allAvailableCommands
                    .FirstOrDefault(c => c.CommandCode == command.CommandCode);

                EditCommandName.Text = command.CommandName;
                EditCommandName.Visibility = WpfVisibility.Visible;
                EditCommandDropdownBorder.Visibility = WpfVisibility.Collapsed;
                EditCommandError.Visibility = WpfVisibility.Collapsed;
            }

            // Populate other form fields
            EditMessage.Text = command.CustomMessage;

            // Set Mode
            foreach (ComboBoxItem item in EditMode.Items)
            {
                if (item.Tag?.ToString() == command.Mode.ToString())
                {
                    EditMode.SelectedItem = item;
                    break;
                }
            }

            // Set Scope (non-super-admins: Company-wide defaults to Project-wise)
            var scopeDisplay = command.Scope;
            if (!_isCompanyAdmin && scopeDisplay == "Company")
            {
                scopeDisplay = "Project";
            }
            foreach (ComboBoxItem item in EditScope.Items)
            {
                if (item.Content?.ToString() == scopeDisplay)
                {
                    EditScope.SelectedItem = item;
                    break;
                }
            }
            // Fallback: select first visible item
            if (EditScope.SelectedItem == null)
            {
                foreach (ComboBoxItem fallbackItem in EditScope.Items)
                {
                    if (fallbackItem.Visibility == WpfVisibility.Visible)
                    {
                        EditScope.SelectedItem = fallbackItem;
                        break;
                    }
                }
            }

            // Set checkboxes
            EditCaptureBeforeScreenshot.IsChecked = command.CaptureBeforeScreenshot;
            EditCaptureAfterScreenshot.IsChecked = command.CaptureAfterScreenshot;
            EditRequireComment.IsChecked = command.RequireComment;
            EditAllowAdminOverride.IsChecked = command.AllowAdminOverride;
            EditSendEmail.IsChecked = command.SendEmail;

            EditPanel.Visibility = WpfVisibility.Visible;

            // Auto-open command dropdown for new commands so cursor is ready in search
            if (_isNewCommand)
            {
                var autoOpenTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(150)
                };
                autoOpenTimer.Tick += (s, args) =>
                {
                    autoOpenTimer.Stop();
                    EditCommandDropdownToggle.IsChecked = true;
                };
                autoOpenTimer.Start();
            }
        }

        private void CloseEditPanel_Click(object sender, RoutedEventArgs e)
        {
            RemoveUnsavedNewCommand();
            ApplyFilters();
            UpdateStatusBar();
            CloseEditPanel();
        }

        private void CancelEdit_Click(object sender, RoutedEventArgs e)
        {
            RemoveUnsavedNewCommand();
            ApplyFilters();
            UpdateStatusBar();
            CloseEditPanel();
        }

        private void CloseEditPanel()
        {
            EditPanel.Visibility = WpfVisibility.Collapsed;
            _editingCommand = null;
            _isNewCommand = false;
        }

        private async void SaveEdit_Click(object sender, RoutedEventArgs e)
        {
            if (_editingCommand == null) return;

            // Get command from searchable dropdown (used for both new and edit)
            if (_selectedEditCommand != null)
            {
                _editingCommand.CommandCode = _selectedEditCommand.CommandCode;
                _editingCommand.CommandName = _selectedEditCommand.CommandName;
            }
            else
            {
                // No command selected — show visible validation error
                EditCommandDropdownBorder.BorderBrush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#EF4444"));
                EditCommandError.Visibility = WpfVisibility.Visible;
                StatusMessage.Text = "Please select a command";
                return;
            }

            // Update command from form
            _editingCommand.CustomMessage = EditMessage.Text;

            // Get Mode from ComboBox
            if (EditMode.SelectedItem is ComboBoxItem modeItem)
            {
                var modeTag = modeItem.Tag?.ToString();
                _editingCommand.Mode = modeTag switch
                {
                    "Notify" => InterventionMode.Notify,
                    "Assist" => InterventionMode.Assist,
                    "Protect" => InterventionMode.Protect,
                    _ => InterventionMode.Notify
                };
            }

            // Get Scope from ComboBox
            if (EditScope.SelectedItem is ComboBoxItem scopeItem)
            {
                _editingCommand.IsCompanyLevel = scopeItem.Content?.ToString() == "Company";
            }

            // Update checkboxes
            _editingCommand.CaptureBeforeScreenshot = EditCaptureBeforeScreenshot.IsChecked ?? false;
            _editingCommand.CaptureAfterScreenshot = EditCaptureAfterScreenshot.IsChecked ?? false;
            _editingCommand.RequireComment = EditRequireComment.IsChecked ?? false;
            _editingCommand.AllowAdminOverride = EditAllowAdminOverride.IsChecked ?? false;
            _editingCommand.SendEmail = EditSendEmail.IsChecked ?? false;

            // Update modified info
            _editingCommand.ModifiedBy = UserDisplayNameCache.GetDisplayName(_profileId ?? _revitUsername);
            _editingCommand.ModifiedAt = DateTime.UtcNow;

            // Save to database
            var commandName = _editingCommand.CommandName;
            var success = await _viewModel.SaveCommandAsync(_editingCommand);
            var actionText = _isNewCommand ? "Command Added!" : "Command Updated!";

            if (success)
            {
                ShowSuccessPopup(actionText, $"'{commandName}' has been saved");
                StatusMessage.Text = $"Command '{commandName}' saved";

                // Refresh command protection bindings to register/update the command
                RefreshCommandProtectionBindings();
            }
            else
            {
                StatusMessage.Text = $"Failed to save '{commandName}'";
            }

            // Refresh the grid to show updated command name
            ApplyFilters();

            CloseEditPanel();
        }

        #endregion

        #region Search and Filter

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void ModeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            if (_viewModel == null || CommandsDataGrid == null) return;

            var searchText = SearchBox?.Text?.ToLower() ?? "";
            // Status filter has been removed from the toolbar — pass empty string so the
            // ViewModel's filter pipeline treats it as "no status restriction".
            var modeTag = (ModeFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

            _viewModel.ApplyFilters(searchText, "", modeTag);
            UpdateStatusBar();
        }

        private void UpdateStatusBar()
        {
            CommandCount.Text = $"{_viewModel.FilteredCommands.Count} commands";
            DatabaseStatus.Text = "● Connected";
        }

        #endregion

        #region Edit Panel Command Dropdown

        private void EditCommandDropdown_Checked(object sender, RoutedEventArgs e)
        {
            // Auto-focus search box and scroll to selected item with small delay for popup to fully render
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                EditCommandSearchBox?.Focus();
                if (EditCommandSearchBox != null)
                    Keyboard.Focus(EditCommandSearchBox);

                // Auto-scroll to the selected item
                if (EditCommandList?.SelectedItem != null)
                {
                    EditCommandList.ScrollIntoView(EditCommandList.SelectedItem);
                }
                else if (_selectedEditCommand != null)
                {
                    foreach (var item in EditCommandList.Items)
                    {
                        if (item is AvailableCommand cmd && cmd.CommandCode == _selectedEditCommand.CommandCode)
                        {
                            EditCommandList.SelectedItem = item;
                            EditCommandList.ScrollIntoView(item);
                            break;
                        }
                    }
                }
            };
            timer.Start();
        }

        private void EditCommandDropdown_Unchecked(object sender, RoutedEventArgs e)
        {
            // Clear search when dropdown closes
            if (EditCommandSearchBox != null)
            {
                EditCommandSearchBox.Text = "";
            }
        }

        private void EditCommandSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (EditCommandList == null) return;

            var searchText = EditCommandSearchBox?.Text?.ToLower() ?? "";

            // Filter out already added commands
            var existingCodes = _viewModel.Commands
                .Where(c => c != _editingCommand)
                .Select(c => c.CommandCode)
                .ToHashSet();

            var filtered = _allAvailableCommands
                .Where(c => !existingCodes.Contains(c.CommandCode))
                .Where(c => string.IsNullOrEmpty(searchText) ||
                           c.CommandName.ToLower().Contains(searchText) ||
                           c.CommandCode.ToLower().Contains(searchText))
                .ToList();

            EditCommandList.ItemsSource = filtered;
        }

        private void EditCommandList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (EditCommandList?.SelectedItem is AvailableCommand selectedCommand)
            {
                _selectedEditCommand = selectedCommand;
                EditCommandDisplayText.Text = selectedCommand.CommandName;
                EditCommandDisplayText.Foreground = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#1E293B"));

                // Clear validation error
                EditCommandDropdownBorder.BorderBrush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#E2E8F0"));
                EditCommandError.Visibility = WpfVisibility.Collapsed;

                // Close the popup
                EditCommandDropdownToggle.IsChecked = false;
            }
        }

        #endregion

        #region DataGrid Events

        private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Selection handling
        }

        #endregion

        #region Window Chrome

        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
        }
        private void CloseButton_Click(object sender, System.Windows.RoutedEventArgs e) => Close();

        #endregion

        #region Window Events

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;

                // Clean up any unsaved state before closing
                if (EditPanel.Visibility == WpfVisibility.Visible)
                {
                    RemoveUnsavedNewCommand();
                }

                Close();
            }
        }

        #endregion

        #region Command Protection Binding

        /// <summary>
        /// Refresh command protection bindings after save/delete.
        /// This ensures changes take effect immediately without restart.
        /// </summary>
        private void RefreshCommandProtectionBindings()
        {
            if (_commandProtectionBinding == null)
            {
                _logger?.LogWarning("CommandProtectionBinding not available - bindings will not be refreshed until next Revit session");
                return;
            }

            try
            {
                _commandProtectionBinding.LoadAndRegisterCommands();
                _logger?.LogInfo($"Command protection bindings refreshed ({_commandProtectionBinding.ActiveBindingCount} active)");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to refresh command protection bindings: {ex.Message}", ex);
            }
        }

        #endregion
    }

    #region Converters

    public class ModeToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var param = parameter?.ToString() ?? "";

            // Handle boolean values for toggle and bool columns
            if (value is bool boolValue)
            {
                if (param == "toggleBg")
                {
                    return new SolidColorBrush(boolValue ?
                        (WpfColor)ColorConverter.ConvertFromString("#22C55E") :
                        (WpfColor)ColorConverter.ConvertFromString("#CBD5E1"));
                }
                if (param == "bool")
                {
                    return new SolidColorBrush(boolValue ?
                        (WpfColor)ColorConverter.ConvertFromString("#22C55E") :
                        (WpfColor)ColorConverter.ConvertFromString("#EF4444"));
                }
                // Radio button style - black filled for true, transparent for false
                if (param == "radio")
                {
                    return new SolidColorBrush(boolValue ?
                        (WpfColor)ColorConverter.ConvertFromString("#000000") :
                        Colors.Transparent);
                }
                // Radio button stroke - black for both true and false (outline always visible)
                if (param == "radioStroke")
                {
                    return new SolidColorBrush(boolValue ?
                        (WpfColor)ColorConverter.ConvertFromString("#000000") :
                        (WpfColor)ColorConverter.ConvertFromString("#000000"));
                }
            }

            // Handle InterventionMode values
            if (value is InterventionMode mode)
            {
                var color = mode switch
                {
                    InterventionMode.Notify => "#3B82F6",  // Blue - Notify
                    InterventionMode.Assist => "#F59E0B",    // Orange - Assist
                    InterventionMode.Protect => "#EF4444", // Red - Protect
                    _ => "#64748B"
                };

                var lightColor = mode switch
                {
                    InterventionMode.Notify => "#EFF6FF",
                    InterventionMode.Assist => "#FFF7ED",
                    InterventionMode.Protect => "#FEF2F2",
                    _ => "#F8FAFC"
                };

                if (param == "light")
                {
                    return new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString(lightColor));
                }

                return new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString(color));
            }

            return new SolidColorBrush(Colors.Gray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            }
            return WpfVisibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToIconConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? "✓" : "✕";
            }
            return "—";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToAlignmentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            }
            return HorizontalAlignment.Left;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    #endregion

    /// <summary>
    /// Represents an available Revit command that can be protected
    /// </summary>
    public class AvailableCommand
    {
        public string CommandCode { get; set; } = "";
        public string CommandName { get; set; } = "";
        public string? Description { get; set; }
    }
}
