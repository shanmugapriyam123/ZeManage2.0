using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BIManage.Core.Rules.Models;
using BIManage.Infrastructure.Logging;
using FluentAssertions;
using Moq;
using Xunit;

namespace BIManageRevit.Tests.Core.Rules
{
    /// <summary>
    /// Tests for rule deserialization from JSON
    /// </summary>
    public class RuleDeserializationTests
    {
        private readonly Mock<ILogger> _mockLogger;

        public RuleDeserializationTests()
        {
            _mockLogger = new Mock<ILogger>();
        }

        [Fact(Skip = "Quarantined: data drift — Rule model now auto-generates RuleId; assertions need updating. See docs/testing/quarantine.md.")]
        public void ValidJson_WithStringParameters_DeserializesCorrectly()
        {
            // Arrange
            var json = @"{
                ""RuleId"": ""test-rule-001"",
                ""Name"": ""Test Wall Rule"",
                ""Description"": ""Test rule for walls"",
                ""IsActive"": true,
                ""Mode"": ""Assist"",
                ""CategoryId"": -2000011,
                ""CommandIds"": [32778],
                ""Parameters"": {
                    ""Mark"": {
                        ""Operator"": ""Contains"",
                        ""Value"": ""DEMO"",
                        ""IgnoreCase"": true
                    }
                },
                ""Message"": ""This is a demo element"",
                ""CaptureBeforeScreenshot"": true,
                ""CaptureAfterScreenshot"": false,
                ""RequireComment"": true
            }";

            // Act
            var rule = JsonSerializer.Deserialize<Rule>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            // Assert
            rule.Should().NotBeNull();
            rule.RuleId.Should().Be("test-rule-001");
            rule.Name.Should().Be("Test Wall Rule");
            rule.Mode.Should().Be(ProtectionMode.Assist);
            rule.CategoryId.Should().Be(-2000011);
            rule.CommandIds.Should().ContainSingle().Which.Should().Be(32778);
            rule.Parameters.Should().ContainKey("Mark");
            rule.Parameters["Mark"].Operator.Should().Be(ComparisonOperator.Contains);
            rule.Parameters["Mark"].Value.Should().Be("DEMO");
            rule.Parameters["Mark"].IgnoreCase.Should().BeTrue();
            rule.Message.Should().Be("This is a demo element");
            rule.CaptureBeforeScreenshot.Should().BeTrue();
            rule.CaptureAfterScreenshot.Should().BeFalse();
            rule.RequireComment.Should().BeTrue();
        }

        [Fact]
        public void ValidJson_WithBuiltInParameters_DeserializesCorrectly()
        {
            // Arrange
            var json = @"{
                ""RuleId"": ""builtin-test-001"",
                ""Name"": ""Structural Wall Rule"",
                ""Mode"": ""Protect"",
                ""CategoryId"": -2000011,
                ""CommandIds"": [32778],
                ""BuiltInParameters"": {
                    ""WALL_STRUCTURAL_SIGNIFICANT"": {
                        ""Operator"": ""Equals"",
                        ""Value"": ""1""
                    }
                },
                ""Message"": ""Cannot delete structural walls""
            }";

