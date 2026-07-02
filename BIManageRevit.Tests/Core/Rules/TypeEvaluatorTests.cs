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
    /// Tests for family/type-based rule evaluation
    /// </summary>
    public class TypeEvaluatorTests
    {
        private readonly Mock<ILogger> _mockLogger;
        private readonly TypeEvaluator _evaluator;

        public TypeEvaluatorTests()
        {
            _mockLogger = new Mock<ILogger>();
            _evaluator = new TypeEvaluator(_mockLogger.Object);
        }

        [Fact]
        public void Matches_NoFamilyOrTypeSpecified_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                FamilyName = null,
                TypeName = null
            };

            var mockElement = new Mock<Element>();

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("Rules with no family/type conditions should match any element");
        }

        [Fact]
        public void Matches_FamilyNameMatches_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                FamilyName = "Basic Wall",
                TypeName = null
            };

            var mockFamilySymbol = new Mock<FamilySymbol>();
            mockFamilySymbol.Setup(fs => fs.FamilyName).Returns("Basic Wall");

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.Document).Returns(Mock.Of<Document>());

            var mockElementType = new Mock<ElementType>();
            mockElementType.Setup(et => et.FamilyName).Returns("Basic Wall");

            // Setup GetTypeId to return a valid ID
            var typeId = new ElementId(12345);
            mockElement.Setup(e => e.GetTypeId()).Returns(typeId);

            // Setup document to return the element type
            var mockDoc = new Mock<Document>();
            mockDoc.Setup(d => d.GetElement(typeId)).Returns(mockElementType.Object);
            mockElement.Setup(e => e.Document).Returns(mockDoc.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void Matches_FamilyNameDoesNotMatch_ReturnsFalse()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                FamilyName = "Basic Wall",
                TypeName = null
            };

            var mockElementType = new Mock<ElementType>();
            mockElementType.Setup(et => et.FamilyName).Returns("Different Wall");

            var typeId = new ElementId(12345);
            var mockDoc = new Mock<Document>();
            mockDoc.Setup(d => d.GetElement(typeId)).Returns(mockElementType.Object);

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.GetTypeId()).Returns(typeId);
            mockElement.Setup(e => e.Document).Returns(mockDoc.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void Matches_TypeNameMatches_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                FamilyName = null,
                TypeName = "Generic - 200mm"
            };

            var mockElementType = new Mock<ElementType>();
            mockElementType.Setup(et => et.Name).Returns("Generic - 200mm");

            var typeId = new ElementId(12345);
            var mockDoc = new Mock<Document>();
            mockDoc.Setup(d => d.GetElement(typeId)).Returns(mockElementType.Object);

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.GetTypeId()).Returns(typeId);
            mockElement.Setup(e => e.Document).Returns(mockDoc.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue();
        }

        [Fact]
        public void Matches_BothFamilyAndTypeMatch_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                FamilyName = "Basic Wall",
                TypeName = "Generic - 200mm"
            };

            var mockElementType = new Mock<ElementType>();
            mockElementType.Setup(et => et.FamilyName).Returns("Basic Wall");
            mockElementType.Setup(et => et.Name).Returns("Generic - 200mm");

            var typeId = new ElementId(12345);
            var mockDoc = new Mock<Document>();
            mockDoc.Setup(d => d.GetElement(typeId)).Returns(mockElementType.Object);

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.GetTypeId()).Returns(typeId);
            mockElement.Setup(e => e.Document).Returns(mockDoc.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("Both family and type name should match");
        }

        [Fact]
        public void Matches_FamilyMatchesButTypeDoesNot_ReturnsFalse()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                FamilyName = "Basic Wall",
                TypeName = "Generic - 200mm"
            };

            var mockElementType = new Mock<ElementType>();
            mockElementType.Setup(et => et.FamilyName).Returns("Basic Wall");
            mockElementType.Setup(et => et.Name).Returns("Generic - 300mm"); // Different type

            var typeId = new ElementId(12345);
            var mockDoc = new Mock<Document>();
            mockDoc.Setup(d => d.GetElement(typeId)).Returns(mockElementType.Object);

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.GetTypeId()).Returns(typeId);
            mockElement.Setup(e => e.Document).Returns(mockDoc.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeFalse("Type name does not match");
        }

        [Fact]
        public void Matches_InvalidTypeId_ReturnsFalse()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                FamilyName = "Basic Wall"
            };

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.GetTypeId()).Returns(ElementId.InvalidElementId);
            mockElement.Setup(e => e.Document).Returns(Mock.Of<Document>());
            mockElement.Setup(e => e.Id).Returns(new ElementId(99999));

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void Matches_ElementTypeNotFound_ReturnsFalse()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                FamilyName = "Basic Wall"
            };

            var typeId = new ElementId(12345);
            var mockDoc = new Mock<Document>();
            mockDoc.Setup(d => d.GetElement(typeId)).Returns((Element)null);

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.GetTypeId()).Returns(typeId);
            mockElement.Setup(e => e.Document).Returns(mockDoc.Object);
            mockElement.Setup(e => e.Id).Returns(new ElementId(99999));

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeFalse();
        }

        [Fact]
        public void Matches_FamilyNameCaseInsensitive_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                FamilyName = "basic wall" // lowercase
            };

            var mockElementType = new Mock<ElementType>();
            mockElementType.Setup(et => et.FamilyName).Returns("Basic Wall"); // Mixed case

            var typeId = new ElementId(12345);
            var mockDoc = new Mock<Document>();
            mockDoc.Setup(d => d.GetElement(typeId)).Returns(mockElementType.Object);

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.GetTypeId()).Returns(typeId);
            mockElement.Setup(e => e.Document).Returns(mockDoc.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("Family name comparison should be case-insensitive");
        }

        [Fact]
        public void Matches_TypeNameCaseInsensitive_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                TypeName = "GENERIC - 200MM" // uppercase
            };

            var mockElementType = new Mock<ElementType>();
            mockElementType.Setup(et => et.Name).Returns("Generic - 200mm"); // Mixed case

            var typeId = new ElementId(12345);
            var mockDoc = new Mock<Document>();
            mockDoc.Setup(d => d.GetElement(typeId)).Returns(mockElementType.Object);

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.GetTypeId()).Returns(typeId);
            mockElement.Setup(e => e.Document).Returns(mockDoc.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("Type name comparison should be case-insensitive");
        }

        [Fact]
        public void Matches_ExceptionDuringEvaluation_ReturnsFalseAndLogs()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                FamilyName = "Basic Wall"
            };

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.GetTypeId()).Throws(new InvalidOperationException("Test exception"));

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeFalse();
            _mockLogger.Verify(
                l => l.LogError(
                    It.Is<string>(s => s.Contains("Error in type matching")),
                    It.IsAny<Exception>()),
                Times.Once);
        }

        [Fact]
        public void Matches_OnlyFamilySpecified_TypeNull_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                FamilyName = "Basic Wall",
                TypeName = null // Only family matters
            };

            var mockElementType = new Mock<ElementType>();
            mockElementType.Setup(et => et.FamilyName).Returns("Basic Wall");
            mockElementType.Setup(et => et.Name).Returns("Any Type Name");

            var typeId = new ElementId(12345);
            var mockDoc = new Mock<Document>();
            mockDoc.Setup(d => d.GetElement(typeId)).Returns(mockElementType.Object);

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.GetTypeId()).Returns(typeId);
            mockElement.Setup(e => e.Document).Returns(mockDoc.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("Should match any type when TypeName is null but family matches");
        }

        [Fact]
        public void Matches_OnlyTypeSpecified_FamilyNull_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                FamilyName = null, // Any family
                TypeName = "Generic - 200mm"
            };

            var mockElementType = new Mock<ElementType>();
            mockElementType.Setup(et => et.FamilyName).Returns("Any Family");
            mockElementType.Setup(et => et.Name).Returns("Generic - 200mm");

            var typeId = new ElementId(12345);
            var mockDoc = new Mock<Document>();
            mockDoc.Setup(d => d.GetElement(typeId)).Returns(mockElementType.Object);

            var mockElement = new Mock<Element>();
            mockElement.Setup(e => e.GetTypeId()).Returns(typeId);
            mockElement.Setup(e => e.Document).Returns(mockDoc.Object);

            // Act
            var result = _evaluator.Matches(rule, mockElement.Object);

            // Assert
            result.Should().BeTrue("Should match any family when FamilyName is null but type matches");
        }
    }
}
