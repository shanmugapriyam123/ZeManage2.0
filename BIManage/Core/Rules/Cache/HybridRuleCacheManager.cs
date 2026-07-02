using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BIManage.Core.Rules.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;

namespace BIManage.Core.Rules.Cache
{
    /// <summary>
    /// Hybrid caching strategy: In-memory for performance + SQLite for persistence
    /// </summary>
    public class HybridRuleCacheManager : IRuleCacheManager, IDisposable
    {
        private readonly RuleRepository _repository;
        private readonly ILogger _logger;

        // In-memory cache for fast access
        private Dictionary<string, Rule> _rulesCache;
        private readonly object _cacheLock = new object();
        private DateTime _lastRefresh = DateTime.MinValue;

        public HybridRuleCacheManager(RuleRepository repository, ILogger logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _rulesCache = new Dictionary<string, Rule>();
        }

        /// <summary>
        /// Get all active rules from memory cache
        /// </summary>
        public List<Rule> GetActiveRules()
        {
            lock (_cacheLock)
            {
                return _rulesCache.Values
                    .Where(r => r.IsEnabled)
                    .OrderByDescending(r => r.Priority)
                    .ToList();
            }
        }

        /// <summary>
        /// Get rules for specific project (null = global rules)
        /// </summary>
        public List<Rule> GetRulesForProject(string? projectId)
        {
            lock (_cacheLock)
            {
                return _rulesCache.Values
                    .Where(r => r.IsEnabled)
                    .Where(r => r.ProjectId == null || r.ProjectId == projectId)
                    .OrderByDescending(r => r.Priority)
                    .ToList();
            }
        }

        /// <summary>
        /// Get rules for specific company (null = global rules)
        /// </summary>
        public List<Rule> GetRulesForCompany(string? companyId)
        {
            lock (_cacheLock)
            {
                return _rulesCache.Values
                    .Where(r => r.IsEnabled)
                    .Where(r => r.CompanyId == null || r.CompanyId == companyId)
                    .OrderByDescending(r => r.Priority)
                    .ToList();
            }
        }

        /// <summary>
        /// Refresh memory cache from SQLite
        /// </summary>
        public async Task<int> RefreshCache()
        {
            try
            {
                _logger?.LogInfo("Refreshing rule cache from SQLite...");

                var rules = await _repository.GetAllRulesAsync();

                lock (_cacheLock)
                {
                    _rulesCache.Clear();

                    foreach (var rule in rules)
                    {
                        _rulesCache[rule.RuleId] = rule;
                    }

                    _lastRefresh = DateTime.UtcNow;
                }

                _logger?.LogInfo($"Rule cache refreshed: {rules.Count} rules loaded");

                // Log conflicts if any
                var conflicts = DetectConflicts();
                if (conflicts.Any())
                {
                    _logger?.LogWarning($"Detected {conflicts.Count} rule conflicts");
                    foreach (var conflict in conflicts.Take(5)) // Log first 5
                    {
                        _logger?.LogWarning($"  Conflict: {conflict.Description}");
                    }
                }

                return rules.Count;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to refresh rule cache: {ex.Message}", ex);
                return 0;
            }
        }

