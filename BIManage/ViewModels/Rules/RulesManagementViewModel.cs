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
using System.Windows.Input;
using BIManage.Core.Identity;
using BIManage.Core.Rules;
using BIManage.Core.Rules.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Api;
using BIManage.Infrastructure.Logging;
using Microsoft.Win32;

namespace BIManageRevit.BIManage.ViewModels.Rules
{
    /// <summary>
    /// Static cache mapping command name/code → description (from API).
    /// Populated when RulesManagementViewModel loads commands, queried by RuleViewModel.CommandNameTooltip.
    /// </summary>
    internal static class RuleCommandDescriptionCache
    {
        private static readonly Dictionary<string, string?> _map = new(StringComparer.OrdinalIgnoreCase);

        public static void Populate(IEnumerable<(string CommandName, string? MemberName, string? Description)> rows)
        {
            if (rows == null) return;
            foreach (var (name, member, desc) in rows)
            {
                if (!string.IsNullOrEmpty(name)) _map[name] = desc;
                if (!string.IsNullOrEmpty(member)) _map[member] = desc;
            }
        }

        public static void Populate(IEnumerable<RevitCommand> rows)
        {
            if (rows == null) return;
            foreach (var c in rows)
            {
                if (!string.IsNullOrEmpty(c.CommandName)) _map[c.CommandName] = c.Description;
                if (!string.IsNullOrEmpty(c.MemberName)) _map[c.MemberName!] = c.Description;
            }
        }

        public static string? Get(string? key) =>
            !string.IsNullOrEmpty(key) && _map.TryGetValue(key!, out var d) ? d : null;
    }

    /// <summary>
    /// ViewModel for Rules Management Dialog
    /// Handles CRUD operations for rules with database connectivity
    /// Supports Excel import/export functionality
    /// </summary>
    public class RulesManagementViewModel : INotifyPropertyChanged
    {
        private readonly IRuleService _ruleService;
        private readonly RuleRepository _ruleRepository;
        private readonly CacheRepository? _cacheRepository;
        private readonly ILogger? _logger;
        private readonly string _revitUsername;
        private readonly string? _profileId;
        private readonly RevitApiService? _revitApiService;
        private readonly RulesSyncService? _rulesSyncService;
        private readonly string _apiBaseUrl;
        private readonly string _databasePath;
        private readonly RegisteredModelsRepository? _registeredModelsRepository;

        private ObservableCollection<RuleViewModel> _rules;
        private ObservableCollection<RuleViewModel> _filteredRules;
        private ObservableCollection<string> _categories;
        private ObservableCollection<string> _filteredCategories;
        private ObservableCollection<RevitCommand> _commands;
        private ObservableCollection<RevitCommand> _filteredCommands;
        private ObservableCollection<RevitCategory> _apiCategories;
        private List<RegisteredModel> _registeredModels = new();
        private RuleViewModel? _selectedRule;
        private bool _isLoading;
        private string _statusMessage = "Ready";
        private bool _isEditing;
        private RuleViewModel? _editingRule;
        private bool _isDatabaseConnected = true;
        private string _databaseError = string.Empty;
        private string _searchText = "";
        private string _statusFilter = "";
        private string _modeFilter = "";
        private readonly bool _isCompanyAdmin;

        /// <summary>
        /// Current model's GUID, set by the dialog so override paths can resolve the
        /// project's GUID via AutoDetectScopeIds. Without it, project-level overrides
        /// POST with a null/empty projectId and the server rejects with 400.
        /// </summary>
        public string? CurrentModelGuid { get; set; }

        /// <summary>
        /// Current project's GUID, set by the dialog as a backup when AutoDetectScopeIds
        /// returns null (registered_models row stale or missing zemanage_project_id).
        /// </summary>
        public string? CurrentProjectIdGuid { get; set; }

