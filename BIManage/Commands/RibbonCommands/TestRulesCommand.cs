using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using BIManage.Core.Features;
using BIManage.Core.Rules;
using BIManage.Core.Rules.Models;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Commands;
using Nice3point.Revit.Toolkit.External;

namespace BIManageRevit.Commands.RibbonCommands
{
    /// <summary>
    /// Interactive command to test rule evaluation on selected elements
    /// </summary>
    [Transaction(TransactionMode.ReadOnly)]
    public class TestRulesCommand : ExternalCommand
    {
        public override void Execute()
        {
            try
            {
                var app = BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services", 
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as ServiceRegistry;

                if (services == null)
                {
                    TaskDialog.Show("Test Rules", "Service registry not available");
                    return;
                }

                var logger = services.GetService<ILogger>();
                var featureToggleService = services.GetService<IFeatureToggleService>();
                var ruleService = services.GetService<IRuleService>();

                // Check if developer tools are enabled
                if (featureToggleService != null && !featureToggleService.IsFeatureEnabled("DeveloperTools"))
                {
                    TaskDialog.Show("Access Denied",
                        "This feature requires developer tools to be enabled.\n\n" +
                        "Test Rules is a developer-only command for testing rule evaluation.");
                    return;
                }

                if (ruleService == null)
                {
                    TaskDialog.Show("Test Rules", "RuleService not available");
                    return;
                }

                // Get current selection or prompt user to select
                var selection = UiApplication.ActiveUIDocument.Selection.GetElementIds();
                if (selection.Count == 0)
                {
                    TaskDialog.Show("Test Rules", 
                        "Please select one or more elements first.\n\n" +
                        "The rule evaluation will test all active rules against your selection.");
                    return;
                }

                var elements = selection
                    .Select(id => UiApplication.ActiveUIDocument.Document.GetElement(id))
                    .Where(e => e != null)
                    .ToList();

                if (!elements.Any())
                {
                    TaskDialog.Show("Test Rules", "No valid elements in selection");
                    return;
                }

                // Prompt for command to simulate (dynamic list from rules + all known commands)
                var commandId = PromptForCommand(ruleService);
                if (commandId == null)
                    return;

                logger?.LogInfo($"Testing rules on {elements.Count} elements with command {commandId}");

                // Evaluate rules
                var result = ruleService.EvaluateRulesBatch(elements, commandId.Value);

                // Display results
                DisplayResults(elements.Count, commandId.Value, result);
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Test Rules Error", 
                    $"An error occurred:\n\n{ex.Message}\n\n{ex.StackTrace}");
            }
        }

        private int? PromptForCommand(IRuleService ruleService)
        {
            // Build dynamic command list: rule commands + all known commands
            var commandMap = new Dictionary<int, string>();

            // Add commands from enabled rules (priority)
            var ruleCommands = ruleService.GetDistinctRuleCommandIds();
            foreach (var (cmdId, cmdName) in ruleCommands)
            {
                if (cmdId > 0 && !commandMap.ContainsKey(cmdId))
                    commandMap[cmdId] = cmdName;
            }

            // Add all known commands from CommandNameResolver
            foreach (var kvp in CommandNameResolver.GetAllCommands())
            {
                if (!commandMap.ContainsKey(kvp.Key))
                    commandMap[kvp.Key] = kvp.Value;
            }

            if (commandMap.Count == 0)
            {
                TaskDialog.Show("Test Rules", "No commands available for testing.");
                return null;
            }

            var commandList = commandMap.Select(kvp => (kvp.Key, kvp.Value)).ToList();

            var dialog = new BIManageRevit.BIManage.Views.Developer.SelectCommandDialog(commandList);
            new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = System.Diagnostics.Process.GetCurrentProcess().MainWindowHandle };
            var result = dialog.ShowDialog();

            if (result == true && dialog.SelectedCommandId.HasValue)
                return dialog.SelectedCommandId;

