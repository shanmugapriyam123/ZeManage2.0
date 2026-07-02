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
    /// Tests for batch evaluation of multiple elements
    /// </summary>
    public class BatchEvaluationTests
    {
        private readonly Mock<ILogger> _mockLogger;
        private readonly RuleEvaluator _evaluator;

        public BatchEvaluationTests()
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
        public void EvaluateBatch_EmptyElementList_ReturnsEmptyResult()
        {
            // Arrange
            var elements = new List<Element>();
            var rules = new List<Rule>
            {
                new Rule { RuleId = "test", Mode = ProtectionMode.Notify, CategoryId = -2000011, CommandIds = new List<int> { 32778 } }
            };

            // Act
            var result = _evaluator.EvaluateBatch(elements, 32778, rules);

            // Assert
            result.Should().NotBeNull();
            result.MatchedRules.Should().BeEmpty();
        }

        [Fact]
        public void EvaluateBatch_NullElementList_ReturnsEmptyResult()
        {
            // Arrange
            var rules = new List<Rule>
            {
                new Rule { RuleId = "test", Mode = ProtectionMode.Notify, CategoryId = -2000011, CommandIds = new List<int> { 32778 } }
            };

            // Act
            var result = _evaluator.EvaluateBatch(null, 32778, rules);

            // Assert
            result.Should().NotBeNull();
            result.MatchedRules.Should().BeEmpty();
        }

        [Fact]
        public void EvaluateBatch_EmptyRulesList_ReturnsEmptyResult()
        {
            // Arrange
            var elements = CreateMockWallElements(5);
            var rules = new List<Rule>();

            // Act
            var result = _evaluator.EvaluateBatch(elements.Select(m => m.Object), 32778, rules);

            // Assert
            result.Should().NotBeNull();
            result.MatchedRules.Should().BeEmpty();
        }

        [Fact]
        public void EvaluateBatch_SingleElement_SingleRule_Match()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "wall-delete",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var elements = CreateMockWallElements(1);
            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.EvaluateBatch(elements.Select(m => m.Object), 32778, rules);

            // Assert
            result.MatchedRules.Should().ContainSingle();
            result.MatchedRules.First().RuleId.Should().Be("wall-delete");
        }

        [Fact]
        public void EvaluateBatch_MultipleElements_SameRule_CollectsUnique()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "wall-delete",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var elements = CreateMockWallElements(100); // 100 walls
            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.EvaluateBatch(elements.Select(m => m.Object), 32778, rules);

            // Assert
            result.MatchedRules.Should().ContainSingle("Same rule matched across all elements");
            result.MatchedRules.First().RuleId.Should().Be("wall-delete");
        }

        [Fact]
        public void EvaluateBatch_MultipleElements_MultipleRules_CollectsAllUniqueMatches()
        {
            // Arrange
            var rule1 = new Rule
            {
                RuleId = "monitor-all",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var rule2 = new Rule
            {
                RuleId = "guide-demo",
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
                RuleId = "prevent-structural",
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

            // Create mixed elements: some match rule1 only, some match rule1+rule2, some match all 3
            var elements = new List<Mock<Element>>
            {
                CreateMockWallElement("PROD-01", false),     // Matches rule1 only
                CreateMockWallElement("DEMO-01", false),     // Matches rule1, rule2
                CreateMockWallElement("DEMO-02", true),      // Matches all 3 rules
                CreateMockWallElement("PROD-02", false),     // Matches rule1 only
                CreateMockWallElement("DEMO-STRUCT", true)   // Matches all 3 rules
            };

            var rules = new List<Rule> { rule1, rule2, rule3 };

            // Act
            var result = _evaluator.EvaluateBatch(elements.Select(m => m.Object), 32778, rules);

            // Assert
            result.MatchedRules.Should().HaveCount(3, "All 3 unique rules should be collected");
            result.MatchedRules.Should().Contain(r => r.RuleId == "monitor-all");
            result.MatchedRules.Should().Contain(r => r.RuleId == "guide-demo");
            result.MatchedRules.Should().Contain(r => r.RuleId == "prevent-structural");
        }

        [Fact]
        public void EvaluateBatch_MixedCategories_FiltersCorrectly()
        {
            // Arrange
            var wallRule = new Rule
            {
                RuleId = "wall-rule",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011, // Walls
                CommandIds = new List<int> { 32778 }
            };

            var doorRule = new Rule
            {
                RuleId = "door-rule",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000023, // Doors
                CommandIds = new List<int> { 32778 }
            };

            var elements = new List<Mock<Element>>();
            elements.AddRange(CreateMockWallElements(3));  // 3 walls
            elements.AddRange(CreateMockDoorElements(2));  // 2 doors

            var rules = new List<Rule> { wallRule, doorRule };

            // Act
            var result = _evaluator.EvaluateBatch(elements.Select(m => m.Object), 32778, rules);

            // Assert
            result.MatchedRules.Should().HaveCount(2, "Both wall and door rules should match");
            result.MatchedRules.Should().Contain(r => r.RuleId == "wall-rule");
            result.MatchedRules.Should().Contain(r => r.RuleId == "door-rule");
        }

        [Fact]
        public void EvaluateBatch_NoElementsMatch_ReturnsEmptyMatchedRules()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "door-rule",
                CategoryId = -2000023, // Doors only
                CommandIds = new List<int> { 32778 }
            };

            var elements = CreateMockWallElements(5); // All walls
            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.EvaluateBatch(elements.Select(m => m.Object), 32778, rules);

            // Assert
            result.MatchedRules.Should().BeEmpty("No walls should match door rule");
            result.FinalMode.Should().Be(ProtectionMode.Notify, "Default mode when nothing matches");
        }

        [Fact]
        public void EvaluateBatch_ResolvesConflicts_MostRestrictiveWins()
        {
            // Arrange
            var rule1 = new Rule
            {
                RuleId = "Notify",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var rule2 = new Rule
            {
                RuleId = "Assist",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var rule3 = new Rule
            {
                RuleId = "Protect",
                Mode = ProtectionMode.Protect,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var elements = CreateMockWallElements(10);
            var rules = new List<Rule> { rule1, rule2, rule3 };

            // Act
            var result = _evaluator.EvaluateBatch(elements.Select(m => m.Object), 32778, rules);

            // Assert
            result.FinalMode.Should().Be(ProtectionMode.Protect, "Most restrictive mode should win");
            result.MatchedRules.Should().HaveCount(3);
        }

        [Fact]
        public void EvaluateBatch_CombinesScreenshotRequirements()
        {
            // Arrange
            var rule1 = new Rule
            {
                RuleId = "rule1",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                CaptureBeforeScreenshot = true,
                CaptureAfterScreenshot = false
            };

            var rule2 = new Rule
            {
                RuleId = "rule2",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                CaptureBeforeScreenshot = false,
                CaptureAfterScreenshot = true
            };

            var elements = CreateMockWallElements(5);
            var rules = new List<Rule> { rule1, rule2 };

            // Act
            var result = _evaluator.EvaluateBatch(elements.Select(m => m.Object), 32778, rules);

            // Assert
            result.CaptureBeforeScreenshot.Should().BeTrue();
            result.CaptureAfterScreenshot.Should().BeTrue();
        }

        [Fact]
        public void EvaluateBatch_CombinesCommentRequirements()
        {
            // Arrange
            var rule1 = new Rule
            {
                RuleId = "rule1",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                RequireComment = false
            };

            var rule2 = new Rule
            {
                RuleId = "rule2",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                RequireComment = true
            };

            var elements = CreateMockWallElements(3);
            var rules = new List<Rule> { rule1, rule2 };

            // Act
            var result = _evaluator.EvaluateBatch(elements.Select(m => m.Object), 32778, rules);

            // Assert
            result.RequireComment.Should().BeTrue("At least one rule requires comment");
        }

        [Fact]
        public void EvaluateBatch_LargeNumberOfElements_PerformsEfficiently()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "monitor-all",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var elements = CreateMockWallElements(1000); // 1000 elements
            var rules = new List<Rule> { rule };

            // Act
            var startTime = DateTime.Now;
            var result = _evaluator.EvaluateBatch(elements.Select(m => m.Object), 32778, rules);
            var duration = DateTime.Now - startTime;

            // Assert
            result.MatchedRules.Should().ContainSingle();
            duration.TotalSeconds.Should().BeLessThan(5, "Batch evaluation of 1000 elements should complete within 5 seconds");
        }

        [Fact]
        public void EvaluateBatch_LogsBatchInfo()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var elements = CreateMockWallElements(5);
            var rules = new List<Rule> { rule };

            // Act
            _evaluator.EvaluateBatch(elements.Select(m => m.Object), 32778, rules);

            // Assert
            _mockLogger.Verify(
                l => l.LogDebug(It.Is<string>(s => s.Contains("Batch evaluating"))),
                Times.Once);

            _mockLogger.Verify(
                l => l.LogInfo(It.Is<string>(s => s.Contains("Batch evaluation complete"))),
                Times.Once);
        }

        [Fact]
        public void EvaluateBatch_ExceptionInOneElement_ContinuesWithOthers()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var goodElements = CreateMockWallElements(3);
            var badElement = new Mock<Element>();
            badElement.Setup(e => e.Category).Throws(new InvalidOperationException("Test exception"));

            var elements = new List<Mock<Element>>(goodElements);
            elements.Insert(1, badElement); // Insert bad element in the middle

            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.EvaluateBatch(elements.Select(m => m.Object), 32778, rules);

            // Assert
            result.MatchedRules.Should().ContainSingle("Good elements should still be evaluated");
        }

        #region Helper Methods

        private List<Mock<Element>> CreateMockWallElements(int count)
        {
            var elements = new List<Mock<Element>>();
            for (int i = 0; i < count; i++)
            {
                elements.Add(CreateMockWallElement($"WALL-{i:D3}", false));
            }
            return elements;
        }

        private List<Mock<Element>> CreateMockDoorElements(int count)
        {
            var elements = new List<Mock<Element>>();
            for (int i = 0; i < count; i++)
            {
                elements.Add(CreateMockDoorElement($"DOOR-{i:D3}"));
            }
            return elements;
        }

        private Mock<Element> CreateMockWallElement(string mark, bool isStructural)
        {
            var mockElement = new Mock<Element>();
            var mockCategory = new Mock<Category>();
            mockCategory.Setup(c => c.Id).Returns(new ElementId(-2000011)); // Walls
            mockCategory.Setup(c => c.Id.IntegerValue).Returns(-2000011);
            mockCategory.Setup(c => c.Name).Returns("Walls");

            mockElement.Setup(e => e.Category).Returns(mockCategory.Object);
            mockElement.Setup(e => e.Id).Returns(new ElementId(new Random().Next(10000, 99999)));

            // Setup Mark parameter
            var mockMarkParam = new Mock<Parameter>();
            mockMarkParam.Setup(p => p.HasValue).Returns(true);
            mockMarkParam.Setup(p => p.StorageType).Returns(StorageType.String);
            mockMarkParam.Setup(p => p.AsString()).Returns(mark);
            mockElement.Setup(e => e.LookupParameter("Mark")).Returns(mockMarkParam.Object);

            // Setup structural parameter if needed
            if (isStructural)
            {
                var mockStructParam = new Mock<Parameter>();
                mockStructParam.Setup(p => p.HasValue).Returns(true);
                mockStructParam.Setup(p => p.StorageType).Returns(StorageType.Integer);
                mockStructParam.Setup(p => p.AsInteger()).Returns(1);
                mockStructParam.Setup(p => p.AsValueString()).Returns("1");
                mockElement.Setup(e => e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT))
                    .Returns(mockStructParam.Object);
            }

            return mockElement;
        }

        private Mock<Element> CreateMockDoorElement(string mark)
        {
            var mockElement = new Mock<Element>();
            var mockCategory = new Mock<Category>();
            mockCategory.Setup(c => c.Id).Returns(new ElementId(-2000023)); // Doors
            mockCategory.Setup(c => c.Id.IntegerValue).Returns(-2000023);
            mockCategory.Setup(c => c.Name).Returns("Doors");

            mockElement.Setup(e => e.Category).Returns(mockCategory.Object);
            mockElement.Setup(e => e.Id).Returns(new ElementId(new Random().Next(10000, 99999)));

            // Setup Mark parameter
            var mockMarkParam = new Mock<Parameter>();
            mockMarkParam.Setup(p => p.HasValue).Returns(true);
            mockMarkParam.Setup(p => p.StorageType).Returns(StorageType.String);
            mockMarkParam.Setup(p => p.AsString()).Returns(mark);
            mockElement.Setup(e => e.LookupParameter("Mark")).Returns(mockMarkParam.Object);

            return mockElement;
        }

        #endregion
    }
}
