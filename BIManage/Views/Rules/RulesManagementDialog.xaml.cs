using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using BIManage.Core.Rules;
using BIManage.Core.Rules.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Core.Identity;
using BIManageRevit.BIManage.ViewModels.Rules;

// Resolve ambiguous types with Revit API
using WpfColor = System.Windows.Media.Color;
using WpfVisibility = System.Windows.Visibility;
using WpfPoint = System.Windows.Point;


namespace BIManageRevit.BIManage.Views.Rules
{
    public partial class RulesManagementDialog : Window
    {
        private readonly RulesManagementViewModel _viewModel;
        private RuleViewModel? _ruleToDelete;
        private RuleViewModel? _editingRule;
        private bool _isNewRule;
        private readonly string _revitUsername;
        private readonly string? _profileId;
        private readonly string? _currentModelGuid;
        private readonly bool _isCompanyAdmin;
        private readonly bool _isProjectAdmin;
        private readonly ILogger? _logger;
        private HashSet<string> _selectedCommands = new HashSet<string>();
        private string _selectedCategory = string.Empty;
        private readonly ISignalREventBus? _eventBus;
        private Action<ProtectionChangedEvent>? _protectionChangedHandler;

        public RulesManagementDialog(IRuleService ruleService, RuleRepository ruleRepository, ILogger logger, string revitUsername = null, string apiBaseUrl = null, string databasePath = null, RulesSyncService? rulesSyncService = null, string? currentModelGuid = null, bool isCompanyAdmin = false, string? profileId = null, ISignalREventBus? eventBus = null, bool isProjectAdmin = false)
        {
            _revitUsername = revitUsername ?? Environment.UserName;
            _profileId = profileId;
            _currentModelGuid = currentModelGuid;
            _isCompanyAdmin = isCompanyAdmin;
            _isProjectAdmin = isProjectAdmin;
            _eventBus = eventBus;
            _logger = logger;
            _viewModel = new RulesManagementViewModel(ruleService, ruleRepository, logger, _revitUsername, apiBaseUrl, databasePath, rulesSyncService, _isCompanyAdmin, _profileId);
            _viewModel.CurrentModelGuid = _currentModelGuid;
            _viewModel.RequestClose += OnRequestClose;

            DataContext = _viewModel;
            InitializeComponent();
            global::BIManage.Views.Common.WindowRestoreHelper.PreventMinimize(this);

            // Hide the Status toggle column for Company / Project admins — they manage
            // policy via the edit panel, not per-row inline toggles. The column is
            // only relevant for the non-admin path.
            if ((_isCompanyAdmin || _isProjectAdmin) && StatusToggleColumn != null)
                StatusToggleColumn.Visibility = WpfVisibility.Collapsed;

            // Hide options for non-company-admins (project admins / normal users)
            if (!_isCompanyAdmin)
            {
                // Hide "Company-wide" and "Model-specific" scope options (editing only allows Project-wise)
                foreach (ComboBoxItem item in EditRuleScope.Items)
                {
                    var tag = item.Tag?.ToString();
                    if (tag == "1" || tag == "3") // CompanyWide=1, ModelSpecific=3
                    {
                        item.Visibility = WpfVisibility.Collapsed;
                    }
                }
            }

            Loaded += OnLoaded;
            Closed += OnDialogClosed;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            // Auto-sync rules from server (falls back to local DB if API unavailable)
            await _viewModel.FetchRulesFromApiAsync(_currentModelGuid);

            // Load categories and commands from API only
            await _viewModel.LoadCategoriesFromApiAsync();
            await _viewModel.LoadCommandsFromApiAsync();

            // Load registered models for scope-based mapping
            await _viewModel.LoadRegisteredModelsAsync();

            // Resolve and cache the current project's GUID — needed so a Project Admin's
            // override creation POST sends a valid Nullable<Guid>. Local registered_models
            // is preferred; fall back to a live API fetch if zemanage_project_id is missing.
            await ResolveCurrentProjectIdGuidAsync();

            ApplyFilters();

            // Subscribe to real-time protection changes via SignalR event bus
            _protectionChangedHandler = (evt) =>
            {
                if (evt.ProtectionType == "Rule")
                    Dispatcher.InvokeAsync(async () => { await _viewModel.FetchRulesFromApiAsync(_currentModelGuid); });
            };
            _eventBus?.Subscribe<ProtectionChangedEvent>(_protectionChangedHandler);
        }