            return null;
        }

        private void DisplayResults(int elementCount, int commandId, RuleEvaluationResult result)
        {
            var sb = new StringBuilder();
            
            // Header
            sb.AppendLine("═══════════════════════════════════════");
            sb.AppendLine("        RULE EVALUATION RESULTS");
            sb.AppendLine("═══════════════════════════════════════");
            sb.AppendLine();

            // Summary
            sb.AppendLine($"Elements Evaluated: {elementCount}");
            sb.AppendLine($"Command Simulated: {GetCommandName(commandId)} ({commandId})");
            sb.AppendLine();

            // Matched Rules
            if (result.MatchedRules.Any())
            {
                sb.AppendLine($"Matched Rules ({result.MatchedRules.Count}):");
                sb.AppendLine("───────────────────────────────────────");
                foreach (var rule in result.MatchedRules.OrderByDescending(r => (int)r.Mode))
                {
                    var modeIcon = GetModeIcon(rule.Mode);
                    sb.AppendLine($"  {modeIcon} [{rule.Mode}] {rule.Name}");
                    if (!string.IsNullOrEmpty(rule.Description))
                    {
                        sb.AppendLine($"     Description: {rule.Description}");
                    }
                }
                sb.AppendLine();

                // Final Mode
                sb.AppendLine("Final Protection Mode:");
                sb.AppendLine($"  {GetModeIcon(result.FinalMode)} {result.FinalMode}");
                sb.AppendLine();

                // Combined Message
                if (!string.IsNullOrEmpty(result.CombinedMessage))
                {
                    sb.AppendLine("Combined Message:");
                    sb.AppendLine("───────────────────────────────────────");
                    sb.AppendLine(result.CombinedMessage);
                    sb.AppendLine();
                }

                // Requirements
                sb.AppendLine("Requirements:");
                sb.AppendLine("───────────────────────────────────────");
                sb.AppendLine($"  {GetCheckbox(result.CaptureBeforeScreenshot)} Capture Before Screenshot");
                sb.AppendLine($"  {GetCheckbox(result.CaptureAfterScreenshot)} Capture After Screenshot");
                sb.AppendLine($"  {GetCheckbox(result.RequireComment)} Require User Comment");
            }
            else
            {
                sb.AppendLine("No rules matched the selected elements.");
                sb.AppendLine($"Final Mode: {result.FinalMode} (default)");
            }

            // Display in TaskDialog
            var dialog = new TaskDialog("Rule Evaluation Test Results")
            {
                MainInstruction = result.MatchedRules.Any() 
                    ? $"{result.MatchedRules.Count} Rule(s) Matched" 
                    : "No Rules Matched",
                MainContent = sb.ToString(),
                CommonButtons = TaskDialogCommonButtons.Ok,
                ExpandedContent = GetDetailedRuleInfo(result)
            };

            dialog.Show();
        }

        private string GetDetailedRuleInfo(RuleEvaluationResult result)
        {
            if (!result.MatchedRules.Any())
                return "No detailed information available.";

            var sb = new StringBuilder();
            sb.AppendLine("Detailed Rule Information:");
            sb.AppendLine();

            foreach (var rule in result.MatchedRules)
            {
                sb.AppendLine($"Rule ID: {rule.RuleId}");
                sb.AppendLine($"Name: {rule.Name}");
                sb.AppendLine($"Mode: {rule.Mode}");
                sb.AppendLine($"Enabled: {rule.IsEnabled}");
                
                if (rule.CategoryId.HasValue)
                    sb.AppendLine($"Category ID: {rule.CategoryId.Value}");
                
                if (!string.IsNullOrEmpty(rule.FamilyName))
                    sb.AppendLine($"Family: {rule.FamilyName}");
                
                if (!string.IsNullOrEmpty(rule.TypeName))
                    sb.AppendLine($"Type: {rule.TypeName}");
                
                if (rule.CommandIds.Any())
                    sb.AppendLine($"Commands: {string.Join(", ", rule.CommandIds)}");
                
                if (rule.Parameters.Any())
                {
                    sb.AppendLine($"Parameters ({rule.Parameters.Count}):");
                    foreach (var param in rule.Parameters)
                    {
                        sb.AppendLine($"  - {param.Key}: {param.Value.Operator} '{param.Value.Value}'");
                    }
                }
                
                if (rule.BuiltInParameters.Any())
                {
                    sb.AppendLine($"BuiltIn Parameters ({rule.BuiltInParameters.Count}):");
                    foreach (var param in rule.BuiltInParameters)
                    {
                        sb.AppendLine($"  - {param.Key}: {param.Value.Operator} '{param.Value.Value}'");
                    }
                }
                
                sb.AppendLine();
            }

            return sb.ToString();
        }

        private string GetCommandName(int commandId)
        {
            return CommandNameResolver.ResolveIdToName(commandId) ?? $"Command_{commandId}";
        }

        private string GetModeIcon(ProtectionMode mode)
        {
            return mode switch
            {
                ProtectionMode.Notify => "🔵",
                ProtectionMode.Assist => "🟡",
                ProtectionMode.Protect => "🔴",
                _ => "⚪"
            };
        }

        private string GetCheckbox(bool isChecked)
        {
            return isChecked ? "☑" : "☐";
        }
    }
}
