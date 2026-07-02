using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using BIManage.Common.Helpers;

namespace BIManage.AI
{
    /// <summary>
    /// Runs a full multi-check visibility diagnosis on a Revit element in a specific view.
    /// Mirrors the Python pyRevit script logic but executed directly inside the C# plugin
    /// against the live Revit Document — zero user input required.
    ///
    /// All checks run — every confirmed issue is reported:
    ///   0.  Element existence — fails immediately if element ID not found in the document
    ///   1a. Temporary Hide/Isolate (session-only, sunglasses icon)
    ///   1b. Permanent Element Hide (Hide in View → Elements)
    ///   2.  Category hidden in Visibility/Graphics
    ///   3.  Workset hidden
    ///   4.  View Filter hiding element
    ///   5.  Outside Crop Region
    ///   6.  View Template hiding this category
    ///   7.  View Range out of bounds (plan views only)
    ///       — Grid elements: uses Grid.GetExtents() for vertical Z-range check
    ///       — Other elements: uses get_BoundingBox(null) for Z comparison
    ///   8.  Design Option mismatch
    ///   9.  View Discipline mismatch (advisory)
    ///  10.  Built-in 'Visible' parameter (ELEM_VISIBLE_PARAM) set to No (FamilyInstance only)
    ///  11.  Annotation outside Annotation Crop Region (Annotation category only)
    ///  12.  Ceiling projection blocking element (plan views, non-wireframe)
    ///  13.  Detail Level mismatch — geometry absent at current detail level
    ///  14.  Phase / Phase Filter mismatch — element not yet created or already demolished
    ///  15.  Subcategory hidden in Visibility/Graphics (parent ON but subcategory OFF)
    ///  16.  Graphics override — projection colour = background, or 100% transparent / halftone
    ///  17.  Section Box clipping (3D views only)
    ///  For IndependentTag: host element is diagnosed first, then the tag itself.
    ///
    /// View resolution order for DiagnoseAndFormat:
    ///   1. View name mentioned in user message
    ///   2. Active view passed in (UIDocument.ActiveView — Revit main thread)
    ///   3. Generic guidance (no view-specific checks possible)
    /// </summary>
    public static class ElementVisibilityService
    {
        // ── Regex patterns for extracting IDs and view names from user messages ─

