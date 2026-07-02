using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BIManage.Core.Rules.Models;
using FluentAssertions;
using Xunit;

namespace BIManageRevit.Tests.Core.Rules
{
    /// <summary>
    /// Validates that all sample rule JSON files load correctly and are well-formed
    /// </summary>
    public class SampleRulesValidationTests
    {
        private readonly string _samplesDirectory;
        private readonly JsonSerializerOptions _jsonOptions;

        public SampleRulesValidationTests()
        {
            // Navigate from test output directory to samples directory
            _samplesDirectory = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "BIManage", "Rules", "Samples");

            _jsonOptions = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
            };
        }

        [Theory]
        [InlineData("sample-rules.json")]
        [InlineData("builtin-parameter-example.json")]
        [InlineData("sample-command-protection.json")]
        [InlineData("mode-testing.json")]
        public async Task SampleFile_Exists_AndLoadsSuccessfully(string fileName)
        {
            // Arrange
            var filePath = Path.Combine(_samplesDirectory, fileName);

            // Assert file exists
            File.Exists(filePath).Should().BeTrue($"{fileName} should exist in samples directory");

            // Act
            var json = await ReadFileAsync(filePath);
            json.Should().NotBeNullOrEmpty($"{fileName} should not be empty");

            // Attempt to deserialize
            List<Rule> rules = null;
            Action act = () => rules = JsonSerializer.Deserialize<List<Rule>>(json, _jsonOptions);

            // Assert
            act.Should().NotThrow($"{fileName} should deserialize without errors");
            rules.Should().NotBeNull();
            rules.Should().NotBeEmpty($"{fileName} should contain at least one rule");
        }

        [Fact]
        public async Task SampleRules_AreValid()
        {
            // Arrange
            var filePath = Path.Combine(_samplesDirectory, "sample-rules.json");
            if (!File.Exists(filePath))
                return; // Skip if file doesn't exist in test environment

            // Act
            var json = await ReadFileAsync(filePath);
            var rules = JsonSerializer.Deserialize<List<Rule>>(json, _jsonOptions);

            // Assert
            rules.Should().NotBeNull();
            rules.All(r => !string.IsNullOrEmpty(r.RuleId)).Should().BeTrue("All rules should have RuleId");
            rules.All(r => !string.IsNullOrEmpty(r.Name)).Should().BeTrue("All rules should have Name");
        }

        [Fact]
        public async Task BuiltInParameterExamples_AreValid()
        {
            // Arrange
            var filePath = Path.Combine(_samplesDirectory, "builtin-parameter-example.json");
            if (!File.Exists(filePath))
                return;

            // Act
            var json = await ReadFileAsync(filePath);
            var rules = JsonSerializer.Deserialize<List<Rule>>(json, _jsonOptions);

            // Assert
            rules.Should().NotBeNull();
            rules.Any(r => r.BuiltInParameters != null && r.BuiltInParameters.Count > 0)
                .Should().BeTrue("At least one rule should use BuiltInParameters");
            
            // Verify BuiltInParameter rules have valid structure
            foreach (var rule in rules.Where(r => r.BuiltInParameters != null && r.BuiltInParameters.Any()))
            {
                foreach (var param in rule.BuiltInParameters)
                {
                    param.Value.Should().NotBeNull($"Rule {rule.RuleId} should have valid condition for {param.Key}");
                    param.Value.Value.Should().NotBeNull($"Rule {rule.RuleId} condition should have a value");
                }
            }
        }

        [Fact]
        public async Task ModeTestingRules_AreValid()
        {
            // Arrange
            var filePath = Path.Combine(_samplesDirectory, "mode-testing.json");
            if (!File.Exists(filePath))
                return;

            // Act
            var json = await ReadFileAsync(filePath);
            var rules = JsonSerializer.Deserialize<List<Rule>>(json, _jsonOptions);

            // Assert
            rules.Should().NotBeNull();
            rules.All(r => r.IsEnabled).Should().BeTrue("All mode testing rules should be enabled by default");
        }

        [Fact]
        public async Task CommandProtectionRules_AreValid()
        {
            // Arrange
            var filePath = Path.Combine(_samplesDirectory, "sample-command-protection.json");
            if (!File.Exists(filePath))
                return;

            // Act
            var json = await ReadFileAsync(filePath);
            var rules = JsonSerializer.Deserialize<List<Rule>>(json, _jsonOptions);

            // Assert
            rules.Should().NotBeNull();
            rules.Should().NotBeEmpty();
        }

        [Fact]
        public async Task AllSampleRules_HaveUniqueRuleIds()
        {
            // Arrange
            var allRuleIds = new List<string>();
            var sampleFiles = new[]
            {
                "sample-rules.json",
                "builtin-parameter-example.json",
                "sample-command-protection.json",
                "mode-testing.json"
            };

            // Act
            foreach (var fileName in sampleFiles)
            {
                var filePath = Path.Combine(_samplesDirectory, fileName);
                if (!File.Exists(filePath))
                    continue;

                var json = await ReadFileAsync(filePath);
                var rules = JsonSerializer.Deserialize<List<Rule>>(json, _jsonOptions);
                
                if (rules != null)
                {
                    allRuleIds.AddRange(rules.Select(r => r.RuleId));
                }
            }

            // Assert
            var duplicates = allRuleIds
                .GroupBy(id => id)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();

            duplicates.Should().BeEmpty("All RuleIds across all sample files should be unique");
        }

        [Fact]
        public async Task AllSampleRules_HaveValidProtectionModes()
        {
            // Arrange
            var sampleFiles = new[]
            {
                "sample-rules.json",
                "builtin-parameter-example.json",
                "sample-command-protection.json",
                "mode-testing.json"
            };

            // Act & Assert
            foreach (var fileName in sampleFiles)
            {
                var filePath = Path.Combine(_samplesDirectory, fileName);
                if (!File.Exists(filePath))
                    continue;

                var json = await ReadFileAsync(filePath);
                var rules = JsonSerializer.Deserialize<List<Rule>>(json, _jsonOptions);

                foreach (var rule in rules ?? new List<Rule>())
                {
                    rule.Mode.Should().BeOneOf(
                        new[] { ProtectionMode.Notify, ProtectionMode.Assist, ProtectionMode.Protect },
                        $"Rule {rule.RuleId} in {fileName} should have valid protection mode");
                }
            }
        }

        [Fact]
        public async Task AllSampleRules_HaveValidOperators()
        {
            // Arrange
            var sampleFiles = new[]
            {
                "sample-rules.json",
                "builtin-parameter-example.json",
                "sample-command-protection.json",
                "mode-testing.json"
            };

            var validOperators = Enum.GetValues(typeof(ComparisonOperator)).Cast<ComparisonOperator>().ToList();

            // Act & Assert
            foreach (var fileName in sampleFiles)
            {
                var filePath = Path.Combine(_samplesDirectory, fileName);
                if (!File.Exists(filePath))
                    continue;

                var json = await ReadFileAsync(filePath);
                var rules = JsonSerializer.Deserialize<List<Rule>>(json, _jsonOptions);

                foreach (var rule in rules ?? new List<Rule>())
                {
                    // Check string-based parameter operators
                    if (rule.Parameters != null)
                    {
                        foreach (var param in rule.Parameters)
                        {
                            validOperators.Should().Contain(param.Value.Operator,
                                $"Rule {rule.RuleId} parameter {param.Key} should have valid operator");
                        }
                    }

                    // Check BuiltInParameter operators
                    if (rule.BuiltInParameters != null)
                    {
                        foreach (var param in rule.BuiltInParameters)
                        {
                            validOperators.Should().Contain(param.Value.Operator,
                                $"Rule {rule.RuleId} BuiltInParameter {param.Key} should have valid operator");
                        }
                    }
                }
            }
        }

        [Fact]
        public async Task AllSampleRules_WithCategories_HaveValidIds()
        {
            // Arrange
            var sampleFiles = new[]
            {
                "sample-rules.json",
                "builtin-parameter-example.json",
                "sample-command-protection.json",
                "mode-testing.json"
            };

            // Act & Assert
            foreach (var fileName in sampleFiles)
            {
                var filePath = Path.Combine(_samplesDirectory, fileName);
                if (!File.Exists(filePath))
                    continue;

                var json = await ReadFileAsync(filePath);
                var rules = JsonSerializer.Deserialize<List<Rule>>(json, _jsonOptions);

                foreach (var rule in rules ?? new List<Rule>())
                {
                    if (rule.CategoryId.HasValue)
                    {
                        // Category IDs are typically negative for built-in categories
                        rule.CategoryId.Value.Should().BeLessThan(0,
                            $"Rule {rule.RuleId} should have valid built-in category ID (negative)");
                    }
                }
            }
        }

        [Fact]
        public async Task AllSampleRules_WithCommands_HaveValidCommandIds()
        {
            // Arrange
            var sampleFiles = new[]
            {
                "sample-rules.json",
                "builtin-parameter-example.json",
                "sample-command-protection.json",
                "mode-testing.json"
            };

            // Act & Assert
            foreach (var fileName in sampleFiles)
            {
                var filePath = Path.Combine(_samplesDirectory, fileName);
                if (!File.Exists(filePath))
                    continue;

                var json = await ReadFileAsync(filePath);
                var rules = JsonSerializer.Deserialize<List<Rule>>(json, _jsonOptions);

                foreach (var rule in rules ?? new List<Rule>())
                {
                    if (rule.CommandIds != null && rule.CommandIds.Any())
                    {
                        foreach (var commandId in rule.CommandIds)
                        {
                            // PostableCommand IDs are typically in the 32000+ range
                            commandId.Should().BeGreaterThan(0,
                                $"Rule {rule.RuleId} should have valid command IDs");
                        }
                    }
                }
            }
        }

        [Fact]
        public void SamplesDirectory_Exists()
        {
            // Assert
            Directory.Exists(_samplesDirectory).Should().BeTrue(
                $"Samples directory should exist at {_samplesDirectory}");
        }

        [Fact]
        public void SamplesDirectory_ContainsJsonFiles()
        {
            // Arrange
            if (!Directory.Exists(_samplesDirectory))
                return; // Skip if directory doesn't exist

            // Act
            var jsonFiles = Directory.GetFiles(_samplesDirectory, "*.json");

            // Assert
            jsonFiles.Should().NotBeEmpty("Samples directory should contain at least one JSON file");
            jsonFiles.Length.Should().BeGreaterOrEqualTo(3, "Should have at least 3 sample rule files");
        }

        /// <summary>
        /// Helper method for async file reading compatible with .NET Framework 4.8
        /// </summary>
        private async Task<string> ReadFileAsync(string path)
        {
            using (var reader = new StreamReader(path))
            {
                return await reader.ReadToEndAsync();
            }
        }
    }
}
