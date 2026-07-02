using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using BIManage.Common.Helpers;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;
using BIManage.Revit.Protection;

namespace BIManage.Core.Metrics
{
    /// <summary>
    /// Service for collecting Revit model file metrics (columnar format)
    /// Organized by performance impact tier:
    /// - Fast (20 metrics): Negligible/Low-Medium impact (sync/save automatic)
    /// - Medium (10 metrics): Medium/Medium-High impact (daily snapshots or manual)
    /// - Expensive (2 metrics): High/Very-High impact (manual only)
    /// </summary>
    public class ModelFileMetricsCollectorService
    {
        private readonly ILogger? _logger;

        /// <summary>Default family size threshold in bytes (5 MB). Overridden by API health monitor settings.</summary>
        private long _familySizeThresholdBytes = 5L * 1024 * 1024;

        public ModelFileMetricsCollectorService(ILogger? logger = null)
        {
            _logger = logger;
        }

        /// <summary>
        /// Sets the family size threshold from the health monitor API (maxFamilySize in MB).
        /// Call this before CollectExpensiveMetrics if a model-specific threshold is available.
        /// </summary>
        public void SetFamilySizeThresholdMb(long? maxFamilySizeMb)
        {
            if (maxFamilySizeMb.HasValue && maxFamilySizeMb.Value > 0)
                _familySizeThresholdBytes = maxFamilySizeMb.Value * 1024 * 1024;
        }

        /// <summary>Returns the current family size threshold in bytes. Used by ChunkedMetricsAnalyzer.</summary>
        public long GetFamilySizeThresholdBytes() => _familySizeThresholdBytes;

        #region Fast Metrics (20 metrics - Sync/Save automatic)

        /// <summary>
        /// Collect all fast metrics suitable for sync/save operations
        /// </summary>
        public FastMetrics CollectFastMetrics(Document doc)
        {
            var metrics = new FastMetrics();

            try
            {
                // 1. File Size
                metrics.FileSizeBytes = GetFileSizeBytes(doc);

                // 2-5. Organization metrics
                metrics.LevelsCount = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Levels)
                    .WhereElementIsNotElementType()
                    .GetElementCount();

                metrics.GridsCount = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Grids)
                    .WhereElementIsNotElementType()
                    .GetElementCount();

                metrics.DesignOptionsCount = new FilteredElementCollector(doc)
                    .OfClass(typeof(DesignOption))
                    .GetElementCount();

                // 6-8. External references — count distinct DWG file names, not placement
                // instances. A single DWG placed 3 times creates 3 ImportInstance elements
                // but should still report as "1 imported DWG" to match what the user sees
                // in Manage Links / Insert.
                var importInstances = new FilteredElementCollector(doc)
                    .OfClass(typeof(ImportInstance))
                    .Cast<ImportInstance>()
                    .ToList();

                string ResolveImportName(ImportInstance i)
                {
                    string? n = null;
                    try { n = i.Category?.Name; } catch { }
                    if (string.IsNullOrWhiteSpace(n))
                    {
                        try { var t = doc.GetElement(i.GetTypeId()); n = t?.Name; } catch { }
                    }
                    if (string.IsNullOrWhiteSpace(n))
                    {
                        try { n = i.Name; } catch { }
                    }
                    return string.IsNullOrWhiteSpace(n) ? "Unknown" : n;
                }

                metrics.LinkedDwgCount = importInstances.Where(i => i.IsLinked)
                    .Select(ResolveImportName).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                metrics.ImportedDwgCount = importInstances.Where(i => !i.IsLinked)
                    .Select(ResolveImportName).Distinct(StringComparer.OrdinalIgnoreCase).Count();

