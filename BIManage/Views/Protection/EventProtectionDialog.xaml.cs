using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Revit.Protection;
using BIManage.Views.Common;
using BIManageRevit.BIManage.ViewModels.Protection;

using WpfVisibility = System.Windows.Visibility;


namespace BIManageRevit.BIManage.Views.Protection
{
    public partial class EventProtectionDialog : Window
    {
        private readonly EventProtectionViewModel _viewModel;
        private readonly ILogger? _logger;
        private readonly string _revitUsername;
        private readonly string? _profileId;
        private EventSettingViewModel? _editingSetting;
        private readonly bool _isCompanyAdmin;
        private readonly bool _isProjectAdmin;
        private readonly ISignalREventBus? _eventBus;
        private readonly string? _modelGuid;
        private Action<ProtectionChangedEvent>? _protectionChangedHandler;

        public EventProtectionDialog(
            string revitUsername,
            EventProtectionRepository? repository = null,
            int? projectId = null,
            ILogger? logger = null,
            bool isCompanyAdmin = false,
            string? profileId = null,
            EventProtectionSyncService? syncService = null,
            string? modelGuid = null,
            ISignalREventBus? eventBus = null,
            string? currentProjectIdGuid = null,
            bool isProjectAdmin = false)
        {
            _revitUsername = revitUsername ?? Environment.UserName;
            _profileId = profileId;
            _logger = logger;
            _isCompanyAdmin = isCompanyAdmin;
            _isProjectAdmin = isProjectAdmin;
            _eventBus = eventBus;
            _modelGuid = modelGuid;
            _viewModel = new EventProtectionViewModel(_revitUsername, repository, projectId, logger, _profileId, syncService, modelGuid, _isCompanyAdmin);
            _viewModel.CurrentProjectId = currentProjectIdGuid;

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

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Fetch latest from API on open (falls back to DB if unavailable)
            await _viewModel.FetchFromApiAsync();
            ApplyFilters();
            UpdateStatusBar();

            // Resolve current project's GUID in the background so override creation has a
            // valid Nullable<Guid> projectId. Doing it here (post-Loaded) keeps the click-to-open
            // latency snappy — the API roundtrip no longer blocks the UI thread.
            _ = ResolveCurrentProjectIdGuidAsync();

            // Subscribe to real-time protection changes via SignalR event bus
            _protectionChangedHandler = (evt) =>
            {
                if (evt.ProtectionType == "Event")
                    Dispatcher.InvokeAsync(async () => { await _viewModel.FetchFromApiAsync(); ApplyFilters(); UpdateStatusBar(); });
            };
            _eventBus?.Subscribe<ProtectionChangedEvent>(_protectionChangedHandler);
        }

