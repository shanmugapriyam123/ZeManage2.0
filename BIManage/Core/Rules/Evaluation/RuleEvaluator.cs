using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using BIManage.Common.Helpers;
using BIManage.Core.Rules.Models;
using BIManage.Infrastructure.Logging;

namespace BIManage.Core.Rules.Evaluation
{
    /// <summary>
    /// Interface for rule evaluation
    /// </summary>
    public interface IRuleEvaluator
    {
        /// <summary>
        /// Evaluate rules against an element and command
        /// </summary>
        RuleEvaluationResult Evaluate(Element element, int commandId, List<Rule> rules);

        /// <summary>
        /// Evaluate rules against multiple elements (batch)
        /// </summary>
        RuleEvaluationResult EvaluateBatch(IEnumerable<Element> elements, int commandId, List<Rule> rules);
    }

    /// <summary>
    /// Main rule evaluator that coordinates specialized evaluators
    /// </summary>
    public class RuleEvaluator : IRuleEvaluator
    {
        private readonly ICategoryEvaluator _categoryEvaluator;
        private readonly IParameterEvaluator _parameterEvaluator;
        private readonly ITypeEvaluator _typeEvaluator;
        private readonly ILogger _logger;

        public RuleEvaluator(
            ICategoryEvaluator categoryEvaluator,
            IParameterEvaluator parameterEvaluator,
            ITypeEvaluator typeEvaluator,
            ILogger logger)
        {
            _categoryEvaluator = categoryEvaluator ?? throw new ArgumentNullException(nameof(categoryEvaluator));
            _parameterEvaluator = parameterEvaluator ?? throw new ArgumentNullException(nameof(parameterEvaluator));
            _typeEvaluator = typeEvaluator ?? throw new ArgumentNullException(nameof(typeEvaluator));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Evaluate rules against a single element
        /// </summary>
        public RuleEvaluationResult Evaluate(Element element, int commandId, List<Rule> rules)
        {
            var result = new RuleEvaluationResult();

            if (element == null || rules == null || rules.Count == 0)
                return result;

            try
            {
                var categoryId = element.Category?.Id.GetIdValueAsInt32();

                // Filter applicable rules by category and command
                var applicableRules = rules.Where(r => r.IsApplicable(categoryId, commandId)).ToList();

                _logger?.LogDebug($"Evaluating {applicableRules.Count} applicable rules for element {element.Id} (category: {element.Category?.Name})");

                // Evaluate each applicable rule
                foreach (var rule in applicableRules)
                {
                    if (EvaluateRule(rule, element))
                    {
                        result.MatchedRules.Add(rule);
                        _logger?.LogInfo($"Rule matched: {rule.Name} (Mode: {rule.Mode})");
                    }
                }

                // Resolve conflicts and determine final mode
                ResolveConflicts(result);

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error evaluating rules for element {element.Id}: {ex.Message}", ex);
                return result;
            }
        }

        /// <summary>
        /// Evaluate rules against multiple elements (batch)
        /// </summary>
        public RuleEvaluationResult EvaluateBatch(IEnumerable<Element> elements, int commandId, List<Rule> rules)
        {
            var result = new RuleEvaluationResult();

            if (elements == null || !elements.Any() || rules == null || rules.Count == 0)
                return result;

            try
            {
                var elementList = elements.ToList();
                _logger?.LogDebug($"Batch evaluating rules for {elementList.Count} elements");

                // Collect all matched rules across all elements
                var allMatchedRules = new HashSet<Rule>();

                foreach (var element in elementList)
                {
                    var elementResult = Evaluate(element, commandId, rules);
                    foreach (var matchedRule in elementResult.MatchedRules)
                    {
                        allMatchedRules.Add(matchedRule);
                    }
                }

                result.MatchedRules = allMatchedRules.ToList();

                // Resolve conflicts
                ResolveConflicts(result);

                _logger?.LogInfo($"Batch evaluation complete: {result.MatchedRules.Count} rules matched");

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in batch rule evaluation: {ex.Message}", ex);
                return result;
            }
        }

        /// <summary>
        /// Evaluate a single rule against an element
        /// </summary>
        private bool EvaluateRule(Rule rule, Element element)
        {
            try
            {
                // 1. Category check (already done in IsApplicable, but double-check)
                if (!_categoryEvaluator.Matches(rule, element))
                    return false;

                // 2. Type check (if specified)
                if (!_typeEvaluator.Matches(rule, element))
                    return false;

                // 3. Parameter checks (all must match)
                if (!_parameterEvaluator.Matches(rule, element))
                    return false;

                // All conditions matched
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error evaluating rule {rule.RuleId}: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Resolve conflicts using "All Rules Apply" strategy
        /// Most restrictive mode wins: Prevent > Guide > Monitor
        /// </summary>
        private void ResolveConflicts(RuleEvaluationResult result)
        {
            if (result.MatchedRules.Count == 0)
            {
                result.FinalMode = ProtectionMode.Notify;
                return;
            }

            // Sort by mode (Prevent=3, Guide=2, Monitor=1)
            // Most restrictive wins
            result.FinalMode = result.MatchedRules
                .Select(r => r.Mode)
                .OrderByDescending(m => (int)m)
                .First();

            // Combine messages from all matching rules
            var messages = result.MatchedRules
                .Where(r => !string.IsNullOrEmpty(r.Message))
                .Select(r => $"• {r.Message}")
                .ToList();

            if (messages.Any())
            {
                result.CombinedMessage = string.Join(Environment.NewLine, messages);
            }

            // Aggregate screenshot settings (capture if ANY rule requires it)
            result.CaptureBeforeScreenshot = result.MatchedRules.Any(r => r.CaptureBeforeScreenshot);
            result.CaptureAfterScreenshot = result.MatchedRules.Any(r => r.CaptureAfterScreenshot);

            // Require comment if ANY rule requires it
            result.RequireComment = result.MatchedRules.Any(r => r.RequireComment);

            _logger?.LogDebug($"Conflict resolution: {result.MatchedRules.Count} rules → Final mode: {result.FinalMode}");
        }
    }

    /// <summary>
    /// Category-based rule matching
    /// </summary>
    public interface ICategoryEvaluator
    {
        bool Matches(Rule rule, Element element);
    }

    public class CategoryEvaluator : ICategoryEvaluator
    {
        private readonly ILogger _logger;

        public CategoryEvaluator(ILogger logger)
        {
            _logger = logger;
        }

        public bool Matches(Rule rule, Element element)
        {
            try
            {
                // True wildcard: no category specified at all
                if (!rule.CategoryId.HasValue &&
                    string.IsNullOrEmpty(rule.CategoryName) &&
                    string.IsNullOrEmpty(rule.CategoryCode))
                    return true;

                // Use stored CategoryId or resolve at runtime from the element's document.
                // Runtime resolution handles cases where CategoryCode (e.g. "OST_ImportedCategories")
                // failed to parse at save-time (older SDK / enum not yet defined) or where the
                // category_cache only stored the display name without a code.
                var resolvedId = rule.CategoryId
                    ?? TryResolveCategoryId(rule.CategoryCode, rule.CategoryName, element.Document);

                if (resolvedId.HasValue)
                {
                    var elementCategoryId = element.Category?.Id.GetIdValueAsInt32();
                    if (!elementCategoryId.HasValue)
                    {
                        _logger?.LogDebug("Element has no category");
                        return false;
                    }

                    // Direct (leaf) category match
                    if (resolvedId.Value == elementCategoryId.Value)
                        return true;

                    // Walk the full parent chain.
                    // Imported DWG/DXF elements have a dynamic file-specific leaf category
                    // (e.g. "Drawing1.dwg") whose parent is OST_ImportedCategories, and layer
                    // sub-elements sit one level deeper. Walking up catches both cases.
                    var parent = element.Category?.Parent;
                    while (parent != null)
                    {
                        if (resolvedId.Value == parent.Id.GetIdValueAsInt32())
                            return true;
                        parent = parent.Parent;
                    }

                    return false;
                }

                // ID resolution failed (OST_ImportedCategories not in this SDK version's
                // BuiltInCategory enum, and not found in doc.Settings.Categories).
                // Fall back to two further tiers:

                // Tier 3: Walk the element's category chain by NAME.
                // For an ImportInstance, element.Category.Name = "Drawing1.dwg" and
                // element.Category.Parent.Name = "Imported Categories", which matches the
                // rule's displayName via the StartsWith check in NameMatches.
                var cat = element.Category;
                while (cat != null)
                {
                    if (NameMatches(cat.Name, rule.CategoryCode, rule.CategoryName))
                        return true;
                    cat = cat.Parent;
                }

                // Tier 4: Element-type check for import-related category codes.
                // OST_ImportedCategories covers imported DWG/DXF files — all of which are
                // ImportInstance elements. When the category ID and name chain both fail to
                // resolve (older Revit SDK), match by element type as a cross-version fallback.
                if (!string.IsNullOrEmpty(rule.CategoryCode) &&
                    rule.CategoryCode.IndexOf("Import", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    element is ImportInstance)
                    return true;

                _logger?.LogDebug($"Category '{rule.CategoryName ?? rule.CategoryCode}' could not be resolved for element {element.Id}");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in category matching: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Attempts to resolve a category ID from a code or display name.
        /// 1. Case-insensitive BuiltInCategory enum parse (covers OST_ImportedCategories etc.)
        /// 2. Scan doc.Settings.Categories by display name (covers categories whose enum name
        ///    doesn't exist in the compiled SDK version).
        /// </summary>
        private int? TryResolveCategoryId(string? categoryCode, string? categoryName, Document doc)
        {
            // Try case-insensitive enum parse on the OST_ code
            if (!string.IsNullOrEmpty(categoryCode) &&
                Enum.TryParse<BuiltInCategory>(categoryCode, ignoreCase: true, out var bic))
                return (int)bic;

            // Scan document categories by display name
            if (doc != null && (!string.IsNullOrEmpty(categoryCode) || !string.IsNullOrEmpty(categoryName)))
            {
                try
                {
                    foreach (Category cat in doc.Settings.Categories)
                    {
                        if (NameMatches(cat.Name, categoryCode, categoryName))
                            return cat.Id.GetIdValueAsInt32();
                    }
                }
                catch { }
            }

            return null;
        }

        private static bool NameMatches(string catName, string? code, string? displayName)
        {
            if (string.IsNullOrEmpty(catName)) return false;
            if (!string.IsNullOrEmpty(code) &&
                string.Equals(catName, code, StringComparison.OrdinalIgnoreCase))
                return true;
            if (!string.IsNullOrEmpty(displayName) &&
                (string.Equals(catName, displayName, StringComparison.OrdinalIgnoreCase) ||
                 displayName.StartsWith(catName, StringComparison.OrdinalIgnoreCase) ||
                 catName.StartsWith(displayName, StringComparison.OrdinalIgnoreCase)))
                return true;
            return false;
        }

        /// <summary>
        /// Testable overload that accepts a pre-extracted leaf category ID.
        /// Does not walk the parent chain or do runtime resolution — use the Element overload for full matching.
        /// </summary>
        public bool Matches(Rule rule, int? elementCategoryId)
        {
            // True wildcard: no category specified at all
            if (!rule.CategoryId.HasValue &&
                string.IsNullOrEmpty(rule.CategoryName) &&
                string.IsNullOrEmpty(rule.CategoryCode))
                return true;

            if (!rule.CategoryId.HasValue)
                return false;

            if (!elementCategoryId.HasValue)
            {
                _logger?.LogDebug("Element has no category");
                return false;
            }

            return rule.CategoryId.Value == elementCategoryId.Value;
        }
    }

    /// <summary>
    /// Parameter-based rule matching
    /// </summary>
    public interface IParameterEvaluator
    {
        bool Matches(Rule rule, Element element);
    }

    public class ParameterEvaluator : IParameterEvaluator
    {
        private readonly ILogger _logger;

        public ParameterEvaluator(ILogger logger)
        {
            _logger = logger;
        }

        public bool Matches(Rule rule, Element element)
        {
            try
            {
                // If no parameters specified, matches
                if ((rule.Parameters == null || rule.Parameters.Count == 0) &&
                    (rule.BuiltInParameters == null || rule.BuiltInParameters.Count == 0))
                    return true;

                // All string-based parameter conditions must match
                if (rule.Parameters != null)
                {
                    foreach (var paramCondition in rule.Parameters)
                    {
                        if (!EvaluateParameterByName(element, paramCondition.Key, paramCondition.Value))
                            return false;
                    }
                }

                // All BuiltInParameter conditions must match
                if (rule.BuiltInParameters != null)
                {
                    foreach (var paramCondition in rule.BuiltInParameters)
                    {
                        if (!EvaluateBuiltInParameter(element, paramCondition.Key, paramCondition.Value))
                            return false;
                    }
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in parameter matching: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Evaluate parameter by string name (for project parameters, shared parameters)
        /// </summary>
        private bool EvaluateParameterByName(Element element, string parameterName, RuleCondition condition)
        {
            try
            {
                // Try to get parameter by name
                var parameter = element.LookupParameter(parameterName);

                if (parameter == null)
                {
                    _logger?.LogDebug($"Parameter '{parameterName}' not found on element {element.Id}");
                    return condition.Operator == ComparisonOperator.IsEmpty;
                }

                // Evaluate based on storage type
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        var stringValue = parameter.AsString();
                        return condition.Evaluate(stringValue);

                    case StorageType.Integer:
                        var intValue = parameter.AsInteger();
                        return condition.Evaluate(intValue);

                    case StorageType.Double:
                        var doubleValue = parameter.AsDouble();
                        return condition.Evaluate(doubleValue);

                    case StorageType.ElementId:
                        var elemIdValue = parameter.AsElementId();
                        var elemIdString = elemIdValue?.GetIdValue().ToString() ?? "";
                        return condition.Evaluate(elemIdString);

                    default:
                        _logger?.LogWarning($"Unsupported parameter storage type: {parameter.StorageType}");
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error evaluating parameter '{parameterName}': {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Evaluate parameter by BuiltInParameter enum (type-safe, language-independent)
        /// Preferred for built-in Revit parameters
        /// </summary>
        private bool EvaluateBuiltInParameter(Element element, BuiltInParameter builtInParam, RuleCondition condition)
        {
            try
            {
                // Try to get parameter by BuiltInParameter enum
                var parameter = element.get_Parameter(builtInParam);

                if (parameter == null || !parameter.HasValue)
                {
                    _logger?.LogDebug($"BuiltInParameter '{builtInParam}' not found or empty on element {element.Id}");
                    return condition.Operator == ComparisonOperator.IsEmpty;
                }

                // Evaluate based on storage type
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        var stringValue = parameter.AsString();
                        return condition.Evaluate(stringValue);

                    case StorageType.Integer:
                        var intValue = parameter.AsInteger();
                        return condition.Evaluate(intValue);

                    case StorageType.Double:
                        var doubleValue = parameter.AsDouble();
                        return condition.Evaluate(doubleValue);

                    case StorageType.ElementId:
                        var elemIdValue = parameter.AsElementId();
                        var elemIdString = elemIdValue?.GetIdValue().ToString() ?? "";
                        return condition.Evaluate(elemIdString);

                    default:
                        _logger?.LogWarning($"Unsupported parameter storage type: {parameter.StorageType}");
                        return false;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error evaluating BuiltInParameter '{builtInParam}': {ex.Message}", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Type-based rule matching
    /// </summary>
    public interface ITypeEvaluator
    {
        bool Matches(Rule rule, Element element);
    }

    public class TypeEvaluator : ITypeEvaluator
    {
        private readonly ILogger _logger;

        public TypeEvaluator(ILogger logger)
        {
            _logger = logger;
        }

        public bool Matches(Rule rule, Element element)
        {
            try
            {
                // Check type name
                if (!string.IsNullOrEmpty(rule.TypeName))
                {
                    if (!MatchesTypeName(element, rule.TypeName))
                        return false;
                }

                // Check family name
                if (!string.IsNullOrEmpty(rule.FamilyName))
                {
                    if (!MatchesFamilyName(element, rule.FamilyName))
                        return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error in type matching: {ex.Message}", ex);
                return false;
            }
        }

        private bool MatchesTypeName(Element element, string targetTypeName)
        {
            try
            {
                var typeId = element.GetTypeId();
                if (typeId == ElementId.InvalidElementId)
                    return false;

                var doc = element.Document;
                var elementType = doc.GetElement(typeId) as ElementType;

                if (elementType == null)
                    return false;

                var typeName = elementType.Name;
                return string.Equals(typeName, targetTypeName, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error matching type name: {ex.Message}", ex);
                return false;
            }
        }

        private bool MatchesFamilyName(Element element, string targetFamilyName)
        {
            try
            {
                if (element is FamilyInstance familyInstance)
                {
                    var familyName = familyInstance.Symbol?.Family?.Name;
                    if (familyName != null)
                    {
                        return string.Equals(familyName, targetFamilyName, StringComparison.OrdinalIgnoreCase);
                    }
                }

                // For element types, get family directly
                if (element is ElementType elementType && elementType.FamilyName != null)
                {
                    return string.Equals(elementType.FamilyName, targetFamilyName, StringComparison.OrdinalIgnoreCase);
                }

                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error matching family name: {ex.Message}", ex);
                return false;
            }
        }
    }
}