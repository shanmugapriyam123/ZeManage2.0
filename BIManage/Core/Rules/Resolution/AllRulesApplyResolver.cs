using System.Collections.Generic;
using System.Linq;
using System.Text;
using BIManage.Core.Rules.Models;

namespace BIManage.Core.Rules.Resolution
{
    /// <summary>
    /// Simple priority resolver that applies all matching rules and escalates to the highest protection mode.
    /// </summary>
    public class AllRulesApplyResolver : IRulePriorityResolver
    {
        public RuleEvaluationResult Resolve(IReadOnlyList<Rule> matchedRules)
        {
            if (matchedRules.Count == 0)
                return RuleEvaluationResult.Empty;

            var result = new RuleEvaluationResult
            {
                MatchedRules = matchedRules.OrderByDescending(r => r.Priority).ToList(),
            };

            // Prevent > Guide > Monitor
            result.FinalMode = matchedRules.Max(r => r.Mode);

            result.CaptureBeforeScreenshot = matchedRules.Any(r => r.CaptureBeforeScreenshot);
            result.CaptureAfterScreenshot = matchedRules.Any(r => r.CaptureAfterScreenshot);
            result.RequireComment = matchedRules.Any(r => r.RequireComment);
            result.CombinedMessage = BuildCombinedMessage(matchedRules);

            return result;
        }

        private static string BuildCombinedMessage(IEnumerable<Rule> rules)
        {
            var messages = rules
                .Select(r => string.IsNullOrWhiteSpace(r.Message) ? r.Name : r.Message)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Distinct()
                .ToList();

            if (messages.Count == 0)
                return string.Empty;

            var builder = new StringBuilder();
            for (int i = 0; i < messages.Count; i++)
            {
                builder.Append($"- {messages[i]}");
                if (i < messages.Count - 1)
                    builder.AppendLine();
            }

            return builder.ToString();
        }
    }
}