        /// <summary>
        /// Resolves the current project's GUID using AutoDetectScopeIds (in-memory
        /// registered_models) first, falling back to CurrentProjectIdGuid populated by
        /// the dialog from a live API fetch.
        /// </summary>
        private string? ResolveCurrentProjectId()
        {
            var (autoProjectId, _, _, _) = AutoDetectScopeIds(CurrentModelGuid);
            if (!string.IsNullOrWhiteSpace(autoProjectId)) return autoProjectId;
            return CurrentProjectIdGuid;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        public event EventHandler? RequestClose;

        public RulesManagementViewModel(IRuleService ruleService, RuleRepository ruleRepository, ILogger? logger, string revitUsername = null, string apiBaseUrl = null, string databasePath = null, RulesSyncService? rulesSyncService = null, bool isCompanyAdmin = false, string? profileId = null)
        {
            _ruleService = ruleService ?? throw new ArgumentNullException(nameof(ruleService));
            _ruleRepository = ruleRepository ?? throw new ArgumentNullException(nameof(ruleRepository));
            _logger = logger;
            _rulesSyncService = rulesSyncService;
            _isCompanyAdmin = isCompanyAdmin;
            _revitUsername = revitUsername ?? Environment.UserName;
            _profileId = profileId;
            _apiBaseUrl = apiBaseUrl ?? "http://10.10.40.75:5000";

            // Ensure current user's profileId→displayName mapping is in cache
            if (!string.IsNullOrEmpty(_profileId))
                UserDisplayNameCache.Set(_profileId, _revitUsername);
            _databasePath = databasePath;

            _rules = new ObservableCollection<RuleViewModel>();
            _filteredRules = new ObservableCollection<RuleViewModel>();
            _categories = new ObservableCollection<string>();
            _filteredCategories = new ObservableCollection<string>();
            _commands = new ObservableCollection<RevitCommand>();
            _filteredCommands = new ObservableCollection<RevitCommand>();
            _apiCategories = new ObservableCollection<RevitCategory>();

            // Whenever the rules collection changes (load / save / delete), rebuild
            // FilteredCategories so the New Rule dropdown's used-category exclusion
            // (see FilterCategories) stays in sync. Re-applying the empty filter is
            // cheap and avoids each caller having to remember to refresh manually.
            _rules.CollectionChanged += (s, e) => FilterCategories("");

            // Initialize API service for fetching categories and commands
            try
            {
                _revitApiService = new RevitApiService(_apiBaseUrl, logger);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to initialize RevitApiService: {ex.Message}. Using defaults.");
            }

            // Initialize cache repository for category_cache and command_cache tables
            if (!string.IsNullOrEmpty(_databasePath))
            {
                try
                {
                    _cacheRepository = new CacheRepository(_databasePath, logger);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to initialize CacheRepository: {ex.Message}.");
                }

                try
                {
                    _registeredModelsRepository = new RegisteredModelsRepository(_databasePath, logger);
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to initialize RegisteredModelsRepository: {ex.Message}.");
                }
            }

            // Initialize default Revit categories
            InitializeDefaultCategories();

            // Initialize commands
            RefreshCommand = new RelayCommand(async () => await LoadRulesAsync());
            AddRuleCommand = new RelayCommand(AddNewRule);
            EditRuleCommand = new RelayCommand(EditSelectedRule, () => SelectedRule != null);
            DeleteRuleCommand = new RelayCommand(async () => await DeleteSelectedRuleAsync(), () => SelectedRule != null);
            SaveRuleCommand = new RelayCommand(async () => await SaveEditingRuleAsync(), () => IsEditing && EditingRule != null);
            CancelEditCommand = new RelayCommand(CancelEdit, () => IsEditing);
            CloseCommand = new RelayCommand(Close);
            ImportExcelCommand = new RelayCommand(async () => await ImportFromExcelAsync());
            ExportExcelCommand = new RelayCommand(async () => await ExportToExcelAsync());
            FetchFromApiCommand = new RelayCommand(async () => await FetchRulesFromApiAsync());
        }

        #region Properties

        public string CurrentUser => _revitUsername;
        public bool IsCompanyAdmin => _isCompanyAdmin;

        public ObservableCollection<RuleViewModel> Rules
        {
            get => _rules;
            set
            {
                _rules = value;
                OnPropertyChanged();
                ApplyFilters(_searchText, _statusFilter, _modeFilter);
            }
        }

        public ObservableCollection<RuleViewModel> FilteredRules
        {
            get => _filteredRules;
            set
            {
                _filteredRules = value;
                OnPropertyChanged();
            }
        }

        public void ApplyFilters(string searchText, string statusFilter, string modeFilter)
        {
            _searchText = searchText?.ToLower() ?? "";
            _statusFilter = statusFilter ?? "";
            _modeFilter = modeFilter ?? "";

            var filtered = _rules.AsEnumerable();

            // Search filter
            if (!string.IsNullOrEmpty(_searchText))
            {
                filtered = filtered.Where(r =>
                    (r.Name?.ToLower().Contains(_searchText) ?? false) ||
                    (r.Description?.ToLower().Contains(_searchText) ?? false) ||
                    (r.CategoryName?.ToLower().Contains(_searchText) ?? false) ||
                    (r.RuleId?.ToLower().Contains(_searchText) ?? false));
            }

            // Status filter
            if (!string.IsNullOrEmpty(_statusFilter))
            {
                filtered = _statusFilter switch
                {
                    "Active" => filtered.Where(r => r.IsEnabled),
                    "Disabled" => filtered.Where(r => !r.IsEnabled),
                    _ => filtered
                };
            }

            // Mode filter
            if (!string.IsNullOrEmpty(_modeFilter))
            {
                filtered = _modeFilter switch
                {
                    "Notify" => filtered.Where(r => r.Mode == ProtectionMode.Notify),
                    "Assist" => filtered.Where(r => r.Mode == ProtectionMode.Assist),
                    "Protect" => filtered.Where(r => r.Mode == ProtectionMode.Protect),
                    _ => filtered
                };
            }

            var filteredList = filtered.ToList();

            // Assign row numbers (S.No)
            for (int i = 0; i < filteredList.Count; i++)
            {
                filteredList[i].RowNumber = i + 1;
            }

            FilteredRules = new ObservableCollection<RuleViewModel>(filteredList);
        }

        public ObservableCollection<string> Categories
        {
            get => _categories;
            set
            {
                _categories = value;
                OnPropertyChanged();
                // Initialize filtered categories with all categories
                FilterCategories("");
            }
        }

        public ObservableCollection<string> FilteredCategories
        {
            get => _filteredCategories;
            set
            {
                _filteredCategories = value;
                OnPropertyChanged();
            }
        }

        public ObservableCollection<RevitCommand> Commands
        {
            get => _commands;
            set
            {
                _commands = value;
                OnPropertyChanged();
                // Initialize filtered commands with all commands
                FilterCommands("");
            }
        }

        public ObservableCollection<RevitCommand> FilteredCommands
        {
            get => _filteredCommands;
            set
            {
                _filteredCommands = value;
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// Filters categories based on search text AND excludes categories that already
        /// have a rule in the current Rules collection. Prevents duplicate-category rules
        /// from being created via the New Rule dropdown (each category may only own one
        /// rule). When editing an existing rule the dropdown is replaced with a read-only
        /// text box, so this filter does not hide the rule's own category from the edit
        /// flow — it only affects the New Rule path.
        /// </summary>
        public void FilterCategories(string searchText)
        {
            _filteredCategories.Clear();

            // Build a case-insensitive set of categories already in use so duplicates
            // are excluded from the dropdown.
            var usedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var rule in _rules)
            {
                if (!string.IsNullOrWhiteSpace(rule.CategoryName))
                    usedCategories.Add(rule.CategoryName);
            }

            IEnumerable<string> source = _categories;
            if (!string.IsNullOrWhiteSpace(searchText))
                source = source.Where(c => c.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0);

            foreach (var category in source)
            {
                if (usedCategories.Contains(category)) continue; // skip already-used categories
                _filteredCategories.Add(category);
            }
            OnPropertyChanged(nameof(FilteredCategories));
        }

        /// <summary>
        /// Filters commands based on search text
        /// </summary>
        public void FilterCommands(string searchText)
        {
            _filteredCommands.Clear();
            var filtered = string.IsNullOrWhiteSpace(searchText)
                ? _commands
                : _commands.Where(c => c.CommandName.IndexOf(searchText, StringComparison.OrdinalIgnoreCase) >= 0);
            foreach (var command in filtered)
            {
                _filteredCommands.Add(command);
            }
            OnPropertyChanged(nameof(FilteredCommands));
        }

        /// <summary>
        /// Load registered models from database for scope-based mapping
        /// </summary>
        public async Task LoadRegisteredModelsAsync()
        {
            if (_registeredModelsRepository == null) return;

            try
            {
                _registeredModels = await _registeredModelsRepository.GetActiveModelsAsync();
                _logger?.LogDebug($"Loaded {_registeredModels.Count} registered models for scope mapping");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to load registered models: {ex.Message}");
                _registeredModels = new List<RegisteredModel>();
            }
        }

        /// <summary>
        /// Get unique projects (by zemanage_project_id) from registered models
        /// Returns list of (ProjectId, DisplayName) tuples
        /// </summary>
        public List<(string ProjectId, string DisplayName)> GetUniqueProjects()
        {
            return _registeredModels
                .Where(m => !string.IsNullOrEmpty(m.ZemanageProjectId))
                .GroupBy(m => m.ZemanageProjectId)
                .Select(g =>
                {
                    var first = g.First();
                    var display = !string.IsNullOrEmpty(first.ProjectName)
                        ? $"{first.ProjectName} ({first.ZemanageProjectId})"
                        : first.ZemanageProjectId;
                    return (first.ZemanageProjectId, display);
                })
                .OrderBy(p => p.display)
                .ToList();
        }

        /// <summary>
        /// Get registered models for file-specific scope
        /// Returns list of (ModelGuid, DisplayName) tuples
        /// </summary>
        public List<(string ModelGuid, string DisplayName)> GetRegisteredModelsList()
        {
            return _registeredModels
                .Where(m => !string.IsNullOrEmpty(m.ModelGuid))
                .Select(m =>
                {
                    var display = !string.IsNullOrEmpty(m.ModelName)
                        ? $"{m.ModelName} ({m.ModelGuid.Substring(0, Math.Min(8, m.ModelGuid.Length))}...)"
                        : m.ModelGuid;
                    return (m.ModelGuid, display);
                })
                .OrderBy(m => m.display)
                .ToList();
        }

        /// <summary>
        /// Auto-detect project_id and model_guid from the current Revit model
        /// Looks up the currentModelGuid in registered_models and returns the matching data
        /// </summary>
        public (string? ProjectId, string? ProjectName, string? ModelGuid, string? ModelName) AutoDetectScopeIds(string? currentModelGuid)
        {
            if (string.IsNullOrEmpty(currentModelGuid) || _registeredModels.Count == 0)
                return (null, null, null, null);

            var match = _registeredModels.FirstOrDefault(m =>
                !string.IsNullOrEmpty(m.ModelGuid) &&
                m.ModelGuid.Equals(currentModelGuid, StringComparison.OrdinalIgnoreCase));

            if (match == null)
            {
                _logger?.LogWarning($"Auto-detect: Current model GUID '{currentModelGuid}' not found in registered_models");
                return (null, null, null, null);
            }

            _logger?.LogInfo($"Auto-detect: Found registered model '{match.ModelName}' - ProjectId={match.ZemanageProjectId}, ModelGuid={match.ModelGuid}");
            return (match.ZemanageProjectId, match.ProjectName, match.ModelGuid, match.ModelName);
        }

        public ObservableCollection<RevitCategory> ApiCategories
        {
            get => _apiCategories;
            set
            {
                _apiCategories = value;
                OnPropertyChanged();
            }
        }

        public RuleViewModel? SelectedRule
        {
            get => _selectedRule;
            set
            {
                _selectedRule = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectionCountText));
                ((RelayCommand)EditRuleCommand).RaiseCanExecuteChanged();
                ((RelayCommand)DeleteRuleCommand).RaiseCanExecuteChanged();
            }
        }

        public bool HasSelection => SelectedRule != null;

        public string SelectionCountText => SelectedRule != null ? "1 selected" : "0 selected";

