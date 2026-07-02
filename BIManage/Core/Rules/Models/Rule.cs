using System;
using System.Collections.Generic;

namespace BIManage.Core.Rules.Models
{
    /// <summary>
    /// Represents a rule that governs element protection and command interception
    /// </summary>
    public class Rule
    {
        /// <summary>
        /// Unique identifier for the rule
        /// </summary>
        public string RuleId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>
        /// Human-readable name for the rule
        /// </summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>
        /// Description explaining what the rule does
        /// </summary>
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Protection mode: Monitor, Guide, or Prevent
        /// </summary>
        public ProtectionMode Mode { get; set; } = ProtectionMode.Notify;

        /// <summary>
        /// Priority for conflict resolution (higher = evaluated first)
        /// Auto-incremented starting from 1
        /// </summary>
        public int Priority { get; set; } = 1;

        /// <summary>
        /// Whether this rule is currently active
        /// </summary>
        public bool IsEnabled { get; set; } = true;

        /// <summary>
        /// Project ID this rule applies to (null = all projects)
        /// </summary>
        public string? ProjectId { get; set; }

        /// <summary>
        /// Company ID this rule applies to (null = all companies)
        /// </summary>
        public string? CompanyId { get; set; }

        /// <summary>
        /// Category to match (BuiltInCategory integer value)
        /// Null = match all categories
        /// </summary>
        public int? CategoryId { get; set; }

        /// <summary>
        /// Category name for display purposes
        /// </summary>
        public string? CategoryName { get; set; }

        /// <summary>
        /// Type name to match (optional)
        /// Example: "Basic Wall", "M_Door-Single-Flush"
        /// </summary>
        public string? TypeName { get; set; }

        /// <summary>
        /// Family name to match (optional)
        /// Example: "Basic Wall", "M_Door"
        /// </summary>
        public string? FamilyName { get; set; }

        /// <summary>
        /// Parameter conditions that must match (by string name)
        /// Key = parameter name, Value = RuleCondition
        /// Use for project parameters, shared parameters, or parameters not in BuiltInParameter enum
        /// </summary>
        public Dictionary<string, RuleCondition> Parameters { get; set; } = new();

        /// <summary>
        /// Built-in parameter conditions that must match (by BuiltInParameter enum)
        /// Key = BuiltInParameter enum, Value = RuleCondition
        /// Preferred for built-in Revit parameters (type-safe, language-independent)
        /// </summary>
        public Dictionary<BuiltInParameter, RuleCondition> BuiltInParameters { get; set; } = new();

        /// <summary>
        /// Postable command IDs this rule applies to
        /// Example: [32778] for Delete
        /// Empty list = applies to all commands
        /// </summary>
        public List<int> CommandIds { get; set; } = new();

        /// <summary>
        /// Command names for display purposes
        /// Example: ["Delete", "Cut"]
        /// </summary>
        public List<string> CommandNames { get; set; } = new();

        /// <summary>
        /// Message to display when rule is triggered
        /// </summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>
        /// Whether to capture before screenshot
        /// </summary>
        public bool CaptureBeforeScreenshot { get; set; } = false;

        /// <summary>
        /// Whether to capture after screenshot
        /// </summary>
        public bool CaptureAfterScreenshot { get; set; } = false;

        /// <summary>
        /// Whether to require user comment when rule is triggered
        /// </summary>
        public bool RequireComment { get; set; } = false;

        /// <summary>
        /// Whether admins can override this rule
        /// </summary>
        public bool AllowAdminOverride { get; set; } = false;

        /// <summary>
        /// Whether to send email notification when rule is triggered
        /// </summary>
        public bool SendEmail { get; set; } = false;

        /// <summary>
        /// Timestamp when rule was created
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Timestamp when rule was last modified
        /// </summary>
        public DateTime ModifiedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// User who created this rule
        /// </summary>
        public string? CreatedBy { get; set; }

        /// <summary>
        /// User who last modified this rule
        /// </summary>
        public string? ModifiedBy { get; set; }

        /// <summary>
        /// Model GUID for model-specific rules (for filtration)
        /// </summary>
        public string? ModelGuid { get; set; }

        /// <summary>
        /// Rule scope: CompanyWide(1), ProjectWide(2), ModelSpecific(3)
        /// </summary>
        public int RuleScope { get; set; } = (int)RuleScopeType.CompanyWide;