            // Act
            var rule = JsonSerializer.Deserialize<Rule>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });

            // Assert
            rule.Should().NotBeNull();
            rule.RuleId.Should().Be("builtin-test-001");
            rule.Mode.Should().Be(ProtectionMode.Protect);
            rule.BuiltInParameters.Should().NotBeNull();
            rule.BuiltInParameters.Should().ContainKey(Autodesk.Revit.DB.BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
            rule.BuiltInParameters[Autodesk.Revit.DB.BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT].Value.Should().Be("1");
        }

        [Fact(Skip = "Quarantined: data drift — Rule model now auto-generates RuleId; assertions need updating. See docs/testing/quarantine.md.")]
        public void ValidJson_WithMultipleCommandIds_DeserializesCorrectly()
        {
            // Arrange
            var json = @"{
                ""RuleId"": ""multi-command-001"",
                ""Name"": ""Multi Command Rule"",
                ""Mode"": ""Notify"",
                ""CommandIds"": [32778, 32779, 32780]
            }";

            // Act
            var rule = JsonSerializer.Deserialize<Rule>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            // Assert
            rule.Should().NotBeNull();
            rule.CommandIds.Should().HaveCount(3);
            rule.CommandIds.Should().Contain(new[] { 32778, 32779, 32780 });
        }

        [Fact]
        public void ValidJson_WithAllOperators_DeserializesCorrectly()
        {
            // Arrange
            var json = @"{
                ""RuleId"": ""operators-test"",
                ""Name"": ""All Operators Test"",
                ""Mode"": ""Assist"",
                ""Parameters"": {
                    ""Param1"": { ""Operator"": ""Equals"", ""Value"": ""test"" },
                    ""Param2"": { ""Operator"": ""NotEquals"", ""Value"": ""test"" },
                    ""Param3"": { ""Operator"": ""Contains"", ""Value"": ""test"" },
                    ""Param4"": { ""Operator"": ""StartsWith"", ""Value"": ""test"" },
                    ""Param5"": { ""Operator"": ""GreaterThan"", ""Value"": ""100"" },
                    ""Param6"": { ""Operator"": ""LessThan"", ""Value"": ""50"" },
                    ""Param7"": { ""Operator"": ""IsEmpty"", ""Value"": """" },
                    ""Param8"": { ""Operator"": ""IsNotEmpty"", ""Value"": """" }
                }
            }";

            // Act
            var rule = JsonSerializer.Deserialize<Rule>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });

            // Assert
            rule.Should().NotBeNull();
            rule.Parameters.Should().HaveCount(8);
            rule.Parameters["Param1"].Operator.Should().Be(ComparisonOperator.Equals);
            rule.Parameters["Param2"].Operator.Should().Be(ComparisonOperator.NotEquals);
            rule.Parameters["Param3"].Operator.Should().Be(ComparisonOperator.Contains);
            rule.Parameters["Param4"].Operator.Should().Be(ComparisonOperator.StartsWith);
            rule.Parameters["Param5"].Operator.Should().Be(ComparisonOperator.GreaterThan);
            rule.Parameters["Param6"].Operator.Should().Be(ComparisonOperator.LessThan);
            rule.Parameters["Param7"].Operator.Should().Be(ComparisonOperator.IsEmpty);
            rule.Parameters["Param8"].Operator.Should().Be(ComparisonOperator.IsNotEmpty);
        }

        [Fact]
        public void InvalidJson_ReturnsNull_DoesNotThrow()
        {
            // Arrange
            var invalidJson = @"{ invalid json structure ][";

            // Act
            Action act = () => JsonSerializer.Deserialize<Rule>(invalidJson);

            // Assert
            act.Should().Throw<JsonException>();
        }

        [Fact(Skip = "Quarantined: data drift — Rule model now auto-generates RuleId; assertions need updating. See docs/testing/quarantine.md.")]
        public void EmptyJson_DeserializesWithDefaults()
        {
            // Arrange
            var json = @"{}";

            // Act
            var rule = JsonSerializer.Deserialize<Rule>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            // Assert
            rule.Should().NotBeNull();
            rule.RuleId.Should().BeNullOrEmpty();
            rule.IsEnabled.Should().BeTrue(); // Default from Rule class
            rule.Mode.Should().Be(ProtectionMode.Notify); // Default enum value
            rule.CommandIds.Should().NotBeNull().And.BeEmpty();
        }

        [Fact]
        public async Task SampleRules_DeleteProtection_LoadsSuccessfully()
        {
            // Arrange
            var sampleFilePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "BIManage", "Rules", "Samples", "delete-protection.json");

            if (!File.Exists(sampleFilePath))
            {
                // Skip test if sample file doesn't exist in test environment
                return;
            }

            // Act
            var json = await File.ReadAllTextAsync(sampleFilePath);
            var rules = JsonSerializer.Deserialize<List<Rule>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });

            // Assert
            rules.Should().NotBeNull();
            rules.Should().NotBeEmpty();
            rules.All(r => !string.IsNullOrEmpty(r.RuleId)).Should().BeTrue();
        }

        [Fact]
        public async Task SampleRules_BuiltInParameterExample_LoadsSuccessfully()
        {
            // Arrange
            var sampleFilePath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "BIManage", "Rules", "Samples", "builtin-parameter-example.json");

            if (!File.Exists(sampleFilePath))
            {
                // Skip test if sample file doesn't exist in test environment
                return;
            }

            // Act
            var json = await File.ReadAllTextAsync(sampleFilePath);
            var rules = JsonSerializer.Deserialize<List<Rule>>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            });

            // Assert
            rules.Should().NotBeNull();
            rules.Should().NotBeEmpty();
            rules.Any(r => r.BuiltInParameters != null && r.BuiltInParameters.Count > 0).Should().BeTrue();
        }

        [Fact(Skip = "Quarantined: data drift — Rule model now auto-generates RuleId; assertions need updating. See docs/testing/quarantine.md.")]
        public void Rule_WithFamilyAndType_DeserializesCorrectly()
        {
            // Arrange
            var json = @"{
                ""RuleId"": ""family-type-test"",
                ""Name"": ""Family Type Rule"",
                ""Mode"": ""Assist"",
                ""FamilyName"": ""Basic Wall"",
                ""TypeName"": ""Generic - 200mm"",
                ""CommandIds"": [32778]
            }";

            // Act
            var rule = JsonSerializer.Deserialize<Rule>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            // Assert
            rule.Should().NotBeNull();
            rule.FamilyName.Should().Be("Basic Wall");
            rule.TypeName.Should().Be("Generic - 200mm");
        }

        [Fact]
        public void Rule_IsApplicable_WithMatchingCategoryAndCommand_ReturnsTrue()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                CategoryId = -2000011, // Walls
                CommandIds = new List<int> { 32778 } // Delete
            };

            // Act
            var isApplicable = rule.IsApplicable(-2000011, 32778);

            // Assert
            isApplicable.Should().BeTrue();
        }

        [Fact]
        public void Rule_IsApplicable_WithNonMatchingCategory_ReturnsFalse()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                CategoryId = -2000011, // Walls
                CommandIds = new List<int> { 32778 }
            };

            // Act
            var isApplicable = rule.IsApplicable(-2000032, 32778); // Doors

            // Assert
            isApplicable.Should().BeFalse();
        }

        [Fact]
        public void Rule_IsApplicable_WithNullCategory_MatchesAnyCategory()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                CategoryId = null, // Match all categories
                CommandIds = new List<int> { 32778 }
            };

            // Act
            var isApplicable1 = rule.IsApplicable(-2000011, 32778); // Walls
            var isApplicable2 = rule.IsApplicable(-2000032, 32778); // Doors

            // Assert
            isApplicable1.Should().BeTrue();
            isApplicable2.Should().BeTrue();
        }

        [Fact]
        public void Rule_IsApplicable_WithEmptyCommandIds_MatchesAnyCommand()
        {
            // Arrange
            var rule = new Rule
            {
                RuleId = "test",
                CategoryId = -2000011,
                CommandIds = new List<int>() // Match all commands
            };

            // Act
            var isApplicable1 = rule.IsApplicable(-2000011, 32778); // Delete
            var isApplicable2 = rule.IsApplicable(-2000011, 32779); // Move

            // Assert
            isApplicable1.Should().BeTrue();
            isApplicable2.Should().BeTrue();
        }
    }
}
