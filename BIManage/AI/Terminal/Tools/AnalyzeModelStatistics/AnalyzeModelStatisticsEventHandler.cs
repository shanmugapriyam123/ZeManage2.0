using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.Models;

namespace BIManage.AI.Terminal.Tools.AnalyzeModelStatistics;

/// <summary>
/// External event handler for the <c>analyze_model_statistics</c> MCP tool.
/// Walks every non-type element in the active document and summarises by category and level.
/// Can be expensive on large models (use the 120s timeout).
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
internal sealed class AnalyzeModelStatisticsEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);
    private bool _includeDetailedTypes = true;

    public AnalyzeModelStatisticsResult? ResultInfo { get; private set; }

    public void SetParameters(bool includeDetailedTypes)
    {
        _includeDetailedTypes = includeDetailedTypes;
    }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                ResultInfo = new AnalyzeModelStatisticsResult { Success = false, Message = "No active document" };
                return;
            }

            string projectName = doc.Title;

            int totalElements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .GetElementCount();

            int totalTypes = new FilteredElementCollector(doc)
                .WhereElementIsElementType()
                .GetElementCount();

            int totalViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View))
                .Cast<View>()
                .Count(v => !v.IsTemplate);

            int totalSheets = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSheet))
                .GetElementCount();

            var categoryStats = new Dictionary<string, CategoryStatistics>();
            var familyNames = new HashSet<string>();

            var elements = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .ToElements();

            foreach (var elem in elements)
            {
                if (elem.Category == null) continue;
                var catName = elem.Category.Name;

                if (!categoryStats.TryGetValue(catName, out var stats))
                {
                    stats = new CategoryStatistics { CategoryName = catName };
                    categoryStats[catName] = stats;
                }
                stats.ElementCount++;

                if (elem is FamilyInstance fi)
                {
                    var familyName = fi.Symbol?.Family?.Name;
                    var typeName = fi.Symbol?.Name;

                    if (!string.IsNullOrEmpty(familyName))
                        familyNames.Add(familyName!);

                    if (_includeDetailedTypes && !string.IsNullOrEmpty(typeName))
                    {
                        var existing = stats.Types
                            .FirstOrDefault(t => t.TypeName == typeName && t.FamilyName == familyName);

                        if (existing != null)
                        {
                            existing.InstanceCount++;
                        }
                        else
                        {
                            stats.Types.Add(new TypeStatistics
                            {
                                TypeName = typeName,
                                FamilyName = familyName,
                                InstanceCount = 1
                            });
                        }
                    }
                }
            }

            foreach (var stat in categoryStats.Values)
            {
                stat.TypeCount = stat.Types.Select(t => t.TypeName).Distinct().Count();
                stat.FamilyCount = stat.Types.Select(t => t.FamilyName).Distinct().Count();
            }

            // Per-level element counts (ordered by elevation)
            var levelStats = new List<LevelStatistics>();
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation);

            foreach (var level in levels)
            {
                int elementCount = new FilteredElementCollector(doc)
                    .WhereElementIsNotElementType()
                    .Count(e => e.LevelId == level.Id);

                const double mmPerFoot = 304.8;
                levelStats.Add(new LevelStatistics
                {
                    LevelName = level.Name,
                    ElevationFeet = level.Elevation,
                    ElevationMm = Math.Round(level.Elevation * mmPerFoot, 1),
                    Elevation = level.Elevation, // legacy alias
                    ElementCount = elementCount
                });
            }

            ResultInfo = new AnalyzeModelStatisticsResult
            {
                ProjectName = projectName,
                TotalElements = totalElements,
                TotalTypes = totalTypes,
                TotalFamilies = familyNames.Count,
                TotalViews = totalViews,
                TotalSheets = totalSheets,
                Categories = categoryStats.Values.OrderByDescending(c => c.ElementCount).ToList(),
                Levels = levelStats,
                Success = true,
                Message = $"Successfully analyzed model with {totalElements} elements across {categoryStats.Count} categories"
            };
        }
        catch (Exception ex)
        {
            ResultInfo = new AnalyzeModelStatisticsResult
            {
                Success = false,
                Message = $"Error analyzing model statistics: {ex.Message}"
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public string GetName() => "ZeManage Analyze Model Statistics";
}