        // Matches:
        //   "element 123456", "element id 123456", "id 123456", "#123456", "id:123456"
        //   "123456 this element", "123456 is not visible"  (bare number at start/anywhere)
        // Priority: named prefix first, then bare number fallback
        private static readonly Regex ElementIdPrefixPattern = new Regex(
            @"(?:element\s+(?:id\s+)?|id\s*:?\s*|#)(\d{4,10})",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ElementIdBarePattern = new Regex(
            @"(?<!\d)(\d{4,10})(?!\d)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Matches view names in phrases like:
        //   "in level 0", "in level 1", "in Floor Plan Level 1", "in view Ground Floor",
        //   "in the view Roof Plan", "on level 2", "level 0 view"
        private static readonly Regex ViewNamePattern = new Regex(
            @"(?:in|on)\s+(?:the\s+)?(?:view\s+|plan\s+)?[""']?([A-Za-z0-9 \-_./()]{2,50}?)[""']?(?:\s*\?|$|,|\.|and\s)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Also matches "level X" anywhere in the message as a fallback view hint
        private static readonly Regex LevelNamePattern = new Regex(
            @"\blevel\s+(\w+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Keywords that indicate a visibility question.
        // These are regex patterns tested against the lowercased message.
        private static readonly string[] VisibilityKeywords =
        {
            "why can't i see", "why cant i see", "why is element", "not visible",
            "can't see element", "cant see element", "element not showing",
            "element hidden", "hidden element", "visibility", "not showing",
            "element.*not.*visible", "invisible", "why.*hidden", "find element",
            "element.*missing", "missing element", "where is element",
            // Natural phrasing: "find 123456 ... not visible", "can you find ... why is it not"
            @"find\s+\d+.*not\s+visible", @"find\s+\d+.*not\s+showing",
            @"find\s+\d+.*hidden", @"find\s+\d+.*can't\s+see",
            @"\d{4,}.*not\s+visible", @"\d{4,}.*not\s+showing",
            @"\d{4,}.*why.*hidden",
            // Grid-specific visibility patterns
            "grid.*not.*visible", "grid.*not.*showing", "grid.*hidden",
            "can't see grid", "cant see grid", "grid missing", "where is grid",
            "grid not showing", "why.*grid.*visible"
        };

        // ── Public API ──────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the user message appears to be asking about element visibility.
        /// </summary>
        public static bool IsVisibilityQuery(string userMessage)
        {
            if (string.IsNullOrWhiteSpace(userMessage)) return false;
            var lower = userMessage.ToLowerInvariant();
            return VisibilityKeywords.Any(k => Regex.IsMatch(lower, k));
        }

        /// <summary>
        /// Tries to extract a single element ID from the user message.
        /// First tries named prefixes ("element 123", "id 123", "#123"),
        /// then falls back to any standalone 4-10 digit number.
        /// Returns null if none found.
        /// </summary>
        public static int? TryParseElementId(string userMessage)
        {
            var ids = TryParseAllElementIds(userMessage);
            return ids.Count > 0 ? ids[0] : (int?)null;
        }

        /// <summary>
        /// Extracts ALL element IDs mentioned in the user message.
        /// Named prefixes ("element 123", "id 123", "#123") are collected first,
        /// then bare standalone 4-10 digit numbers are added if not already found.
        /// Returns an empty list if none found.
        /// </summary>
        public static List<int> TryParseAllElementIds(string userMessage)
        {
            var result = new List<int>();
            var seen   = new HashSet<int>();

            // Collect all prefixed IDs first ("element 321970 and element 456789")
            foreach (Match m in ElementIdPrefixPattern.Matches(userMessage))
            {
                if (int.TryParse(m.Groups[1].Value, out var id) && seen.Add(id))
                    result.Add(id);
            }

            // If no prefixed IDs found, fall back to bare standalone numbers
            if (result.Count == 0)
            {
                foreach (Match m in ElementIdBarePattern.Matches(userMessage))
                {
                    if (int.TryParse(m.Groups[1].Value, out var id) && seen.Add(id))
                        result.Add(id);
                }
            }

            return result;
        }

        /// <summary>
        /// Tries to extract a view name from the user message.
        /// Tries "in/on [view] Name" pattern first, then "level X" as fallback.
        /// Returns null if none found.
        /// </summary>
        public static string? TryParseViewName(string userMessage)
        {
            // Try "in Floor Plan Level 1", "in view Ground Floor", "in level 0"
            var match = ViewNamePattern.Match(userMessage);
            if (match.Success)
                return match.Groups[1].Value.Trim();

            // Fallback: "level 0", "level 1", "level ground" anywhere in the message
            match = LevelNamePattern.Match(userMessage);
            if (match.Success)
                return "Level " + match.Groups[1].Value.Trim();

            return null;
        }

        /// <summary>
        /// Runs the full multi-check diagnosis and returns a formatted context block
        /// ready to be injected into the AI system prompt.
        ///
        /// View resolution priority:
        ///   1. View named in the user message (e.g. "in Floor Plan Level 1")
        ///   2. <paramref name="activeView"/> — the view currently open in Revit (default fallback)
        ///   3. null — generic checks listed without view-specific diagnosis
        ///
        /// <paramref name="activeView"/> should be <c>UIDocument.ActiveView</c>, captured on
        /// the Revit main thread before any async continuation.
        /// </summary>
        public static string? DiagnoseAndFormat(Document doc, string userMessage, View? activeView = null)
        {
            if (doc == null || doc.IsFamilyDocument) return null;

            var elementIds = TryParseAllElementIds(userMessage);
            if (elementIds.Count == 0)
                return BuildNoIdBlock(userMessage);

            // ── View resolution (shared across all elements) ──────────────────────
            var viewName = TryParseViewName(userMessage);
            View? view = null;
            string viewSource;

            if (!string.IsNullOrEmpty(viewName))
            {
                view = FindViewByName(doc, viewName);
                if (view != null)
                {
                    viewSource = "named view";
                }
                else if (activeView != null && !activeView.IsTemplate)
                {
                    // View name parsed but not found — fall back to active view
                    view = activeView;
                    viewSource = "active view (named view not found)";
                }
                else
                {
                    viewSource = "not identified";
                }
            }
            else if (activeView != null && !activeView.IsTemplate)
            {
                view = activeView;
                viewSource = "active view";
            }
            else
            {
                viewSource = "not identified";
            }

            var viewLabel = view?.Name ?? viewName ?? activeView?.Name ?? "unknown";

            // ── Diagnose each element and collect results ─────────────────────────
            var sb = new StringBuilder();

            if (elementIds.Count > 1)
                sb.AppendLine($"ELEMENT VISIBILITY DIAGNOSIS — {elementIds.Count} ELEMENTS");
            else
                sb.AppendLine("ELEMENT VISIBILITY DIAGNOSIS");

            // State the view once at the top
            if (view != null)
            {
                sb.AppendLine(viewSource == "active view"
                    ? $"View       : {viewLabel} [active view — no view name was mentioned]"
                    : $"View       : {viewLabel} [{viewSource}]");
            }
            else
            {
                sb.AppendLine($"View       : {viewLabel}{(viewName != null ? " (view not found in model)" : " (no view specified)")}");
            }
            sb.AppendLine();

            for (int ei = 0; ei < elementIds.Count; ei++)
            {
                int elemIdValue = elementIds[ei];

                if (elementIds.Count > 1)
                    sb.AppendLine($"════════════════════════════════════════");

                // ── CHECK 0: Element existence ────────────────────────────────────
                var elem = doc.GetElement(new ElementId(elemIdValue));
                if (elem == null)
                {
                    sb.AppendLine(elementIds.Count > 1
                        ? $"ELEMENT {ei + 1} (ID {elemIdValue}): NOT FOUND in document"
                        : $"Element ID {elemIdValue} does not exist in the current document.");
                    sb.AppendLine("• Element may have been deleted, or this ID belongs to a linked file");
                    sb.AppendLine();
                    continue;
                }

                sb.AppendLine($"Element ID : {elemIdValue}");
                sb.AppendLine($"Category   : {elem.Category?.Name ?? "Unknown"}");
                sb.AppendLine($"Name       : {elem.Name}");
                sb.AppendLine();

                if (view == null)
                {
                    // Generic guidance — no view resolved
                    sb.AppendLine("NOTE: View not identified. Common causes (no live checks run):");
                    sb.AppendLine("• Temporarily hidden — View Control Bar → Reset Temporary Hide/Isolate");
                    sb.AppendLine("• Permanently hidden — use Reveal Hidden Elements (light-bulb icon)");
                    sb.AppendLine("• Category off in Visibility/Graphics (VG shortcut)");
                    sb.AppendLine("• Workset hidden in view");
                    sb.AppendLine("• View Filter suppressing this category");
                    sb.AppendLine("• View Range (plan views) excludes element's Z elevation");
                    sb.AppendLine("• Design Option or View Discipline mismatch");
                    sb.AppendLine();
                    sb.AppendLine("Include the view name for a pinpoint diagnosis.");
                    sb.AppendLine("Example: \"Why can't I see element 12345 in Floor Plan Level 1?\"");
                    // Same generic block for all elements when no view available
                    break;
                }

                // Run all 17 checks
                var findings = Diagnose(doc, elem, view);


                if (findings.Count > 0)
                {
                    sb.AppendLine($"ISSUES FOUND: {findings.Count} visibility problem(s) confirmed");
                    sb.AppendLine();
                    for (int i = 0; i < findings.Count; i++)
                    {
                        var (check, fix) = findings[i];
                        sb.AppendLine($"ISSUE {i + 1}: {check}");
                        sb.AppendLine($"FIX {i + 1}:");
                        sb.AppendLine(fix);
                        if (i < findings.Count - 1) sb.AppendLine();
                    }
                }
                else
                {
                    sb.AppendLine("ISSUES FOUND: None — no standard visibility issue detected by all 17 checks.");
                    sb.AppendLine();
                    sb.AppendLine("ADDITIONAL CHECKS TO SUGGEST:");
                    sb.AppendLine("• Element may have no visible geometry (some placeholder families render nothing)");
                    sb.AppendLine("• Element may be in a linked Revit file — check VG → Revit Links tab");
                    sb.AppendLine($"• Phase Filter mismatch — check View Properties → Phase / Phase Filter for view '{view.Name}'");
                    sb.AppendLine("• Element may be very small or its graphics overridden to match the background colour");
                }

                sb.AppendLine();
            }

            if (elementIds.Count > 1)
                sb.AppendLine("════════════════════════════════════════");

            sb.AppendLine("Report EVERY issue listed above. Do NOT speculate beyond these findings.");

            return sb.ToString();
        }

        // ── Full multi-check diagnosis ───────────────────────────────────────────

        /// <summary>
        /// Runs ALL visibility checks and returns every confirmed issue as a list.
        /// Each entry is (checkLabel, fixInstruction).
        /// An empty list means no visibility issue was detected.
        ///
        /// For IndependentTag:
        ///   1. Diagnoses the host element first — if host is hidden, reports "TAG NOT VISIBLE because HOST is hidden"
        ///   2. Then diagnoses the tag itself for tag-specific issues
        /// </summary>
        private static List<(string check, string fix)> Diagnose(Document doc, Element elem, View view)
        {
            var findings = new List<(string check, string fix)>();

            if (elem is IndependentTag tag)
            {
                // ── Step 1: Diagnose the HOST element first ───────────────────────
                Element? hostElem = null;
                try
                {
#if REVIT2022_OR_GREATER
                    // GetTaggedLocalElementIds() requires Revit 2022+ API
                    var taggedIds = tag.GetTaggedLocalElementIds();
                    var hostId    = taggedIds?.FirstOrDefault();
                    hostElem  = hostId != null ? doc.GetElement(hostId) : null;
#endif
                }
                catch { }

                if (hostElem != null)
                {
                    var hostFindings = DiagnoseElement(doc, hostElem, view);
                    if (hostFindings.Count > 0)
                    {
                        // Report host issues with the "TAG NOT VISIBLE because HOST is hidden" framing
                        foreach (var (hc, hf) in hostFindings)
                            findings.Add((
                                $"TAG NOT VISIBLE — HOST ELEMENT (ID {hostElem.Id.GetIdValue()}) is hidden: {hc}",
                                $"A tag is invisible when its host element is hidden.\n" +
                                $"Fix the host element (ID {hostElem.Id.GetIdValue()}) first:\n\n{hf}"
                            ));
                    }
                }

                // ── Step 2: Diagnose the tag element itself ────────────────────────
                var tagFindings = DiagnoseElement(doc, tag, view);
                foreach (var (tc, tf) in tagFindings)
                    findings.Add(($"TAG-SPECIFIC: {tc}", tf));

                return findings;
            }

            // Not a tag — run all checks directly
            findings.AddRange(DiagnoseElement(doc, elem, view));
            return findings;
        }

        /// <summary>
        /// Runs ALL visibility checks (1a, 1b, 2–17) against elem in the given view.
        /// Returns every confirmed issue — does NOT stop at the first match.
        /// </summary>
        private static List<(string check, string fix)> DiagnoseElement(Document doc, Element elem, View view)
        {
            var findings = new List<(string check, string fix)>();
            var elemId   = elem.Id.GetIdValue();
            var catName  = elem.Category?.Name ?? "Unknown";

            // ════════════════════════════════════════════════════════════════════
            // CHECK 1a — Temporary Hide/Isolate (session-only, sunglasses icon)
            // ════════════════════════════════════════════════════════════════════
            try
            {
                if (!view.IsElementVisibleInTemporaryViewMode(
                        TemporaryViewMode.TemporaryHideIsolate, elem.Id))
                    findings.Add((
                        "[TEMPORARY HIDE] Element is hidden by Temporary Hide/Isolate mode (session-only, NOT saved)",
                        "The view's Temporary Hide/Isolate mode is hiding this element.\n" +
                        "This resets when Revit is closed — it is NOT saved in the project file.\n\n" +
                        "To restore:\n" +
                        "1. View Control Bar (bottom of view) → click the sunglasses icon\n" +
                        "2. Choose 'Reset Temporary Hide/Isolate'"
                    ));
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 1b — Permanent Element Hide (Hide in View → Elements)
            // ════════════════════════════════════════════════════════════════════
            try
            {
                if (elem.IsHidden(view))
                    findings.Add((
                        "[PERMANENT HIDE] Element is hidden via Hide in View → Elements (saved in project file)",
                        $"This hide persists across sessions — it is saved in the project file.\n\n" +
                        $"To unhide element {elemId}:\n" +
                        "1. View tab → Graphics panel → click the light-bulb icon (Reveal Hidden Elements)\n" +
                        "   The view tints magenta and the element appears highlighted in bright pink/red\n" +
                        "2. Click the element to select it\n" +
                        "3. Right-click → Unhide in View → Elements\n" +
                        "4. Click the light-bulb icon again to exit Reveal Hidden mode"
                    ));
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 2 — Category Hidden in Visibility/Graphics
            // ════════════════════════════════════════════════════════════════════
            try
            {
                if (elem.Category != null && view.GetCategoryHidden(elem.Category.Id))
                    findings.Add((
                        $"[CATEGORY HIDDEN] '{catName}' category is turned OFF in Visibility/Graphics",
                        $"Every '{catName}' element is hidden because the category checkbox is unchecked.\n\n" +
                        "To fix:\n" +
                        "1. Press VG (or VV) to open Visibility/Graphics Overrides\n" +
                        $"2. Model Categories tab → find '{catName}' → tick the Visibility checkbox\n" +
                        "3. Click OK\n" +
                        "NOTE: Affects ALL elements of this category in this view."
                    ));
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 3 — Workset Hidden
            // ════════════════════════════════════════════════════════════════════
            try
            {
                var wsId = elem.WorksetId;
                if (wsId != null && wsId != WorksetId.InvalidWorksetId && !view.IsWorksetVisible(wsId))
                {
                    var wsName = doc.GetWorksetTable()?.GetWorkset(wsId)?.Name ?? wsId.ToString();
                    findings.Add((
                        $"[WORKSET HIDDEN] Workset '{wsName}' is hidden in this view",
                        $"The element's workset '{wsName}' is set to hidden for this view.\n\n" +
                        "To fix:\n" +
                        "1. Press VG (or VV) → Worksets tab\n" +
                        $"2. Find '{wsName}' → set it to Visible\n" +
                        "3. Click OK"
                    ));
                }
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 4 — View Filter hiding this element
            // Checks category match AND that the filter rule actually passes this element
            // ════════════════════════════════════════════════════════════════════
            try
            {
                foreach (var fid in view.GetFilters())
                {
                    if (view.GetFilterVisibility(fid)) continue;
                    if (doc.GetElement(fid) is not ParameterFilterElement filt) continue;
                    if (elem.Category == null || !filt.GetCategories().Contains(elem.Category.Id)) continue;

                    bool ruleMatches = true;
                    try
                    {
                        // GetElementFilter() returns the compiled Revit filter object.
                        // Use a collector scoped to a single-element list to test if the rule matches.
                        var elemFilter = filt.GetElementFilter();
                        if (elemFilter != null)
                            ruleMatches = new FilteredElementCollector(doc, new List<ElementId> { elem.Id })
                                .WherePasses(elemFilter)
                                .Any();
                    }
                    catch { }

                    if (ruleMatches)
                        findings.Add((
                            $"[VIEW FILTER] Filter '{filt.Name}' has Visibility OFF and matches this element",
                            $"Filter '{filt.Name}' is active, its visibility is unchecked, and its rule matches this element.\n\n" +
                            "To fix:\n" +
                            "1. Press VG (or VV) → Filters tab\n" +
                            $"2. Find '{filt.Name}' → tick the Visibility checkbox\n" +
                            "   OR edit/delete the filter if it is incorrectly targeting this element\n" +
                            "3. Click OK"
                        ));
                }
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 5 — Model element outside Crop Region (non-annotation only)
            // Annotations have their own dedicated check (Check 11).
            //
            // view.CropBox is in VIEW-LOCAL space. elem.get_BoundingBox(view) is
            // also in view-local space but returns null for hidden elements.
            // elem.get_BoundingBox(null) is in WORLD space — always available.
            //
            // Strategy: transform world-space bbox corners into view-local space
            // using view.CropBox.Transform.Inverse, then compare against CropBox.
            //
            // Model elements are only hidden when COMPLETELY outside the crop box.
            // Not applied to 3D views with an active Section Box (Check 17 covers that).
            // ════════════════════════════════════════════════════════════════════
            try
            {
                bool is3DWithSectionBox = view is View3D v3d && v3d.IsSectionBoxActive;
                bool isAnnotation5 = elem.Category?.CategoryType == CategoryType.Annotation;

                if (view.CropBoxActive && !is3DWithSectionBox && !isAnnotation5)
                {
                    var worldBbox = elem.get_BoundingBox(null);
                    if (worldBbox != null)
                    {
                        var crop      = view.CropBox;
                        var invTransf = crop.Transform.Inverse;

                        // Transform all 8 corners of world bbox into view-local space,
                        // then take min/max to get the view-local bounding box.
                        var corners = new[]
                        {
                            new XYZ(worldBbox.Min.X, worldBbox.Min.Y, worldBbox.Min.Z),
                            new XYZ(worldBbox.Max.X, worldBbox.Min.Y, worldBbox.Min.Z),
                            new XYZ(worldBbox.Min.X, worldBbox.Max.Y, worldBbox.Min.Z),
                            new XYZ(worldBbox.Max.X, worldBbox.Max.Y, worldBbox.Min.Z),
                            new XYZ(worldBbox.Min.X, worldBbox.Min.Y, worldBbox.Max.Z),
                            new XYZ(worldBbox.Max.X, worldBbox.Min.Y, worldBbox.Max.Z),
                            new XYZ(worldBbox.Min.X, worldBbox.Max.Y, worldBbox.Max.Z),
                            new XYZ(worldBbox.Max.X, worldBbox.Max.Y, worldBbox.Max.Z),
                        };

                        double lMinX = double.MaxValue, lMinY = double.MaxValue, lMinZ = double.MaxValue;
                        double lMaxX = double.MinValue, lMaxY = double.MinValue, lMaxZ = double.MinValue;
                        foreach (var c in corners)
                        {
                            var lc = invTransf.OfPoint(c);
                            if (lc.X < lMinX) lMinX = lc.X;
                            if (lc.Y < lMinY) lMinY = lc.Y;
                            if (lc.Z < lMinZ) lMinZ = lc.Z;
                            if (lc.X > lMaxX) lMaxX = lc.X;
                            if (lc.Y > lMaxY) lMaxY = lc.Y;
                            if (lc.Z > lMaxZ) lMaxZ = lc.Z;
                        }

                        bool fullyLeft   = lMaxX < crop.Min.X;
                        bool fullyRight  = lMinX > crop.Max.X;
                        bool fullyBelow  = lMaxY < crop.Min.Y;
                        bool fullyAbove  = lMinY > crop.Max.Y;
                        bool fullyFront  = view is View3D && lMaxZ < crop.Min.Z;
                        bool fullyBehind = view is View3D && lMinZ > crop.Max.Z;

                        if (fullyLeft || fullyRight || fullyBelow || fullyAbove || fullyFront || fullyBehind)
                        {
                            string dir;
                            if (fullyLeft)        dir = "left of";
                            else if (fullyRight)  dir = "right of";
                            else if (fullyBelow)  dir = "below";
                            else if (fullyAbove)  dir = "above";
                            else if (fullyFront)  dir = "in front of";
                            else                  dir = "behind";

                            findings.Add((
                                $"[CROP REGION] Element is outside the crop boundary ({dir} the crop box)",
                                "The element's position is outside the view's active crop region.\n\n" +
                                "Option A — Expand the crop region:\n" +
                                "  Click the crop boundary in the view → drag blue handles outward\n\n" +
                                "Option B — Disable crop temporarily:\n" +
                                "  View Properties (VP) → Extents → uncheck 'Crop View'\n\n" +
                                "Option C — Use Show in View:\n" +
                                "  Right-click element in schedules or Project Browser → Show in View"
                            ));
                        }
                    }
                }
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 6 — View Template hiding this category
            // Only fires if the template specifically has this category turned off
            // ════════════════════════════════════════════════════════════════════
            try
            {
                if (view.ViewTemplateId != ElementId.InvalidElementId &&
                    doc.GetElement(view.ViewTemplateId) is View tmpl &&
                    elem.Category != null)
                {
                    bool tmplHides = false;
                    try { tmplHides = tmpl.GetCategoryHidden(elem.Category.Id); } catch { }

                    if (tmplHides)
                        findings.Add((
                            $"[VIEW TEMPLATE] Template '{tmpl.Name}' has '{catName}' category turned OFF",
                            $"The view template '{tmpl.Name}' overrides V/G and hides the '{catName}' category.\n" +
                            "Manually changing VG on this view has NO effect while a template is applied.\n\n" +
                            "Option A — Edit the template:\n" +
                            "  View tab → View Templates → Manage View Templates\n" +
                            $"  Open '{tmpl.Name}' → find '{catName}' → tick Visibility\n\n" +
                            "Option B — Remove the template from this view:\n" +
                            "  View Properties (VP) → Identity Data → View Template → '<None>'"
                        ));
                }
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 7 — View Range out of bounds (plan views only)
            // Grid elements: uses Grid.GetExtents() for vertical Z-range check
            // All other elements: uses get_BoundingBox(null) for Z comparison
            // Reports every plane violation found, not just the first
            // ════════════════════════════════════════════════════════════════════
            try
            {
                if (view is ViewPlan planView)
                {
                    var vr = planView.GetViewRange();
                    if (doc.GetElement(vr.GetLevelId(PlanViewPlane.TopClipPlane))    is Level topLvl &&
                        doc.GetElement(vr.GetLevelId(PlanViewPlane.CutPlane))        is Level cutLvl &&
                        doc.GetElement(vr.GetLevelId(PlanViewPlane.BottomClipPlane)) is Level botLvl &&
                        doc.GetElement(vr.GetLevelId(PlanViewPlane.ViewDepthPlane))  is Level dptLvl)
                    {
                        double topZ = topLvl.Elevation + vr.GetOffset(PlanViewPlane.TopClipPlane);
                        double cutZ = cutLvl.Elevation + vr.GetOffset(PlanViewPlane.CutPlane);
                        double botZ = botLvl.Elevation + vr.GetOffset(PlanViewPlane.BottomClipPlane);
                        double dptZ = dptLvl.Elevation + vr.GetOffset(PlanViewPlane.ViewDepthPlane);

                        var units = doc.GetUnits();
                        string unitLabel = "ft";
                        FormatOptions? fmtOpts = null;
                        try
                        {
                            fmtOpts   = units.GetFormatOptions(SpecTypeId.Length);
                            unitLabel = LabelUtils.GetLabelForUnit(fmtOpts.GetUnitTypeId());
                        }
                        catch { }

                        string FmtZ(double z)
                        {
                            try { return UnitUtils.ConvertFromInternalUnits(z, fmtOpts!.GetUnitTypeId()).ToString("F2"); }
                            catch { return $"{z:F2}"; }
                        }

                        // ── Grid: use GetExtents() for the vertical range check ──────────
                        if (elem is Grid grid)
                        {
                            try
                            {
                                var extents = grid.GetExtents();
                                if (extents != null)
                                {
                                    double gMin = extents.MinimumPoint.Z;
                                    double gMax = extents.MaximumPoint.Z;

                                    // Grid completely below view bottom limit
                                    if (gMax < botZ)
                                        findings.Add((
                                            $"[VIEW RANGE — GRID] Grid '{grid.Name}' is BELOW the view's Bottom Clip Plane " +
                                            $"(grid top {FmtZ(gMax)} {unitLabel}, view bottom {FmtZ(botZ)} {unitLabel})",
                                            $"The grid's vertical extents do not reach the plan view range.\n\n" +
                                            "To fix:\n" +
                                            "Option A — Extend the grid vertically:\n" +
                                            $"  Select grid '{grid.Name}' in a section/elevation view → drag top grip upward past {FmtZ(botZ)} {unitLabel}\n\n" +
                                            "Option B — Lower the view's Bottom Clip Plane:\n" +
                                            "  View Properties (VP) → View Range → Edit → lower Bottom Clip Plane\n\n" +
                                            "ALSO CHECK:\n" +
                                            "• Grid 2D grip may be shortened (reduced) in this view — select the grid, click the 2D/3D toggle handle\n" +
                                            "• Category 'Grids' may be off in VG → Model Categories → Grids\n" +
                                            "• Phase settings mismatch — check View Properties → Phase / Phase Filter"
                                        ));

                                    // Grid completely above view top limit
                                    else if (gMin > topZ)
                                        findings.Add((
                                            $"[VIEW RANGE — GRID] Grid '{grid.Name}' is ABOVE the view's Top Clip Plane " +
                                            $"(grid bottom {FmtZ(gMin)} {unitLabel}, view top {FmtZ(topZ)} {unitLabel})",
                                            $"The grid's vertical extents are above the plan view range.\n\n" +
                                            "To fix:\n" +
                                            "Option A — Lower the grid:\n" +
                                            $"  Select grid '{grid.Name}' in a section/elevation view → drag bottom grip downward below {FmtZ(topZ)} {unitLabel}\n\n" +
                                            "Option B — Raise the view's Top Clip Plane:\n" +
                                            "  View Properties (VP) → View Range → Edit → raise Top Clip Plane\n\n" +
                                            "ALSO CHECK:\n" +
                                            "• Grid 2D grip may be shortened (reduced) in this view — select the grid, click the 2D/3D toggle handle\n" +
                                            "• Category 'Grids' may be off in VG → Model Categories → Grids\n" +
                                            "• Phase settings mismatch — check View Properties → Phase / Phase Filter"
                                        ));

                                    // Grid Z range intersects view — vertical range is fine; add advisory
                                    else
                                        findings.Add((
                                            $"[VIEW RANGE — GRID] Grid '{grid.Name}' vertical extents intersect the view range (no Z issue)",
                                            "The grid's vertical range overlaps the view range, so it should be visible from a Z-elevation perspective.\n\n" +
                                            "If the grid is still not visible, check:\n" +
                                            "• Grid 2D grip may be shortened (reduced) in this view — select the grid → look for a 2D/3D toggle handle at the grid end and click it to restore full extent\n" +
                                            "• Category 'Grids' may be off in VG (press VG → Model Categories → Grids)\n" +
                                            "• Workset visibility — if the grid is on a hidden workset, it won't appear\n" +
                                            "• Phase settings mismatch — check View Properties → Phase / Phase Filter"
                                        ));
                                }
                            }
                            catch { }
                        }
                        else
                        {
                            // ── Non-grid elements: standard bounding box Z check ─────────
                            var worldBbox = elem.get_BoundingBox(null);
                            if (worldBbox != null)
                            {
                                double eMin = worldBbox.Min.Z;
                                double eMax = worldBbox.Max.Z;

                                if (eMax < dptZ)
                                    findings.Add((
                                        $"[VIEW RANGE] Element is BELOW View Depth (element top {FmtZ(eMax)} {unitLabel}, depth {FmtZ(dptZ)} {unitLabel})",
                                        "1. View Properties (VP) → View Range → Edit\n" +
                                        $"2. Lower the View Depth offset to below {FmtZ(eMax)} {unitLabel}\n" +
                                        "3. Or verify the element is placed on the correct level"
                                    ));

                                if (eMin > topZ)
                                    findings.Add((
                                        $"[VIEW RANGE] Element is ABOVE Top Clip Plane (element bottom {FmtZ(eMin)} {unitLabel}, top clip {FmtZ(topZ)} {unitLabel})",
                                        "1. View Properties (VP) → View Range → Edit\n" +
                                        $"2. Raise the Top Clip Plane offset to at least {FmtZ(eMin)} {unitLabel}"
                                    ));

                                if (eMax < botZ)
                                    findings.Add((
                                        $"[VIEW RANGE] Element is BELOW Bottom Clip Plane (element top {FmtZ(eMax)} {unitLabel}, bottom clip {FmtZ(botZ)} {unitLabel})",
                                        "1. View Properties (VP) → View Range → Edit\n" +
                                        $"2. Lower the Bottom Clip Plane offset below {FmtZ(eMax)} {unitLabel}"
                                    ));

                                if (eMin > cutZ && eMax > cutZ && findings.Count == 0)
                                    findings.Add((
                                        $"[VIEW RANGE] Element is ABOVE Cut Plane — projection only (element {FmtZ(eMin)}–{FmtZ(eMax)} {unitLabel}, cut {FmtZ(cutZ)} {unitLabel})",
                                        "Element is above the cut plane so it will not appear as a cut section.\n" +
                                        "It may still appear as a projection depending on the category.\n\n" +
                                        "1. View Properties (VP) → View Range → Edit\n" +
                                        $"2. Raise the Cut Plane above {FmtZ(eMax)} {unitLabel}\n" +
                                        "3. Or check Underlay settings"
                                    ));
                            }
                        }
                    }
                }
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 8 — Design Option mismatch
            // Element belongs to a secondary design option that is not active in this view
            // ════════════════════════════════════════════════════════════════════
            try
            {
                var elemDO = elem.DesignOption;
                if (elemDO != null)
                {
                    // DesignOption.IsPrimary == false → element is in a non-primary option
                    // The view has its own DesignOption property (null = show primary + active secondary)
                    var viewDO = view.DesignOption;

                    // If view shows a specific option, element must be in that option or primary
                    if (viewDO != null && elemDO.Id != viewDO.Id)
                    {
                        var doName = elemDO.Name ?? elemDO.Id.ToString();
                        var viewDOName = viewDO.Name ?? viewDO.Id.ToString();
                        findings.Add((
                            $"[DESIGN OPTION] Element is in option '{doName}' but view is set to show '{viewDOName}'",
                            $"The view is currently configured to display Design Option '{viewDOName}', " +
                            $"but the element belongs to option '{doName}'.\n\n" +
                            "To fix:\n" +
                            "Option A — Switch the view to show the element's option:\n" +
                            "  View Properties (VP) → Identity Data → Design Stage → change to the option containing this element\n\n" +
                            "Option B — Move the element to the main model or primary option:\n" +
                            "  Manage tab → Design Options → make the element's option the Primary"
                        ));
                    }
                    else if (viewDO == null && !elemDO.IsPrimary)
                    {
                        // View shows primary option only — element is in a non-primary secondary option
                        var doName = elemDO.Name ?? elemDO.Id.ToString();
                        findings.Add((
                            $"[DESIGN OPTION] Element is in secondary option '{doName}' which is not active in this view",
                            $"This element belongs to Design Option '{doName}' (a secondary option).\n" +
                            "The current view is not set to display this option.\n\n" +
                            "To fix:\n" +
                            "Option A — Set this view to display the correct option:\n" +
                            "  View Properties (VP) → Identity Data → Design Stage → select the correct option\n\n" +
                            "Option B — Switch to a view that has this design option active"
                        ));
                    }
                }
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 9 — View Discipline mismatch
            // Some categories are hidden based on view discipline setting
            // e.g. Structural elements hidden in Architectural views
            // Only fires if no HIGH-priority issues were found (advisory)
            // ════════════════════════════════════════════════════════════════════
            try
            {
                var disciplineParam = view.get_Parameter(BuiltInParameter.VIEW_DISCIPLINE);
                if (disciplineParam != null && elem.Category != null)
                {
                    int discipline = disciplineParam.AsInteger();

                    // Discipline values: 1=Architectural, 2=Structural, 4=Mechanical, 8=Electrical, 4095=Coordination
                    // Categories commonly hidden by discipline:
                    var catName9 = elem.Category.Name ?? string.Empty;
                    bool disciplineMismatch = false;
                    string disciplineName = discipline switch
                    {
                        1    => "Architectural",
                        2    => "Structural",
                        4    => "Mechanical",
                        8    => "Electrical",
                        4095 => "Coordination",
                        _    => $"discipline #{discipline}"
                    };

                    // Architectural views hide structural framing / structural columns subcategories by default
                    if (discipline == 1 &&
                        (catName9.Equals("Structural Framing", StringComparison.OrdinalIgnoreCase) ||
                         catName9.Equals("Structural Columns", StringComparison.OrdinalIgnoreCase) ||
                         catName9.Equals("Structural Foundations", StringComparison.OrdinalIgnoreCase)))
                        disciplineMismatch = true;

                    // Structural views hide MEP categories by default
                    if (discipline == 2 &&
                        (catName9.StartsWith("Duct", StringComparison.OrdinalIgnoreCase) ||
                         catName9.StartsWith("Pipe", StringComparison.OrdinalIgnoreCase) ||
                         catName9.StartsWith("Cable", StringComparison.OrdinalIgnoreCase) ||
                         catName9.StartsWith("Conduit", StringComparison.OrdinalIgnoreCase)))
                        disciplineMismatch = true;

                    // MEP views (Mechanical/Electrical) may hide architectural elements
                    if ((discipline == 4 || discipline == 8) &&
                        (catName9.Equals("Walls", StringComparison.OrdinalIgnoreCase) ||
                         catName9.Equals("Floors", StringComparison.OrdinalIgnoreCase) ||
                         catName9.Equals("Ceilings", StringComparison.OrdinalIgnoreCase) ||
                         catName9.Equals("Roofs", StringComparison.OrdinalIgnoreCase)))
                        disciplineMismatch = true;

                    if (disciplineMismatch)
                        findings.Add((
                            $"[VIEW DISCIPLINE] View discipline is '{disciplineName}' — '{catName9}' may be suppressed",
                            $"The view's discipline is set to '{disciplineName}', which can automatically hide " +
                            $"'{catName9}' elements.\n\n" +
                            "To fix:\n" +
                            "Option A — Change the view discipline:\n" +
                            "  View Properties (VP) → Graphics → Discipline → change to 'Coordination'\n\n" +
                            "Option B — Explicitly override in VG:\n" +
                            "  Press VG → Model Categories → ensure the category is ticked\n\n" +
                            "NOTE: 'Coordination' discipline shows all categories regardless of system."
                        ));
                }
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 10 — Family Instance: built-in "Visible" parameter (IS_VISIBLE_PARAM)
            // Checks only the single built-in "Visible" Yes/No parameter that directly
            // controls element visibility (shown in Properties palette as "Visible").
            // Does NOT scan all Yes/No params — only this one specific built-in param.
            // ════════════════════════════════════════════════════════════════════
            try
            {
                if (elem is FamilyInstance fi)
                {
                    var visibleParam = fi.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                    if (visibleParam != null &&
                        visibleParam.StorageType == StorageType.Integer &&
                        visibleParam.AsInteger() == 0)
                    {
                        findings.Add((
                            "[FAMILY VISIBILITY PARAM] The built-in 'Visible' parameter is set to No — element is hidden by its own visibility setting",
                            "The element has its built-in 'Visible' parameter turned OFF.\n" +
                            "This is the Yes/No parameter in the Properties palette that directly controls element visibility.\n\n" +
                            "To fix:\n" +
                            "1. Select the element → Properties palette\n" +
                            "2. Find the 'Visible' parameter (under Graphics section)\n" +
                            "3. Tick the checkbox to set it to 'Yes'\n" +
                            "4. This overrides visibility for this specific instance only"
                        ));
                    }
                }
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 11 — Annotation outside Annotation Crop Region (dashed blue)
            //
            // Detection method (mirrors the working pyRevit script):
            //   elem.get_BoundingBox(view) returns null when the annotation is
            //   hidden by the Annotation Crop Region — Revit excludes it from the
            //   view so the view-space bbox is unavailable.
            //
            //   elem.get_BoundingBox(null) returns the world-space bbox and is
            //   always available regardless of visibility. If this is also null
            //   the element simply has no geometry and we skip this check.
            //
            // Conditions to fire:
            //   1. Element is an Annotation category type
            //   2. VIEWER_ANNOTATION_CROP_ACTIVE == 1 on the view
            //   3. get_BoundingBox(view) == null  (Revit excluded it from the view)
            //
            // NOTE: Tags (IndependentTag) have NO world-space bbox — get_BoundingBox(null)
            // always returns null for tags. Do NOT use it as an existence check.
            // Instead confirm the element exists via doc.GetElement(elem.Id).
            // ════════════════════════════════════════════════════════════════════
            try
            {
                if (elem.Category?.CategoryType == CategoryType.Annotation)
                {
                    bool annCropActive = false;
                    try
                    {
                        var p = view.get_Parameter(BuiltInParameter.VIEWER_ANNOTATION_CROP_ACTIVE);
                        annCropActive = p != null && p.AsInteger() == 1;
                    }
                    catch { }

                    if (annCropActive)
                    {
                        // get_BoundingBox(view) == null means Revit excluded this annotation
                        // from the view — the annotation crop is hiding it.
                        // Tags have no world bbox so we verify existence via doc.GetElement.
                        var viewBbox   = elem.get_BoundingBox(view);
                        bool elemExists = elem.Id != ElementId.InvalidElementId
                                          && doc.GetElement(elem.Id) != null;

                        if (viewBbox == null && elemExists)
                        {
                            findings.Add((
                                "[ANNOTATION CROP] Annotation is hidden by the Annotation Crop Region (dashed blue boundary)",
                                "The annotation is outside or touching the Annotation Crop Region (the dashed blue boundary).\n" +
                                "Revit hides any annotation that touches this boundary — the element exists in the model\n" +
                                "but its view-space bounding box is null, confirming the annotation crop is culling it.\n\n" +
                                "To fix:\n" +
                                "Option A — Expand the Annotation Crop Region:\n" +
                                "  Click the dashed blue boundary in the view → drag the handles outward\n\n" +
                                "Option B — Move the annotation inside the boundary:\n" +
                                "  Select the annotation → move it away from the dashed crop edge\n\n" +
                                "Option C — Disable Annotation Crop:\n" +
                                "  View Properties (VP) → Extents → uncheck 'Annotation Crop'"
                            ));
                        }
                    }
                }
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 12 — Ceiling geometry blocking element (projection override)
            // Finds ceilings in the view that sit ABOVE and XY-overlap this element.
            // In non-wireframe display styles, Revit's depth removal hides elements
            // that are projected behind a solid ceiling surface.
            // ════════════════════════════════════════════════════════════════════
            try
            {
                if (view is ViewPlan)
                {
                    var elemBbox = elem.get_BoundingBox(null);
                    if (elemBbox != null)
                    {
                        var displayStyle = view.DisplayStyle;
                        bool isWireframe = displayStyle == DisplayStyle.Wireframe;

                        var blockingCeilingIds = new List<int>();

                        foreach (Element ceiling in new FilteredElementCollector(doc, view.Id)
                            .OfCategory(BuiltInCategory.OST_Ceilings)
                            .WhereElementIsNotElementType())
                        {
                            try
                            {
                                var cbbox = ceiling.get_BoundingBox(null);
                                if (cbbox == null) continue;

                                bool overlapX = !(cbbox.Max.X < elemBbox.Min.X || cbbox.Min.X > elemBbox.Max.X);
                                bool overlapY = !(cbbox.Max.Y < elemBbox.Min.Y || cbbox.Min.Y > elemBbox.Max.Y);
                                bool ceilingAbove = cbbox.Min.Z > elemBbox.Max.Z;

                                if (overlapX && overlapY && ceilingAbove)
#if REVIT2025_OR_GREATER
                                    blockingCeilingIds.Add((int)ceiling.Id.Value);
#else
                                    blockingCeilingIds.Add(ceiling.Id.GetIdValueAsInt32());
#endif
                            }
                            catch { }
                        }

                        if (blockingCeilingIds.Count > 0)
                        {
                            var idList = string.Join(", ", blockingCeilingIds);
                            if (isWireframe)
                                findings.Add((
                                    $"[CEILING PROJECTION] {blockingCeilingIds.Count} ceiling(s) sit above and overlap this element (IDs: {idList}) — Wireframe mode so element remains visible",
                                    $"Ceiling(s) ID {idList} sit above this element and overlap its XY footprint.\n" +
                                    "The current display style is Wireframe, which ignores depth removal, so the element should be visible.\n\n" +
                                    "If you switch to a shaded or hidden-line display style, the ceiling may occlude this element.\n\n" +
                                    "To prevent occlusion in other display styles:\n" +
                                    "  Override ceiling graphics in VG → make ceiling surface transparent or a cut pattern\n" +
                                    "  OR use Wireframe display style in this view"
                                ));
                            else
                                findings.Add((
                                    $"[CEILING PROJECTION] {blockingCeilingIds.Count} ceiling(s) sit above and overlap this element (IDs: {idList}) and may be blocking it via depth removal",
                                    $"Ceiling(s) ID {idList} sit above this element and overlap its XY footprint.\n" +
                                    $"The current display style is '{displayStyle}', which uses depth removal — the ceiling's solid surface may be overriding the projection of this element.\n\n" +
                                    "To fix:\n" +
                                    "Option A — Switch view to Wireframe:\n" +
                                    "  View Control Bar → Visual Style → Wireframe (bypasses depth removal)\n\n" +
                                    "Option B — Override ceiling surface transparency in VG:\n" +
                                    "  Press VG → Model Categories → Ceilings → Surface Patterns → set to transparent\n\n" +
                                    "Option C — Turn off ceiling projection in VG:\n" +
                                    "  Press VG → Model Categories → Ceilings → uncheck Projection Lines visibility"
                                ));
                        }
                    }
                }
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 13 — Detail Level mismatch
            // Compare geometry volume at Fine vs current detail level.
            // If Fine has solid geometry but current level does not → detail level is the cause.
            // Recurse into GeometryInstance to find actual solid volumes (same logic as pyRevit script).
            // ════════════════════════════════════════════════════════════════════
            static bool HasSolidGeometry(Element e, ViewDetailLevel level)
            {
                var opt = new Options
                {
                    DetailLevel              = level,
                    IncludeNonVisibleObjects = false
                };
                var geo = e.get_Geometry(opt);
                if (geo == null) return false;
                foreach (GeometryObject g in geo)
                {
                    if (g is Solid s && s.Volume > 1e-9) return true;
                    if (g is GeometryInstance gi)
                    {
                        foreach (GeometryObject ig in gi.GetInstanceGeometry())
                            if (ig is Solid s2 && s2.Volume > 1e-9) return true;
                    }
                }
                return false;
            }

            try
            {
                if (elem.Category?.CategoryType == CategoryType.Annotation)
                    goto SkipCheck13;

                if (elem is not FamilyInstance)
                    goto SkipCheck13;

                var currentDetailLevel = view.DetailLevel;
                // Only relevant if view is Coarse or Medium — Fine is the richest level
                if (currentDetailLevel == ViewDetailLevel.Fine)
                    goto SkipCheck13;

                bool hasGeoAtCurrent = HasSolidGeometry(elem, currentDetailLevel);
                bool hasGeoAtFine    = HasSolidGeometry(elem, ViewDetailLevel.Fine);

                if (!hasGeoAtCurrent && hasGeoAtFine)
                {
                    string levelName = currentDetailLevel switch
                    {
                        ViewDetailLevel.Coarse => "Coarse",
                        ViewDetailLevel.Medium => "Medium",
                        _                      => currentDetailLevel.ToString()
                    };

                    findings.Add((
                        $"[DETAIL LEVEL] Family geometry is not visible at '{levelName}' detail level",
                        $"The family's Visibility Settings have the '{levelName}' detail level checkbox unticked.\n" +
                        $"The view is currently set to '{levelName}' detail level, so the element is not visible.\n" +
                        "The family does have geometry visible at Fine detail level.\n\n" +
                        "To fix:\n" +
                        $"Option A — Change the view's Detail Level to Fine:\n" +
                        "  View Control Bar (bottom of view) → Detail Level icon\n\n" +
                        "Option B — Edit the family to enable the missing detail level:\n" +
                        "  Select the family instance → Edit Family (or double-click)\n" +
                        "  Select the geometry → Visibility Settings button in ribbon\n" +
                        $"  Tick the '{levelName}' checkbox under Detail Levels\n" +
                        "  Load Back into Project"
                    ));
                }
            }
            catch { }
            SkipCheck13:;

            // ════════════════════════════════════════════════════════════════════
            // CHECK 14 — Phase / Phase Filter mismatch
            // The element's Created/Demolished phase vs. the view's Phase and
            // Phase Filter determines whether the element is shown, greyed, or hidden.
            // This is one of the most common causes of "missing" elements.
            // ════════════════════════════════════════════════════════════════════
            try
            {
                var elemPhaseCreatedParam    = elem.get_Parameter(BuiltInParameter.PHASE_CREATED);
                var elemPhaseDemolishedParam = elem.get_Parameter(BuiltInParameter.PHASE_DEMOLISHED);
                var viewPhaseParam           = view.get_Parameter(BuiltInParameter.VIEW_PHASE);
                var viewPhaseFilterParam     = view.get_Parameter(BuiltInParameter.VIEW_PHASE_FILTER);

                if (elemPhaseCreatedParam != null && viewPhaseParam != null)
                {
                    var elemPhaseCreatedId    = elemPhaseCreatedParam.AsElementId();
                    var elemPhaseDemolishedId = elemPhaseDemolishedParam?.AsElementId() ?? ElementId.InvalidElementId;
                    var viewPhaseId           = viewPhaseParam.AsElementId();

                    // Resolve phase names for readable output
                    string ElemPhaseCreatedName() =>
                        doc.GetElement(elemPhaseCreatedId)?.Name ?? elemPhaseCreatedId.ToString();
                    string ElemPhaseDemolishedName() =>
                        elemPhaseDemolishedId == ElementId.InvalidElementId ? "not demolished"
                        : doc.GetElement(elemPhaseDemolishedId)?.Name ?? elemPhaseDemolishedId.ToString();
                    string ViewPhaseName() =>
                        doc.GetElement(viewPhaseId)?.Name ?? viewPhaseId.ToString();

                    // Element created in a later phase than the view phase
                    if (elemPhaseCreatedId != ElementId.InvalidElementId &&
                        viewPhaseId        != ElementId.InvalidElementId)
                    {
                        // Compare phase sequence order using the PhaseArray
                        var phases = doc.Phases;
                        int elemCreatedOrder = -1, viewPhaseOrder = -1, elemDemoOrder = -1;
                        for (int pi = 0; pi < phases.Size; pi++)
                        {
                            var ph = phases.get_Item(pi);
                            if (ph.Id == elemPhaseCreatedId)    elemCreatedOrder = pi;
                            if (ph.Id == viewPhaseId)           viewPhaseOrder   = pi;
                            if (ph.Id == elemPhaseDemolishedId) elemDemoOrder    = pi;
                        }

                        if (elemCreatedOrder > viewPhaseOrder && elemCreatedOrder != -1 && viewPhaseOrder != -1)
                        {
                            findings.Add((
                                $"[PHASE] Element was created in phase '{ElemPhaseCreatedName()}' but the view is set to phase '{ViewPhaseName()}' — element does not yet exist in this view's phase",
                                $"The element was created in phase '{ElemPhaseCreatedName()}', which comes AFTER the view's current phase '{ViewPhaseName()}'.\n" +
                                "In Revit, elements are only visible in phases at or after their creation phase.\n\n" +
                                "To fix:\n" +
                                "Option A — Change the view to a later phase:\n" +
                                $"  View Properties (VP) → Phasing → Phase → change to '{ElemPhaseCreatedName()}' or later\n\n" +
                                "Option B — Change the element's phase:\n" +
                                "  Select element → Properties → Phasing → Phase Created → change to an earlier phase"
                            ));
                        }
                        else if (elemDemoOrder != -1 && elemDemoOrder <= viewPhaseOrder)
                        {
                            // Element was demolished in the same or earlier phase than the view
                            var viewPhaseFilterName = viewPhaseFilterParam != null
                                ? doc.GetElement(viewPhaseFilterParam.AsElementId())?.Name ?? "unknown"
                                : "unknown";
                            findings.Add((
                                $"[PHASE] Element was demolished in phase '{ElemPhaseDemolishedName()}' — visibility depends on Phase Filter '{viewPhaseFilterName}'",
                                $"The element was demolished in phase '{ElemPhaseDemolishedName()}' which is at or before the view's phase '{ViewPhaseName()}'.\n" +
                                "Whether a demolished element is shown depends on the view's Phase Filter setting.\n\n" +
                                "Phase Filter rules for demolished elements:\n" +
                                "• 'Show All' → shown as demolished (dashed lines)\n" +
                                "• 'Show New' → hidden (demolished = old, not new)\n" +
                                "• 'Show Previous + New' → may be hidden\n" +
                                "• 'Show Complete' → hidden\n\n" +
                                "To fix:\n" +
                                "Option A — Change the Phase Filter to show demolished elements:\n" +
                                "  View Properties (VP) → Phasing → Phase Filter → choose 'Show All'\n\n" +
                                "Option B — Remove the demolition assignment from the element:\n" +
                                "  Select element → Properties → Phasing → Phase Demolished → change to 'None'"
                            ));
                        }
                    }
                }
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 15 — Subcategory hidden in Visibility/Graphics
            // Parent category may be ON but a subcategory (e.g. "Walls → Hidden Lines")
            // can be turned off independently, hiding parts or all of the element.
            // Reports the first hidden subcategory found (most elements have few).
            // ════════════════════════════════════════════════════════════════════
            try
            {
                if (elem.Category != null)
                {
                    var hiddenSubcats = new List<string>();
                    foreach (Category subCat in elem.Category.SubCategories)
                    {
                        try
                        {
                            if (view.GetCategoryHidden(subCat.Id))
                                hiddenSubcats.Add(subCat.Name);
                        }
                        catch { }
                    }

                    if (hiddenSubcats.Count > 0)
                    {
                        var subList = string.Join(", ", hiddenSubcats.Select(s => $"'{s}'"));
                        findings.Add((
                            $"[SUBCATEGORY HIDDEN] {hiddenSubcats.Count} subcategory(ies) of '{catName}' are hidden in VG: {subList}",
                            $"The parent category '{catName}' is visible, but one or more subcategories are turned off in Visibility/Graphics.\n" +
                            "Subcategories control specific line types within a category (e.g. Hidden Lines, Projection Lines, Surface Patterns).\n\n" +
                            $"Hidden subcategory(ies): {subList}\n\n" +
                            "To fix:\n" +
                            "1. Press VG (or VV) → Model Categories tab\n" +
                            $"2. Expand '{catName}' using the '+' arrow\n" +
                            $"3. Find the subcategory(ies) listed above → tick their Visibility checkbox\n" +
                            "4. Click OK\n\n" +
                            "NOTE: If a View Template is applied, you must edit the template to change subcategory visibility."
                        ));
                    }
                }
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 16 — Element graphics override: invisible colour or 100% transparent
            // An element can be technically "visible" but rendered in the background
            // colour (white on white, or black on black) or set to 100% transparent,
            // making it appear absent. Also catches halftone at very low contrast.
            // ════════════════════════════════════════════════════════════════════
            try
            {
                var overrides = view.GetElementOverrides(elem.Id);

                // Projection line colour override (most common — lines render invisible)
                bool projColorInvisible = false;
                string projColorNote    = string.Empty;
                try
                {
                    var projColor = overrides.ProjectionLineColor;
                    if (projColor.IsValid)
                    {
                        // Revit background is almost always white (255,255,255)
                        bool isWhite = projColor.Red > 240 && projColor.Green > 240 && projColor.Blue > 240;
                        bool isBlack = projColor.Red < 15  && projColor.Green < 15  && projColor.Blue < 15;
                        if (isWhite || isBlack)
                        {
                            projColorInvisible = true;
                            projColorNote = $"RGB({projColor.Red},{projColor.Green},{projColor.Blue})";
                        }
                    }
                }
                catch { }

                // Surface transparency override (100% = invisible surfaces)
                bool fullyTransparent = false;
                try { fullyTransparent = overrides.Transparency >= 95; } catch { }

                // Halftone override (element appears very faint — not invisible but near-invisible)
                bool isHalftone = false;
                try { isHalftone = overrides.Halftone; } catch { }

                if (projColorInvisible)
                    findings.Add((
                        $"[GRAPHICS OVERRIDE] Projection line colour is overridden to {projColorNote} — element may be invisible against the background",
                        "A per-element colour override is set that makes the projection lines blend into the background.\n\n" +
                        "To fix:\n" +
                        "Option A — Remove the colour override:\n" +
                        "  Select the element → View tab → Override Graphics in View → By Element\n" +
                        "  → Projection Lines: click the colour swatch → click 'No Override' (clear button)\n\n" +
                        "Option B — Change the view background colour:\n" +
                        "  Application menu → Options → Graphics → Background colour\n\n" +
                        "Option C — Use 'Reset Element Overrides' if this was applied accidentally"
                    ));

                if (fullyTransparent)
                    findings.Add((
                        $"[GRAPHICS OVERRIDE] Surface transparency is set to {overrides.Transparency}% — surfaces are invisible",
                        "A per-element override sets the surface transparency to near 100%, making solid surfaces appear invisible.\n\n" +
                        "To fix:\n" +
                        "  Select the element → View tab → Override Graphics in View → By Element\n" +
                        "  → Surface Patterns/Fills: set Transparency to 0% (or click 'No Override')"
                    ));

                if (isHalftone && !projColorInvisible && !fullyTransparent)
                    findings.Add((
                        "[GRAPHICS OVERRIDE] Element is set to Halftone — appears very faint/washed out",
                        "A per-element override enables Halftone, which makes the element appear at very low contrast.\n" +
                        "The element IS rendered but may look absent at typical zoom levels.\n\n" +
                        "To fix:\n" +
                        "  Select the element → View tab → Override Graphics in View → By Element\n" +
                        "  → uncheck 'Halftone'"
                    ));
            }
            catch { }

            // ════════════════════════════════════════════════════════════════════
            // CHECK 17 — Section Box clipping (3D views only)
            // In a 3D view with the Section Box active, elements whose bounding box
            // falls entirely outside the section box are clipped (not rendered).
            // ════════════════════════════════════════════════════════════════════
            try
            {
                if (view is View3D view3d && view3d.IsSectionBoxActive)
                {
                    var sectionBox = view3d.GetSectionBox();
                    var worldBbox  = elem.get_BoundingBox(null);

                    if (sectionBox != null && worldBbox != null)
                    {
                        // Transform both to the same coordinate system.
                        // SectionBox.Transform maps from section-box-local to world space.
                        // Invert it to bring the element bbox into section-box space.
                        var invTransform = sectionBox.Transform.Inverse;

                        // Transform element bbox corners into section-box space
                        var eLLB = invTransform.OfPoint(worldBbox.Min);
                        var eURB = invTransform.OfPoint(worldBbox.Max);
                        double eMinX = Math.Min(eLLB.X, eURB.X), eMaxX = Math.Max(eLLB.X, eURB.X);
                        double eMinY = Math.Min(eLLB.Y, eURB.Y), eMaxY = Math.Max(eLLB.Y, eURB.Y);
                        double eMinZ = Math.Min(eLLB.Z, eURB.Z), eMaxZ = Math.Max(eLLB.Z, eURB.Z);

                        // Section box extents are in section-box local space
                        double sMinX = sectionBox.Min.X, sMaxX = sectionBox.Max.X;
                        double sMinY = sectionBox.Min.Y, sMaxY = sectionBox.Max.Y;
                        double sMinZ = sectionBox.Min.Z, sMaxZ = sectionBox.Max.Z;

                        bool outsideX = eMaxX < sMinX || eMinX > sMaxX;
                        bool outsideY = eMaxY < sMinY || eMinY > sMaxY;
                        bool outsideZ = eMaxZ < sMinZ || eMinZ > sMaxZ;

                        if (outsideX || outsideY || outsideZ)
                        {
                            var axis = outsideX ? "X (left-right)" : outsideY ? "Y (front-back)" : "Z (up-down)";
                            findings.Add((
                                $"[SECTION BOX] Element is outside the active Section Box on the {axis} axis — clipped from view",
                                "The 3D view has an active Section Box, and this element's bounding box falls entirely\n" +
                                "outside the box boundary on the " + axis + " axis.\n\n" +
                                "To fix:\n" +
                                "Option A — Expand the Section Box to include this element:\n" +
                                "  In the 3D view, select the Section Box (click its boundary) → drag the face handles outward\n\n" +
                                "Option B — Disable the Section Box:\n" +
                                "  View Properties (VP) → Extents → uncheck 'Section Box'\n\n" +
                                "Option C — Reset the Section Box to the full model extents:\n" +
                                "  View tab → Windows → Orient to a Direction → then re-enable Section Box"
                            ));
                        }
                    }
                }
            }
            catch { }

            return findings;
        }

        // ── Helpers ─────────────────────────────────────────────────────────────

        private static View? FindViewByName(Document doc, string viewName)
        {
            return new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Where(v => !v.IsTemplate)
                .FirstOrDefault(v =>
                    string.Equals(v.Name, viewName, StringComparison.OrdinalIgnoreCase) ||
                    v.Name.IndexOf(viewName, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string BuildNoIdBlock(string userMessage)
        {
            return
                "ELEMENT VISIBILITY DIAGNOSIS\n" +
                "No element ID found in the question.\n\n" +
                "To get a pinpoint diagnosis, include the element ID. Example:\n" +
                "  \"Why can't I see element 123456 in Floor Plan Level 1?\"\n\n" +
                "You can find the element ID in Revit by:\n" +
                "  Selecting the element → Manage tab → Inquiry → IDs of Selection\n" +
                "  Or: right-click the element → Element Properties → bottom of dialog shows ID";
        }

        private static string BuildElementNotFoundBlock(int id)
        {
            return
                $"ELEMENT VISIBILITY DIAGNOSIS\n" +
                $"CHECK 0 — ELEMENT EXISTENCE: FAILED\n" +
                $"Element ID {id} does not exist in the current document.\n\n" +
                "This is the most fundamental check — an element that has been deleted or never existed cannot be made visible.\n\n" +
                "Possible reasons:\n" +
                "• The element has been deleted from the model\n" +
                "• The ID belongs to an element in a linked Revit file, not the host model\n" +
                "  → Check VG → Revit Links tab — linked elements have their own IDs\n" +
                "• The ID was misread or mistyped\n" +
                "  → Re-select the element → Manage tab → Inquiry → IDs of Selection to confirm\n\n" +
                "ACTION: Confirm the element ID is correct before investigating visibility settings.";
        }
    }
}
