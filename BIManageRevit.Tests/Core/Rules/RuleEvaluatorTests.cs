using System;
using System.Collections.Generic;
using System.Linq;
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
    /// Integration tests for the main RuleEvaluator coordinating all specialized evaluators
    /// </summary>
    public class RuleEvaluatorTests
    {
        private readonly Mock<ILogger> _mockLogger;
        private readonly RuleEvaluator _evaluator;

        public RuleEvaluatorTests()
        {
            _mockLogger = new Mock<ILogger>();
            
            var categoryEvaluator = new CategoryEvaluator(_mockLogger.Object);
            var parameterEvaluator = new ParameterEvaluator(_mockLogger.Object);
            var typeEvaluator = new TypeEvaluator(_mockLogger.Object);

            _evaluator = new RuleEvaluator(
                categoryEvaluator,
                parameterEvaluator,
                typeEvaluator,
                _mockLogger.Object);
        }

        [Fact]
        public void Evaluate_NullElement_ReturnsEmptyResult()
        {
            // Arrange
            var rules = new List<Rule>
            {
                new Rule { RuleId = "test", Mode = ProtectionMode.Notify }
            };

            // Act
            var result = _evaluator.Evaluate(null, 32778, rules);

            // Assert
            result.Should().NotBeNull();
            result.MatchedRules.Should().BeEmpty();
        }

        [Fact]
        public void Evaluate_EmptyRulesList_ReturnsEmptyResult()
        {
            // Arrange
            var mockElement = CreateMockWallElement();
            var rules = new List<Rule>();

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.Should().NotBeNull();
            result.MatchedRules.Should().BeEmpty();
        }

        [Fact]
        public void Evaluate_RuleMatchesAllConditions_ReturnsMatchedRule()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "wall-demo-delete",
                Name = "Demo Wall Delete Protection",
                Mode = ProtectionMode.Protect,
                CategoryId = -2000011, // Walls
                CommandIds = new List<int> { 32778 }, // Delete
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Mark"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.Contains,
                        Value = "DEMO",
                        IgnoreCase = true
                    }
                },
                Message = "Cannot delete demo walls"
            };

            var mockElement = CreateMockWallElement("DEMO-123");
            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.MatchedRules.Should().ContainSingle();
            result.MatchedRules.First().RuleId.Should().Be("wall-demo-delete");
            result.FinalMode.Should().Be(ProtectionMode.Protect);
        }

        [Fact]
        public void Evaluate_CategoryDoesNotMatch_NoRulesMatched()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "wall-rule",
                CategoryId = -2000011, // Walls
                CommandIds = new List<int> { 32778 }
            };

            var mockElement = CreateMockDoorElement(); // Different category
            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.MatchedRules.Should().BeEmpty();
        }

        [Fact]
        public void Evaluate_CommandDoesNotMatch_NoRulesMatched()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "delete-rule",
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 } // Delete only
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32779, rules); // Move command

            // Assert
            result.MatchedRules.Should().BeEmpty();
        }

        [Fact]
        public void Evaluate_ParameterConditionFails_NoRulesMatched()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "demo-rule",
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Mark"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.Contains,
                        Value = "DEMO"
                    }
                }
            };

            var mockElement = CreateMockWallElement("PROD-123"); // Not DEMO
            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.MatchedRules.Should().BeEmpty();
        }

        [Fact]
        public void Evaluate_MultipleRules_AllMatchingRulesReturned()
        {
            // Arrange
            var rule1 = new Rule
            {
                RuleId = "rule1",
                Name = "Monitor All Deletes",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var rule2 = new Rule
            {
                RuleId = "rule2",
                Name = "Guide Demo Elements",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Mark"] = new RuleCondition { Operator = ComparisonOperator.Contains, Value = "DEMO" }
                }
            };

            var rule3 = new Rule
            {
                RuleId = "rule3",
                Name = "Prevent Structural Walls",
                Mode = ProtectionMode.Protect,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                BuiltInParameters = new Dictionary<BuiltInParameter, RuleCondition>
                {
                    [BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT] = new RuleCondition
                    {
                        Operator = ComparisonOperator.Equals,
                        Value = "1"
                    }
                }
            };

            var mockElement = CreateMockStructuralWallElement("DEMO-STRUCT-01");
            var rules = new List<Rule> { rule1, rule2, rule3 };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.MatchedRules.Should().HaveCount(3, "All three rules should match");
            result.FinalMode.Should().Be(ProtectionMode.Protect, "Most restrictive mode should win");
        }

        [Fact]
        public void Evaluate_WithFamilyAndTypeFilters_MatchesCorrectly()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "specific-wall-type",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                FamilyName = "Basic Wall",
                TypeName = "Generic - 200mm"
            };

            var mockElement = CreateMockWallElement("TEST", "Basic Wall", "Generic - 200mm");
            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.MatchedRules.Should().ContainSingle();
            result.MatchedRules.First().RuleId.Should().Be("specific-wall-type");
        }

        [Fact]
        public void Evaluate_ExceptionInRuleEvaluation_ContinuesWithOtherRules()
        {
            // Arrange
            var goodRule = new Rule
            {
                RuleId = "good-rule",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var badRule = new Rule
            {
                RuleId = "bad-rule",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var mockElement = CreateMockWallElement();
            // Force exception for parameter lookup (will be caught in parameter evaluator)
            var rules = new List<Rule> { goodRule, badRule };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.MatchedRules.Should().Contain(r => r.RuleId == "good-rule",
                "Good rule should still be evaluated despite exception in another rule");
        }

        [Fact]
        public void Evaluate_LogsApplicableRulesCount()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule };

            // Act
            _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            _mockLogger.Verify(
                l => l.LogDebug(It.Is<string>(s => s.Contains("applicable rules"))),
                Times.AtLeastOnce);
        }

        [Fact]
        public void Evaluate_LogsMatchedRules()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Name = "Test Rule",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule };

            // Act
            _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            _mockLogger.Verify(
                l => l.LogInfo(It.Is<string>(s => s.Contains("Rule matched"))),
                Times.AtLeastOnce);
        }

        #region Helper Methods

        private Mock<Element> CreateMockWallElement(
            string mark = "TEST",
            string familyName = "Basic Wall",
            string typeName = "Generic - 200mm")
        {
            var mockElement = new Mock<Element>();
            var mockCategory = new Mock<Category>();
            mockCategory.Setup(c => c.Id).Returns(new ElementId(-2000011)); // Walls
            mockCategory.Setup(c => c.Id.IntegerValue).Returns(-2000011);
            mockCategory.Setup(c => c.Name).Returns("Walls");

            mockElement.Setup(e => e.Category).Returns(mockCategory.Object);
            mockElement.Setup(e => e.Id).Returns(new ElementId(12345));

            // Setup Mark parameter
            var mockMarkParam = new Mock<Parameter>();
            mockMarkParam.Setup(p => p.HasValue).Returns(!string.IsNullOrEmpty(mark));
            mockMarkParam.Setup(p => p.StorageType).Returns(StorageType.String);
            mockMarkParam.Setup(p => p.AsString()).Returns(mark);
            mockElement.Setup(e => e.LookupParameter("Mark")).Returns(mockMarkParam.Object);

            // Setup ElementType
            var mockElementType = new Mock<ElementType>();
            mockElementType.Setup(et => et.FamilyName).Returns(familyName);
            mockElementType.Setup(et => et.Name).Returns(typeName);

            var typeId = new ElementId(54321);
            mockElement.Setup(e => e.GetTypeId()).Returns(typeId);

            var mockDoc = new Mock<Document>();
            mockDoc.Setup(d => d.GetElement(typeId)).Returns(mockElementType.Object);
            mockElement.Setup(e => e.Document).Returns(mockDoc.Object);

            return mockElement;
        }

        private Mock<Element> CreateMockStructuralWallElement(string mark = "TEST")
        {
            var mockElement = CreateMockWallElement(mark);

            // Add WALL_STRUCTURAL_SIGNIFICANT parameter
            var mockStructParam = new Mock<Parameter>();
            mockStructParam.Setup(p => p.HasValue).Returns(true);
            mockStructParam.Setup(p => p.StorageType).Returns(StorageType.Integer);
            mockStructParam.Setup(p => p.AsInteger()).Returns(1);
            mockStructParam.Setup(p => p.AsValueString()).Returns("1");

            mockElement.Setup(e => e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT))
                .Returns(mockStructParam.Object);

            return mockElement;
        }

        private Mock<Element> CreateMockDoorElement()
        {
            var mockElement = new Mock<Element>();
            var mockCategory = new Mock<Category>();
            mockCategory.Setup(c => c.Id).Returns(new ElementId(-2000023)); // Doors
            mockCategory.Setup(c => c.Id.IntegerValue).Returns(-2000023);
            mockCategory.Setup(c => c.Name).Returns("Doors");

            mockElement.Setup(e => e.Category).Returns(mockCategory.Object);
            mockElement.Setup(e => e.Id).Returns(new ElementId(67890));

            return mockElement;
        }

        #endregion
    }
}