                metrics.LinkedRevitCount = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkType))
                    .GetElementCount();

                // 8. Raster images — count placed instances, not just image type definitions
                metrics.RasterImagesCount = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_RasterImages)
                    .WhereElementIsNotElementType()
                    .GetElementCount();

                // 9-10. Quality metrics from warnings (language-neutral via FailureDefinitionId)
                var warnings = doc.GetWarnings();
                metrics.WarningsCount = warnings.Count;

                // Duplicate elements — language-neutral
                metrics.DuplicateElementsCount = CountWarningsByFailureId(warnings,
                    BuiltInFailures.OverlapFailures.DuplicateInstances);

                // 11-12. Groups — use BuiltInCategory for language-neutral detection
                metrics.ModelGroupsCount = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_IOSModelGroups)
                    .WhereElementIsNotElementType()
                    .GetElementCount();

                metrics.DetailGroupsCount = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_IOSDetailGroups)
                    .WhereElementIsNotElementType()
                    .GetElementCount();

                // 13-15. Views, sheets, families
                metrics.TotalViewsCount = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Count(v => !v.IsTemplate);

                metrics.SheetsCount = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .GetElementCount();

                metrics.TotalFamiliesCount = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .GetElementCount();

                // 16. Total worksets count (workshared models only)
                try
                {
                    if (doc.IsWorkshared)
                    {
                        int worksetsCount = 0;
                        var worksetCollector = new FilteredWorksetCollector(doc);
                        foreach (Workset workset in worksetCollector)
                        {
                            if (workset.Kind == WorksetKind.UserWorkset)
                                worksetsCount++;
                        }
                        metrics.TotalWorksetsCount = worksetsCount;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"Failed to count worksets: {ex.Message}");
                }

                // 17. Non-native object styles count — include parent categories AND their
                // sub-categories (CAD layers). Previously only top-level categories counted,
                // which made the count equal the DWG-file count instead of the style count.
                try
                {
                    int nonNativeCount = 0;
                    foreach (Category cat in doc.Settings.Categories)
                    {
                        if (cat.Id.GetIdValue() > 0)
                        {
                            nonNativeCount++;
                            if (cat.SubCategories != null)
                            {
                                foreach (Category subCat in cat.SubCategories)
                                {
                                    if (subCat.Id.GetIdValue() > 0)
                                        nonNativeCount++;
                                }
                            }
                        }
                    }
                    metrics.NonNativeObjectStylesCount = nonNativeCount;
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"Failed to count non-native object styles: {ex.Message}");
                }

                // 18. View templates count
                try
                {
                    metrics.ViewTemplatesCount = new FilteredElementCollector(doc)
                        .OfClass(typeof(View))
                        .Cast<View>()
                        .Count(v => v.IsTemplate);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"Failed to count view templates: {ex.Message}");
                }

                // 19-21. Shared coordinates
                try
                {
                    var basePoint = new FilteredElementCollector(doc)
                        .OfClass(typeof(BasePoint))
                        .Cast<BasePoint>()
                        .FirstOrDefault(bp => bp.IsShared);

                    if (basePoint != null)
                    {
                        var nsParam = basePoint.get_Parameter(BuiltInParameter.BASEPOINT_NORTHSOUTH_PARAM);
                        var ewParam = basePoint.get_Parameter(BuiltInParameter.BASEPOINT_EASTWEST_PARAM);
                        var elevParam = basePoint.get_Parameter(BuiltInParameter.BASEPOINT_ELEVATION_PARAM);

                        // Determine project length display unit and convert from Revit internal (feet)
                        string unitLabel = "ft";
                        double factor = 1.0;
                        try
                        {
                            var fmtOpts = doc.GetUnits().GetFormatOptions(SpecTypeId.Length);
                            var uid = fmtOpts.GetUnitTypeId();
                            if (uid == UnitTypeId.Millimeters) { unitLabel = "mm"; factor = 304.8; }
                            else if (uid == UnitTypeId.Meters) { unitLabel = "m"; factor = 0.3048; }
                            else if (uid == UnitTypeId.Centimeters) { unitLabel = "cm"; factor = 30.48; }
                        }
                        catch { /* fallback: feet */ }

                        metrics.SharedCoordNs = nsParam != null ? nsParam.AsDouble() * factor : (double?)null;
                        metrics.SharedCoordEw = ewParam != null ? ewParam.AsDouble() * factor : (double?)null;
                        metrics.SharedCoordElevation = elevParam != null ? elevParam.AsDouble() * factor : (double?)null;
                        metrics.SharedCoordUnit = unitLabel;
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"Failed to get shared coordinates: {ex.Message}");
                }

                _logger?.LogInfo($"Collected 20 fast metrics for {doc.Title}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error collecting fast metrics: {ex.Message}", ex);
            }

            return metrics;
        }

        #endregion

        #region Medium Metrics (10 metrics - Periodic daily or manual)

        /// <summary>
        /// Collect all medium-cost metrics suitable for daily snapshots
        /// </summary>
        public MediumMetrics CollectMediumMetrics(Document doc)
        {
            var metrics = new MediumMetrics();

            try
            {
                // 1-3. Total / Model / Annotative element counts — single category-walk pass.
                //
                // Earlier this code computed Total via an unfiltered FilteredElementCollector
                // and Model/Annotative via category enumeration. The asymmetry blew up the
                // "Other" bucket on the Health dashboard (Other = Total − Model − Annotative)
                // because the unfiltered collector includes ~10⁵–10⁶ Revit bookkeeping
                // elements per workshared model that have no category — sketch curves, line/
                // fill patterns, view templates, parameter elements, render/sun/MEP settings,
                // design-option/phase/transaction nodes, etc. None of those appear in Project
                // Browser or any schedule, but they pushed "Other Elements" to the millions.
                //
                // Now we sum the three buckets we actually display: Model + Annotation +
                // Other (= Internal + AnalyticalModel) and report the sum as Total. This makes
                // Total = Model + Annotative + Other by construction, removes the noise, and
                // matches what users see in Revit's own UI.
                int modelCount = 0;
                int annotativeCount = 0;
                int otherCategorizedCount = 0;
                try
                {
                    foreach (Category cat in doc.Settings.Categories)
                    {
                        try
                        {
                            int c = new FilteredElementCollector(doc)
                                .OfCategoryId(cat.Id)
                                .WhereElementIsNotElementType()
                                .GetElementCount();

                            switch (cat.CategoryType)
                            {
                                case CategoryType.Model:
                                    modelCount += c;
                                    break;
                                case CategoryType.Annotation:
                                    annotativeCount += c;
                                    break;
                                case CategoryType.Internal:
                                case CategoryType.AnalyticalModel:
                                    otherCategorizedCount += c;
                                    break;
                                // CategoryType.Invalid intentionally excluded — internal Revit
                                // sentinel that appears for un-categorised infrastructure.
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"Category enumeration failed, falling back: {ex.Message}");
                }
                metrics.ModelElementsCount = modelCount;
                metrics.AnnotativeElementsCount = annotativeCount;
                metrics.TotalElementsCount = modelCount + annotativeCount + otherCategorizedCount;

                // 4. In-place families — query Family objects directly (not every FamilyInstance)
                metrics.InplaceFamiliesCount = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Count(f => f.IsInPlace);

                // 5-6. Room metrics
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .Cast<Room>()
                    .ToList();

                // Unplaced rooms: room exists in schedule but has no Location (never placed
                // in the model). Mutually exclusive from "unenclosed" — keep the two metrics
                // distinct so the dashboard tile counts don't double up the same problematic room.
                metrics.UnplacedRoomsCount = rooms.Count(r => r.Location == null);

                // Unenclosed rooms — placed in the model but boundary is open (Revit can't
                // compute the area). Combine TWO signals because either alone misses cases:
                //   (a) FailureDefinitionId-matched warning — language-neutral, but only
                //       populated after Revit has evaluated room boundaries; on a cold-
                //       opened doc the warning list may be empty even when rooms ARE open.
                //   (b) Placed (Location != null) AND Area == 0 — direct geometric test,
                //       always accurate but less semantically explicit than the warning.
                // We count each ROOM (not each signal) once: a room is unenclosed if it's
                // placed AND either signal flags it. Math.Max would undercount when the
                // two signals point at *different* rooms, hence the per-room union.
                var warnings = doc.GetWarnings();
                var roomsFlaggedByWarning = new HashSet<long>();
                foreach (var w in warnings)
                {
                    try
                    {
                        if (w.GetFailureDefinitionId() == BuiltInFailures.RoomFailures.RoomNotEnclosed)
                        {
                            var failingIds = w.GetFailingElements();
                            if (failingIds != null)
                            {
                                foreach (var id in failingIds)
                                    roomsFlaggedByWarning.Add(id.GetIdValue());
                            }
                        }
                    }
                    catch { }
                }

                metrics.UnenclosedRoomsCount = rooms.Count(r =>
                    r.Location != null  // strict mutual-exclusivity with UnplacedRoomsCount
                    && (r.Area == 0 || roomsFlaggedByWarning.Contains(r.Id.GetIdValue())));

                // 7. Views not on sheets — counted via ViewSheet.GetAllPlacedViews()
                // (canonical, matches the Detail report's ViewsNotOnSheets list at line ~937
                // of this file). The previous VIEW_REFERENCING_SHEET parameter check was
                // unreliable — it returns null for many views that ARE placed on sheets
                // (legends, schedules placed on multiple sheets, certain workshared docs),
                // which inflated the count. Stay aligned with the detail list so the dashboard
                // tile and the drill-down show the same number.
                var sheetCountSet = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>();
                var viewsOnSheetsForCount = new HashSet<long>();
                foreach (var sheet in sheetCountSet)
                {
                    foreach (var vpId in sheet.GetAllPlacedViews())
                        viewsOnSheetsForCount.Add(vpId.GetIdValue());
                }
                metrics.ViewsNotOnSheetsCount = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Count(v => !v.IsTemplate
                                && v.ViewType != ViewType.ProjectBrowser
                                && v.ViewType != ViewType.SystemBrowser
                                && v.ViewType != ViewType.DrawingSheet
                                && !(v is ViewSheet)
                                && !viewsOnSheetsForCount.Contains(v.Id.GetIdValue()));

                // 8-10. MEP/structural disconnections — language-neutral via FailureDefinitionId.
                // For pipes/ducts we also enumerate ConnectorManager.Connectors and take the
                // MAX of (warnings-based count, connector-based count). Revit's warning list
                // is incomplete on some real-world models (the user repro: 3 pipes visually
                // disconnected, GetWarnings() returns 0 matching OpenConnector entries) — the
                // direct connector check fills that gap. Walls stay warnings-only since walls
                // don't expose a ConnectorManager (they "connect" via geometric joins).
                metrics.WallsNotConnectedCount = CountWarningsByFailureIdAndCategory(doc, warnings,
                    new[] { BuiltInCategory.OST_Walls },
                    BuiltInFailures.ConnectorFailures.OpenConnector,
                    BuiltInFailures.ConnectorFailures.ElementsAreDisconnected);

                // Pipes not connected — warnings OR direct connector check, whichever is higher.
                int pipesViaWarnings = CountWarningsByFailureIdAndCategory(doc, warnings,
                    new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory },
                    BuiltInFailures.ConnectorFailures.OpenConnector,
                    BuiltInFailures.ConnectorFailures.ElementsAreDisconnected);
                int pipesViaConnectors = CountElementsWithDisconnectedConnectors(doc,
                    new[] { BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_PipeFitting, BuiltInCategory.OST_PipeAccessory });
                metrics.PipesNotConnectedCount = Math.Max(pipesViaWarnings, pipesViaConnectors);

                // Ducts not connected — same pattern.
                int ductsViaWarnings = CountWarningsByFailureIdAndCategory(doc, warnings,
                    new[] { BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory },
                    BuiltInFailures.ConnectorFailures.OpenConnector,
                    BuiltInFailures.ConnectorFailures.ElementsAreDisconnected);
                int ductsViaConnectors = CountElementsWithDisconnectedConnectors(doc,
                    new[] { BuiltInCategory.OST_DuctCurves, BuiltInCategory.OST_DuctFitting, BuiltInCategory.OST_DuctAccessory });
                metrics.DuctsNotConnectedCount = Math.Max(ductsViaWarnings, ductsViaConnectors);

                _logger?.LogInfo($"Collected 10 medium metrics for {doc.Title}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error collecting medium metrics: {ex.Message}", ex);
            }

            return metrics;
        }

        #endregion

        #region Expensive Metrics (2 metrics - Manual only)

        /// <summary>
        /// Collect all expensive metrics (manual trigger only)
        /// </summary>
        public ExpensiveMetrics CollectExpensiveMetrics(Document doc)
        {
            return CollectExpensiveMetrics(doc, null, null);
        }

        public ExpensiveMetrics CollectExpensiveMetrics(Document doc, Action<string, int>? progressCallback)
        {
            return CollectExpensiveMetrics(doc, progressCallback, null);
        }

        /// <summary>
        /// Collect all expensive metrics with progress reporting and cooperative cancellation.
        /// FamiliesOver5MbCount: measures actual family document size by extracting to temp.
        /// PurgeableElementsCount: uses HashSet of used TypeIds for efficient detection.
        /// <paramref name="isCancelled"/> is polled inside both inner loops and at phase
        /// boundaries — when it returns true the method exits early and the partially-filled
        /// metrics are returned so the caller can decide what to do.
        /// </summary>
        public ExpensiveMetrics CollectExpensiveMetrics(Document doc, Action<string, int>? progressCallback, Func<bool>? isCancelled)
        {
            var metrics = new ExpensiveMetrics();

            try
            {
                if (isCancelled?.Invoke() == true) return metrics;

                // Phase 1: Families over threshold (0% - 40%)
                var thresholdMb = _familySizeThresholdBytes / (1024.0 * 1024.0);
                progressCallback?.Invoke($"Collecting families (threshold: {thresholdMb:F0} MB)...", 5);
                metrics.FamiliesOver5MbCount = 0;
                try
                {
                    var families = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .Where(f => !f.IsInPlace) // Skip in-place families (no extractable document)
                        .ToList();

                    progressCallback?.Invoke($"Analyzing {families.Count} families...", 10);

                    var tempDir = Path.Combine(Path.GetTempPath(), "BIManage_FamilySize");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);

                    for (int i = 0; i < families.Count; i++)
                    {
                        if (isCancelled?.Invoke() == true)
                        {
                            _logger?.LogInfo($"[ExpensiveMetrics] Cancelled at family {i}/{families.Count}");
                            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                            return metrics;
                        }
                        var family = families[i];
                        try
                        {
                            var familyDoc = doc.EditFamily(family);
                            if (familyDoc != null)
                            {
                                try
                                {
                                    // Save to temp to measure actual file size
                                    var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid()}.rfa");
                                    var saveOpts = new SaveAsOptions { OverwriteExistingFile = true, Compact = false };
                                    using (EventProtectionSuppression.BeginScope())
                                    {
                                        familyDoc.SaveAs(tempPath, saveOpts);
                                    }

                                    var fileSize = new FileInfo(tempPath).Length;
                                    if (fileSize > _familySizeThresholdBytes)
                                    {
                                        metrics.FamiliesOver5MbCount++;
                                        _logger?.LogDebug($"Large family: {family.Name} ({fileSize / (1024.0 * 1024.0):F1} MB)");
                                    }

                                    // Cleanup temp file
                                    try { File.Delete(tempPath); } catch { }
                                }
                                finally
                                {
                                    // Close the family document without saving
                                    familyDoc.Close(false);
                                }
                            }
                            else
                            {
                                _logger?.LogWarning($"[FamilySize-Phase2] EditFamily returned null for '{family.Name}' (treated as cancelled)");
                            }
                        }
                        catch (Exception exFam)
                        {
                            _logger?.LogWarning($"[FamilySize-Phase2] EditFamily threw for '{family.Name}': {exFam.GetType().Name}: {exFam.Message}");
                        }

                        // Report progress every ~20% of families
                        if (families.Count > 0 && (i + 1) % Math.Max(1, families.Count / 5) == 0)
                        {
                            int familyProgress = 10 + (int)(30.0 * (i + 1) / families.Count);
                            progressCallback?.Invoke($"Analyzing families ({i + 1}/{families.Count})...", familyProgress);
                        }
                    }

                    // Cleanup temp directory
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to count large families: {ex.Message}");
                }

                if (isCancelled?.Invoke() == true) return metrics;

                // Phase 2: Purgeable elements count (40% - 95%)
                // Uses HashSet of all used TypeIds for O(1) lookup instead of per-type collector
                progressCallback?.Invoke("Checking purgeable elements...", 40);
                metrics.PurgeableElementsCount = 0;
                try
                {
                    // Build a set of all TypeIds actually in use by non-type elements
                    progressCallback?.Invoke("Building used types index...", 45);
                    var usedTypeIds = new HashSet<long>();
                    var allElements = new FilteredElementCollector(doc)
                        .WhereElementIsNotElementType()
                        .ToElements();

                    int elemCheckPoint = 0;
                    foreach (var elem in allElements)
                    {
                        // Poll cancellation every ~500 elements so a click on Cancel responds quickly
                        // on big models without paying a function call per element.
                        if ((++elemCheckPoint % 500) == 0 && isCancelled?.Invoke() == true) return metrics;
                        try
                        {
                            var typeId = elem.GetTypeId();
                            if (typeId != null && typeId != ElementId.InvalidElementId)
                                usedTypeIds.Add(typeId.GetIdValue());
                        }
                        catch { }
                    }

                    if (isCancelled?.Invoke() == true) return metrics;

                    progressCallback?.Invoke($"Checking types against {usedTypeIds.Count} used types...", 55);

                    // Count ALL ElementType subclasses that are unused (matches Revit's Purge Unused dialog scope)
                    var allTypes = new FilteredElementCollector(doc)
                        .WhereElementIsElementType()
                        .ToElements();

                    progressCallback?.Invoke($"Scanning {allTypes.Count} element types...", 60);

                    int typeCheckPoint = 0;
                    foreach (var type in allTypes)
                    {
                        if ((++typeCheckPoint % 500) == 0 && isCancelled?.Invoke() == true) return metrics;
                        try
                        {
                            if (!usedTypeIds.Contains(type.Id.GetIdValue()))
                                metrics.PurgeableElementsCount++;
                        }
                        catch { }
                    }

                    if (isCancelled?.Invoke() == true) return metrics;

                    progressCallback?.Invoke("Scanning materials and patterns...", 80);

                    // Also include Materials, LinePatterns, FillPatterns (not ElementType subclasses)
                    var extraPurgeable = new[]
                    {
                        typeof(Material), typeof(LinePatternElement), typeof(FillPatternElement)
                    };

                    foreach (var cls in extraPurgeable)
                    {
                        if (isCancelled?.Invoke() == true) return metrics;
                        try
                        {
                            var extras = new FilteredElementCollector(doc).OfClass(cls).ToElements();
                            foreach (var e in extras)
                            {
                                if (!usedTypeIds.Contains(e.Id.GetIdValue()))
                                    metrics.PurgeableElementsCount++;
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"Failed to count purgeable elements: {ex.Message}");
                }

                progressCallback?.Invoke("Finalizing...", 95);
                _logger?.LogInfo($"Collected 2 expensive metrics for {doc.Title}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error collecting expensive metrics: {ex.Message}", ex);
            }

            return metrics;
        }

        #endregion

        #region Detailed Metrics (for report only)

        /// <summary>
        /// Collects detailed list data for the detailed HTML report.
        /// Only called when "Generate Detailed Report" checkbox is checked.
        /// </summary>
        public DetailedMetrics CollectDetailedMetrics(Document doc, Action<string, int>? progressCallback)
        {
            var dm = new DetailedMetrics();

            // 1. Family sizes with names
            progressCallback?.Invoke("Collecting family details for report...", 0);
            try
            {
                var families = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .Where(f => !f.IsInPlace && f.IsEditable)
                    .ToList();

                int skippedCount = 0;

                {
                    var tempDir = Path.Combine(Path.GetTempPath(), "BIManage_DetailedReport");
                    if (!Directory.Exists(tempDir))
                        Directory.CreateDirectory(tempDir);

                    long totalKB = 0;
                    for (int i = 0; i < families.Count; i++)
                    {
                        var family = families[i];
                        try
                        {
                            var familyDoc = doc.EditFamily(family);
                            if (familyDoc != null)
                            {
                                try
                                {
                                    var tempPath = Path.Combine(tempDir, $"{Guid.NewGuid()}.rfa");
                                    var saveOpts = new SaveAsOptions { OverwriteExistingFile = true, Compact = false };
                                    using (EventProtectionSuppression.BeginScope())
                                    {
                                        familyDoc.SaveAs(tempPath, saveOpts);
                                    }
                                    var sizeKB = new FileInfo(tempPath).Length / 1024;
                                    dm.FamilySizes.Add((family.Name, sizeKB));
                                    totalKB += sizeKB;
                                    try { File.Delete(tempPath); } catch { }
                                }
                                finally
                                {
                                    familyDoc.Close(false);
                                }
                            }
                            else
                            {
                                // EditFamily returned null - add with 0 KB.
                                // Most common cause: a dialog raised during
                                // EditFamily was overridden with a result
                                // code that Revit treats as "user cancelled"
                                // (e.g. IDCANCEL=2). Logged so we can tell
                                // null-return apart from thrown-exception in
                                // diagnostics.
                                _logger?.LogWarning($"[FamilySize-Detailed] EditFamily returned null for '{family.Name}' (treated as cancelled — recorded as 0 KB)");
                                dm.FamilySizes.Add((family.Name, 0));
                                skippedCount++;
                            }
                        }
                        catch (Exception exFam)
                        {
                            // EditFamily / SaveAs threw — log the exception
                            // type and message so we can distinguish:
                            //   - InvalidOperationException (called inside a transaction)
                            //   - ArgumentException (family invalid / unloadable)
                            //   - Anything else
                            _logger?.LogWarning($"[FamilySize-Detailed] EditFamily threw for '{family.Name}': {exFam.GetType().Name}: {exFam.Message}");
                            dm.FamilySizes.Add((family.Name, 0));
                            skippedCount++;
                        }

                        if (families.Count > 0 && (i + 1) % Math.Max(1, families.Count / 5) == 0)
                            progressCallback?.Invoke($"Analyzing families ({i + 1}/{families.Count})...", (int)(50.0 * (i + 1) / families.Count));
                    }

                    dm.TotalFamilySizeKB = totalKB;
                    dm.FamilySizes.Sort((a, b) => b.SizeKB.CompareTo(a.SizeKB));

                    if (skippedCount > 0)
                        _logger?.LogDebug($"Skipped {skippedCount} non-editable families during detailed report collection (sizes shown as 0 KB)");

                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to collect family details: {ex.Message}");
            }

            // 2. Non-native object style names — pair each sub-category (CAD layer) with
            // its parent category (the DWG file name) so rows like "0", "Defpoints" carry
            // their source context. Without this, identical layer names from multiple DWGs
            // sort together and lose their origin.
            progressCallback?.Invoke("Collecting object style details...", 55);
            try
            {
                foreach (Category cat in doc.Settings.Categories)
                {
                    if (cat.Id.GetIdValue() > 0)
                    {
                        var parentName = string.IsNullOrWhiteSpace(cat.Name) ? "Unknown" : cat.Name;
                        dm.NonNativeObjectStyles.Add(parentName);

                        if (cat.SubCategories != null)
                        {
                            foreach (Category subCat in cat.SubCategories)
                            {
                                if (subCat.Id.GetIdValue() > 0)
                                {
                                    var subName = string.IsNullOrWhiteSpace(subCat.Name) ? "Unknown" : subCat.Name;
                                    dm.NonNativeObjectStyles.Add($"{parentName} | {subName}");
                                }
                            }
                        }
                    }
                }
                dm.NonNativeObjectStyles.Sort();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to collect non-native styles: {ex.Message}");
            }

            // 3. Workset names
            progressCallback?.Invoke("Collecting workset details...", 70);
            try
            {
                if (doc.IsWorkshared)
                {
                    var worksetCollector = new FilteredWorksetCollector(doc);
                    foreach (Workset workset in worksetCollector)
                    {
                        if (workset.Kind == WorksetKind.UserWorkset)
                            dm.Worksets.Add(workset.Name);
                    }
                    dm.Worksets.Sort();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to collect workset details: {ex.Message}");
            }

            // 4. Warning messages grouped by FailureDefinitionId (the underlying warning
            //    TYPE) rather than by the rendered description text. This is the same
            //    grouping intent the field Python sketch had — w.GetFailureDefinitionId() is
            //    the stable type ID, while w.GetDescriptionText() embeds variable bits like
            //    system numbers ("Elements in SAD 1 are not connected…", "…SAD 2…", "…SAD
            //    3…"). Grouping by description gave one row per system (e.g. 200 rows of
            //    Count=1) instead of one row of Count=200 for the underlying error.
            //    Display label is the first warning's description in each group; the count
            //    column carries the true total so user-visible numbers match the Revit
            //    Review Warnings dialog.
            progressCallback?.Invoke("Collecting warning details...", 80);
            try
            {
                var warnings = doc.GetWarnings();
                var warnGroups = new Dictionary<Guid, (string Message, int Count)>();
                foreach (var w in warnings)
                {
                    Guid typeKey;
                    try { typeKey = w.GetFailureDefinitionId().Guid; }
                    catch { typeKey = Guid.Empty; }
                    var msg = w.GetDescriptionText() ?? "Unknown warning";
                    if (warnGroups.TryGetValue(typeKey, out var existing))
                        warnGroups[typeKey] = (existing.Message, existing.Count + 1);
                    else
                        warnGroups[typeKey] = (msg, 1);
                }
                dm.WarningsByType = warnGroups.Values
                    .OrderByDescending(g => g.Count)
                    .ThenBy(g => g.Message)
                    .Select(g => (g.Message, g.Count))
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to collect warning details: {ex.Message}");
            }

            // 5. Levels
            progressCallback?.Invoke("Collecting levels...", 82);
            try
            {
                dm.Levels = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Levels)
                    .WhereElementIsNotElementType()
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .Select(l => l.Name)
                    .ToList();
            }
            catch { }

            // 6. Grids
            progressCallback?.Invoke("Collecting grids...", 84);
            try
            {
                dm.Grids = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Grids)
                    .WhereElementIsNotElementType()
                    .Select(g => g.Name)
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { }

            // 7. Linked Revit models
            progressCallback?.Invoke("Collecting links...", 85);
            try
            {
                dm.LinkedRevitModels = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Select(l => l.Name)
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { }

            // 8. Linked & Imported DWGs — ImportInstance.Name returns the placement string
            // (e.g. "location <Not Shared>"), not the DWG file name. The actual file name
            // lives on the Category (Revit creates a Category per imported DWG named after
            // the file). Fall back to the type element's name, then i.Name as a last resort.
            try
            {
                var imports = new FilteredElementCollector(doc)
                    .OfClass(typeof(ImportInstance))
                    .Cast<ImportInstance>()
                    .ToList();

                string ResolveDwgName(ImportInstance i)
                {
                    string? n = null;
                    try { n = i.Category?.Name; } catch { }
                    if (string.IsNullOrWhiteSpace(n))
                    {
                        try
                        {
                            var t = doc.GetElement(i.GetTypeId());
                            n = t?.Name;
                        }
                        catch { }
                    }
                    if (string.IsNullOrWhiteSpace(n))
                    {
                        try { n = i.Name; } catch { }
                    }
                    return string.IsNullOrWhiteSpace(n) ? "Unknown" : n;
                }

                dm.LinkedDwgFiles = imports.Where(i => i.IsLinked).Select(ResolveDwgName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
                dm.ImportedDwgFiles = imports.Where(i => !i.IsLinked).Select(ResolveDwgName)
                    .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n).ToList();
            }
            catch { }

            // 9. Raster images
            try
            {
                dm.RasterImages = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_RasterImages)
                    .WhereElementIsNotElementType()
                    .Select(r => r.Name)
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { }

            // 10. Views & Sheets
            progressCallback?.Invoke("Collecting views and sheets...", 88);
            try
            {
                // Filter out Revit's internal/system views so the report only lists views
                // that actually appear in the Project Browser. Without this, the list
                // includes:
                //   - "Project View" (ViewType.ProjectBrowser) — the Project Browser pane
                //     itself, not a user view.
                //   - "System Browser" (ViewType.SystemBrowser) — the MEP System Browser
                //     pane, also not a user view.
                //   - ViewType.Internal — other Revit-internal views never shown to users.
                //   - "<Revision Schedule>" (ViewSchedule with IsTitleblockRevisionSchedule
                //     = true) — auto-generated revision tracker that lives inside title
                //     blocks; Revit surfaces it only under "Revisions on Sheet", never in
                //     the Project Browser.
                // Matches the filter ViewsNotOnSheetsCount uses higher up in this file so
                // counts and report contents agree.
                var allViews = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => IsUserVisibleView(v))
                    .ToList();

                dm.Views = allViews
                    .OrderBy(v => v.ViewType.ToString())
                    .ThenBy(v => v.Name)
                    .Select(v => (v.Name, v.ViewType.ToString()))
                    .ToList();

                dm.ViewTemplates = new FilteredElementCollector(doc)
                    .OfClass(typeof(View))
                    .Cast<View>()
                    .Where(v => v.IsTemplate)
                    .Select(v => v.Name)
                    .OrderBy(n => n)
                    .ToList();

                dm.Sheets = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .OrderBy(s => s.SheetNumber)
                    .Select(s => $"{s.SheetNumber} - {s.Name}")
                    .ToList();

                // Views not on sheets
                var sheetsSet = new FilteredElementCollector(doc)
                    .OfClass(typeof(ViewSheet))
                    .Cast<ViewSheet>()
                    .ToList();
                var viewsOnSheets = new HashSet<long>();
                foreach (var sheet in sheetsSet)
                {
                    foreach (var vpId in sheet.GetAllPlacedViews())
                        viewsOnSheets.Add(vpId.GetIdValue());
                }
                dm.ViewsNotOnSheets = allViews
                    .Where(v => !viewsOnSheets.Contains(v.Id.GetIdValue())
                                && !(v is ViewSheet)
                                && v.ViewType != ViewType.DrawingSheet)
                    .Select(v => $"{v.Name} ({v.ViewType})")
                    .OrderBy(n => n)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to collect view details: {ex.Message}");
            }

            // 11. Groups
            progressCallback?.Invoke("Collecting groups...", 92);
            try
            {
                dm.ModelGroups = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_IOSModelGroups)
                    .WhereElementIsNotElementType()
                    .Select(g => g.Name)
                    .GroupBy(n => n)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} (x{g.Count()})")
                    .ToList();

                dm.DetailGroups = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_IOSDetailGroups)
                    .WhereElementIsNotElementType()
                    .Select(g => g.Name)
                    .GroupBy(n => n)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} (x{g.Count()})")
                    .ToList();
            }
            catch { }

            // 12. Design Options
            try
            {
                dm.DesignOptions = new FilteredElementCollector(doc)
                    .OfClass(typeof(DesignOption))
                    .Select(o => o.Name)
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { }

            // 13. In-place families
            try
            {
                dm.InPlaceFamilyNames = new FilteredElementCollector(doc)
                    .OfClass(typeof(FamilyInstance))
                    .Cast<FamilyInstance>()
                    .Where(fi => fi.Symbol?.Family?.IsInPlace == true)
                    .Select(fi => fi.Symbol.Family.Name)
                    .Distinct()
                    .OrderBy(n => n)
                    .ToList();
            }
            catch { }

            // 14. Unplaced & Unenclosed rooms
            progressCallback?.Invoke("Collecting room details...", 96);
            try
            {
                var rooms = new FilteredElementCollector(doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType()
                    .Cast<SpatialElement>()
                    .ToList();

                dm.UnplacedRooms = rooms
                    .Where(r => r.Location == null)
                    .Select(r => r.Name ?? $"Room {r.Id.GetIdValue()}")
                    .OrderBy(n => n)
                    .ToList();

                dm.UnenclosedRooms = rooms
                    .Where(r => r.Location != null && r.Area <= 0)
                    .Select(r => r.Name ?? $"Room {r.Id.GetIdValue()}")
                    .OrderBy(n => n)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to collect room details: {ex.Message}");
            }

            progressCallback?.Invoke("Details collected", 100);
            return dm;
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// Gets file size in bytes. For cloud models, searches the local CollaborationCache.
        /// </summary>
        private long? GetFileSizeBytes(Document doc)
        {
            try
            {
                var path = doc.PathName;
                if (string.IsNullOrEmpty(path)) return null;

                // Local or network-mapped file
                if (File.Exists(path))
                    return new FileInfo(path).Length;

                // Cloud model — search CollaborationCache by model GUID
                if (doc.IsModelInCloud)
                    return TryGetCloudModelFileSizeFromCache(doc);

                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to get file size: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Searches %LOCALAPPDATA%\Autodesk\Revit\*\CollaborationCache for the cloud model's
        /// local cached .rvt file. Cache structure: CollaborationCache/{accountId}/{projectGuid}/{modelGuid}.rvt
        /// </summary>
        private long? TryGetCloudModelFileSizeFromCache(Document doc)
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var revitBasePath = Path.Combine(appData, "Autodesk", "Revit");
                if (!Directory.Exists(revitBasePath)) return null;

                // Primary: search by cloud model GUID (cache files are named {modelGuid}.rvt)
                string? modelGuidSearch = null;
                try
                {
                    var cloudPath = doc.GetCloudModelPath();
                    if (cloudPath != null)
                    {
                        var modelGuid = cloudPath.GetModelGUID();
                        if (modelGuid != Guid.Empty)
                            modelGuidSearch = modelGuid.ToString() + ".rvt";
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"GetCloudModelPath failed: {ex.Message}");
                }

                // Fallback: search by document title
                string? titleSearch = null;
                var docTitle = doc.Title ?? string.Empty;
                if (!string.IsNullOrEmpty(docTitle))
                {
                    titleSearch = docTitle.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
                        ? docTitle
                        : docTitle + ".rvt";
                }

                if (modelGuidSearch == null && titleSearch == null) return null;

                FileInfo? bestMatch = null;
                foreach (var versionDir in Directory.GetDirectories(revitBasePath, "Autodesk Revit *"))
                {
                    var cacheDir = Path.Combine(versionDir, "CollaborationCache");
                    if (!Directory.Exists(cacheDir)) continue;

                    // Try GUID-based search first (exact match)
                    if (modelGuidSearch != null)
                    {
                        var guidFiles = Directory.GetFiles(cacheDir, modelGuidSearch, SearchOption.AllDirectories);
                        foreach (var f in guidFiles)
                        {
                            var fi = new FileInfo(f);
                            if (fi.Length > 0 && (bestMatch == null || fi.LastWriteTimeUtc > bestMatch.LastWriteTimeUtc))
                                bestMatch = fi;
                        }
                    }

                    // If GUID search found nothing, try title-based fallback
                    if (bestMatch == null && titleSearch != null)
                    {
                        var titleFiles = Directory.GetFiles(cacheDir, titleSearch, SearchOption.AllDirectories);
                        foreach (var f in titleFiles)
                        {
                            var fi = new FileInfo(f);
                            if (fi.Length > 0 && (bestMatch == null || fi.LastWriteTimeUtc > bestMatch.LastWriteTimeUtc))
                                bestMatch = fi;
                        }
                    }
                }

                if (bestMatch != null)
                    _logger?.LogDebug($"Cloud model cache found: {bestMatch.FullName} ({bestMatch.Length} bytes)");

                return bestMatch?.Length;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"CollaborationCache search failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Count warnings matching a specific FailureDefinitionId (language-neutral).
        /// </summary>
        private static int CountWarningsByFailureId(IList<FailureMessage> warnings, FailureDefinitionId targetId)
        {
            if (warnings == null || warnings.Count == 0) return 0;

            return warnings.Count(w =>
            {
                try { return w.GetFailureDefinitionId() == targetId; }
                catch { return false; }
            });
        }

        /// <summary>
        /// Count warnings matching any of the given FailureDefinitionIds AND involving elements
        /// from the specified categories. Language-neutral.
        /// </summary>
        private static int CountWarningsByFailureIdAndCategory(
            Document doc,
            IList<FailureMessage> warnings,
            BuiltInCategory[] categories,
            params FailureDefinitionId[] targetIds)
        {
            if (warnings == null || warnings.Count == 0) return 0;

            return warnings.Count(w =>
            {
                try
                {
                    var failId = w.GetFailureDefinitionId();
                    if (!targetIds.Any(id => id == failId))
                        return false;

                    // Check if warning involves elements from target categories
                    var failingIds = w.GetFailingElements();
                    if (failingIds == null || failingIds.Count == 0)
                        return false;

                    foreach (var elemId in failingIds)
                    {
                        try
                        {
                            var elem = doc.GetElement(elemId);
                            if (elem?.Category != null)
                            {
                                var catId = elem.Category.Id.GetIdValue();
                                if (categories.Any(cat => (long)cat == catId))
                                    return true;
                            }
                        }
                        catch { }
                    }

                    return false;
                }
                catch { return false; }
            });
        }

        /// <summary>
        /// Direct connector check: counts elements in the given categories that have at least
        /// one End-type connector with IsConnected == false. Used alongside the warnings-based
        /// counter for pipes/ducts because Revit's warning list does not always emit
        /// OpenConnector / ElementsAreDisconnected entries for visually-disconnected MEP
        /// curves (e.g. user-removed connections that never produced a warning).
        /// </summary>
        private static int CountElementsWithDisconnectedConnectors(Document doc, BuiltInCategory[] categories)
        {
            int count = 0;
            foreach (var cat in categories)
            {
                FilteredElementCollector collector;
                try
                {
                    collector = new FilteredElementCollector(doc)
                        .OfCategory(cat)
                        .WhereElementIsNotElementType();
                }
                catch { continue; }

                foreach (var elem in collector)
                {
                    try
                    {
                        ConnectorManager? cm = null;
                        if (elem is MEPCurve mep)
                        {
                            cm = mep.ConnectorManager;
                        }
                        else if (elem is FamilyInstance fi && fi.MEPModel != null)
                        {
                            cm = fi.MEPModel.ConnectorManager;
                        }

                        if (cm == null) continue;

                        bool hasOpenEnd = false;
                        foreach (Connector conn in cm.Connectors)
                        {
                            try
                            {
                                if (conn.ConnectorType == ConnectorType.End && !conn.IsConnected)
                                {
                                    hasOpenEnd = true;
                                    break;
                                }
                            }
                            catch { }
                        }
                        if (hasOpenEnd) count++;
                    }
                    catch { }
                }
            }
            return count;
        }

        /// <summary>
        /// Predicate matching the views Revit actually shows in the Project Browser. Used to
        /// keep the detailed report's view listings consistent with what users see in Revit.
        /// Excludes:
        ///   - View templates (IsTemplate) — never user-facing views by definition.
        ///   - ViewType.ProjectBrowser / SystemBrowser / Internal — the browser panes
        ///     themselves; Revit returns them as View instances but they are not user
        ///     views and never appear in the Project Browser tree.
        ///   - ViewSchedule.Definition.IsTitleblockRevisionSchedule — the auto-generated
        ///     "&lt;Revision Schedule&gt;" view that lives inside title block families.
        ///     Revit surfaces it only under "Revisions on Sheet", never in the Project
        ///     Browser.
        /// </summary>
        private static bool IsUserVisibleView(View v)
        {
            if (v == null) return false;
            if (v.IsTemplate) return false;
            var t = v.ViewType;
            if (t == ViewType.ProjectBrowser || t == ViewType.SystemBrowser || t == ViewType.Internal)
                return false;
            // Revit names its auto-generated schedules with angle brackets — e.g.
            // "<Revision Schedule>" (the titleblock revision tracker), "<Sheet List>" before
            // it's first edited, etc. Those are not user-facing views; they live inside
            // title blocks / sheet placeholders and never appear in the Project Browser.
            // The angle-bracket convention is stable across Revit 2021-2026 and avoids
            // depending on ScheduleDefinition.IsTitleblockRevisionSchedule which isn't
            // present on every Revit API version we target.
            if (v is ViewSchedule)
            {
                var name = v.Name;
                if (!string.IsNullOrEmpty(name) && name.StartsWith("<") && name.EndsWith(">"))
                    return false;
            }
            return true;
        }

        #endregion
    }
}
