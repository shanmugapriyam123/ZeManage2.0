using System.Text.Json;
using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;

namespace BIManage.AI.Terminal.Tools.ColorSplash;

/// <summary>
/// External event handler for the <c>color_splash</c> MCP tool.
/// Groups elements by parameter value in the active view and applies a unique color
/// override per group. Modifies view overrides only — does NOT modify model elements
/// or parameters. The Transaction is required by Revit's API for view overrides.
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
internal sealed class ColorSplashEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);
    private readonly Random _random = new();

    private string? _categoryName;
    private string? _parameterName;
    private bool _useGradient;
    private List<int[]>? _customColors; // pre-parsed RGB triples

    public object? ColoringResults { get; private set; }

    public void SetParameters(string categoryName, string parameterName, bool useGradient, List<int[]>? customColors)
    {
        _categoryName = categoryName;
        _parameterName = parameterName;
        _useGradient = useGradient;
        _customColors = customColors;
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
                ColoringResults = new { success = false, message = "No active document" };
                return;
            }

            var activeView = doc.ActiveView;
            if (!activeView.CanUseTemporaryVisibilityModes())
            {
                ColoringResults = new
                {
                    success = false,
                    message = $"Cannot modify visibility settings in {activeView.ViewType} views"
                };
                return;
            }

            // Find category by name (case-insensitive)
            Category? category = null;
            foreach (Category cat in doc.Settings.Categories)
            {
                if (string.Equals(cat.Name, _categoryName, StringComparison.OrdinalIgnoreCase))
                {
                    category = cat;
                    break;
                }
            }

            if (category == null)
            {
                ColoringResults = new
                {
                    success = false,
                    message = $"Category '{_categoryName}' not found"
                };
                return;
            }

            var elements = new FilteredElementCollector(doc, activeView.Id)
                .OfCategoryId(category.Id)
                .WhereElementIsNotElementType()
                .WhereElementIsViewIndependent()
                .ToElements();

            if (elements.Count == 0)
            {
                ColoringResults = new
                {
                    success = false,
                    message = $"No elements of category '{_categoryName}' found in the current view"
                };
                return;
            }

            // Group element IDs by parameter value
            var parameterValueGroups = new Dictionary<string, List<ElementId>>();
            foreach (var element in elements)
            {
                var parameter = element.LookupParameter(_parameterName);
                if (parameter == null)
                {
                    var typeId = element.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                    {
                        var elementType = doc.GetElement(typeId);
                        parameter = elementType?.LookupParameter(_parameterName);
                    }
                }

                var key = parameter is { HasValue: true }
                    ? GetParameterValueAsString(doc, parameter)
                    : "None";

                if (!parameterValueGroups.TryGetValue(key, out var bucket))
                {
                    bucket = new List<ElementId>();
                    parameterValueGroups[key] = bucket;
                }
                bucket.Add(element.Id);
            }

            if (parameterValueGroups.Count == 0)
            {
                ColoringResults = new
                {
                    success = false,
                    message = $"No elements with parameter '{_parameterName}' found"
                };
                return;
            }

            var colorMap = GenerateColors(parameterValueGroups.Keys.ToList());
            var solidFillPatternId = GetSolidFillPatternId(doc);

            using var transaction = new Transaction(doc, "ZeManage Color Splash");
            transaction.Start();

            var results = new List<object>();
            foreach (var group in parameterValueGroups)
            {
                var rgb = colorMap[group.Key];
                var color = new Color((byte)rgb[0], (byte)rgb[1], (byte)rgb[2]);

                var overrides = new OverrideGraphicSettings();
                overrides.SetProjectionLineColor(color);
                overrides.SetSurfaceForegroundPatternColor(color);
                overrides.SetCutForegroundPatternColor(color);

                if (solidFillPatternId != ElementId.InvalidElementId)
                {
                    overrides.SetSurfaceForegroundPatternId(solidFillPatternId);
                    overrides.SetCutForegroundPatternId(solidFillPatternId);
                }

                foreach (var id in group.Value)
                {
                    activeView.SetElementOverrides(id, overrides);
                }

                results.Add(new
                {
                    parameterValue = group.Key,
                    count = group.Value.Count,
                    color = new { r = rgb[0], g = rgb[1], b = rgb[2] },
                    elementIds = group.Value.Select(id => id.GetValue().ToString()).ToList()
                });
            }

            transaction.Commit();

            ColoringResults = new
            {
                success = true,
                totalElements = elements.Count,
                coloredGroups = parameterValueGroups.Count,
                results
            };
        }
        catch (Exception ex)
        {
            ColoringResults = new { success = false, message = $"Error: {ex.Message}" };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public string GetName() => "ZeManage Color Splash";

    private static string GetParameterValueAsString(Document doc, Parameter parameter)
    {
        if (!parameter.HasValue) return "None";

        switch (parameter.StorageType)
        {
            case StorageType.Double:
                return parameter.AsValueString() ?? parameter.AsDouble().ToString();

            case StorageType.ElementId:
                var id = parameter.AsElementId();
                if (id == ElementId.InvalidElementId) return "None";
                return doc.GetElement(id)?.Name ?? id.GetValue().ToString();

            case StorageType.Integer:
                // Booleans show up as 0/1 integers; surface them as True/False when we can prove
                // the parameter is a Yes/No type. Otherwise fall back to the raw value.
                if (parameter.Definition is InternalDefinition internalDef)
                {
#if REVIT2023_OR_GREATER
                    try
                    {
                        var paramTypeId = internalDef.GetDataType();
                        if (paramTypeId != null && paramTypeId.Equals(SpecTypeId.Boolean.YesNo))
                        {
                            return parameter.AsInteger() == 1 ? "True" : "False";
                        }
                    }
                    catch
                    {
                        // Fall through to known-built-in fallback below.
                    }
#endif
                    if (internalDef.BuiltInParameter == BuiltInParameter.IS_VISIBLE_PARAM)
                    {
                        return parameter.AsInteger() == 1 ? "True" : "False";
                    }
                }
                return parameter.AsValueString() ?? parameter.AsInteger().ToString();

            case StorageType.String:
                return parameter.AsString() ?? "None";

            default:
                return "None";
        }
    }

    private Dictionary<string, int[]> GenerateColors(List<string> paramValues)
    {
        var colorMap = new Dictionary<string, int[]>();

        if (_customColors is { Count: > 0 })
        {
            for (int i = 0; i < paramValues.Count; i++)
            {
                colorMap[paramValues[i]] = i < _customColors.Count
                    ? _customColors[i]
                    : GenerateRandomColor();
            }
        }
        else if (_useGradient && paramValues.Count > 1)
        {
            int[] startColor = { 0, 0, 180 };
            int[] endColor = { 180, 0, 0 };
            for (int i = 0; i < paramValues.Count; i++)
            {
                double ratio = (double)i / (paramValues.Count - 1);
                colorMap[paramValues[i]] = new[]
                {
                    (int)(startColor[0] + (endColor[0] - startColor[0]) * ratio),
                    (int)(startColor[1] + (endColor[1] - startColor[1]) * ratio),
                    (int)(startColor[2] + (endColor[2] - startColor[2]) * ratio)
                };
            }
        }
        else
        {
            foreach (var value in paramValues)
                colorMap[value] = GenerateRandomColor();
        }

        return colorMap;
    }

    private int[] GenerateRandomColor() => new[]
    {
        _random.Next(30, 200),
        _random.Next(30, 200),
        _random.Next(30, 200)
    };

    private static ElementId GetSolidFillPatternId(Document doc)
    {
        foreach (FillPatternElement patternElement in
                 new FilteredElementCollector(doc).OfClass(typeof(FillPatternElement)))
        {
            if (patternElement.GetFillPattern().IsSolidFill)
                return patternElement.Id;
        }
        return ElementId.InvalidElementId;
    }
}
