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
    /// Tests for conflict resolution and "All Rules Apply" strategy
    /// </summary>
    public class ConflictResolutionTests
    {
        private readonly Mock<ILogger> _mockLogger;
        private readonly RuleEvaluator _evaluator;

        public ConflictResolutionTests()
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
        public void ResolveConflicts_SingleMonitorRule_SetsFinalModeToMonitor()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "monitor-rule",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.FinalMode.Should().Be(ProtectionMode.Notify);
            result.MatchedRules.Should().ContainSingle();
        }

        [Fact]
        public void ResolveConflicts_SingleGuideRule_SetsFinalModeToGuide()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "guide-rule",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.FinalMode.Should().Be(ProtectionMode.Assist);
        }

        [Fact]
        public void ResolveConflicts_SinglePreventRule_SetsFinalModeToPrevent()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "prevent-rule",
                Mode = ProtectionMode.Protect,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.FinalMode.Should().Be(ProtectionMode.Protect);
        }

        [Fact]
        public void ResolveConflicts_MonitorAndGuide_GuideWins()
        {
            // Arrange (Guide = 2, Monitor = 1)
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

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule1, rule2 };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.FinalMode.Should().Be(ProtectionMode.Assist, "Guide (2) is more restrictive than Monitor (1)");
            result.MatchedRules.Should().HaveCount(2, "Both rules should match (All Rules Apply)");
        }

        [Fact]
        public void ResolveConflicts_GuideAndPrevent_PreventWins()
        {
            // Arrange (Prevent = 3, Guide = 2)
            var rule1 = new Rule
            {
                RuleId = "Assist",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var rule2 = new Rule
            {
                RuleId = "Protect",
                Mode = ProtectionMode.Protect,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule1, rule2 };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.FinalMode.Should().Be(ProtectionMode.Protect, "Prevent (3) is more restrictive than Guide (2)");
            result.MatchedRules.Should().HaveCount(2);
        }

        [Fact]
        public void ResolveConflicts_MonitorGuideAndPrevent_PreventWins()
        {
            // Arrange (Prevent = 3, Guide = 2, Monitor = 1)
            var rule1 = new Rule
            {
                RuleId = "Notify",
                Name = "Monitor All",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var rule2 = new Rule
            {
                RuleId = "Assist",
                Name = "Guide Important",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var rule3 = new Rule
            {
                RuleId = "Protect",
                Name = "Prevent Critical",
                Mode = ProtectionMode.Protect,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule1, rule2, rule3 };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.FinalMode.Should().Be(ProtectionMode.Protect, 
                "Most restrictive mode (Prevent) should win when all three modes match");
            result.MatchedRules.Should().HaveCount(3, "All rules should be collected (All Rules Apply)");
        }

        [Fact]
        public void ResolveConflicts_CombinesMessagesFromAllRules()
        {
            // Arrange
            var rule1 = new Rule
            {
                RuleId = "rule1",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                Message = "Tracking this deletion"
            };

            var rule2 = new Rule
            {
                RuleId = "rule2",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                Message = "Please coordinate with team"
            };

            var rule3 = new Rule
            {
                RuleId = "rule3",
                Mode = ProtectionMode.Protect,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                Message = "Critical element - deletion not allowed"
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule1, rule2, rule3 };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.CombinedMessage.Should().NotBeNullOrEmpty();
            result.CombinedMessage.Should().Contain("Tracking this deletion");
            result.CombinedMessage.Should().Contain("Please coordinate with team");
            result.CombinedMessage.Should().Contain("Critical element - deletion not allowed");
            result.CombinedMessage.Split(new[] { Environment.NewLine }, StringSplitOptions.None)
                .Should().HaveCount(3, "Each message should be on a separate line");
        }

        [Fact]
        public void ResolveConflicts_IgnoresEmptyMessages()
        {
            // Arrange
            var rule1 = new Rule
            {
                RuleId = "rule1",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                Message = "" // Empty
            };

            var rule2 = new Rule
            {
                RuleId = "rule2",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                Message = "Important message"
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule1, rule2 };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.CombinedMessage.Should().NotBeNullOrEmpty();
            result.CombinedMessage.Should().Contain("Important message");
            result.CombinedMessage.Split(new[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries)
                .Should().ContainSingle("Only one non-empty message should be included");
        }

        [Fact]
        public void ResolveConflicts_CaptureScreenshot_AnyRuleRequires_IsTrue()
        {
            // Arrange
            var rule1 = new Rule
            {
                RuleId = "rule1",
                Mode = ProtectionMode.Notify,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                CaptureBeforeScreenshot = false,
                CaptureAfterScreenshot = false
            };

            var rule2 = new Rule
            {
                RuleId = "rule2",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                CaptureBeforeScreenshot = true, // One rule requires before
                CaptureAfterScreenshot = false
            };

            var rule3 = new Rule
            {
                RuleId = "rule3",
                Mode = ProtectionMode.Protect,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 },
                CaptureBeforeScreenshot = false,
                CaptureAfterScreenshot = true // One rule requires after
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule1, rule2, rule3 };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.CaptureBeforeScreenshot.Should().BeTrue("At least one rule requires before screenshot");
            result.CaptureAfterScreenshot.Should().BeTrue("At least one rule requires after screenshot");
        }

        [Fact]
        public void ResolveConflicts_RequireComment_AnyRuleRequires_IsTrue()
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
                RequireComment = true // One rule requires comment
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule1, rule2 };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.RequireComment.Should().BeTrue("At least one rule requires a comment");
        }

        [Fact]
        public void ResolveConflicts_NoRulesMatch_DefaultsToMonitor()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "rule1",
                Mode = ProtectionMode.Protect,
                CategoryId = -2000032, // Doors
                CommandIds = new List<int> { 32778 }
            };

            var mockElement = CreateMockWallElement(); // Wall, not Door
            var rules = new List<Rule> { rule };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.MatchedRules.Should().BeEmpty();
            result.FinalMode.Should().Be(ProtectionMode.Notify, "Default mode when no rules match");
        }

        [Fact]
        public void ResolveConflicts_MultipleGuideRules_GuideWins()
        {
            // Arrange
            var rule1 = new Rule
            {
                RuleId = "guide1",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var rule2 = new Rule
            {
                RuleId = "guide2",
                Mode = ProtectionMode.Assist,
                CategoryId = -2000011,
                CommandIds = new List<int> { 32778 }
            };

            var mockElement = CreateMockWallElement();
            var rules = new List<Rule> { rule1, rule2 };

            // Act
            var result = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            result.FinalMode.Should().Be(ProtectionMode.Assist);
            result.MatchedRules.Should().HaveCount(2, "Both Guide rules should match");
        }

        [Fact]
        public void ResolveConflicts_IsDeterministic_SameInputProducesSameOutput()
        {
            // Arrange
            var rules = new List<Rule>
            {
                new Rule { RuleId = "Notify", Mode = ProtectionMode.Notify, CategoryId = -2000011, CommandIds = new List<int> { 32778 } },
                new Rule { RuleId = "Assist", Mode = ProtectionMode.Assist, CategoryId = -2000011, CommandIds = new List<int> { 32778 } },
                new Rule { RuleId = "Protect", Mode = ProtectionMode.Protect, CategoryId = -2000011, CommandIds = new List<int> { 32778 } }
            };

            var mockElement = CreateMockWallElement();

            // Act - Run evaluation multiple times
            var result1 = _evaluator.Evaluate(mockElement.Object, 32778, rules);
            var result2 = _evaluator.Evaluate(mockElement.Object, 32778, rules);
            var result3 = _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert - All results should be identical
            result1.FinalMode.Should().Be(result2.FinalMode).And.Be(result3.FinalMode);
            result1.MatchedRules.Count.Should().Be(result2.MatchedRules.Count).And.Be(result3.MatchedRules.Count);
            result1.CaptureBeforeScreenshot.Should().Be(result2.CaptureBeforeScreenshot).And.Be(result3.CaptureBeforeScreenshot);
            result1.RequireComment.Should().Be(result2.RequireComment).And.Be(result3.RequireComment);
        }

        [Fact]
        public void ResolveConflicts_LogsConflictResolution()
        {
            // Arrange
            var rules = new List<Rule>
            {
                new Rule { RuleId = "Notify", Mode = ProtectionMode.Notify, CategoryId = -2000011, CommandIds = new List<int> { 32778 } },
                new Rule { RuleId = "Protect", Mode = ProtectionMode.Protect, CategoryId = -2000011, CommandIds = new List<int> { 32778 } }
            };

            var mockElement = CreateMockWallElement();

            // Act
            _evaluator.Evaluate(mockElement.Object, 32778, rules);

            // Assert
            _mockLogger.Verify(
                l => l.LogDebug(It.Is<string>(s => s.Contains("Conflict resolution"))),
                Times.Once);
        }

        #region Helper Methods

        private Mock<Element> CreateMockWallElement()
        {
            var mockElement = new Mock<Element>();
            var mockCategory = new Mock<Category>();
            mockCategory.Setup(c => c.Id).Returns(new ElementId(-2000011)); // Walls
            mockCategory.Setup(c => c.Id.IntegerValue).Returns(-2000011);
            mockCategory.Setup(c => c.Name).Returns("Walls");

            mockElement.Setup(e => e.Category).Returns(mockCategory.Object);
            mockElement.Setup(e => e.Id).Returns(new ElementId(12345));

            // Setup basic Document mock
            var mockDoc = new Mock<Document>();
            mockElement.Setup(e => e.Document).Returns(mockDoc.Object);

            return mockElement;
        }

        #endregion
    }
}
