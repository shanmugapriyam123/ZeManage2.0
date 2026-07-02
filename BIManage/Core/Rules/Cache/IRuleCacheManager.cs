using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.Core.Rules.Models;

namespace BIManage.Core.Rules.Cache
{
    /// <summary>
    /// Interface for rule caching with hybrid strategy support
    /// </summary>
    public interface IRuleCacheManager
    {
        /// <summary>
        /// Get all active rules (from memory cache)
        /// </summary>
        List<Rule> GetActiveRules();

        /// <summary>
        /// Get rules filtered by project
        /// </summary>
        List<Rule> GetRulesForProject(string? projectId);

        /// <summary>
        /// Get rules filtered by company
        /// </summary>
        List<Rule> GetRulesForCompany(string? companyId);

        /// <summary>
        /// Reload rules from SQLite into memory
        /// </summary>
        Task<int> RefreshCache();

        /// <summary>
        /// Add or update a rule
        /// </summary>
        Task<bool> SaveRule(Rule rule);

        /// <summary>
        /// Delete a rule
        /// </summary>
        Task<bool> DeleteRule(string ruleId);

        /// <summary>
        /// Detect conflicts between rules
        /// </summary>
        List<RuleConflict> DetectConflicts();

        /// <summary>
        /// Clear memory cache
        /// </summary>
        void ClearCache();

        /// <summary>
        /// Get cache statistics
        /// </summary>
        CacheStatistics GetStatistics();
    }
}
