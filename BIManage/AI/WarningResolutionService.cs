using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using BIManage.Common.Helpers;

namespace BIManage.AI
{
    /// <summary>
    /// Diagnoses Revit model warnings and formats actionable resolution instructions
    /// for injection into the AI system prompt. Mirrors the ElementVisibilityService pattern.
    /// </summary>
    public static class WarningResolutionService
    {
        // ── Keyword detection ────────────────────────────────────────────────

        private static readonly string[] WarningKeywords =
        {
            "warning", "warnings", "resolve warning", "fix warning", "model warning",
            "model warnings", "errors in model", "model issues", "model errors",
            "health check", "model health", "duplicate", "unenclosed",
            "not connected", "disconnected", "overlap", "room not enclosed",
            "how many warnings", "list warnings", "show warnings"
        };

        public static bool IsWarningQuery(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return false;
            var lower = userMessage.ToLowerInvariant();
            return WarningKeywords.Any(kw => lower.Contains(kw));
        }

        // ── Main diagnosis ───────────────────────────────────────────────────

        /// <summary>
        /// Reads all warnings from the active document, categorizes them,
        /// and returns a formatted diagnosis block for AI prompt injection.
        /// Returns null if not a warning query or no document available.
        /// Must be called on the Revit main thread.
        /// </summary>
        public static string? DiagnoseAndFormat(Document doc, string userMessage)
        {
            if (doc == null || doc.IsFamilyDocument)
                return null;

            if (!IsWarningQuery(userMessage))
                return null;

            try
            {
                var warnings = doc.GetWarnings();
                if (warnings == null || warnings.Count == 0)
                {
                    return FormatNoWarnings(doc.Title);
                }

                var groups = CategorizeWarnings(warnings, doc);
                return FormatWarningDiagnosis(groups, warnings.Count, doc.Title);
            }
            catch (Exception ex)
            {
                return $"WARNING RESOLUTION DIAGNOSIS\nERROR: Failed to read warnings from document: {ex.Message}";
            }
        }

        // ── Categorization ───────────────────────────────────────────────────

        private static List<WarningGroup> CategorizeWarnings(IList<FailureMessage> warnings, Document doc)
        {
            var groupMap = new Dictionary<string, WarningGroup>();

            foreach (var warning in warnings)
            {
                try
                {
                    var failId = warning.GetFailureDefinitionId();
                    var description = warning.GetDescriptionText();
                    var category = ClassifyWarning(failId, description);
                    var failingElements = warning.GetFailingElements();

                    if (!groupMap.TryGetValue(category, out var group))
                    {
                        group = new WarningGroup
                        {
                            Category = category,
                            FixInstruction = GetFixInstruction(category)
                        };
                        groupMap[category] = group;
                    }

                    group.Count++;

                    // Collect sample element info (up to 5 per category)
                    if (group.SampleElements.Count < 5 && failingElements.Count > 0)
                    {
                        foreach (var elemId in failingElements.Take(3))
                        {
                            try
                            {
                                var elem = doc.GetElement(elemId);
                                if (elem != null)
                                {
                                    group.SampleElements.Add(new ElementSample
                                    {
                                        Id = (int)elemId.GetIdValue(),
                                        Category = elem.Category?.Name ?? "Unknown",
                                        Name = elem.Name ?? "Unnamed"
                                    });
                                }
                            }
                            catch { /* skip unreadable element */ }
                        }
                    }

                    // Keep first unique description per group
                    if (group.SampleDescriptions.Count < 3 && !group.SampleDescriptions.Contains(description))
                        group.SampleDescriptions.Add(description);
                }
                catch { /* skip unreadable warning */ }
            }

            return groupMap.Values.OrderByDescending(g => g.Count).ToList();
        }

        private static string ClassifyWarning(FailureDefinitionId failId, string description)
        {
            if (failId == BuiltInFailures.OverlapFailures.DuplicateInstances)
                return "Duplicate Elements";

            if (failId == BuiltInFailures.RoomFailures.RoomNotEnclosed)
                return "Unenclosed Rooms";

            if (failId == BuiltInFailures.RoomFailures.RoomTagNotInRoom)
                return "Room Tags Not In Room";

            if (failId == BuiltInFailures.ConnectorFailures.OpenConnector ||
                failId == BuiltInFailures.ConnectorFailures.ElementsAreDisconnected)
                return "Connectivity Issues";

            var lower = description?.ToLowerInvariant() ?? "";

            if (lower.Contains("overlap"))
                return "Overlapping Elements";

            if (lower.Contains("join") || lower.Contains("wall join"))
                return "Join Issues";

            if (lower.Contains("room") || lower.Contains("area"))
                return "Room/Area Issues";

            if (lower.Contains("stair") || lower.Contains("railing"))
                return "Stair/Railing Issues";

            if (lower.Contains("dimension") || lower.Contains("constraint"))
                return "Dimension/Constraint Issues";

            if (lower.Contains("family") || lower.Contains("type"))
                return "Family/Type Issues";

            if (lower.Contains("level") || lower.Contains("offset"))
                return "Level/Offset Issues";

            return "Other Warnings";
        }

        // ── Fix instructions per category ────────────────────────────────────

