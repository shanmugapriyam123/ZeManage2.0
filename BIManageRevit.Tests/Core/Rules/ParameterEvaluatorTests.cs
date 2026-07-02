using System;
using System.Collections.Generic;
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
    /// Tests for parameter-based rule evaluation
    /// </summary>
    public class ParameterEvaluatorTests
    {
        private readonly Mock<ILogger> _mockLogger;
        private readonly ParameterEvaluator _evaluator;

        public ParameterEvaluatorTests()
        {
            _mockLogger = new Mock<ILogger>();
            _evaluator = new ParameterEvaluator(_mockLogger.Object);
        }

        #region String-based Parameter Tests

        [Fact]
        public void Matches_NoParametersSpecified_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>()
            };

            var mockElement = new Mock<Element>();

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("Rules with no parameter conditions should match any element");
        }

        [Fact]
        public void Matches_EqualsOperator_CaseSensitive_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Mark"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.Equals,
                        Value = "DEMO",
                        IgnoreCase = false
                    }
                }
            };

            var mockParameter = new Mock<Parameter>();
            mockParameter.Setup(p => p.HasValue).Returns(true);
            mockParameter.Setup(p => p.StorageType).Returns(StorageType.String);
            mockParameter.Setup(p => p.AsString()).Returns("DEMO");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Mark")).Returns(mockParameter.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void Matches_EqualsOperator_CaseInsensitive_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Mark"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.Equals,
                        Value = "DEMO",
                        IgnoreCase = true
                    }
                }
            };

            var mockParameter = new Mock<Parameter>();
            mockParameter.Setup(p => p.HasValue).Returns(true);
            mockParameter.Setup(p => p.StorageType).Returns(StorageType.String);
            mockParameter.Setup(p => p.AsString()).Returns("demo"); // lowercase

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Mark")).Returns(mockParameter.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("Case-insensitive comparison should match 'DEMO' and 'demo'");
        }

        [Fact]
        public void Matches_NotEqualsOperator_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Mark"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.NotEquals,
                        Value = "DEMO"
                    }
                }
            };

            var mockParameter = new Mock<Parameter>();
            mockParameter.Setup(p => p.HasValue).Returns(true);
            mockParameter.Setup(p => p.StorageType).Returns(StorageType.String);
            mockParameter.Setup(p => p.AsString()).Returns("PROD");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Mark")).Returns(mockParameter.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void Matches_ContainsOperator_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Mark"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.Contains,
                        Value = "DEMO",
                        IgnoreCase = true
                    }
                }
            };

            var mockParameter = new Mock<Parameter>();
            mockParameter.Setup(p => p.HasValue).Returns(true);
            mockParameter.Setup(p => p.StorageType).Returns(StorageType.String);
            mockParameter.Setup(p => p.AsString()).Returns("Wall-DEMO-123");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Mark")).Returns(mockParameter.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("'Wall-DEMO-123' should contain 'DEMO'");
        }

        [Fact]
        public void Matches_StartsWithOperator_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Mark"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.StartsWith,
                        Value = "DEMO"
                    }
                }
            };

            var mockParameter = new Mock<Parameter>();
            mockParameter.Setup(p => p.HasValue).Returns(true);
            mockParameter.Setup(p => p.StorageType).Returns(StorageType.String);
            mockParameter.Setup(p => p.AsString()).Returns("DEMO-123");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Mark")).Returns(mockParameter.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void Matches_GreaterThanOperator_WithNumericValues_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Height"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.GreaterThan,
                        Value = "100"
                    }
                }
            };

            var mockParameter = new Mock<Parameter>();
            mockParameter.Setup(p => p.HasValue).Returns(true);
            mockParameter.Setup(p => p.StorageType).Returns(StorageType.Double);
            mockParameter.Setup(p => p.AsDouble()).Returns(150.0);
            mockParameter.Setup(p => p.AsValueString()).Returns("150");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Height")).Returns(mockParameter.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("150 should be greater than 100");
        }

        [Fact]
        public void Matches_LessThanOperator_WithNumericValues_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Height"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.LessThan,
                        Value = "100"
                    }
                }
            };

            var mockParameter = new Mock<Parameter>();
            mockParameter.Setup(p => p.HasValue).Returns(true);
            mockParameter.Setup(p => p.StorageType).Returns(StorageType.Double);
            mockParameter.Setup(p => p.AsDouble()).Returns(50.0);
            mockParameter.Setup(p => p.AsValueString()).Returns("50");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Height")).Returns(mockParameter.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("50 should be less than 100");
        }

        [Fact]
        public void Matches_IsEmptyOperator_WithNullParameter_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Comments"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.IsEmpty
                    }
                }
            };

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Comments")).Returns((Parameter)null);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("Missing parameter should be considered empty");
        }

        [Fact]
        public void Matches_IsEmptyOperator_WithEmptyString_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Comments"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.IsEmpty
                    }
                }
            };

            var mockParameter = new Mock<Parameter>();
            mockParameter.Setup(p => p.HasValue).Returns(true);
            mockParameter.Setup(p => p.StorageType).Returns(StorageType.String);
            mockParameter.Setup(p => p.AsString()).Returns("");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Comments")).Returns(mockParameter.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void Matches_IsNotEmptyOperator_WithValue_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Comments"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.IsNotEmpty
                    }
                }
            };

            var mockParameter = new Mock<Parameter>();
            mockParameter.Setup(p => p.HasValue).Returns(true);
            mockParameter.Setup(p => p.StorageType).Returns(StorageType.String);
            mockParameter.Setup(p => p.AsString()).Returns("Some comment");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Comments")).Returns(mockParameter.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void Matches_ParameterNotFound_ReturnsFalse()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["NonExistentParam"] = new RuleCondition
                    {
                        Operator = ComparisonOperator.Equals,
                        Value = "test"
                    }
                }
            };

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("NonExistentParam")).Returns((Parameter)null);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeFalse("Missing parameter should not match non-IsEmpty operators");
        }

        [Fact]
        public void Matches_MultipleParameters_AllMustMatch_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Mark"] = new RuleCondition { Operator = ComparisonOperator.Contains, Value = "DEMO" },
                    ["Comments"] = new RuleCondition { Operator = ComparisonOperator.IsNotEmpty }
                }
            };

            var mockMarkParam = new Mock<Parameter>();
            mockMarkParam.Setup(p => p.HasValue).Returns(true);
            mockMarkParam.Setup(p => p.StorageType).Returns(StorageType.String);
            mockMarkParam.Setup(p => p.AsString()).Returns("DEMO-123");

            var mockCommentsParam = new Mock<Parameter>();
            mockCommentsParam.Setup(p => p.HasValue).Returns(true);
            mockCommentsParam.Setup(p => p.StorageType).Returns(StorageType.String);
            mockCommentsParam.Setup(p => p.AsString()).Returns("Some comment");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Mark")).Returns(mockMarkParam.Object);
            mockElement.Setup(e => e.LookupParameter("Comments")).Returns(mockCommentsParam.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("All parameter conditions should match");
        }

        [Fact]
        public void Matches_MultipleParameters_OneFails_ReturnsFalse()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Mark"] = new RuleCondition { Operator = ComparisonOperator.Contains, Value = "DEMO" },
                    ["Comments"] = new RuleCondition { Operator = ComparisonOperator.IsNotEmpty }
                }
            };

            var mockMarkParam = new Mock<Parameter>();
            mockMarkParam.Setup(p => p.HasValue).Returns(true);
            mockMarkParam.Setup(p => p.StorageType).Returns(StorageType.String);
            mockMarkParam.Setup(p => p.AsString()).Returns("DEMO-123");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Mark")).Returns(mockMarkParam.Object);
            mockElement.Setup(e => e.LookupParameter("Comments")).Returns((Parameter)null); // Missing Comments

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeFalse("If any parameter condition fails, entire match should fail");
        }

        #endregion

        #region BuiltInParameter Tests

        [Fact]
        public void Matches_BuiltInParameter_Equals_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                BuiltInParameters = new Dictionary<BuiltInParameter, RuleCondition>
                {
                    [BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT] = new RuleCondition
                    {
                        Operator = ComparisonOperator.Equals,
                        Value = "1"
                    }
                }
            };

            var mockParameter = new Mock<Parameter>();
            mockParameter.Setup(p => p.HasValue).Returns(true);
            mockParameter.Setup(p => p.StorageType).Returns(StorageType.Integer);
            mockParameter.Setup(p => p.AsInteger()).Returns(1);
            mockParameter.Setup(p => p.AsValueString()).Returns("1");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT))
                .Returns(mockParameter.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void Matches_BuiltInParameter_NotFound_ReturnsFalse()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                BuiltInParameters = new Dictionary<BuiltInParameter, RuleCondition>
                {
                    [BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT] = new RuleCondition
                    {
                        Operator = ComparisonOperator.Equals,
                        Value = "1"
                    }
                }
            };

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT))
                .Returns((Parameter)null);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void Matches_BothStringAndBuiltInParameters_AllMustMatch()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Mark"] = new RuleCondition { Operator = ComparisonOperator.Contains, Value = "DEMO" }
                },
                BuiltInParameters = new Dictionary<BuiltInParameter, RuleCondition>
                {
                    [BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT] = new RuleCondition
                    {
                        Operator = ComparisonOperator.Equals,
                        Value = "1"
                    }
                }
            };

            var mockMarkParam = new Mock<Parameter>();
            mockMarkParam.Setup(p => p.HasValue).Returns(true);
            mockMarkParam.Setup(p => p.StorageType).Returns(StorageType.String);
            mockMarkParam.Setup(p => p.AsString()).Returns("DEMO-123");

            var mockBuiltInParam = new Mock<Parameter>();
            mockBuiltInParam.Setup(p => p.HasValue).Returns(true);
            mockBuiltInParam.Setup(p => p.StorageType).Returns(StorageType.Integer);
            mockBuiltInParam.Setup(p => p.AsInteger()).Returns(1);
            mockBuiltInParam.Setup(p => p.AsValueString()).Returns("1");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter("Mark")).Returns(mockMarkParam.Object);
            mockElement.Setup(e => e.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT))
                .Returns(mockBuiltInParam.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("Both string and BuiltInParameter conditions should match");
        }

        #endregion

        #region Error Handling Tests

        [Fact]
        public void Matches_ExceptionDuringEvaluation_ReturnsFalseAndLogs()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                Parameters = new Dictionary<string, RuleCondition>
                {
                    ["Mark"] = new RuleCondition { Operator = ComparisonOperator.Equals, Value = "test" }
                }
            };

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.LookupParameter(It.IsAny<string>()))
                .Throws(new InvalidOperationException("Test exception"));

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeFalse();
            _mockLogger.Verify(
                l => l.LogError(
                    It.Is<string>(s => s.Contains("Error in parameter matching")),
                    It.IsAny<Exception>()),
                Times.Once);
        }

        #endregion
    }
}