        /// <summary>
        /// Category code (e.g., OST_Walls, OST_Doors, OST_StructuralFraming)
        /// </summary>
        public string? CategoryCode { get; set; }

        /// <summary>
        /// Rule version for conflict detection
        /// </summary>
        public int Version { get; set; } = 1;

        /// <summary>
        /// Check if this rule matches the specified element and command
        /// </summary>
        public bool IsApplicable(int? categoryId, int commandId)
        {
            if (!IsEnabled)
                return false;

            // Category matching is fully delegated to CategoryEvaluator which has access to the
            // full Element, parent-category chain, and document context for runtime resolution.

            // Check command match (empty = all commands)
            if (CommandIds.Count > 0)
            {
                if (!CommandIds.Contains(commandId))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// Create a deep copy of this rule
        /// </summary>
        public Rule Clone()
        {
            return new Rule
            {
                RuleId = this.RuleId,
                Name = this.Name,
                Description = this.Description,
                Mode = this.Mode,
                Priority = this.Priority,
                IsEnabled = this.IsEnabled,
                ProjectId = this.ProjectId,
                CompanyId = this.CompanyId,
                CategoryId = this.CategoryId,
                CategoryName = this.CategoryName,
                TypeName = this.TypeName,
                FamilyName = this.FamilyName,
                Parameters = new Dictionary<string, RuleCondition>(this.Parameters),
                BuiltInParameters = new Dictionary<BuiltInParameter, RuleCondition>(this.BuiltInParameters),
                CommandIds = new List<int>(this.CommandIds),
                CommandNames = new List<string>(this.CommandNames),
                Message = this.Message,
                CaptureBeforeScreenshot = this.CaptureBeforeScreenshot,
                CaptureAfterScreenshot = this.CaptureAfterScreenshot,
                RequireComment = this.RequireComment,
                AllowAdminOverride = this.AllowAdminOverride,
                SendEmail = this.SendEmail,
                CreatedAt = this.CreatedAt,
                ModifiedAt = this.ModifiedAt,
                CreatedBy = this.CreatedBy,
                ModifiedBy = this.ModifiedBy,
                ModelGuid = this.ModelGuid,
                RuleScope = this.RuleScope,
                CategoryCode = this.CategoryCode,
                Version = this.Version
            };
        }
    }

    /// <summary>
    /// Represents a condition for parameter matching
    /// </summary>
    public class RuleCondition
    {
        /// <summary>
        /// Expected value to compare against
        /// </summary>
        public string Value { get; set; } = string.Empty;

        /// <summary>
        /// Comparison operator
        /// </summary>
        public ComparisonOperator Operator { get; set; } = ComparisonOperator.Equals;

        /// <summary>
        /// Whether to ignore case in string comparisons
        /// </summary>
        public bool IgnoreCase { get; set; } = true;

        /// <summary>
        /// Evaluate this condition against an actual value
        /// </summary>
        public bool Evaluate(string? actualValue)
        {
            if (actualValue == null)
                return Operator == ComparisonOperator.IsEmpty;

            var compareValue = IgnoreCase ? actualValue.ToLowerInvariant() : actualValue;
            var targetValue = IgnoreCase ? Value.ToLowerInvariant() : Value;

            return Operator switch
            {
                ComparisonOperator.Equals => compareValue == targetValue,
                ComparisonOperator.NotEquals => compareValue != targetValue,
                ComparisonOperator.Contains => compareValue.Contains(targetValue),
                ComparisonOperator.StartsWith => compareValue.StartsWith(targetValue),
                ComparisonOperator.IsEmpty => string.IsNullOrEmpty(actualValue),
                ComparisonOperator.IsNotEmpty => !string.IsNullOrEmpty(actualValue),
                _ => false
            };
        }

        /// <summary>
        /// Evaluate this condition against a numeric value
        /// </summary>
        public bool Evaluate(double actualValue)
        {
            if (!double.TryParse(Value, out double targetValue))
                return false;

            return Operator switch
            {
                ComparisonOperator.Equals => Math.Abs(actualValue - targetValue) < 0.0001,
                ComparisonOperator.NotEquals => Math.Abs(actualValue - targetValue) >= 0.0001,
                ComparisonOperator.GreaterThan => actualValue > targetValue,
                ComparisonOperator.LessThan => actualValue < targetValue,
                _ => false
            };
        }

        /// <summary>
        /// Evaluate this condition against a boolean value
        /// </summary>
        public bool Evaluate(bool actualValue)
        {
            if (!bool.TryParse(Value, out bool targetValue))
                return false;

            return Operator switch
            {
                ComparisonOperator.Equals => actualValue == targetValue,
                ComparisonOperator.NotEquals => actualValue != targetValue,
                _ => false
            };
        }
    }

    /// <summary>
    /// Rule scope levels for rule applicability
    /// </summary>
    public enum RuleScopeType
    {
        /// <summary>
        /// Rule applies company-wide (all projects)
        /// </summary>
        CompanyWide = 1,

        /// <summary>
        /// Rule applies to a specific project
        /// </summary>
        ProjectWide = 2,

        /// <summary>
        /// Rule applies to a specific model file
        /// </summary>
        ModelSpecific = 3
    }

    /// <summary>
    /// Protection modes for rule enforcement
    /// </summary>
    public enum ProtectionMode
    {
        /// <summary>
        /// Passive tracking - no user intervention, capture evidence only
        /// </summary>
        Notify = 1,

        /// <summary>
        /// Show guidance dialog - user can proceed or cancel
        /// </summary>
        Assist = 2,

        /// <summary>
        /// Block action - require admin override
        /// </summary>
        Protect = 3
    }

    /// <summary>
    /// Comparison operators for parameter matching
    /// </summary>
    public enum ComparisonOperator
    {
        /// <summary>
        /// Value equals target (==)
        /// </summary>
        Equals,

        /// <summary>
        /// Value does not equal target (!=)
        /// </summary>
        NotEquals,

        /// <summary>
        /// String contains target (substring match)
        /// </summary>
        Contains,

        /// <summary>
        /// String starts with target
        /// </summary>
        StartsWith,

        /// <summary>
        /// Numeric value greater than target (>)
        /// </summary>
        GreaterThan,

        /// <summary>
        /// Numeric value less than target (<)
        /// </summary>
        LessThan,

        /// <summary>
        /// Value is null or empty string
        /// </summary>
        IsEmpty,

        /// <summary>
        /// Value is not null and not empty string
        /// </summary>
        IsNotEmpty
    }

    /// <summary>
    /// Represents a conflict between rules
    /// </summary>
    public class RuleConflict
    {
        public int Id { get; set; }
        public string RuleId1 { get; set; } = string.Empty;
        public string RuleId2 { get; set; } = string.Empty;
        public string ConflictType { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTime DetectedAt { get; set; } = DateTime.UtcNow;
        public bool Resolved { get; set; }
        public string WinningRuleId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Result of rule evaluation
    /// </summary>
    public class RuleEvaluationResult
    {
        /// <summary>
        /// Empty result when no rules match
        /// </summary>
        public static RuleEvaluationResult Empty => new RuleEvaluationResult();

        /// <summary>
        /// Rules that matched the element/command
        /// </summary>
        public List<Rule> MatchedRules { get; set; } = new();

        /// <summary>
        /// Final protection mode after conflict resolution
        /// Protect > Assist > Notify
        /// </summary>
        public ProtectionMode FinalMode { get; set; } = ProtectionMode.Notify;

        /// <summary>
        /// Whether any rule matched
        /// </summary>
        public bool HasMatch => MatchedRules.Count > 0;

        /// <summary>
        /// Whether action should be blocked
        /// </summary>
        public bool ShouldBlock => FinalMode == ProtectionMode.Protect;

        /// <summary>
        /// Whether guidance dialog should be shown
        /// </summary>
        public bool ShouldGuide => FinalMode == ProtectionMode.Assist;

        /// <summary>
        /// Combined message from all matching rules
        /// </summary>
        public string CombinedMessage { get; set; } = string.Empty;

        /// <summary>
        /// Whether to capture before screenshot
        /// </summary>
        public bool CaptureBeforeScreenshot { get; set; } = false;

        /// <summary>
        /// Whether to capture after screenshot
        /// </summary>
        public bool CaptureAfterScreenshot { get; set; } = false;

        /// <summary>
        /// Whether to require user comment
        /// </summary>
        public bool RequireComment { get; set; } = false;
    }
}