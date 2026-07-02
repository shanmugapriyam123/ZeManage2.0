using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using BIManage.Core.Rules.Cache;
using BIManage.Core.Rules.Models;

namespace BIManage.Core.Rules
{
    /// <summary>
    /// Main interface for rule management and evaluation
    /// </summary>
    public interface IRuleService
    {
        /// <summary>
        /// Initialize the rule service (load rules from SQLite/JSON)
        /// </summary>
        Task<bool> InitializeAsync();

        /// <summary>
        /// Evaluate rules for a single element and command
        /// </summary>
        RuleEvaluationResult EvaluateRules(Element element, int commandId);

        /// <summary>
        /// Evaluate rules for multiple elements (batch)
        /// </summary>
        RuleEvaluationResult EvaluateRulesBatch(IEnumerable<Element> elements, int commandId);

        /// <summary>
        /// Get all active rules
        /// </summary>
        List<Rule> GetActiveRules();

        /// <summary>
        /// Get rules for specific project
        /// </summary>
        List<Rule> GetRulesForProject(string? projectId);

        /// <summary>
        /// Save a rule
        /// </summary>
        Task<bool> SaveRuleAsync(Rule rule);

        /// <summary>
        /// Delete a rule
        /// </summary>
        Task<bool> DeleteRuleAsync(string ruleId);

        /// <summary>
        /// Reload rules from persistence
        /// </summary>
        Task<int> RefreshRules();

        /// <summary>
        /// Get cache statistics
        /// </summary>
        CacheStatistics GetCacheStatistics();

        /// <summary>
        /// Detect rule conflicts
        /// </summary>
        List<RuleConflict> DetectConflicts();

        /// <summary>
        /// Get rules by their IDs (for event protection conditional activation)
        /// </summary>
        List<Rule> GetRulesByIds(List<string> ruleIds);

        /// <summary>
        /// Get all distinct command IDs referenced by enabled rules.
        /// Used for dynamic command binding registration and TestRulesCommand.
        /// </summary>
        List<(int CommandId, string CommandName)> GetDistinctRuleCommandIds();
    }
}