        /// <summary>
        /// Resolves the project GUID for the current model — first via local registered_models,
        /// then via a live API fetch if the local row is stale or missing the GUID. Caches
        /// the result on the ViewModel so the project-admin override path can attach it.
        /// </summary>
        private async System.Threading.Tasks.Task ResolveCurrentProjectIdGuidAsync()
        {
            // If the caller already passed in a valid GUID, no work to do.
            if (!string.IsNullOrWhiteSpace(_viewModel.CurrentProjectId)) return;
            if (string.IsNullOrEmpty(_modelGuid)) return;

            try
            {
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;
                if (services == null) return;

                // 1) Local DB
                try
                {
                    var logDir = System.IO.Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "BIManageRevit", "Logs");
                    var dbPath = System.IO.Path.Combine(logDir, "bimanage.db");
                    var registeredRepo = new global::BIManage.Data.SQLite.RegisteredModelsRepository(dbPath, _logger);
                    var registered = await registeredRepo.GetModelAsync(_modelGuid);
                    if (!string.IsNullOrWhiteSpace(registered?.ZemanageProjectId))
                    {
                        _viewModel.CurrentProjectId = registered.ZemanageProjectId;
                        _logger?.LogInfo($"EventProtectionDialog: resolved projectIdGuid from local DB: {registered.ZemanageProjectId}");
                        return;
                    }
                }
                catch (Exception localEx)
                {
                    _logger?.LogDebug($"EventProtectionDialog: local registered_models lookup failed: {localEx.Message}");
                }

                // 2) API fallback
                var modelSync = services.GetService<global::BIManage.Infrastructure.Api.ModelSyncService>();
                if (modelSync != null)
                {
                    try
                    {
                        var apiModel = await modelSync.FetchModelByGuidAsync(_modelGuid);
                        var fromApi = apiModel?.ZemanageProjectId ?? apiModel?.ProjectIdAlternate;
                        if (!string.IsNullOrWhiteSpace(fromApi))
                        {
                            _viewModel.CurrentProjectId = fromApi;
                            _logger?.LogInfo($"EventProtectionDialog: resolved projectIdGuid via API fallback: {fromApi}");
                        }
                        else
                        {
                            _logger?.LogWarning($"EventProtectionDialog: project GUID still unresolved for model {_modelGuid}");
                        }
                    }
                    catch (Exception apiEx)
                    {
                        _logger?.LogWarning($"EventProtectionDialog: API project-id fallback failed — {apiEx.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"EventProtectionDialog.ResolveCurrentProjectIdGuidAsync failed: {ex.Message}");
            }
        }

        private void OnClosed(object sender, EventArgs e)
        {
            if (_protectionChangedHandler != null)
                _eventBus?.Unsubscribe<ProtectionChangedEvent>(_protectionChangedHandler);
        }

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
            _popupTimer.Tick += (s, ev) =>
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

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is EventSettingViewModel settingVm)
            {
                _viewModel.SelectedSetting = settingVm;
                _editingSetting = settingVm;
                OpenEditPanel(settingVm);
            }
        }

