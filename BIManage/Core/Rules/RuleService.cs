using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using BIManage.Core.Rules.Cache;
using BIManage.Core.Rules.Evaluation;
using BIManage.Core.Rules.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Commands;

namespace BIManage.Core.Rules
{
    /// <summary>
    /// Main rule service orchestrating evaluation, caching, and persistence
    /// </summary>
    public class RuleService : IRuleService, IDisposable
    {
        private readonly IRuleEvaluator _evaluator;
        private readonly IRuleCacheManager _cacheManager;
        private readonly RuleRepository _repository;
        private readonly ILogger _logger;
        private readonly string _samplesPath;
        private bool _initialized = false;

        public RuleService(
            IRuleEvaluator evaluator,
            IRuleCacheManager cacheManager,
            RuleRepository repository,
            ILogger logger,
            string samplesPath = null)
        {
            _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
            _cacheManager = cacheManager ?? throw new ArgumentNullException(nameof(cacheManager));
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _samplesPath = samplesPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Rules", "Samples");
        }

        /// <summary>
        /// Initialize the rule service
        /// Loads rules from SQLite, or if empty, from JSON samples
        /// </summary>
        public async Task<bool> InitializeAsync()
        {
            try
            {
                _logger.LogInfo("Initializing Rule Service...");

                // Load rules from SQLite into cache
                var loadedCount = await _cacheManager.RefreshCache();

                // Sample-rule auto-import DISABLED (2026-05-26). In production the server
                // is the authoritative source of rules — auto-importing 9 sample rules
                // from BIManage\Rules\Samples\*.json on first run created a permanent
                // mismatch where the web dashboard showed 1 rule (the admin's actual
                // rule) and Revit showed 10 (1 server + 9 local samples). Those samples
                // never round-trip to the server, so they pollute the local DB forever
                // and the user can't tell which rules are "real". A fresh install should
                // simply show whatever the server has assigned to the user's company.
                // Sample JSONs are still available for manual Import via the dialog if
                // a customer wants starter templates — they just don't load on their own.
                if (loadedCount == 0)
                {
                    _logger.LogInfo("No rules found in database — sample auto-import is disabled; rules will populate from the server on first model open.");
                }

                // Finalize rules: resolve commands, detect & persist conflicts
                await FinalizeRulesAsync();

                // Log statistics
                var stats = _cacheManager.GetStatistics();
                _logger.LogInfo($"Rule Service initialized: {stats}");

                _initialized = true;
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to initialize Rule Service: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Evaluate rules for a single element
        /// </summary>
        public RuleEvaluationResult EvaluateRules(Element element, int commandId)
        {
            if (!_initialized)
            {
                _logger.LogWarning("RuleService not initialized, returning empty result");
                return new RuleEvaluationResult();
            }

            try
            {
                var rules = _cacheManager.GetActiveRules();
                return _evaluator.Evaluate(element, commandId, rules);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error evaluating rules: {ex.Message}", ex);
                return new RuleEvaluationResult();
            }
        }

        /// <summary>
        /// Evaluate rules for multiple elements (batch)
        /// </summary>
        public RuleEvaluationResult EvaluateRulesBatch(IEnumerable<Element> elements, int commandId)
        {
            if (!_initialized)
            {
                _logger.LogWarning("RuleService not initialized, returning empty result");
                return new RuleEvaluationResult();
            }

            try
            {
                var rules = _cacheManager.GetActiveRules();
                _logger.LogInfo($"Evaluating with {rules.Count} active rules from cache (commandId: {commandId})");

                if (rules.Count == 0)
                {
                    _logger.LogWarning("No rules in cache! Rules may not have been loaded. Check RuleService initialization.");
                }

                return _evaluator.EvaluateBatch(elements, commandId, rules);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error evaluating rules (batch): {ex.Message}", ex);
                return new RuleEvaluationResult();
            }
        }

        public List<Rule> GetActiveRules() => _cacheManager.GetActiveRules();

        /// <summary>
        /// Get rules by their IDs, filtering to enabled rules only
        /// </summary>
        public List<Rule> GetRulesByIds(List<string> ruleIds)
        {
            if (ruleIds == null || ruleIds.Count == 0)
                return new List<Rule>();

            var ruleIdSet = new HashSet<string>(ruleIds, StringComparer.OrdinalIgnoreCase);
            return _cacheManager.GetActiveRules()
                .Where(r => ruleIdSet.Contains(r.RuleId))
                .ToList();
        }

        public List<Rule> GetRulesForProject(string? projectId) => _cacheManager.GetRulesForProject(projectId);

        public async Task<bool> SaveRuleAsync(Rule rule) => await _cacheManager.SaveRule(rule);

        public async Task<bool> DeleteRuleAsync(string ruleId) => await _cacheManager.DeleteRule(ruleId);

        public async Task<int> RefreshRules()
        {
            var count = await _cacheManager.RefreshCache();

            // Ensure the service is marked initialized after any successful cache refresh.
            // RefreshRules is called far more often than InitializeAsync (after every save,
            // sync, or API fetch), so setting the flag here prevents the silent fail-open
            // state where Dispose() or a failed startup leaves _initialized = false and all
            // subsequent EvaluateRulesBatch calls return empty results until Revit restarts.
            _initialized = true;

            // Re-resolve command names → IDs and category codes → IDs for newly added rules
            var allRules = _cacheManager.GetActiveRules();
            var commandsResolved = ParseAndResolveCommands(allRules);
            var categoriesResolved = ResolveCategories(allRules);

            if (commandsResolved > 0 || categoriesResolved > 0)
            {
                // Persist resolved data back to DB
                foreach (var rule in allRules)
                {
                    await _repository.SaveRuleAsync(rule);
                }
                // Refresh cache with resolved data
                await _cacheManager.RefreshCache();
                _logger.LogInfo($"Post-refresh finalization: {commandsResolved} commands resolved, {categoriesResolved} categories resolved");
                allRules = _cacheManager.GetActiveRules();
            }

            // Re-run conflict detection so stale conflicts don't linger after SignalR/HTTP pushes
            try
            {
                var conflicts = DetectConflictsWithResolution(allRules);
                await _repository.ClearUnresolvedConflictsAsync();
                if (conflicts.Count > 0)
                {
                    await _repository.SaveConflictsAsync(conflicts);
                }
                _logger.LogInfo($"Post-refresh conflict detection: {conflicts.Count} conflicts detected");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Post-refresh conflict detection failed: {ex.Message}", ex);
            }

            return count;
        }

        public CacheStatistics GetCacheStatistics() => _cacheManager.GetStatistics();

        public List<RuleConflict> DetectConflicts() => _cacheManager.DetectConflicts();

        /// <summary>
        /// Get all distinct command IDs referenced by enabled rules.
        /// Used for dynamic command binding registration and TestRulesCommand.
        /// </summary>
        public List<(int CommandId, string CommandName)> GetDistinctRuleCommandIds()
        {
            var activeRules = _cacheManager.GetActiveRules();
            var distinctCommands = new Dictionary<int, string>();

            foreach (var rule in activeRules)
            {
                for (int i = 0; i < rule.CommandIds.Count; i++)
                {
                    var cmdId = rule.CommandIds[i];
                    if (cmdId > 0 && !distinctCommands.ContainsKey(cmdId))
                    {
                        var cmdName = i < rule.CommandNames.Count
                            ? rule.CommandNames[i]
                            : CommandNameResolver.ResolveIdToName(cmdId) ?? $"Command_{cmdId}";
                        distinctCommands[cmdId] = cmdName;
                    }
                }
            }

            return distinctCommands.Select(kvp => (kvp.Key, kvp.Value)).ToList();
        }

        /// <summary>
        /// Post-load finalization: resolve command names to IDs, detect and persist conflicts.
        /// Called from InitializeAsync after rules are loaded into cache.
        /// </summary>
        private async Task FinalizeRulesAsync()
        {
            try
            {
                var allRules = _cacheManager.GetActiveRules();
                if (allRules.Count == 0)
                {
                    _logger.LogDebug("No active rules to finalize");
                    return;
                }

                // Step 1: Parse and resolve command names ↔ IDs
                var commandsResolved = ParseAndResolveCommands(allRules);

                // Step 1b: Resolve category codes → IDs
                var categoriesResolved = ResolveCategories(allRules);

                // Step 2: Persist resolved data back to DB (all rules, not just those with CommandIds)
                var rulesUpdated = 0;
                if (commandsResolved > 0 || categoriesResolved > 0)
                {
                    foreach (var rule in allRules)
                    {
                        if (await _repository.SaveRuleAsync(rule))
                            rulesUpdated++;
                    }

                    // Refresh cache with resolved data
                    await _cacheManager.RefreshCache();
                }

                // Step 3: Detect and persist conflicts
                var conflicts = DetectConflictsWithResolution(allRules);
                await _repository.ClearUnresolvedConflictsAsync();
                if (conflicts.Count > 0)
                {
                    await _repository.SaveConflictsAsync(conflicts);
                    foreach (var conflict in conflicts.Take(5))
                    {
                        _logger.LogWarning($"  Conflict: {conflict.Description}");
                    }
                }

                _logger.LogInfo($"Rule finalization: {commandsResolved} commands resolved, {categoriesResolved} categories resolved, " +
                                $"{rulesUpdated} rules updated, {conflicts.Count} conflicts detected");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error during rule finalization: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Resolve command names to IDs and vice versa for all rules.
        /// Returns count of rules that were modified.
        /// </summary>
        private int ParseAndResolveCommands(List<Rule> rules)
        {
            int resolvedCount = 0;

            foreach (var rule in rules)
            {
                bool modified = false;

                // Case 1: Has command names but no valid IDs (API-sourced rules stored with command_id=0)
                if (rule.CommandNames.Count > 0 &&
                    (rule.CommandIds.Count == 0 || rule.CommandIds.All(id => id == 0)))
                {
                    rule.CommandIds.Clear();
                    foreach (var name in rule.CommandNames)
                    {
                        var id = CommandNameResolver.ResolveNameToId(name);
                        if (id > 0)
                        {
                            rule.CommandIds.Add(id);
                            modified = true;
                        }
                        else
                        {
                            _logger.LogWarning($"Rule '{rule.Name}': Unknown command name '{name}'");
                            rule.CommandIds.Add(0); // Keep parallel list alignment
                        }
                    }
                }

                // Case 2: Has command IDs but no names (legacy or numeric-only rules)
                if (rule.CommandIds.Count > 0 && rule.CommandIds.Any(id => id > 0) &&
                    rule.CommandNames.Count == 0)
                {
                    foreach (var id in rule.CommandIds)
                    {
                        var name = id > 0 ? CommandNameResolver.ResolveIdToName(id) : null;
                        rule.CommandNames.Add(name ?? $"Command_{id}");
                        modified = true;
                    }
                }

                // Case 3: Ensure parallel list alignment
                while (rule.CommandNames.Count < rule.CommandIds.Count)
                {
                    var id = rule.CommandIds[rule.CommandNames.Count];
                    rule.CommandNames.Add(CommandNameResolver.ResolveIdToName(id) ?? $"Command_{id}");
                    modified = true;
                }
                while (rule.CommandIds.Count < rule.CommandNames.Count)
                {
                    var name = rule.CommandNames[rule.CommandIds.Count];
                    rule.CommandIds.Add(CommandNameResolver.ResolveNameToId(name));
                    modified = true;
                }

                if (modified)
                {
                    resolvedCount++;
                    _logger.LogDebug($"Resolved commands for rule '{rule.Name}': " +
                                    $"[{string.Join(", ", rule.CommandNames)}] → [{string.Join(", ", rule.CommandIds)}]");
                }
            }

            return resolvedCount;
        }

        /// <summary>
        /// Resolve CategoryCode (e.g., "OST_DuctTerminal") to CategoryId (BuiltInCategory integer).
        /// Always re-resolves when CategoryCode is available — handles cases where CategoryId
        /// was set to a wrong value (e.g., server-side ID from API sync instead of BuiltInCategory).
        /// Returns count of rules that were modified.
        /// </summary>
        private int ResolveCategories(List<Rule> rules)
        {
            int resolvedCount = 0;

            foreach (var rule in rules)
            {
                // Skip if no CategoryCode available
                if (string.IsNullOrEmpty(rule.CategoryCode))
                    continue;

                try
                {
                    if (Enum.TryParse<BuiltInCategory>(rule.CategoryCode, ignoreCase: true, out var builtInCat))
                    {
                        var resolvedId = (int)builtInCat;
                        if (rule.CategoryId != resolvedId)
                        {
                            rule.CategoryId = resolvedId;
                            resolvedCount++;
                            _logger.LogDebug($"Resolved category for rule '{rule.Name}': {rule.CategoryCode} → {rule.CategoryId}");
                        }
                    }
                    else
                    {
                        _logger.LogWarning($"Rule '{rule.Name}': Unknown category code '{rule.CategoryCode}'");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Rule '{rule.Name}': Failed to resolve category code '{rule.CategoryCode}': {ex.Message}");
                }
            }

            return resolvedCount;
        }

        /// <summary>
        /// Detect conflicts between enabled rules with resolution info (higher protection wins).
        /// </summary>
        private List<RuleConflict> DetectConflictsWithResolution(List<Rule> rules)
        {
            var conflicts = new List<RuleConflict>();
            var enabledRules = rules.Where(r => r.IsEnabled).ToList();

            for (int i = 0; i < enabledRules.Count; i++)
            {
                for (int j = i + 1; j < enabledRules.Count; j++)
                {
                    var rule1 = enabledRules[i];
                    var rule2 = enabledRules[j];

                    // Same category (both null = wildcard, matches all)
                    bool categoryOverlap = (!rule1.CategoryId.HasValue && !rule2.CategoryId.HasValue) ||
                                           (rule1.CategoryId.HasValue && rule2.CategoryId.HasValue &&
                                            rule1.CategoryId.Value == rule2.CategoryId.Value);
                    if (!categoryOverlap) continue;

                    // Overlapping commands (both empty = applies to all, so they overlap)
                    bool commandOverlap;
                    if (rule1.CommandIds.Count == 0 || rule2.CommandIds.Count == 0)
                    {
                        commandOverlap = true; // Empty = all commands
                    }
                    else
                    {
                        commandOverlap = rule1.CommandIds.Where(id => id > 0)
                            .Intersect(rule2.CommandIds.Where(id => id > 0)).Any();
                    }
                    if (!commandOverlap) continue;

                    // Different modes — this is a conflict
                    if (rule1.Mode == rule2.Mode) continue;

                    // Winner: higher protection mode
                    var winner = (int)rule1.Mode >= (int)rule2.Mode ? rule1 : rule2;

                    conflicts.Add(new RuleConflict
                    {
                        RuleId1 = rule1.RuleId,
                        RuleId2 = rule2.RuleId,
                        ConflictType = "ModeConflict",
                        Description = $"Rules '{rule1.Name}' ({rule1.Mode}) and '{rule2.Name}' ({rule2.Mode}) " +
                                      $"overlap on category '{rule1.CategoryName ?? "All"}'. " +
                                      $"Resolution: '{winner.Name}' ({winner.Mode}) wins.",
                        WinningRuleId = winner.RuleId,
                        DetectedAt = DateTime.UtcNow,
                        Resolved = false
                    });
                }
            }

            return conflicts;
        }

        /// <summary>
        /// Load sample rules from JSON files
        /// </summary>
        private async Task<int> LoadSampleRulesAsync()
        {
            int importedCount = 0;

            try
            {
                if (!Directory.Exists(_samplesPath))
                {
                    _logger.LogWarning($"Sample rules directory not found: {_samplesPath}");
                    return 0;
                }

                var jsonFiles = Directory.GetFiles(_samplesPath, "*.json");
                _logger.LogInfo($"Found {jsonFiles.Length} sample rule files");

                foreach (var jsonFile in jsonFiles)
                {
                    try
                    {
                        _logger.LogDebug($"Loading rules from: {Path.GetFileName(jsonFile)}");
                        var json = await ReadAllTextAsync(jsonFile);
                        var rules = JsonSerializer.Deserialize<List<Rule>>(json, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true
                        });

                        if (rules != null)
                        {
                            foreach (var rule in rules)
                            {
                                if (await _repository.SaveRuleAsync(rule))
                                {
                                    importedCount++;
                                    _logger.LogDebug($"  Imported rule: {rule.Name}");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Failed to load rules from {Path.GetFileName(jsonFile)}: {ex.Message}", ex);
                    }
                }

                _logger.LogInfo($"Successfully imported {importedCount} rules from JSON samples");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading sample rules: {ex.Message}", ex);
            }

            return importedCount;
        }

        /// <summary>
        /// Async file read helper that works on both .NET Framework and .NET Core
        /// </summary>
        private async Task<string> ReadAllTextAsync(string path)
        {
            using (var reader = new StreamReader(path, Encoding.UTF8))
            {
                return await reader.ReadToEndAsync();
            }
        }

        public void Dispose()
        {
            _cacheManager?.ClearCache();
            _initialized = false;
        }
    }
}