        private static string GetFixInstruction(string category)
        {
            switch (category)
            {
                case "Duplicate Elements":
                    return "Select duplicates via Manage > Warnings, identify which is correct, and delete the duplicate. " +
                           "Use 'Select All Instances in View' to batch-select and review.";

                case "Unenclosed Rooms":
                    return "Open plan view, check room boundaries for gaps. Use Room Separator lines to close gaps. " +
                           "Check that bounding walls reach the ceiling/roof. Go to Manage > Room & Area > Room Boundaries.";

                case "Room Tags Not In Room":
                    return "Drag room tags back inside their room boundaries. If rooms were deleted, remove orphan tags.";

                case "Connectivity Issues":
                    return "Open a plan or 3D view, select disconnected elements, and use Tab to cycle connections. " +
                           "Use 'Connect Into' or move endpoints to snap. Check pipe/duct sizing at connection points.";

                case "Overlapping Elements":
                    return "Review overlapping elements in a 3D view. Delete or move the overlapping instance. " +
                           "Use Interference Check (Collaborate > Interference Check) to find all overlaps.";

                case "Join Issues":
                    return "Select the affected walls, go to Modify > Wall Joins, and choose the appropriate join type. " +
                           "Use 'Unjoin Geometry' then 'Join Geometry' to reset problematic joins.";

                case "Room/Area Issues":
                    return "Check room boundaries in plan views. Ensure walls are set as Room Bounding. " +
                           "Verify area scheme assignments in Area Plans.";

                case "Stair/Railing Issues":
                    return "Edit the stair or railing in place. Check run/landing connections and railing host assignments. " +
                           "Rebuild the stair if path is corrupted.";

                case "Dimension/Constraint Issues":
                    return "Review constrained elements in affected views. Remove or reassign broken constraints. " +
                           "Check that referenced elements still exist and are in the correct location.";

                case "Family/Type Issues":
                    return "Open the family in Family Editor and check for errors. Reload the family if corrupt. " +
                           "Purge and reimport if the type definition is missing.";

                case "Level/Offset Issues":
                    return "Check element base/top constraints and offsets. Ensure elements reference valid levels. " +
                           "Reconnect elements to correct levels if they became disassociated.";

                default:
                    return "Open Manage > Warnings, select the warning, and click 'Show' to navigate to the affected elements. " +
                           "Review and fix each issue based on the warning description.";
            }
        }

        // ── Formatting ───────────────────────────────────────────────────────

        private static string FormatNoWarnings(string? modelTitle)
        {
            return $@"WARNING RESOLUTION DIAGNOSIS
Model: {modelTitle ?? "Unknown"}

WARNINGS FOUND: None

This model has zero warnings. The model is in a healthy state.

RECOMMENDATION:
Continue to monitor warnings after major edits, especially after:
- Linking or unlinking models
- Mass copy/move operations
- Family reloading
- Worksharing synchronization";
        }

        private static string FormatWarningDiagnosis(List<WarningGroup> groups, int totalCount, string? modelTitle)
        {
            var sb = new StringBuilder();
            sb.AppendLine("WARNING RESOLUTION DIAGNOSIS");
            sb.AppendLine($"Model: {modelTitle ?? "Unknown"}");
            sb.AppendLine();
            sb.AppendLine($"WARNINGS FOUND: {totalCount} total across {groups.Count} categories");
            sb.AppendLine();

            // Health rating
            string healthRating;
            if (totalCount == 0) healthRating = "Excellent";
            else if (totalCount < 10) healthRating = "Good";
            else if (totalCount < 50) healthRating = "Fair - attention needed";
            else if (totalCount < 200) healthRating = "Poor - significant cleanup needed";
            else healthRating = "Critical - immediate attention required";

            sb.AppendLine($"MODEL HEALTH: {healthRating}");
            sb.AppendLine();

            int groupIndex = 1;
            foreach (var group in groups)
            {
                sb.AppendLine($"CATEGORY {groupIndex}: {group.Category} ({group.Count} warning{(group.Count != 1 ? "s" : "")})");

                if (group.SampleDescriptions.Count > 0)
                {
                    sb.AppendLine("  Sample warnings:");
                    foreach (var desc in group.SampleDescriptions)
                        sb.AppendLine($"    - {desc}");
                }

                if (group.SampleElements.Count > 0)
                {
                    sb.AppendLine("  Affected elements (sample):");
                    foreach (var elem in group.SampleElements)
                        sb.AppendLine($"    - ID {elem.Id}: {elem.Category} '{elem.Name}'");
                }

                sb.AppendLine($"  FIX: {group.FixInstruction}");
                sb.AppendLine();
                groupIndex++;
            }

            sb.AppendLine("PRIORITY ORDER: Fix categories with the highest count first.");
            sb.AppendLine("TIP: Use Manage > Warnings to navigate directly to each warning.");

            return sb.ToString();
        }

        // ── Internal types ───────────────────────────────────────────────────

        private class WarningGroup
        {
            public string Category { get; set; } = string.Empty;
            public int Count { get; set; }
            public string FixInstruction { get; set; } = string.Empty;
            public List<ElementSample> SampleElements { get; } = new List<ElementSample>();
            public List<string> SampleDescriptions { get; } = new List<string>();
        }

        private class ElementSample
        {
            public int Id { get; set; }
            public string Category { get; set; } = string.Empty;
            public string Name { get; set; } = string.Empty;
        }
    }
}