        public bool IsDatabaseConnected
        {
            get => _isDatabaseConnected;
            set
            {
                _isDatabaseConnected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DatabaseStatusText));
            }
        }

        public string DatabaseError
        {
            get => _databaseError;
            set
            {
                _databaseError = value;
                OnPropertyChanged();
            }
        }

        public string DatabaseStatusText => IsDatabaseConnected ? "Database Connected" : "Database Error";

        public bool IsLoading
        {
            get => _isLoading;
            set
            {
                _isLoading = value;
                OnPropertyChanged();
            }
        }

        public string StatusMessage
        {
            get => _statusMessage;
            set
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }

        public bool IsEditing
        {
            get => _isEditing;
            set
            {
                _isEditing = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotEditing));
                ((RelayCommand)SaveRuleCommand).RaiseCanExecuteChanged();
                ((RelayCommand)CancelEditCommand).RaiseCanExecuteChanged();
            }
        }

        public bool IsNotEditing => !IsEditing;

        public RuleViewModel? EditingRule
        {
            get => _editingRule;
            set
            {
                _editingRule = value;
                OnPropertyChanged();
                ((RelayCommand)SaveRuleCommand).RaiseCanExecuteChanged();
            }
        }

        public string[] ProtectionModeOptions => new[] { "notify", "assist", "protect" };

        #endregion

        #region Commands

        public ICommand RefreshCommand { get; }
        public ICommand AddRuleCommand { get; }
        public ICommand EditRuleCommand { get; }
        public ICommand DeleteRuleCommand { get; }
        public ICommand SaveRuleCommand { get; }
        public ICommand CancelEditCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand ImportExcelCommand { get; }
        public ICommand ExportExcelCommand { get; }
        public ICommand FetchFromApiCommand { get; }

        #endregion

        #region Methods

        private void InitializeDefaultCategories()
        {
            // Categories and Commands are now loaded from API only
            // No default categories - will be populated from API
            _categories.Clear();
            _filteredCategories.Clear();
        }

        /// <summary>
        /// Loads categories: First checks cache, only fetches from API if cache is empty
        /// Data is cached once and reused - no duplicate fetching from API
        /// </summary>
        public async Task LoadCategoriesFromApiAsync()
        {
            _logger?.LogInfo($"LoadCategoriesFromApiAsync: Starting... CacheRepo={(_cacheRepository != null ? "OK" : "NULL")}, ApiService={(_revitApiService != null ? "OK" : "NULL")}");

            // Step 1: Check if cache already has data
            if (_cacheRepository != null)
            {
                try
                {
                    var cacheCount = await _cacheRepository.GetCategoryCacheCountAsync();
                    _logger?.LogInfo($"LoadCategoriesFromApiAsync: Cache count = {cacheCount}");

                    if (cacheCount > 0)
                    {
                        // Cache has data - use it directly (no API call needed)
                        StatusMessage = "Loading categories from cache...";
                        var cachedCategories = await _cacheRepository.GetAllCategoriesFromCacheAsync();

                        _apiCategories.Clear();
                        _categories.Clear();

                        foreach (var (categoryName, categoryCode) in cachedCategories.OrderBy(c => c.CategoryName))
                        {
                            _apiCategories.Add(new RevitCategory
                            {
                                CategoryName = categoryName,
                                CategoryCode = categoryCode
                            });
                            _categories.Add(categoryName);
                        }

                        FilterCategories("");
                        _logger?.LogInfo($"Loaded {cachedCategories.Count} categories from cache (data already cached)");
                        OnPropertyChanged(nameof(Categories));
                        OnPropertyChanged(nameof(ApiCategories));
                        return; // Data loaded from cache, done
                    }
                    else
                    {
                        _logger?.LogInfo("LoadCategoriesFromApiAsync: Cache is empty, will fetch from API");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Error checking category cache: {ex.Message}");
                }
            }
            else
            {
                _logger?.LogWarning("LoadCategoriesFromApiAsync: CacheRepository is NULL - cannot check/save cache");
            }

            // Step 2: Cache is empty - fetch from API and cache it (ONE TIME ONLY)
            if (_revitApiService != null)
            {
                try
                {
                    _logger?.LogInfo("LoadCategoriesFromApiAsync: Fetching from API...");
                    StatusMessage = "Loading categories from API (first time)...";
                    var apiCategories = await _revitApiService.GetCategoriesAsync();
                    _logger?.LogInfo($"LoadCategoriesFromApiAsync: API returned {apiCategories.Count} categories");

                    if (apiCategories.Count > 0)
                    {
                        _apiCategories.Clear();
                        _categories.Clear();

                        foreach (var cat in apiCategories.OrderBy(c => c.CategoryName))
                        {
                            _apiCategories.Add(cat);
                            _categories.Add(cat.CategoryName);
                        }

                        FilterCategories("");
                        _logger?.LogInfo($"Loaded {apiCategories.Count} categories from API");
                        OnPropertyChanged(nameof(Categories));
                        OnPropertyChanged(nameof(ApiCategories));

                        // Cache to SQLite for future use (INSERT OR IGNORE skips if already exists)
                        if (_cacheRepository != null)
                        {
                            try
                            {
                                var categoryList = apiCategories
                                    .Where(c => !string.IsNullOrEmpty(c.CategoryName))
                                    .Select(c => (c.CategoryName, c.CategoryCode))
                                    .ToList();
                                _logger?.LogInfo($"LoadCategoriesFromApiAsync: Caching {categoryList.Count} categories to database...");
                                var cachedCount = await _cacheRepository.InsertCategoryCacheBatchAsync(categoryList);
                                _logger?.LogInfo($"Cached {cachedCount} categories to database (one-time cache)");
                            }
                            catch (Exception cacheEx)
                            {
                                _logger?.LogError($"Failed to cache categories: {cacheEx.Message}", cacheEx);
                            }
                        }
                        else
                        {
                            _logger?.LogWarning("LoadCategoriesFromApiAsync: CacheRepository is NULL - cannot save to cache");
                        }
                        return; // Data loaded from API and cached, done
                    }
                    else
                    {
                        _logger?.LogWarning("LoadCategoriesFromApiAsync: API returned 0 categories");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"API unavailable for categories: {ex.Message}", ex);
                }
            }
            else
            {
                _logger?.LogWarning("LoadCategoriesFromApiAsync: RevitApiService is NULL - cannot fetch from API");
            }

            // Step 3: Both cache and API unavailable
            _logger?.LogWarning("No categories available - cache is empty and API is unavailable");
        }

        /// <summary>
        /// Loads commands: First checks cache, only fetches from API if cache is empty.
        /// Only shows commands where canWorkWithSelection = true (filtered for Rules Management).
        /// Data is cached once and reused - no duplicate fetching from API.
        /// </summary>
        /// <summary>
        /// Tells each loaded rule to re-raise its CommandNameTooltip property so the UI
        /// refreshes once descriptions are populated into the static cache.
        /// </summary>
        private void RefreshRuleTooltips()
        {
            try
            {
                foreach (var rule in _rules) rule.RaiseCommandTooltipChanged();
                foreach (var rule in _filteredRules) rule.RaiseCommandTooltipChanged();
            }
            catch { /* best-effort UI refresh */ }
        }

        /// <summary>
        /// Fetches descriptions via the list endpoint (fast, one call) + detail endpoint for leftovers.
        /// The list endpoint sometimes returns null descriptions for specific commands; detail fills those.
        /// </summary>
        public async Task FetchMissingCommandDescriptionsAsync()
        {
            if (_revitApiService == null) return;
            try
            {
                // Step 1: Bulk refresh via the list endpoint
                var all = await _revitApiService.GetCommandsAsync();
                if (all != null && all.Count > 0)
                {
                    RuleCommandDescriptionCache.Populate(all);
                    if (_cacheRepository != null)
                    {
                        var list = all
                            .Where(c => !string.IsNullOrEmpty(c.CommandName))
                            .Select(c => (c.CommandName, c.MemberName, c.Description, c.CanHaveBinding, c.NeedBinding, c.CanWorkWithSelection, c.Code))
                            .ToList();
                        await _cacheRepository.UpsertCommandCacheBatchAsync(list);
                        _logger?.LogInfo($"Rules: Refreshed command_cache with {list.Count} commands");
                    }
                    RefreshRuleTooltips();
                }

                // Step 2: For command names in rules STILL without descriptions, hit the detail endpoint
                var neededNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rule in _rules)
                {
                    var names = rule.CommandName?.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    if (names == null) continue;
                    foreach (var n in names)
                    {
                        var t = n.Trim();
                        if (string.IsNullOrEmpty(t)) continue;
                        if (string.IsNullOrWhiteSpace(RuleCommandDescriptionCache.Get(t)))
                            neededNames.Add(t);
                    }
                }

                foreach (var name in neededNames)
                {
                    try
                    {
                        var d = await _revitApiService.GetCommandDetailsAsync(name);
                        if (d != null && !string.IsNullOrWhiteSpace(d.Description))
                        {
                            RuleCommandDescriptionCache.Populate(new[]
                            {
                                (d.CommandName, (string?)d.MemberName, (string?)d.Description)
                            });
                        }
                    }
                    catch { /* skip */ }
                }

                RefreshRuleTooltips();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"FetchMissingCommandDescriptionsAsync: {ex.Message}");
            }
        }

        public async Task LoadCommandsFromApiAsync()
        {
            _logger?.LogInfo($"LoadCommandsFromApiAsync: Starting... CacheRepo={(_cacheRepository != null ? "OK" : "NULL")}, ApiService={(_revitApiService != null ? "OK" : "NULL")}");

            // Step 1: Always fetch from server first
            if (_revitApiService != null)
            {
                try
                {
                    _logger?.LogInfo("LoadCommandsFromApiAsync: Fetching from server...");
                    StatusMessage = "Loading commands from server...";
                    var commands = await _revitApiService.GetCommandsAsync();
                    _logger?.LogInfo($"LoadCommandsFromApiAsync: Server returned {commands.Count} commands");

                    if (commands.Count > 0)
                    {
                        // Only show commands where canWorkWithSelection = true in UI
                        _commands.Clear();
                        foreach (var cmd in commands.Where(c => c.CanWorkWithSelection).OrderBy(c => c.CommandName))
                        {
                            _commands.Add(cmd);
                        }
                        RuleCommandDescriptionCache.Populate(commands);
                        RefreshRuleTooltips();

                        FilterCommands("");
                        _logger?.LogInfo($"Loaded {_commands.Count} commands with selection capability from server (total: {commands.Count})");
                        OnPropertyChanged(nameof(Commands));

                        // Update local cache with latest server data
                        if (_cacheRepository != null)
                        {
                            try
                            {
                                var commandList = commands
                                    .Where(c => !string.IsNullOrEmpty(c.CommandName))
                                    .Select(c => (c.CommandName, c.MemberName, c.Description, c.CanHaveBinding, c.NeedBinding, c.CanWorkWithSelection, c.Code))
                                    .ToList();
                                _logger?.LogInfo($"LoadCommandsFromApiAsync: Updating cache with {commandList.Count} commands...");
                                var cachedCount = await _cacheRepository.UpsertCommandCacheBatchAsync(commandList);
                                _logger?.LogInfo($"Cache updated with {cachedCount} commands from server");
                            }
                            catch (Exception cacheEx)
                            {
                                _logger?.LogWarning($"Failed to update command cache: {cacheEx.Message}");
                            }
                        }
                        return;
                    }
                    else
                    {
                        _logger?.LogWarning("LoadCommandsFromApiAsync: Server returned 0 commands, falling back to cache");
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"LoadCommandsFromApiAsync: Server unavailable ({ex.Message}), falling back to cache");
                }
            }
            else
            {
                _logger?.LogWarning("LoadCommandsFromApiAsync: RevitApiService is NULL, falling back to cache");
            }

            // Step 2: Server unavailable or returned 0 — fall back to local cache
            if (_cacheRepository != null)
            {
                try
                {
                    var cachedCommands = await _cacheRepository.GetCommandsWithSelectionAsync();
                    _logger?.LogInfo($"LoadCommandsFromApiAsync: Cache fallback returned {cachedCommands.Count} commands");

                    if (cachedCommands.Count > 0)
                    {
                        StatusMessage = "Loading commands from cache (server unavailable)...";
                        _commands.Clear();
                        foreach (var (commandName, memberName, description) in cachedCommands.OrderBy(c => c.CommandName))
                        {
                            _commands.Add(new RevitCommand
                            {
                                CommandName = commandName,
                                MemberName = memberName,
                                Description = description
                            });
                        }
                        RuleCommandDescriptionCache.Populate(cachedCommands);
                        RefreshRuleTooltips();

                        FilterCommands("");
                        _logger?.LogInfo($"Loaded {cachedCommands.Count} commands from cache (server was unavailable)");
                        OnPropertyChanged(nameof(Commands));
                        return;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Error loading from cache: {ex.Message}");
                }
            }

            // Step 3: Both server and cache unavailable
            _logger?.LogWarning("No commands available — server is unreachable and cache is empty");
        }

        /// <summary>
        /// Gets category info (including CategoryCode) for a given category name
        /// </summary>
        public RevitCategory? GetCategoryByName(string categoryName)
        {
            return _apiCategories.FirstOrDefault(c =>
                c.CategoryName.Equals(categoryName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Fetches category codes from API and inserts into category_cache table
        /// Called when user selects categories in the dropdown
        /// </summary>
        public async Task FetchAndCacheCategoryCodesAsync(IEnumerable<string> categoryNames)
        {
            if (_revitApiService == null || _cacheRepository == null)
            {
                _logger?.LogWarning("Cannot cache category codes: API service or cache repository not initialized");
                return;
            }

            try
            {
                var namesToFetch = categoryNames.ToList();
                if (namesToFetch.Count == 0) return;

                _logger?.LogInfo($"Fetching category codes for: {string.Join(", ", namesToFetch)}");

                // Fetch category codes from API
                var categoryCodes = await _revitApiService.GetCategoryCodesBatchAsync(namesToFetch);

                if (categoryCodes.Count > 0)
                {
                    // Insert into category_cache table (convert Dictionary to List for duplicates support)
                    var categoryList = categoryCodes.Select(kvp => (kvp.Key, (string?)kvp.Value)).ToList();
                    var insertedCount = await _cacheRepository.InsertCategoryCacheBatchAsync(categoryList);
                    _logger?.LogInfo($"Cached {insertedCount} category codes");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error fetching/caching category codes: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Fetches command details from API and inserts into command_cache table
        /// Called when user selects commands in the dropdown
        /// </summary>
        public async Task FetchAndCacheCommandDetailsAsync(IEnumerable<string> commandNames)
        {
            if (_revitApiService == null || _cacheRepository == null)
            {
                _logger?.LogWarning("Cannot cache command details: API service or cache repository not initialized");
                return;
            }

            try
            {
                var namesToFetch = commandNames.ToList();
                if (namesToFetch.Count == 0) return;

                _logger?.LogInfo($"Fetching command details for: {string.Join(", ", namesToFetch)}");

                foreach (var commandName in namesToFetch)
                {
                    // Fetch command details from API
                    var commandDetails = await _revitApiService.GetCommandDetailsAsync(commandName);

                    if (commandDetails != null)
                    {
                        // Insert into command_cache table
                        await _cacheRepository.UpsertCommandCacheAsync(
                            commandDetails.CommandName,
                            commandDetails.MemberName,
                            commandDetails.Description);
                    }
                }

                _logger?.LogInfo($"Cached command details for {namesToFetch.Count} commands");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error fetching/caching command details: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Returns the first value in (modifiedBy, createdBy, signed-in-user) that's
        /// an actual person's name. Anything that looks like an internal identifier
        /// — "System", a raw GUID, the "User &lt;hex&gt;" placeholder emitted by
        /// UserDisplayNameCache for unresolved GUIDs, or empty — is skipped over.
        /// Per user spec: "only name show, don't show System or any id".
        /// </summary>
        private string ResolveDisplayName(string? primary, string? fallback)
        {
            if (IsRealName(primary))   return primary!;
            if (IsRealName(fallback))  return fallback!;

            // Last resort: the signed-in user. Try the cached display first
            // (profileId → "Zestine Techno"), then fall back to the raw Revit
            // username (which is a Windows account name, not a GUID, so it's
            // still a "name").
            var currentDisplay = !string.IsNullOrWhiteSpace(_profileId)
                ? UserDisplayNameCache.GetDisplayName(_profileId)
                : null;
            if (IsRealName(currentDisplay)) return currentDisplay!;

            if (!string.IsNullOrWhiteSpace(_revitUsername)
                && !string.Equals(_revitUsername, "System", StringComparison.OrdinalIgnoreCase))
                return _revitUsername;

            return string.Empty;
        }

        /// <summary>
        /// True when <paramref name="value"/> is a real person's display name —
        /// not empty, not "System", not a GUID, not the "User &lt;hex&gt;"
        /// placeholder. Used to filter values rendered in Modified By / Created By.
        /// </summary>
        private static bool IsRealName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var v = value.Trim();
            if (string.Equals(v, "System", StringComparison.OrdinalIgnoreCase)) return false;

            // GUID-shaped (with or without hyphens) → never rendered.
            if (v.Length == 36 && v[8] == '-' && v[13] == '-' && v[18] == '-' && v[23] == '-')
                return false;
            if (v.Length == 32 && IsHex(v)) return false;

            // The "User <8hex>" placeholder UserDisplayNameCache emits for
            // unresolved GUIDs — still not a real name.
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

        public async Task LoadRulesAsync()
        {
            try
            {
                IsLoading = true;
                StatusMessage = "Loading rules from database...";
                _logger?.LogInfo("LoadRulesAsync: loading rules from DB...");

                var rules = await _ruleRepository.GetAllRulesAsync();
                _logger?.LogInfo($"LoadRulesAsync: DB returned {rules.Count} rule(s) — UI about to bind them");

                Rules.Clear();
                foreach (var rule in rules)
                {
                    // Convert profileIds → displayNames using cache
                    if (!string.IsNullOrWhiteSpace(rule.CreatedBy))
                    {
                        rule.CreatedBy = UserDisplayNameCache.GetDisplayName(rule.CreatedBy);
                    }
                    if (!string.IsNullOrWhiteSpace(rule.ModifiedBy))
                    {
                        rule.ModifiedBy = UserDisplayNameCache.GetDisplayName(rule.ModifiedBy);
                    }

                    // Per user spec: "only name show, don't show System or any id".
                    // ResolveDisplayName picks the first value in the priority chain
                    // that is an actual person's name — never "System", never a raw
                    // GUID, never the "User <hex>" placeholder UserDisplayNameCache
                    // emits for unresolved GUIDs. Final fallback is the signed-in
                    // user's name so the column always renders something meaningful.
                    rule.ModifiedBy = ResolveDisplayName(rule.ModifiedBy, rule.CreatedBy);
                    rule.CreatedBy  = ResolveDisplayName(rule.CreatedBy, null);
                    Rules.Add(new RuleViewModel(rule));
                }

                // Update categories from loaded rules
                UpdateCategoriesFromRules(rules);

                // Notify UI that Rules collection has changed
                OnPropertyChanged(nameof(Rules));

                // Update filtered rules
                ApplyFilters(_searchText, _statusFilter, _modeFilter);

                // Update database connection status
                IsDatabaseConnected = true;
                DatabaseError = string.Empty;
                StatusMessage = $"✓ Ready";
                _logger?.LogInfo($"Loaded {Rules.Count} rules from database");

                // Refresh tooltips and fetch missing descriptions in the background
                RefreshRuleTooltips();
                _ = FetchMissingCommandDescriptionsAsync();

                // Show message if no rules found
                if (Rules.Count == 0)
                {
                    StatusMessage = "⚠ No rules found in database";
                    _logger?.LogWarning("No rules found in database - database may be empty");
                }
            }
            catch (Exception ex)
            {
                IsDatabaseConnected = false;
                DatabaseError = ex.Message;
                StatusMessage = $"❌ Database Error";
                Rules.Clear();
                _logger?.LogError($"Error loading rules: {ex.Message}", ex);

                // Show detailed error to user
                System.Windows.MessageBox.Show(
                    $"Failed to load rules from database:\n\n{ex.Message}\n\nPlease check the database file exists and is accessible.",
                    "Database Connection Error",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Fetches rules from the backend API via GET, saves them to local database, and refreshes UI.
        /// When modelGuid is provided, uses GET /api/v1/Revit/rule-protections/by-model/{modelGuid}.
        /// Otherwise falls back to legacy GET /api/v1/Revit/command-rules.
        /// </summary>
        public async Task FetchRulesFromApiAsync(string? modelGuid = null)
        {
            if (_rulesSyncService == null)
            {
                _logger?.LogWarning("FetchRulesFromApiAsync: RulesSyncService is not initialized");
                // Still load local rules
                await LoadRulesAsync();
                StatusMessage = "API sync service not available";
                return;
            }

            try
            {
                IsLoading = true;
                StatusMessage = "Fetching rules from server...";

                List<Rule> apiRules;

                if (!string.IsNullOrEmpty(modelGuid))
                {
                    _logger?.LogInfo($"Fetching rule protections by model: {modelGuid}");
                    apiRules = await _rulesSyncService.FetchRuleProtectionsByModelAsync(modelGuid);
                }
                else
                {
                    _logger?.LogInfo("Fetching rules from API (no model GUID, using legacy endpoint)...");
                    apiRules = await _rulesSyncService.FetchAllRulesFromApiAsync();
                }

                // An empty API response is valid — it means the admin deleted all rules
                // on the web side. The fetch above has already reconciled local SQLite
                // (deleted orphans), so we MUST still refresh the in-memory cache and
                // reload the UI; otherwise stale rules linger until next restart.
                if (apiRules.Count == 0)
                {
                    _logger?.LogInfo("No rules returned from API — refreshing cache + UI to reflect deletions");
                }

                // Refresh the rule cache (local DB already updated/reconciled by fetch)
                await _ruleService.RefreshRules();

                // Reload UI from database
                await LoadRulesAsync();

                StatusMessage = apiRules.Count == 0
                    ? "No rules found on server"
                    : $"Fetched {apiRules.Count} rules from server";
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error fetching rules from API: {ex.Message}", ex);
                // Fallback: load local rules
                await LoadRulesAsync();
                StatusMessage = $"Error fetching from server: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        /// <summary>
        /// Public method to save a rule directly (called from UI events like toggle switches)
        /// Uses PUT API for updates, POST for new rules.
        /// Project admin editing a company-wide rule → POST new override via rule-protections endpoint.
        /// </summary>
        public async Task<bool> SaveRuleDirectAsync(Rule rule, bool isNewRule = false)
        {
            try
            {
                var profileId = _profileId ?? _revitUsername;
                var displayName = UserDisplayNameCache.GetDisplayName(profileId);

                // Store display name in database for UI
                rule.ModifiedBy = displayName;
                rule.ModifiedAt = DateTime.UtcNow;

                // Project admin override: editing a company-wide rule must produce a project-level row
                // (server rejects PUT/PATCH on company-wide rules from non-CompanyAdmins).
                // If an override for the same rule already exists, update it instead of creating another one.
                var isProjectAdminOverride = !_isCompanyAdmin
                    && !isNewRule
                    && rule.RuleScope == (int)RuleScopeType.CompanyWide;

                bool isOverrideUpdate = false;
                if (isProjectAdminOverride)
                {
                    var autoProjectId = ResolveCurrentProjectId();
                    var existingOverride = FindExistingProjectOverride(rule);
                    if (existingOverride != null)
                    {
                        // Reuse the existing project-level override row
                        rule.RuleId = existingOverride.RuleId;
                        rule.RuleScope = (int)RuleScopeType.ProjectWide;
                        rule.ProjectId = !string.IsNullOrWhiteSpace(existingOverride.ProjectId) ? existingOverride.ProjectId : autoProjectId;
                        rule.ModelGuid = null;
                        rule.CreatedAt = existingOverride.CreatedAt;
                        rule.CreatedBy = existingOverride.CreatedBy;
                        isOverrideUpdate = true;
                        _logger?.LogInfo($"Project admin override: updating existing project-level override {rule.RuleId} for '{rule.Name}'");
                    }
                    else
                    {
                        rule.RuleId = Guid.NewGuid().ToString();
                        rule.RuleScope = (int)RuleScopeType.ProjectWide;
                        rule.ProjectId = autoProjectId;
                        rule.ModelGuid = null;
                        rule.CreatedAt = DateTime.UtcNow;
                        rule.CreatedBy = displayName;
                        isNewRule = true;
                        _logger?.LogInfo($"Project admin override: creating new project-level rule from company-wide rule (projectId={autoProjectId ?? "<null>"})");
                    }
                }

                var preCount = (await _ruleRepository.GetAllRulesAsync()).Count;
                _logger?.LogInfo($"SaveRuleDirectAsync: BEFORE save — local DB has {preCount} rule(s), saving '{rule.Name}' (id={rule.RuleId}, scope={rule.RuleScope}, companyId={rule.CompanyId ?? "<null>"})");
                var success = await _ruleRepository.SaveRuleAsync(rule);
                if (success)
                {
                    var postCount = (await _ruleRepository.GetAllRulesAsync()).Count;
                    _logger?.LogInfo($"SaveRuleDirectAsync: AFTER save — local DB has {postCount} rule(s) (delta={postCount - preCount})");
                    // Refresh the cache
                    await _ruleService.RefreshRules();
                    _logger?.LogInfo($"Rule saved to database: {rule.Name}");

                    // Sync to backend API with profileId
                    if (_rulesSyncService != null)
                    {
                        rule.ModifiedBy = profileId; // Temporarily set profileId for API
                        if (isProjectAdminOverride && isOverrideUpdate)
                        {
                            // Existing project-level override → PUT update (do NOT create another row)
                            var updateResult = await _rulesSyncService.UpdateRuleAsync(rule);
                            _logger?.LogInfo($"Rule protection override PUT result: {(updateResult ? "synced/queued" : "failed")}");
                        }
                        else if (isProjectAdminOverride)
                        {
                            // POST to rule-protections endpoint for project admin override (first time)
                            var overrideResult = await _rulesSyncService.PostRuleProtectionOverrideAsync(rule);
                            _logger?.LogInfo($"Rule protection override POST result: {(overrideResult ? "synced/queued" : "failed")}");
                        }
                        else if (isNewRule)
                        {
                            var syncResult = await _rulesSyncService.SyncRuleAsync(rule);
                            _logger?.LogInfo($"Rule POST sync result: {(syncResult ? "synced/queued" : "failed")}");
                        }
                        else
                        {
                            var updateResult = await _rulesSyncService.UpdateRuleAsync(rule);
                            _logger?.LogInfo($"Rule PUT update result: {(updateResult ? "synced/queued" : "failed")}");
                        }
                        rule.ModifiedBy = displayName; // Restore display name
                    }
                }
                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error saving rule: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Looks for an existing project-level override that mirrors the given company-wide rule
        /// (matched by Name + Category) so toggles/edits update it instead of creating duplicates.
        /// </summary>
        private Rule? FindExistingProjectOverride(Rule companyRule)
        {
            if (companyRule == null) return null;
            var name = companyRule.Name?.Trim();
            if (string.IsNullOrEmpty(name)) return null;

            foreach (var vm in _rules)
            {
                var existing = vm.ToRule();
                if (existing.RuleScope != (int)RuleScopeType.ProjectWide) continue;
                if (string.Equals(existing.RuleId, companyRule.RuleId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.Equals(existing.Name?.Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;

                var sameCategory =
                    (existing.CategoryId.HasValue && companyRule.CategoryId.HasValue && existing.CategoryId == companyRule.CategoryId)
                    || string.Equals(existing.CategoryName?.Trim(), companyRule.CategoryName?.Trim(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(existing.CategoryCode?.Trim(), companyRule.CategoryCode?.Trim(), StringComparison.OrdinalIgnoreCase);
                if (!sameCategory) continue;

                return existing;
            }
            return null;
        }

        /// <summary>
        /// Save rule priorities after drag-drop reordering
        /// Updates the priority of all rules based on their position in the collection
        /// </summary>
        public async Task SaveRulePrioritiesAsync()
        {
            try
            {
                StatusMessage = "Saving rule order...";
                _logger?.LogInfo("Saving rule priorities after reorder...");

                // Calculate priorities based on position (higher position = higher priority)
                var rulesCount = _rules.Count;
                var rulesToSync = new List<Rule>();
                for (int i = 0; i < rulesCount; i++)
                {
                    var ruleVm = _rules[i];
                    var newPriority = (rulesCount - i) * 10; // 10, 20, 30, etc. from top to bottom

                    if (ruleVm.Priority != newPriority)
                    {
                        ruleVm.Priority = newPriority;
                        var rule = ruleVm.ToRule();
                        rule.ModifiedAt = DateTime.UtcNow;
                        rule.ModifiedBy = _profileId ?? _revitUsername; // Store profileId in database
                        await _ruleRepository.SaveRuleAsync(rule);
                        rulesToSync.Add(rule);
                    }
                }

                // Refresh the cache
                await _ruleService.RefreshRules();

                // Update rules via PUT API
                if (_rulesSyncService != null && rulesToSync.Count > 0)
                {
                    foreach (var rule in rulesToSync)
                    {
                        await _rulesSyncService.UpdateRuleAsync(rule);
                    }
                    _logger?.LogInfo($"Updated {rulesToSync.Count} rules via PUT after priority change");
                }

                StatusMessage = "Rule order saved";
                _logger?.LogInfo("Rule priorities saved successfully");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error saving order: {ex.Message}";
                _logger?.LogError($"Error saving rule priorities: {ex.Message}", ex);
            }
        }

        private void UpdateCategoriesFromRules(List<Rule> rules)
        {
            var ruleCategories = rules
                .Where(r => !string.IsNullOrWhiteSpace(r.CategoryName))
                .Select(r => r.CategoryName!)
                .Distinct()
                .OrderBy(c => c);

            foreach (var cat in ruleCategories)
            {
                if (!Categories.Contains(cat))
                {
                    Categories.Add(cat);
                }
            }
        }

        private void AddNewRule()
        {
            _logger?.LogInfo("Creating new rule...");

            // Calculate next priority (count + 1 for sequential 1, 2, 3...)
            var nextPriority = Rules.Count + 1;

            var newRule = new Rule
            {
                RuleId = Guid.NewGuid().ToString(),
                Name = "New Rule",
                Description = "Enter rule description",
                Mode = ProtectionMode.Notify,
                Priority = nextPriority,
                IsEnabled = true,
                CreatedAt = DateTime.UtcNow,
                ModifiedAt = DateTime.UtcNow,
                CreatedBy = _revitUsername
            };

            EditingRule = new RuleViewModel(newRule) { IsNew = true };
            IsEditing = true;
            StatusMessage = "Creating new rule...";
        }

        private void EditSelectedRule()
        {
            if (SelectedRule == null) return;

            _logger?.LogInfo($"Editing rule: {SelectedRule.Name}");

            // Create a copy for editing
            var ruleCopy = SelectedRule.ToRule().Clone();
            EditingRule = new RuleViewModel(ruleCopy) { IsNew = false };
            IsEditing = true;
            StatusMessage = $"Editing rule: {SelectedRule.Name}";
        }

        private async Task SaveEditingRuleAsync()
        {
            if (EditingRule == null) return;

            try
            {
                IsLoading = true;
                StatusMessage = "Saving rule...";

                var rule = EditingRule.ToRule();
                rule.ModifiedAt = DateTime.UtcNow;
                var profileId = _profileId ?? _revitUsername;
                var displayName = UserDisplayNameCache.GetDisplayName(profileId);

                // Store display name in database for UI
                rule.ModifiedBy = displayName;

                // Project admin editing a company-wide rule → reuse or create a project-level override
                // (server rejects PUT/PATCH on company-wide rules from non-CompanyAdmins).
                var isProjectAdminOverride = !_isCompanyAdmin
                    && !EditingRule.IsNew
                    && rule.RuleScope == (int)RuleScopeType.CompanyWide;

                bool isOverrideUpdate = false;
                if (isProjectAdminOverride)
                {
                    var autoProjectId = ResolveCurrentProjectId();
                    var existingOverride = FindExistingProjectOverride(rule);
                    if (existingOverride != null)
                    {
                        rule.RuleId = existingOverride.RuleId;
                        rule.RuleScope = (int)RuleScopeType.ProjectWide;
                        rule.ProjectId = !string.IsNullOrWhiteSpace(existingOverride.ProjectId) ? existingOverride.ProjectId : autoProjectId;
                        rule.ModelGuid = null;
                        rule.CreatedAt = existingOverride.CreatedAt;
                        rule.CreatedBy = existingOverride.CreatedBy;
                        isOverrideUpdate = true;
                        _logger?.LogInfo($"Project admin override (dialog): updating existing project-level override {rule.RuleId} for '{rule.Name}'");
                    }
                    else
                    {
                        rule.RuleId = Guid.NewGuid().ToString();
                        rule.RuleScope = (int)RuleScopeType.ProjectWide;
                        rule.ProjectId = autoProjectId;
                        rule.ModelGuid = null;
                        rule.CreatedAt = DateTime.UtcNow;
                        rule.CreatedBy = displayName;
                        _logger?.LogInfo($"Project admin override (dialog): creating new project-level rule from company-wide rule '{rule.Name}' (projectId={autoProjectId ?? "<null>"})");
                    }
                }

                var preCount2 = (await _ruleRepository.GetAllRulesAsync()).Count;
                _logger?.LogInfo($"SaveEditingRuleAsync: BEFORE save — local DB has {preCount2} rule(s), saving '{rule.Name}' (id={rule.RuleId}, scope={rule.RuleScope}, companyId={rule.CompanyId ?? "<null>"}, isNew={EditingRule.IsNew})");
                var success = await _ruleRepository.SaveRuleAsync(rule);

                if (success)
                {
                    var postCount2 = (await _ruleRepository.GetAllRulesAsync()).Count;
                    _logger?.LogInfo($"SaveEditingRuleAsync: AFTER save — local DB has {postCount2} rule(s) (delta={postCount2 - preCount2})");
                    _logger?.LogInfo($"Rule saved: {rule.Name}");

                    // Sync to backend API with profileId
                    if (_rulesSyncService != null)
                    {
                        rule.ModifiedBy = profileId; // Temporarily set profileId for API
                        if (isProjectAdminOverride && isOverrideUpdate)
                        {
                            var updateResult = await _rulesSyncService.UpdateRuleAsync(rule);
                            _logger?.LogInfo($"Rule protection override PUT result: {(updateResult ? "synced/queued" : "failed")}");
                        }
                        else if (isProjectAdminOverride)
                        {
                            rule.CreatedBy = profileId;
                            var overrideResult = await _rulesSyncService.PostRuleProtectionOverrideAsync(rule);
                            _logger?.LogInfo($"Rule protection override POST result: {(overrideResult ? "synced/queued" : "failed")}");
                        }
                        else if (EditingRule.IsNew)
                        {
                            rule.CreatedBy = profileId;
                            var syncResult = await _rulesSyncService.SyncRuleAsync(rule);
                            _logger?.LogInfo($"Rule POST sync result: {(syncResult ? "synced/queued" : "failed")}");
                        }
                        else
                        {
                            var updateResult = await _rulesSyncService.UpdateRuleAsync(rule);
                            _logger?.LogInfo($"Rule PUT update result: {(updateResult ? "success" : "queued/failed")}");
                        }
                        rule.ModifiedBy = displayName; // Restore display name
                    }

                    // Refresh the cache
                    await _ruleService.RefreshRules();

                    // Reload the list
                    await LoadRulesAsync();

                    IsEditing = false;
                    EditingRule = null;
                    StatusMessage = $"Rule '{rule.Name}' saved successfully";
                }
                else
                {
                    StatusMessage = "Failed to save rule";
                    _logger?.LogError("Failed to save rule to database");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error saving rule: {ex.Message}";
                _logger?.LogError($"Error saving rule: {ex.Message}", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task DeleteSelectedRuleAsync()
        {
            if (SelectedRule == null) return;

            var ruleName = SelectedRule.Name;
            var ruleId = SelectedRule.RuleId;
            var rule = SelectedRule.ToRule();

            try
            {
                IsLoading = true;

                // Project admin deleting a company-wide rule → reuse or create a disabled project-level override
                // (server rejects DELETE on company-wide rows from non-CompanyAdmins).
                if (!_isCompanyAdmin && rule.RuleScope == (int)RuleScopeType.CompanyWide)
                {
                    var profileId = _profileId ?? _revitUsername;
                    var displayName = UserDisplayNameCache.GetDisplayName(profileId);

                    var autoProjectId = ResolveCurrentProjectId();
                    var existingOverride = FindExistingProjectOverride(rule);
                    Rule overrideRule;
                    bool isOverrideUpdate;
                    if (existingOverride != null)
                    {
                        overrideRule = existingOverride.Clone();
                        overrideRule.IsEnabled = false;
                        overrideRule.ModifiedAt = DateTime.UtcNow;
                        overrideRule.ModifiedBy = displayName;
                        if (string.IsNullOrWhiteSpace(overrideRule.ProjectId)) overrideRule.ProjectId = autoProjectId;
                        isOverrideUpdate = true;
                        StatusMessage = $"Disabling override for '{ruleName}'...";
                        _logger?.LogInfo($"Project admin override delete: disabling existing project-level override {overrideRule.RuleId} for '{ruleName}'");
                    }
                    else
                    {
                        overrideRule = rule.Clone();
                        overrideRule.RuleId = Guid.NewGuid().ToString();
                        overrideRule.RuleScope = (int)RuleScopeType.ProjectWide;
                        overrideRule.ProjectId = autoProjectId;
                        overrideRule.ModelGuid = null;
                        overrideRule.IsEnabled = false;
                        overrideRule.CreatedAt = DateTime.UtcNow;
                        overrideRule.ModifiedAt = DateTime.UtcNow;
                        overrideRule.CreatedBy = displayName;
                        overrideRule.ModifiedBy = displayName;
                        isOverrideUpdate = false;
                        StatusMessage = $"Creating override for '{ruleName}'...";
                        _logger?.LogInfo($"Project admin override delete: creating new disabled project-level rule from company-wide rule {ruleId} (projectId={autoProjectId ?? "<null>"})");
                    }

                    var saved = await _ruleRepository.SaveRuleAsync(overrideRule);
                    if (saved)
                    {
                        await _ruleService.RefreshRules();

                        if (_rulesSyncService != null)
                        {
                            overrideRule.ModifiedBy = profileId;
                            if (isOverrideUpdate)
                                await _rulesSyncService.UpdateRuleAsync(overrideRule);
                            else
                                await _rulesSyncService.PostRuleProtectionOverrideAsync(overrideRule);
                        }

                        await LoadRulesAsync();
                        StatusMessage = isOverrideUpdate
                            ? $"Override disabled for '{ruleName}'"
                            : $"Override created for '{ruleName}'";
                    }
                    return;
                }

                StatusMessage = $"Deleting rule '{ruleName}'...";
                _logger?.LogInfo($"Deleting rule: {ruleName} ({ruleId})");

                var success = await _ruleRepository.DeleteRuleAsync(ruleId);

                if (success)
                {
                    // Refresh the cache
                    await _ruleService.RefreshRules();

                    // Delete from backend API
                    if (_rulesSyncService != null)
                    {
                        var deleteResult = await _rulesSyncService.DeleteRuleAsync(ruleId);
                        _logger?.LogInfo($"Rule API DELETE result: {(deleteResult ? "success" : "queued/failed")}");
                    }

                    // Remove from local collection and reload to refresh row numbers
                    Rules.Remove(SelectedRule);
                    SelectedRule = null;
                    await LoadRulesAsync();

                    StatusMessage = $"Rule '{ruleName}' deleted";
                    _logger?.LogInfo($"Rule deleted: {ruleName}");
                }
                else
                {
                    StatusMessage = $"Failed to delete rule '{ruleName}'";
                    _logger?.LogError($"Failed to delete rule: {ruleName}");
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error deleting rule: {ex.Message}";
                _logger?.LogError($"Error deleting rule: {ex.Message}", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ImportFromExcelAsync()
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "ZE Files (*.ze)|*.ze|All Files (*.*)|*.*",
                    Title = "Import Rules"
                };

                if (openFileDialog.ShowDialog() != true) return;

                IsLoading = true;
                StatusMessage = "Importing rules from JSON...";
                _logger?.LogInfo($"Importing rules from: {openFileDialog.FileName}");

                var jsonContent = await Task.Run(() => File.ReadAllText(openFileDialog.FileName, Encoding.UTF8));

                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    ReadCommentHandling = JsonCommentHandling.Skip
                };

                var importedRules = JsonSerializer.Deserialize<List<RuleExportModel>>(jsonContent, options);

                if (importedRules == null || importedRules.Count == 0)
                {
                    StatusMessage = "No rules found in JSON file";
                    return;
                }

                var importedCount = 0;
                // Calculate starting priority based on count (sequential 1, 2, 3...)
                var currentPriority = Rules.Count;

                foreach (var importedRule in importedRules)
                {
                    // Use imported priority if specified, otherwise sequential count + 1
                    var rulePriority = importedRule.Priority ?? ++currentPriority;

                    var rule = new Rule
                    {
                        RuleId = !string.IsNullOrWhiteSpace(importedRule.Id) ? importedRule.Id : Guid.NewGuid().ToString().Substring(0, 8).ToUpper(),
                        Name = importedRule.Name ?? $"Imported Rule {importedCount + 1}",
                        Description = importedRule.Description ?? "",
                        Mode = ParseProtectionMode(importedRule.Mode),
                        Priority = rulePriority,
                        IsEnabled = importedRule.IsEnabled ?? true,
                        CategoryName = importedRule.Category,
                        CategoryCode = importedRule.CategoryCode,
                        FamilyName = importedRule.Family,
                        TypeName = importedRule.Type,
                        Message = importedRule.Message ?? "",
                        CaptureBeforeScreenshot = importedRule.CaptureBeforeScreenshot ?? false,
                        CaptureAfterScreenshot = importedRule.CaptureAfterScreenshot ?? false,
                        RequireComment = importedRule.RequireComment ?? false,
                        AllowAdminOverride = importedRule.AllowAdminOverride ?? true,
                        CreatedAt = DateTime.UtcNow,
                        ModifiedAt = DateTime.UtcNow,
                        CreatedBy = _profileId ?? _revitUsername,
                        ModifiedBy = _profileId ?? _revitUsername,
                        ModelGuid = importedRule.ModelGuid,
                        RuleScope = importedRule.RuleScopeInt ?? ParseRuleScope(importedRule.RuleScope)
                    };

                    // Add command IDs if present
                    if (importedRule.CommandIds != null)
                    {
                        foreach (var cmdId in importedRule.CommandIds)
                        {
                            rule.CommandIds.Add(cmdId);
                        }
                    }

                    var saved = await _ruleRepository.SaveRuleAsync(rule);
                    if (saved)
                    {
                        importedCount++;
                        // Sync to backend API
                        if (_rulesSyncService != null)
                        {
                            await _rulesSyncService.SyncRuleAsync(rule);
                        }
                    }
                }

                // Refresh
                await _ruleService.RefreshRules();
                await LoadRulesAsync();

                _logger?.LogInfo($"Imported and synced {importedCount} rules");
                StatusMessage = $"Imported {importedCount} rules successfully";
                _logger?.LogInfo($"Imported {importedCount} rules from JSON");
            }
            catch (JsonException jsonEx)
            {
                StatusMessage = $"Invalid JSON format: {jsonEx.Message}";
                _logger?.LogError($"JSON parsing error: {jsonEx.Message}", jsonEx);
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error importing: {ex.Message}";
                _logger?.LogError($"Error importing rules: {ex.Message}", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task ExportToExcelAsync()
        {
            try
            {
                var saveFileDialog = new SaveFileDialog
                {
                    Filter = "ZE Files (*.ze)|*.ze|All Files (*.*)|*.*",
                    Title = "Export Rules",
                    FileName = $"Rules_Export_{DateTime.Now:yyyyMMdd_HHmmss}.ze"
                };

                if (saveFileDialog.ShowDialog() != true) return;

                IsLoading = true;
                StatusMessage = "Exporting rules to JSON...";
                _logger?.LogInfo($"Exporting rules to: {saveFileDialog.FileName}");

                var rules = await _ruleRepository.GetAllRulesAsync();

                var exportModels = rules.Select(rule => new RuleExportModel
                {
                    Id = rule.RuleId,
                    Name = rule.Name,
                    Description = rule.Description,
                    Mode = rule.Mode.ToString(),
                    Priority = rule.Priority,
                    IsEnabled = rule.IsEnabled,
                    Category = rule.CategoryName,
                    CategoryCode = rule.CategoryCode,
                    Family = rule.FamilyName,
                    Type = rule.TypeName,
                    Message = rule.Message,
                    CaptureBeforeScreenshot = rule.CaptureBeforeScreenshot,
                    CaptureAfterScreenshot = rule.CaptureAfterScreenshot,
                    RequireComment = rule.RequireComment,
                    AllowAdminOverride = rule.AllowAdminOverride,
                    CommandIds = rule.CommandIds.Count > 0 ? rule.CommandIds.ToList() : null,
                    CreatedBy = rule.CreatedBy,
                    ModifiedBy = rule.ModifiedBy,
                    ModelGuid = rule.ModelGuid,
                    RuleScopeInt = rule.RuleScope,
                    RuleScope = ((RuleScopeType)rule.RuleScope).ToString(),
                    CreatedOn = rule.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                    UpdatedOn = rule.ModifiedAt.ToString("yyyy-MM-dd HH:mm:ss")
                }).ToList();

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
                };

                var jsonContent = JsonSerializer.Serialize(exportModels, options);
                await Task.Run(() => File.WriteAllText(saveFileDialog.FileName, jsonContent, Encoding.UTF8));

                StatusMessage = $"Exported {rules.Count} rules to {Path.GetFileName(saveFileDialog.FileName)}";
                _logger?.LogInfo($"Exported {rules.Count} rules to JSON");
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error exporting: {ex.Message}";
                _logger?.LogError($"Error exporting rules: {ex.Message}", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private ProtectionMode ParseProtectionMode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return ProtectionMode.Notify;
            return value.ToLowerInvariant() switch
            {
                "notify" or "1" => ProtectionMode.Notify,
                "assist" or "2" => ProtectionMode.Assist,
                "protect" or "3" => ProtectionMode.Protect,
                _ => ProtectionMode.Notify
            };
        }

        private int ParseRuleScope(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return (int)RuleScopeType.CompanyWide;
            // Support both integer strings and display names
            if (int.TryParse(value, out var intVal) && intVal >= 1 && intVal <= 3)
                return intVal;
            return value.ToLowerInvariant() switch
            {
                "company-wide" or "companywide" => (int)RuleScopeType.CompanyWide,
                "project-wise" or "projectwide" or "projectwise" => (int)RuleScopeType.ProjectWide,
                "model-specific" or "modelspecific" or "file-specific" => (int)RuleScopeType.ModelSpecific,
                _ => (int)RuleScopeType.CompanyWide
            };
        }

        private void CancelEdit()
        {
            IsEditing = false;
            EditingRule = null;
            StatusMessage = "Edit cancelled";
        }

        private void Close()
        {
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        #endregion
    }

    /// <summary>
    /// ViewModel wrapper for a single Rule
    /// </summary>
    public class RuleViewModel : INotifyPropertyChanged
    {
        private readonly Rule _rule;
        public bool IsNew { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;

        public RuleViewModel(Rule rule)
        {
            _rule = rule ?? throw new ArgumentNullException(nameof(rule));
        }

        public string RuleId => _rule.RuleId;

        public string Name
        {
            get => _rule.Name;
            set
            {
                _rule.Name = value;
                OnPropertyChanged();
            }
        }

        public string Description
        {
            get => _rule.Description;
            set
            {
                _rule.Description = value;
                OnPropertyChanged();
            }
        }

        public ProtectionMode Mode
        {
            get => _rule.Mode;
            set
            {
                _rule.Mode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ModeDisplay));
                OnPropertyChanged(nameof(ModeDisplayNew));
            }
        }

        public string ModeDisplay => Mode.ToString();

        public string ModeDisplayNew => Mode switch
        {
            ProtectionMode.Notify => "Notify",
            ProtectionMode.Assist => "Assist",
            ProtectionMode.Protect => "Protect",
            _ => "1-Notify"
        };

        public int ModeIndex
        {
            get => (int)_rule.Mode - 1; // ProtectionMode starts at 1
            set
            {
                _rule.Mode = (ProtectionMode)(value + 1);
                OnPropertyChanged();
                OnPropertyChanged(nameof(Mode));
                OnPropertyChanged(nameof(ModeDisplay));
                OnPropertyChanged(nameof(ModeDisplayNew));
            }
        }

        public int Priority
        {
            get => _rule.Priority;
            set
            {
                _rule.Priority = value;
                OnPropertyChanged();
            }
        }

        public bool IsEnabled
        {
            get => _rule.IsEnabled;
            set
            {
                _rule.IsEnabled = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusDisplay));
            }
        }

        public string StatusDisplay => IsEnabled ? "Enabled" : "Disabled";

        // Row number for S.No column (set by parent collection)
        private int _rowNumber;
        public int RowNumber
        {
            get => _rowNumber;
            set
            {
                _rowNumber = value;
                OnPropertyChanged();
            }
        }

        // Tooltip properties
        public string StatusTooltip => IsEnabled ? "Status: Enabled\nRule is active and enforced" : "Status: Disabled\nRule is inactive";
        public string ModeTooltip => Mode switch
        {
            ProtectionMode.Notify => "Mode: Notify\nNotify user about the action without blocking",
            ProtectionMode.Assist => "Mode: Assist\nGuide user with the action and provide assistance",
            ProtectionMode.Protect => "Mode: Protect\nBlock the action completely",
            _ => "Mode: Notify"
        };
        public string ModifiedByTooltip => $"Modified By: {ModifiedBy}\nDate: {ModifiedAt}";

        // Rule Name tooltip showing RuleId, Description, Message
        public string RuleNameTooltip => $"Rule ID: {RuleId}\n" +
            $"Description: {Description}\n" +
            $"Message: {Message}";

        // Full data tooltip for row hover
        public string FullDataTooltip => $"Rule: {Name}\n" +
            $"ID: {RuleId}\n" +
            $"Description: {Description}\n" +
            $"Mode: {ModeDisplayNew}\n" +
            $"Status: {StatusDisplay}\n" +
            $"Category: {CategoryName}\n" +
            $"Scope: {RuleScopeDisplay}\n" +
            $"Message: {Message}\n" +
            $"Priority: {Priority}\n" +
            $"Capture Before: {(CaptureBeforeScreenshot ? "Yes" : "No")}\n" +
            $"Capture After: {(CaptureAfterScreenshot ? "Yes" : "No")}\n" +
            $"Require Comment: {(RequireComment ? "Yes" : "No")}\n" +
            $"Admin Override: {(AllowAdminOverride ? "Yes" : "No")}\n" +
            $"Modified By: {ModifiedBy}\n" +
            $"Modified At: {ModifiedAt}";

        public string CategoryName
        {
            get => _rule.CategoryName ?? string.Empty;
            set
            {
                _rule.CategoryName = string.IsNullOrWhiteSpace(value) ? null : value;
                OnPropertyChanged();
            }
        }

        public string TypeName
        {
            get => _rule.TypeName ?? string.Empty;
            set
            {
                _rule.TypeName = string.IsNullOrWhiteSpace(value) ? null : value;
                OnPropertyChanged();
            }
        }

        public string FamilyName
        {
            get => _rule.FamilyName ?? string.Empty;
            set
            {
                _rule.FamilyName = string.IsNullOrWhiteSpace(value) ? null : value;
                OnPropertyChanged();
            }
        }

        public string Message
        {
            get => _rule.Message;
            set
            {
                _rule.Message = value;
                OnPropertyChanged();
            }
        }

        public bool CaptureBeforeScreenshot
        {
            get => _rule.CaptureBeforeScreenshot;
            set
            {
                _rule.CaptureBeforeScreenshot = value;
                OnPropertyChanged();
            }
        }

        public bool CaptureAfterScreenshot
        {
            get => _rule.CaptureAfterScreenshot;
            set
            {
                _rule.CaptureAfterScreenshot = value;
                OnPropertyChanged();
            }
        }

        public bool RequireComment
        {
            get => _rule.RequireComment;
            set
            {
                _rule.RequireComment = value;
                OnPropertyChanged();
            }
        }

        public bool AllowAdminOverride
        {
            get => _rule.AllowAdminOverride;
            set
            {
                _rule.AllowAdminOverride = value;
                OnPropertyChanged();
            }
        }

        public bool SendEmail
        {
            get => _rule.SendEmail;
            set
            {
                _rule.SendEmail = value;
                OnPropertyChanged();
            }
        }

        public string CreatedBy => _rule.CreatedBy ?? "Unknown";
        public string ModifiedBy
        {
            get => _rule.ModifiedBy ?? "Unknown";
            set { _rule.ModifiedBy = value; OnPropertyChanged(); }
        }
        public string CreatedAt => _rule.CreatedAt.ToString("yyyy-MM-dd HH:mm");
        public string ModifiedAt => _rule.ModifiedAt.ToString("yyyy-MM-dd HH:mm");

        public string ModelGuid
        {
            get => _rule.ModelGuid ?? string.Empty;
            set
            {
                _rule.ModelGuid = string.IsNullOrWhiteSpace(value) ? null : value;
                OnPropertyChanged();
            }
        }

        public int RuleScope
        {
            get => _rule.RuleScope;
            set
            {
                _rule.RuleScope = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RuleScopeDisplay));
            }
        }

        public string RuleScopeDisplay => RuleScope switch
        {
            (int)RuleScopeType.CompanyWide => "Company",
            (int)RuleScopeType.ProjectWide => "Project",
            (int)RuleScopeType.ModelSpecific => "Model",
            _ => "Company"
        };

        public string CategoryCode
        {
            get => _rule.CategoryCode ?? string.Empty;
            set
            {
                _rule.CategoryCode = string.IsNullOrWhiteSpace(value) ? null : value;
                OnPropertyChanged();
            }
        }

        public string CommandIdsText
        {
            get => string.Join(", ", _rule.CommandIds);
            set
            {
                _rule.CommandIds.Clear();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var ids = value.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var id in ids)
                    {
                        if (int.TryParse(id.Trim(), out int commandId))
                        {
                            _rule.CommandIds.Add(commandId);
                        }
                    }
                }
                OnPropertyChanged();
            }
        }

        public string CommandName
        {
            get => string.Join(", ", _rule.CommandNames);
            set
            {
                _rule.CommandNames.Clear();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    var names = value.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var name in names)
                    {
                        _rule.CommandNames.Add(name.Trim());
                    }
                }
                OnPropertyChanged();
                OnPropertyChanged(nameof(CommandNameTooltip));
            }
        }

        /// <summary>Allow external code to request a UI refresh of the command tooltip.</summary>
        public void RaiseCommandTooltipChanged() => OnPropertyChanged(nameof(CommandNameTooltip));

        /// <summary>Tooltip showing descriptions from the API for each command in this rule. Returns null if no descriptions are available (hides tooltip).</summary>
        public string? CommandNameTooltip
        {
            get
            {
                if (_rule.CommandNames == null || _rule.CommandNames.Count == 0)
                    return null;
                var sb = new System.Text.StringBuilder();
                bool anyDesc = false;
                foreach (var name in _rule.CommandNames)
                {
                    var desc = RuleCommandDescriptionCache.Get(name);
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        sb.AppendLine($"{name}: {desc}");
                        anyDesc = true;
                    }
                }
                return anyDesc ? sb.ToString().TrimEnd() : null;
            }
        }

        // Display properties for DataGrid
        public string CaptureBeforeDisplay => CaptureBeforeScreenshot ? "✓" : "✗";
        public string CaptureAfterDisplay => CaptureAfterScreenshot ? "✓" : "✗";
        public string RequireCommentDisplay => RequireComment ? "✓" : "✗";
        public string AllowOverrideDisplay => AllowAdminOverride ? "✓" : "✗";

        public string ModelGuidShort
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ModelGuid)) return "";
                return ModelGuid.Length > 8 ? ModelGuid.Substring(0, 8) + "..." : ModelGuid;
            }
        }

        public Rule ToRule() => _rule;

        protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// Simple RelayCommand implementation for MVVM
    /// </summary>
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool>? _canExecute;

        public event EventHandler? CanExecuteChanged;

        public RelayCommand(Action execute, Func<bool>? canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }

        public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

        public void Execute(object? parameter) => _execute();

        public void RaiseCanExecuteChanged()
        {
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Model for JSON import/export of rules
    /// </summary>
    public class RuleExportModel
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("mode")]
        public string? Mode { get; set; }

        [JsonPropertyName("priority")]
        public int? Priority { get; set; }

        [JsonPropertyName("isEnabled")]
        public bool? IsEnabled { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("family")]
        public string? Family { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("captureBeforeScreenshot")]
        public bool? CaptureBeforeScreenshot { get; set; }

        [JsonPropertyName("captureAfterScreenshot")]
        public bool? CaptureAfterScreenshot { get; set; }

        [JsonPropertyName("requireComment")]
        public bool? RequireComment { get; set; }

        [JsonPropertyName("allowAdminOverride")]
        public bool? AllowAdminOverride { get; set; }

        [JsonPropertyName("commandIds")]
        public List<int>? CommandIds { get; set; }

        [JsonPropertyName("createdBy")]
        public string? CreatedBy { get; set; }

        [JsonPropertyName("modifiedBy")]
        public string? ModifiedBy { get; set; }

        [JsonPropertyName("modelGuid")]
        public string? ModelGuid { get; set; }

        [JsonPropertyName("ruleScope")]
        public string? RuleScope { get; set; }

        [JsonPropertyName("ruleScopeInt")]
        public int? RuleScopeInt { get; set; }

        [JsonPropertyName("categoryCode")]
        public string? CategoryCode { get; set; }

        [JsonPropertyName("createdOn")]
        public string? CreatedOn { get; set; }

        [JsonPropertyName("updatedOn")]
        public string? UpdatedOn { get; set; }
    }
}
