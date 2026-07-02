using System;
using Autodesk.Revit.DB;
using BIManage.Core.Rules.Evaluation;
using BIManage.Core.Rules.Models;
using BIManage.Infrastructure.Logging;
using FluentAssertions;
using Moq;
using Xunit;

namespace BIManageRevit.Tests.Core.Rules
{
    /// <summary>
    /// Tests for category-based rule evaluation.
    /// Uses the int? overload to avoid mocking non-virtual Revit properties (Category.Id).
    /// </summary>
    public class CategoryEvaluatorTests
    {
        private readonly Mock<ILogger> _mockLogger;
        private readonly CategoryEvaluator _evaluator;

        public CategoryEvaluatorTests()
        {
            _mockLogger = new Mock<ILogger>();
            _evaluator = new CategoryEvaluator(_mockLogger.Object);
        }

        [Fact]
        public void Matches_RuleWithNullCategory_MatchesAnyElement()
        {
            var rule = new Rule { RuleId = "test", CategoryId = null };
            _evaluator.Matches(rule, (int?)-2000011).Should().BeTrue();
        }

        [Fact]
        public void Matches_RuleWithMatchingCategory_ReturnsTrue()
        {
            var rule = new Rule { RuleId = "test", CategoryId = -2000011 }; // Walls
            _evaluator.Matches(rule, (int?)-2000011).Should().BeTrue();
        }

        [Fact]
        public void Matches_RuleWithNonMatchingCategory_ReturnsFalse()
        {
            var rule = new Rule { RuleId = "test", CategoryId = -2000011 }; // Walls
            _evaluator.Matches(rule, (int?)-2000032).Should().BeFalse(); // Doors
        }

        [Fact]
        public void Matches_ElementWithNullCategory_ReturnsFalse()
        {
            var rule = new Rule { RuleId = "test", CategoryId = -2000011 };
            _evaluator.Matches(rule, (int?)null).Should().BeFalse();
        }

        [Fact]
        public void Matches_CommonBuiltInCategories_WorkCorrectly()
        {
            var testCases = new[]
            {
                new { CategoryId = (int)BuiltInCategory.OST_Walls, Name = "Walls" },
                new { CategoryId = (int)BuiltInCategory.OST_Doors, Name = "Doors" },
                new { CategoryId = (int)BuiltInCategory.OST_Windows, Name = "Windows" },
                new { CategoryId = (int)BuiltInCategory.OST_Floors, Name = "Floors" },
                new { CategoryId = (int)BuiltInCategory.OST_Roofs, Name = "Roofs" },
                new { CategoryId = (int)BuiltInCategory.OST_StructuralColumns, Name = "Structural Columns" }
            };

            foreach (var tc in testCases)
            {
                var rule = new Rule { RuleId = $"test-{tc.Name}", CategoryId = tc.CategoryId };
                _evaluator.Matches(rule, (int?)tc.CategoryId).Should().BeTrue($"{tc.Name} should match");
            }
        }

        // ExceptionDuringEvaluation test removed — Element.Category is non-virtual in Revit 2025+,
        // cannot be mocked. The catch block in the Element overload is trivial error handling.

        [Fact]
        public void Matches_MultipleRulesSameElement_IndependentEvaluations()
        {
            var rule1 = new Rule { RuleId = "test1", CategoryId = -2000011 }; // Walls
            var rule2 = new Rule { RuleId = "test2", CategoryId = -2000032 }; // Doors
            var rule3 = new Rule { RuleId = "test3", CategoryId = null };     // All

            int? wallCategoryId = -2000011;

            _evaluator.Matches(rule1, wallCategoryId).Should().BeTrue("Wall matches Wall rule");
            _evaluator.Matches(rule2, wallCategoryId).Should().BeFalse("Wall doesn't match Door rule");
            _evaluator.Matches(rule3, wallCategoryId).Should().BeTrue("Null category matches all");
        }
    }
}