        /// <summary>
        /// Project-scoped event protections are user-deletable (DELETE
        /// /api/v1/Revit/event-protections/{eventProtectionId}). Company-scoped
        /// rows are read-only here — the button's Visibility binds to
        /// IsProjectLevel so it never appears for Company rows.
        /// </summary>
        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.DataContext is EventSettingViewModel settingVm))
                return;

            // Defensive: refuse if somehow invoked on a Company-scoped row
            if (settingVm.IsCompanyLevel)
            {
                _logger?.LogWarning($"DeleteButton_Click invoked on Company-scoped row '{settingVm.ProtectionName}' — refusing");
                return;
            }

            var confirmResult = MessageBox.Show(
                $"Delete project-scoped event protection '{settingVm.ProtectionName}'?\n\n" +
                "This removes the project override. The company-default behaviour will resume.",
                "Confirm Delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirmResult != MessageBoxResult.Yes) return;

            try
            {
                if (string.IsNullOrEmpty(settingVm.Id))
                {
                    _logger?.LogWarning($"Delete: no Id on '{settingVm.ProtectionName}' — removing from UI only");
                    _viewModel.Settings.Remove(settingVm);
                    return;
                }

                var ok = await _viewModel.DeleteSettingAsync(settingVm.Id, settingVm.DummyCommandId);
                if (ok)
                {
                    _viewModel.Settings.Remove(settingVm);
                    _logger?.LogInfo($"Deleted event protection '{settingVm.ProtectionName}' ({settingVm.Id})");
                }
                else
                {
                    MessageBox.Show(
                        $"Failed to delete '{settingVm.ProtectionName}'. The change was queued for retry.",
                        "Delete Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"DeleteButton_Click error: {ex.Message}", ex);
                MessageBox.Show($"Failed to delete: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OpenEditPanel(EventSettingViewModel setting)
        {
            EditProtectionName.Text = setting.ProtectionName;
            EditMessage.Text = setting.CustomMessage;

            // Set Mode
            foreach (ComboBoxItem item in EditMode.Items)
            {
                if (item.Tag?.ToString() == setting.Mode.ToString())
                {
                    EditMode.SelectedItem = item;
                    break;
                }
            }

            // Set Scope (non-super-admins: Company-wide defaults to Project-wise)
            var scopeDisplay = setting.Scope;
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
            EditEnabled.IsChecked = setting.Enabled;
            EditCaptureBeforeScreenshot.IsChecked = setting.CaptureBeforeScreenshot;
            EditCaptureAfterScreenshot.IsChecked = setting.CaptureAfterScreenshot;
            EditRequireComment.IsChecked = setting.RequireComment;
            EditAllowAdminOverride.IsChecked = setting.AllowAdminOverride;
            EditSendEmail.IsChecked = setting.SendEmail;

            EditPanel.Visibility = WpfVisibility.Visible;
        }

        private void CloseEditPanel_Click(object sender, RoutedEventArgs e)
        {
            CloseEditPanel();
        }

        private void CancelEdit_Click(object sender, RoutedEventArgs e)
        {
            CloseEditPanel();
        }

        private void CloseEditPanel()
        {
            EditPanel.Visibility = WpfVisibility.Collapsed;
            _editingSetting = null;
        }

        private async void SaveEdit_Click(object sender, RoutedEventArgs e)
        {
            if (_editingSetting == null) return;

            // Update from form
            _editingSetting.CustomMessage = EditMessage.Text;

            // Get Mode
            if (EditMode.SelectedItem is ComboBoxItem modeItem)
            {
                var modeTag = modeItem.Tag?.ToString();
                _editingSetting.Mode = modeTag switch
                {
                    "Notify" => InterventionMode.Notify,
                    "Assist" => InterventionMode.Assist,
                    "Protect" => InterventionMode.Protect,
                    _ => InterventionMode.Notify
                };
            }

            // Get Scope
            if (EditScope.SelectedItem is ComboBoxItem scopeItem)
            {
                _editingSetting.IsCompanyLevel = scopeItem.Content?.ToString() == "Company";
            }

            // Update checkboxes
            _editingSetting.Enabled = EditEnabled.IsChecked ?? false;
            _editingSetting.CaptureBeforeScreenshot = EditCaptureBeforeScreenshot.IsChecked ?? false;
            _editingSetting.CaptureAfterScreenshot = EditCaptureAfterScreenshot.IsChecked ?? false;
            _editingSetting.RequireComment = EditRequireComment.IsChecked ?? false;
            _editingSetting.AllowAdminOverride = EditAllowAdminOverride.IsChecked ?? false;
            _editingSetting.SendEmail = EditSendEmail.IsChecked ?? false;

            // Save to database
            var protectionName = _editingSetting.ProtectionName;
            var success = await _viewModel.SaveSettingAsync(_editingSetting);

            if (success)
            {
                ShowSuccessPopup("Setting Updated!", $"'{protectionName}' has been saved");
                StatusMessage.Text = $"Setting '{protectionName}' saved";
            }
            else
            {
                StatusMessage.Text = $"Failed to save '{protectionName}'";
            }

            ApplyFilters();
            CloseEditPanel();
        }

        private async void ToggleEnabled_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is Button button) || !(button.DataContext is EventSettingViewModel settingVm))
                return;

            // Project Admin guard: rows where the server reports HasCompanyScope=true
            // are owned by Company Admin. A Project Admin may NOT disable them from
            // Revit. They CAN still enable them (toggle on). Only block the
            // disable direction. Company Admins are unrestricted.
            if (_isProjectAdmin && !_isCompanyAdmin
                && settingVm.HasCompanyScope
                && settingVm.Enabled)  // currently ON, user is trying to turn it OFF
            {
                _logger?.LogInfo($"[EventProt] Project Admin attempted to disable company-scoped row '{settingVm.ProtectionName}' — blocked, showing popup");
                ZeMessageBox.Show(
                    "Cannot Disable Protection",
                    $"'{settingVm.ProtectionName}' is managed at the Company level.\n\n" +
                    "Project Admins can enable but not disable Company-scoped event protections. " +
                    "Ask a Company Administrator to change this setting, or override it locally via the edit panel.",
                    ZeMessageType.Warning);
                return;  // Do not flip the toggle, do not push to server
            }

            settingVm.Enabled = !settingVm.Enabled;
            await _viewModel.ToggleSettingEnabledAsync(settingVm);
            ApplyFilters();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await _viewModel.FetchFromApiAsync();
            ApplyFilters();
            UpdateStatusBar();
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            var result = await _viewModel.ImportFromJsonAsync();
            if (result.Success)
            {
                ApplyFilters();
                UpdateStatusBar();
                ShowSuccessPopup("Import Complete", result.Message);
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

        private void NeedHelp_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string? sessionId = null;
                string? revitVersion = null;
                string? revitBuild = null;
                string? modelName = null;
                global::BIManage.Infrastructure.Auth.AuthenticatedHttpClient? httpClient = null;
                try
                {
                    var services = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance?.Services;
                    var context = services?.GetService<global::BIManage.Revit.Context.IRevitContext>();
                    sessionId = context?.SessionId.ToString();
                    httpClient = services?.GetService<global::BIManage.Infrastructure.Auth.AuthenticatedHttpClient>();

                    // Get Revit version from ControlledApplication
                    var ctrlApp = context?.ControlledApplication;
                    if (ctrlApp != null)
                    {
                        revitVersion = ctrlApp.VersionNumber;
                        revitBuild = ctrlApp.VersionBuild;
                    }

                    // Get model name from active session
                    var sessionRepo = services?.GetService<global::BIManage.Data.SQLite.SessionRepository>();
                    if (sessionRepo != null && sessionId != null)
                    {
                        try
                        {
                            var session = System.Threading.Tasks.Task.Run(() => sessionRepo.GetSessionAsync(sessionId)).GetAwaiter().GetResult();
                            if (session != null)
                            {
                                // Get the latest active document from document_sessions
                                var docs = System.Threading.Tasks.Task.Run(() => sessionRepo.GetActiveDocumentsAsync(sessionId)).GetAwaiter().GetResult();
                                if (docs != null && docs.Count > 0)
                                    modelName = docs[0].DocumentTitle;
                            }
                        }
                        catch { }
                    }
                }
                catch { }

                var dialog = new BIManageRevit.BIManage.Views.Support.ReportIssueDialog(
                    httpClient: httpClient,
                    revitVersion: revitVersion,
                    revitBuild: revitBuild,
                    revitUsername: _revitUsername,
                    sessionId: sessionId,
                    modelName: modelName,
                    preSelectModule: "Protection");
                dialog.Owner = this;
                dialog.ShowDialog();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Failed to open help dialog: {ex.Message}", "Error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        #endregion

        #region Search and Filter

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void StatusFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void ModeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void EventTypeFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ApplyFilters();
        }

        private void ApplyFilters()
        {
            if (_viewModel == null || EventsDataGrid == null) return;

            var searchText = SearchBox?.Text?.ToLower() ?? "";
            var statusTag = (StatusFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var modeTag = (ModeFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var eventTypeTag = (EventTypeFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

            _viewModel.ApplyFilters(searchText, statusTag, modeTag, eventTypeTag);
            UpdateStatusBar();
        }

        private void UpdateStatusBar()
        {
            SettingCount.Text = $"{_viewModel.FilteredSettings.Count} event protections";
            DatabaseStatus.Text = "● Connected";
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
                if (EditPanel.Visibility == WpfVisibility.Visible)
                {
                    CloseEditPanel();
                }
                else
                {
                    Close();
                }
            }
        }

        #endregion
    }
}