        private void OnRequestClose(object? sender, EventArgs e)
        {
            Close();
        }

        #region Success Popup

        private System.Windows.Threading.DispatcherTimer? _popupTimer;

        private void ShowSuccessPopup(string title, string message)
        {
            SuccessTitle.Text = title;
            SuccessMessage.Text = message;
            SuccessPopup.Visibility = WpfVisibility.Visible;

            // Auto-hide after 3 seconds
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

        private void AddNewRule_Click(object sender, RoutedEventArgs e)
        {
            // Remove any existing unsaved new rule first to prevent extra rows
            if (_isNewRule && _editingRule != null)
            {
                _viewModel.Rules.Remove(_editingRule);
                _editingRule = null;
                _isNewRule = false;
            }

            // Calculate next priority (count + 1 for sequential 1, 2, 3...)
            var nextPriority = _viewModel.Rules.Count + 1;

            // Create a new rule and add to the list
            var newRule = new Rule
            {
                RuleId = Guid.NewGuid().ToString(), // Full GUID for new rules (lowercase to match server convention)
                Name = "New Rule",
                Description = "",
                Message = "",
                Mode = ProtectionMode.Notify,
                Priority = nextPriority,
                IsEnabled = true,
                RuleScope = _isCompanyAdmin ? (int)RuleScopeType.CompanyWide : (int)RuleScopeType.ProjectWide,
                CaptureBeforeScreenshot = false,
                CaptureAfterScreenshot = false,
                RequireComment = false,
                AllowAdminOverride = false,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                CreatedBy = UserDisplayNameCache.GetDisplayName(_profileId ?? _revitUsername),
                ModifiedBy = UserDisplayNameCache.GetDisplayName(_profileId ?? _revitUsername),
                Version = 1 // First version for new rules
            };

            var newRuleVM = new RuleViewModel(newRule) { IsNew = true };
            _viewModel.Rules.Insert(0, newRuleVM);
            _viewModel.ApplyFilters("", "", "");

            // Select the new rule and open edit panel
            _viewModel.SelectedRule = newRuleVM;
            _editingRule = newRuleVM;
            _isNewRule = true;
            OpenEditPanel(newRuleVM);
        }

        private void EditButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is RuleViewModel ruleViewModel)
            {
                _viewModel.SelectedRule = ruleViewModel;
                _editingRule = ruleViewModel;
                _isNewRule = false;
                OpenEditPanel(ruleViewModel);
            }
        }

        private void OpenEditPanel(RuleViewModel rule)
        {
            EditPanelTitle.Text = _isNewRule ? "New Rule" : "Edit Rule";

            // Populate form fields
            EditCommandName.Text = rule.CommandName;
            EditDescription.Text = rule.Description;
            EditMessage.Text = rule.Message;

            // Set Commands (multi-select)
            _selectedCommands.Clear();
            if (!string.IsNullOrEmpty(rule.CommandName))
            {
                var commands = rule.CommandName.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var cmd in commands)
                {
                    _selectedCommands.Add(cmd.Trim());
                }
            }
            UpdateCommandDisplay();
            UpdateCommandCheckboxes();

            // Set Mode
            foreach (ComboBoxItem item in EditMode.Items)
            {
                if (item.Tag?.ToString() == rule.Mode.ToString())
                {
                    EditMode.SelectedItem = item;
                    break;
                }
            }

            // Set Category
            _selectedCategory = rule.CategoryName ?? "";
            if (_isNewRule)
            {
                // New rule: show searchable dropdown
                CategoryDropdownBorder.Visibility = WpfVisibility.Visible;
                EditCategoryReadOnly.Visibility = WpfVisibility.Collapsed;
            }
            else
            {
                // Editing existing rule: category is locked (read-only)
                EditCategoryReadOnly.Text = rule.CategoryName ?? "";
                EditCategoryReadOnly.Visibility = WpfVisibility.Visible;
                CategoryDropdownBorder.Visibility = WpfVisibility.Collapsed;
            }
            UpdateCategoryDisplay();

