using System.Collections.Generic;
using BIManage.Core.Rules.Models;

namespace BIManage.Core.Rules.Resolution
{
    public interface IRulePriorityResolver
    {
        RuleEvaluationResult Resolve(IReadOnlyList<Rule> matchedRules);
    }
}