        /// <summary>
        /// Save rule to both SQLite and memory cache
        /// </summary>
        public async Task<bool> SaveRule(Rule rule)
        {
            try
            {
                if (rule == null)
                    throw new ArgumentNullException(nameof(rule));

                _logger?.LogInfo($"Saving rule: {rule.RuleId} - {rule.Name}");

                // Update timestamp
                rule.ModifiedAt = DateTime.UtcNow;
                rule.Version++;

                // Save to SQLite
                var success = await _repository.SaveRuleAsync(rule);

                if (success)
                {
                    // Update memory cache
                    lock (_cacheLock)
                    {
                        _rulesCache[rule.RuleId] = rule.Clone();
                    }

                    _logger?.LogInfo($"Rule saved successfully: {rule.RuleId}");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to save rule {rule?.RuleId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Delete rule from both SQLite and memory cache
        /// </summary>
        public async Task<bool> DeleteRule(string ruleId)
        {
            try
            {
                if (string.IsNullOrEmpty(ruleId))
                    throw new ArgumentNullException(nameof(ruleId));

                _logger?.LogInfo($"Deleting rule: {ruleId}");

                // Delete from SQLite
                var success = await _repository.DeleteRuleAsync(ruleId);

                if (success)
                {
                    // Remove from memory cache
                    lock (_cacheLock)
                    {
                        _rulesCache.Remove(ruleId);
                    }

                    _logger?.LogInfo($"Rule deleted successfully: {ruleId}");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to delete rule {ruleId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Detect conflicts between rules
        /// Uses simple heuristics for Phase 1
        /// </summary>
        public List<RuleConflict> DetectConflicts()
        {
            var conflicts = new List<RuleConflict>();

            lock (_cacheLock)
            {
                var enabledRules = _rulesCache.Values.Where(r => r.IsEnabled).ToList();

                // Check for rules with same conditions but different modes
                for (int i = 0; i < enabledRules.Count; i++)
                {
                    for (int j = i + 1; j < enabledRules.Count; j++)
                    {
                        var rule1 = enabledRules[i];
                        var rule2 = enabledRules[j];

                        // Same category and commands but different modes?
                        if (rule1.CategoryId == rule2.CategoryId &&
                            rule1.CommandIds.Intersect(rule2.CommandIds).Any() &&
                            rule1.Mode != rule2.Mode)
                        {
                            // Check if parameters also match
                            if (ParametersMatch(rule1, rule2))
                            {
                                conflicts.Add(new RuleConflict
                                {
                                    RuleId1 = rule1.RuleId,
                                    RuleId2 = rule2.RuleId,
                                    ConflictType = "SameConditionsDifferentModes",
                                    Description = $"Rules '{rule1.Name}' ({rule1.Mode}) and '{rule2.Name}' ({rule2.Mode}) have same conditions but different protection modes"
                                });
                            }
                        }
                    }
                }
            }

            return conflicts;
        }

        /// <summary>
        /// Check if two rules have matching parameters
        /// </summary>
        private bool ParametersMatch(Rule rule1, Rule rule2)
        {
            // If both have no parameters, they match
            if ((rule1.Parameters == null || rule1.Parameters.Count == 0) &&
                (rule2.Parameters == null || rule2.Parameters.Count == 0))
                return true;

            // If one has parameters and the other doesn't, no match
            if ((rule1.Parameters == null || rule1.Parameters.Count == 0) ||
                (rule2.Parameters == null || rule2.Parameters.Count == 0))
                return false;

            // Different number of parameters = no match
            if (rule1.Parameters.Count != rule2.Parameters.Count)
                return false;

            // Check if all parameter conditions match
            foreach (var param1 in rule1.Parameters)
            {
                if (!rule2.Parameters.TryGetValue(param1.Key, out var param2))
                    return false;

                if (param1.Value.Operator != param2.Operator ||
                    param1.Value.Value != param2.Value)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Clear memory cache (keeps SQLite data)
        /// </summary>
        public void ClearCache()
        {
            lock (_cacheLock)
            {
                _rulesCache.Clear();
                _lastRefresh = DateTime.MinValue;
            }

            _logger?.LogInfo("Rule cache cleared");
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public CacheStatistics GetStatistics()
        {
            lock (_cacheLock)
            {
                return new CacheStatistics
                {
                    TotalRules = _rulesCache.Count,
                    EnabledRules = _rulesCache.Values.Count(r => r.IsEnabled),
                    DisabledRules = _rulesCache.Values.Count(r => !r.IsEnabled),
                    LastRefresh = _lastRefresh,
                    MonitorRules = _rulesCache.Values.Count(r => r.Mode == ProtectionMode.Notify),
                    GuideRules = _rulesCache.Values.Count(r => r.Mode == ProtectionMode.Assist),
                    PreventRules = _rulesCache.Values.Count(r => r.Mode == ProtectionMode.Protect)
                };
            }
        }

        public void Dispose()
        {
            ClearCache();
        }
    }
}