            // Set Rule Scope by matching Tag (integer)
            // Non-super-admins: Company-wide rules default to Project-wise (Company-wide is hidden)
            var scopeValue = rule.RuleScope;
            if (!_isCompanyAdmin && scopeValue == (int)RuleScopeType.CompanyWide)
            {
                scopeValue = (int)RuleScopeType.ProjectWide;
            }
            var scopeTag = scopeValue.ToString();
            foreach (ComboBoxItem scopeItem in EditRuleScope.Items)
            {
                if (scopeItem.Tag?.ToString() == scopeTag)
                {
                    EditRuleScope.SelectedItem = scopeItem;
                    break;
                }
            }
            // Fallback: select first visible item if no match
            if (EditRuleScope.SelectedItem == null)
            {
                foreach (ComboBoxItem fallbackItem in EditRuleScope.Items)
                {
                    if (fallbackItem.Visibility == WpfVisibility.Visible)
                    {
                        EditRuleScope.SelectedItem = fallbackItem;
                        break;
                    }
                }
            }

            // Set checkboxes
            EditCaptureBeforeScreenshot.IsChecked = rule.CaptureBeforeScreenshot;
            EditCaptureAfterScreenshot.IsChecked = rule.CaptureAfterScreenshot;
            EditRequireComment.IsChecked = rule.RequireComment;
            EditAllowAdminOverride.IsChecked = rule.AllowAdminOverride;
            EditSendEmail.IsChecked = rule.SendEmail;

            EditPanel.Visibility = WpfVisibility.Visible;

            // Auto-open Category dropdown for new rules so cursor is ready in search
            if (_isNewRule)
            {
                var autoOpenTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(150)
                };
                autoOpenTimer.Tick += (s, args) =>
                {
                    autoOpenTimer.Stop();
                    CategoryDropdownToggle.IsChecked = true;
                };
                autoOpenTimer.Start();
            }
        }

        private void CloseEditPanel_Click(object sender, RoutedEventArgs e)
        {
            RemoveUnsavedNewRule();
            CloseEditPanel();
        }

        private void CancelEdit_Click(object sender, RoutedEventArgs e)
        {
            RemoveUnsavedNewRule();
            CloseEditPanel();
        }

        private void RemoveUnsavedNewRule()
        {
            if (_isNewRule && _editingRule != null)
            {
                _viewModel.Rules.Remove(_editingRule);
                _viewModel.ApplyFilters("", "", "");
            }
        }

        private void CloseEditPanel()
        {
            EditPanel.Visibility = WpfVisibility.Collapsed;
            _editingRule = null;
            _isNewRule = false;
        }

        private async void SaveEdit_Click(object sender, RoutedEventArgs e)
        {
            if (_editingRule == null) return;

            // Validate required fields
            var hasError = false;

            var categoryText = _selectedCategory?.Trim() ?? "";
            if (string.IsNullOrEmpty(categoryText))
            {
                CategoryDropdownBorder.BorderBrush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#EF4444"));
                EditCategoryError.Visibility = WpfVisibility.Visible;
                hasError = true;
            }
            else
            {
                CategoryDropdownBorder.BorderBrush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#E2E8F0"));
                EditCategoryError.Visibility = WpfVisibility.Collapsed;
            }

            if (_selectedCommands == null || _selectedCommands.Count == 0)
            {
                CommandDropdownBorder.BorderBrush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#EF4444"));
                EditCommandError.Visibility = WpfVisibility.Visible;
                hasError = true;
            }
            else
            {
                CommandDropdownBorder.BorderBrush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#E2E8F0"));
                EditCommandError.Visibility = WpfVisibility.Collapsed;
            }

            if (hasError)
            {
                _viewModel.StatusMessage = "Please fill in all required fields";
                return;
            }

            // Get command names from multi-select (comma-separated)
            var commandName = string.Join(", ", _selectedCommands);

            // Get Mode from ComboBox
            var mode = ProtectionMode.Notify;
            if (EditMode.SelectedItem is ComboBoxItem modeItem)
            {
                var modeTag = modeItem.Tag?.ToString();
                mode = modeTag switch
                {
                    "Notify" => ProtectionMode.Notify,
                    "Assist" => ProtectionMode.Assist,
                    "Protect" => ProtectionMode.Protect,
                    _ => ProtectionMode.Notify
                };
            }

            // Get common values
            var description = EditDescription.Text;
            var message = EditMessage.Text;
            var ruleScopeTag = (EditRuleScope.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "1";
            var ruleScope = int.TryParse(ruleScopeTag, out var scopeInt) ? scopeInt : (int)RuleScopeType.CompanyWide;
            var captureBeforeScreenshot = EditCaptureBeforeScreenshot.IsChecked ?? false;
            var captureAfterScreenshot = EditCaptureAfterScreenshot.IsChecked ?? false;
            var requireComment = EditRequireComment.IsChecked ?? false;
            var allowAdminOverride = EditAllowAdminOverride.IsChecked ?? false;
            var sendEmail = EditSendEmail.IsChecked ?? false;

            // Save rule — use category name as rule name
            _editingRule.Name = !string.IsNullOrEmpty(categoryText) ? categoryText : _editingRule.Name;
            _editingRule.CommandName = commandName;
            _editingRule.Description = description;
            _editingRule.Message = message;
            _editingRule.Mode = mode;
            _editingRule.CategoryName = categoryText;
            _editingRule.RuleScope = ruleScope;
            _editingRule.CaptureBeforeScreenshot = captureBeforeScreenshot;
            _editingRule.CaptureAfterScreenshot = captureAfterScreenshot;
            _editingRule.RequireComment = requireComment;
            _editingRule.AllowAdminOverride = allowAdminOverride;
            _editingRule.SendEmail = sendEmail;

            // Set category code from category name (reuse categoryText from validation above)
            if (!string.IsNullOrEmpty(categoryText))
            {
                var categoryInfo = _viewModel.GetCategoryByName(categoryText);
                if (categoryInfo != null)
                {
                    _editingRule.CategoryCode = categoryInfo.CategoryCode ?? "";
                }
                else
                {
                    _editingRule.CategoryCode = "";
                }
            }
            else
            {
                _editingRule.CategoryCode = "";
            }

            // Get rule and update timestamps
            var rule = _editingRule.ToRule();
            rule.ModifiedAt = DateTime.UtcNow;
            rule.ModifiedBy = _profileId ?? _revitUsername;
            _editingRule.ModifiedBy = UserDisplayNameCache.GetDisplayName(_profileId ?? _revitUsername);

            // Auto-detect project_id / model_guid from current Revit model.
            // CRITICAL (2026-05-25): only overwrite the rule's existing ID when it's missing.
            // On edits, the rule already carries the correct ProjectId / ModelGuid from the
            // server. The previous unconditional overwrite would clobber that with the
            // currently-open model's auto-detected values — which are null when no doc is
            // open OR when the doc isn't in registered_models. A PUT then went out with
            // projectId=null for a project-scope rule, the server couldn't identify the
            // tenant scope, silently no-op'd, and the next fetch reverted the user's mode
            // change. Preserve existing IDs on edits; auto-detect only when truly missing.
            switch (ruleScope)
            {
                case (int)RuleScopeType.ProjectWide:
                    if (string.IsNullOrWhiteSpace(rule.ProjectId))
                        rule.ProjectId = GetAutoDetectedProjectId();
                    rule.ModelGuid = null;
                    break;

                case (int)RuleScopeType.ModelSpecific:
                    if (string.IsNullOrWhiteSpace(rule.ModelGuid))
                        rule.ModelGuid = GetAutoDetectedModelGuid();
                    if (string.IsNullOrWhiteSpace(rule.ProjectId))
                        rule.ProjectId = GetAutoDetectedProjectId();
                    break;

                default: // CompanyWide
                    rule.ProjectId = null;
                    rule.ModelGuid = null;
                    break;
            }

            // Set command names for database (API provides names only, no IDs)
            rule.CommandIds.Clear();
            rule.CommandNames.Clear();
            foreach (var cmd in _selectedCommands)
            {
                rule.CommandNames.Add(cmd);
            }

            // Version handling: 1 for new rules, auto-increment on edit
            if (_isNewRule)
            {
                rule.Version = 1;
            }
            else
            {
                rule.Version++;
            }

            // Save to database and sync to API (POST for new, PUT for updates)
            var success = await _viewModel.SaveRuleDirectAsync(rule, _isNewRule);
            if (success)
            {
                var actionText = _isNewRule ? "Created" : "Updated";
                ShowSuccessPopup($"Rule {actionText}!", $"'{rule.Name}' has been saved successfully");
                _viewModel.StatusMessage = $"Rule '{rule.Name}' saved";
                if (_isNewRule)
                {
                    _editingRule.IsNew = false;
                }
            }

            CloseEditPanel();
        }

        private void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is RuleViewModel ruleViewModel)
            {
                _ruleToDelete = ruleViewModel;
                DeleteRuleName.Text = $"'{ruleViewModel.Name}'";
                DeletePopup.Visibility = WpfVisibility.Visible;
                e.Handled = true;
            }
        }

        private void CancelDelete_Click(object sender, RoutedEventArgs e)
        {
            DeletePopup.Visibility = WpfVisibility.Collapsed;
            _ruleToDelete = null;
        }

        private async void ConfirmDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_ruleToDelete != null)
            {
                var ruleName = _ruleToDelete.Name;
                _viewModel.SelectedRule = _ruleToDelete;
                if (_viewModel.DeleteRuleCommand.CanExecute(null))
                {
                    _viewModel.DeleteRuleCommand.Execute(null);
                    // Small delay to allow delete to complete
                    await Task.Delay(100);
                    // Refresh to update row numbers
                    ApplyFilters();
                    ShowSuccessPopup("Rule Deleted!", $"'{ruleName}' has been removed");
                }
            }
            DeletePopup.Visibility = WpfVisibility.Collapsed;
            _ruleToDelete = null;
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

        private void ApplyFilters()
        {
            if (_viewModel == null || RulesDataGrid == null) return;

            var searchText = SearchBox?.Text?.ToLower() ?? "";
            var statusTag = (StatusFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";
            var modeTag = (ModeFilter?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "";

            _viewModel.ApplyFilters(searchText, statusTag, modeTag);
        }

        #endregion

        #region Status Toggle

        private async void StatusToggle_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.DataContext is RuleViewModel ruleViewModel)
            {
                var rule = ruleViewModel.ToRule();
                rule.ModifiedAt = DateTime.UtcNow;
                rule.ModifiedBy = _profileId ?? _revitUsername;
                ruleViewModel.ModifiedBy = UserDisplayNameCache.GetDisplayName(_profileId ?? _revitUsername);
                await _viewModel.SaveRuleDirectAsync(rule);
            }
        }

        private async void StatusToggle_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button button && button.DataContext is RuleViewModel ruleViewModel)
            {
                // Toggle the status
                ruleViewModel.IsEnabled = !ruleViewModel.IsEnabled;

                var rule = ruleViewModel.ToRule();
                rule.ModifiedAt = DateTime.UtcNow;
                rule.ModifiedBy = _profileId ?? _revitUsername;
                ruleViewModel.ModifiedBy = UserDisplayNameCache.GetDisplayName(_profileId ?? _revitUsername);

                var success = await _viewModel.SaveRuleDirectAsync(rule);
                if (success)
                {
                    ShowSuccessPopup(ruleViewModel.IsEnabled ? "Rule Enabled" : "Rule Disabled",
                        $"'{ruleViewModel.Name}' status updated");
                }
            }
        }

        #endregion

        #region Command Multi-Select

        private void CommandDropdown_Checked(object sender, RoutedEventArgs e)
        {
            // Clear search and show all commands
            CommandSearchBox.Text = "";
            _viewModel.FilterCommands("");
            Dispatcher.BeginInvoke(new Action(() => UpdateCommandCheckboxes()),
                System.Windows.Threading.DispatcherPriority.Background);

            // Auto-focus search box with small delay for popup to fully render
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                CommandSearchBox.Focus();
                Keyboard.Focus(CommandSearchBox);
            };
            timer.Start();
        }

        private void CommandDropdown_Unchecked(object sender, RoutedEventArgs e)
        {
            // Popup closes automatically
        }

        private void CommandSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                _viewModel.FilterCommands(textBox.Text);
                // Update checkboxes after filtering
                Dispatcher.BeginInvoke(new Action(() => UpdateCommandCheckboxes()),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private async void CommandCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox && checkBox.Content != null)
            {
                var command = checkBox.Content.ToString();
                if (string.IsNullOrEmpty(command)) return;

                if (checkBox.IsChecked == true)
                {
                    _selectedCommands.Add(command);

                    // Fetch command details from API and insert into cache table
                    await _viewModel.FetchAndCacheCommandDetailsAsync(new[] { command });
                }
                else
                {
                    _selectedCommands.Remove(command);
                }

                UpdateCommandDisplay();

                // Clear validation error when command is selected
                if (_selectedCommands.Count > 0)
                {
                    CommandDropdownBorder.BorderBrush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#E2E8F0"));
                    EditCommandError.Visibility = WpfVisibility.Collapsed;
                }
            }
        }

        #endregion

        #region Category Searchable Dropdown

        private void CategoryDropdown_Checked(object sender, RoutedEventArgs e)
        {
            // Clear search and show all categories
            CategorySearchBox.Text = "";
            _viewModel.FilterCategories("");

            // Auto-focus search box with small delay for popup to fully render
            var timer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(50)
            };
            timer.Tick += (s, args) =>
            {
                timer.Stop();
                CategorySearchBox.Focus();
                Keyboard.Focus(CategorySearchBox);
            };
            timer.Start();
        }

        private void CategoryDropdown_Unchecked(object sender, RoutedEventArgs e)
        {
            // Popup closes automatically
        }

        private void CategorySearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                _viewModel.FilterCategories(textBox.Text);
            }
        }

        private void CategoryItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is string categoryName)
            {
                _selectedCategory = categoryName;
                UpdateCategoryDisplay();

                // Close popup
                CategoryDropdownToggle.IsChecked = false;

                // Clear validation error
                CategoryDropdownBorder.BorderBrush = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#E2E8F0"));
                EditCategoryError.Visibility = WpfVisibility.Collapsed;
            }
        }

        private void UpdateCategoryDisplay()
        {
            if (string.IsNullOrEmpty(_selectedCategory))
            {
                CategoryDisplayText.Text = "Select category...";
                CategoryDisplayText.Foreground = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#94A3B8"));
            }
            else
            {
                CategoryDisplayText.Text = _selectedCategory;
                CategoryDisplayText.Foreground = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#1E293B"));
            }
        }

        #endregion

        #region Command Multi-Select Helpers

        private void UpdateCommandDisplay()
        {
            if (_selectedCommands.Count == 0)
            {
                CommandDisplayText.Text = "Select commands...";
                CommandDisplayText.Foreground = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#94A3B8"));
            }
            else
            {
                CommandDisplayText.Text = string.Join(", ", _selectedCommands);
                CommandDisplayText.Foreground = new SolidColorBrush((WpfColor)ColorConverter.ConvertFromString("#1E293B"));
            }
        }

        private void UpdateCommandCheckboxes()
        {
            // Update checkboxes based on selected commands
            foreach (var item in CommandCheckList.Items)
            {
                var container = CommandCheckList.ItemContainerGenerator.ContainerFromItem(item);
                if (container != null)
                {
                    var checkBox = FindVisualChild<CheckBox>(container);
                    if (checkBox != null && checkBox.Content != null)
                    {
                        checkBox.IsChecked = _selectedCommands.Contains(checkBox.Content.ToString() ?? "");
                    }
                }
            }
        }

        private static T? FindVisualChild<T>(DependencyObject? parent) where T : DependencyObject
        {
            if (parent == null) return null;

            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T result)
                    return result;

                var childResult = FindVisualChild<T>(child);
                if (childResult != null)
                    return childResult;
            }
            return null;
        }

        #endregion

        #region Scope-Based Auto-Detection

        private void EditRuleScope_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // No UI to update - project_id/model_guid are auto-detected on save
        }

        /// <summary>
        /// Get auto-detected project_id from current Revit model
        /// </summary>
        private string? GetAutoDetectedProjectId()
        {
            return _viewModel.AutoDetectScopeIds(_currentModelGuid).ProjectId;
        }

        /// <summary>
        /// Get auto-detected model_guid from current Revit model
        /// </summary>
        private string? GetAutoDetectedModelGuid()
        {
            return _viewModel.AutoDetectScopeIds(_currentModelGuid).ModelGuid;
        }

        /// <summary>
        /// Resolves the current model's project GUID — first via local registered_models,
        /// then a live API fallback if the local row is stale or missing the GUID. Caches
        /// the result on the ViewModel so override paths can use it without a network roundtrip.
        /// </summary>
        private async System.Threading.Tasks.Task ResolveCurrentProjectIdGuidAsync()
        {
            try
            {
                var fromLocal = _viewModel.AutoDetectScopeIds(_currentModelGuid).ProjectId;
                if (!string.IsNullOrWhiteSpace(fromLocal))
                {
                    _viewModel.CurrentProjectIdGuid = fromLocal;
                    return;
                }

                if (string.IsNullOrEmpty(_currentModelGuid)) return;

                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;
                var modelSync = services?.GetService<global::BIManage.Infrastructure.Api.ModelSyncService>();
                if (modelSync == null) return;

                var apiModel = await modelSync.FetchModelByGuidAsync(_currentModelGuid);
                _viewModel.CurrentProjectIdGuid = apiModel?.ZemanageProjectId ?? apiModel?.ProjectIdAlternate;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"ResolveCurrentProjectIdGuidAsync failed: {ex.Message}");
            }
        }

        #endregion

        #region DataGrid Events

        private void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Selection handling
        }

        #endregion

        #region Drag-Drop Row Reordering

        private RuleViewModel? _draggedItem;
        private WpfPoint _startPoint;

        private void DataGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _startPoint = e.GetPosition(null);
        }

        private void DataGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var currentPosition = e.GetPosition(null);
            var diff = _startPoint - currentPosition;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                var dataGrid = sender as DataGrid;
                var row = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);

                if (row != null && dataGrid != null)
                {
                    _draggedItem = row.Item as RuleViewModel;
                    if (_draggedItem != null)
                    {
                        DragDrop.DoDragDrop(dataGrid, _draggedItem, DragDropEffects.Move);
                    }
                }
            }
        }

        private async void DataGrid_Drop(object sender, DragEventArgs e)
        {
            if (_draggedItem == null) return;

            var targetRow = FindVisualParent<DataGridRow>(e.OriginalSource as DependencyObject);
            if (targetRow == null) return;

            var targetItem = targetRow.Item as RuleViewModel;
            if (targetItem == null || targetItem == _draggedItem) return;

            var rules = _viewModel.Rules;
            var oldIndex = rules.IndexOf(_draggedItem);
            var newIndex = rules.IndexOf(targetItem);

            if (oldIndex >= 0 && newIndex >= 0 && oldIndex != newIndex)
            {
                rules.Move(oldIndex, newIndex);

                // Persist the new order to database
                await _viewModel.SaveRulePrioritiesAsync();

                // Refresh to update row numbers
                ApplyFilters();
                ShowSuccessPopup("Order Updated!", "Rule order has been saved");
            }

            _draggedItem = null;
        }

        private void DataGrid_DragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }

        private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T parent)
                    return parent;
                child = VisualTreeHelper.GetParent(child);
            }
            return null;
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
                Close();
            }
        }

        private void OnDialogClosed(object sender, EventArgs e)
        {
            if (_protectionChangedHandler != null)
                _eventBus?.Unsubscribe<ProtectionChangedEvent>(_protectionChangedHandler);
        }

        #endregion
    }

}
