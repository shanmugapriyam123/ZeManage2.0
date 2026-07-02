using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.Models;

namespace BIManage.AI.Terminal.Tools.GetMaterialQuantities;

/// <summary>
/// External event handler for the <c>get_material_quantities</c> MCP tool.
/// Aggregates per-material area and volume across either the current selection or
/// the whole document (optionally filtered by category).
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
internal sealed class GetMaterialQuantitiesEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);
    private List<string>? _categoryFilters;
    private bool _selectedElementsOnly;

    public GetMaterialQuantitiesResult? ResultInfo { get; private set; }

    public void SetParameters(List<string>? categoryFilters, bool selectedElementsOnly)
    {
        _categoryFilters = categoryFilters;
        _selectedElementsOnly = selectedElementsOnly;
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
            var uiDoc = app.ActiveUIDocument;
            if (uiDoc == null)
            {
                ResultInfo = new GetMaterialQuantitiesResult { Success = false, Message = "No active document" };
                return;
            }
            var doc = uiDoc.Document;

            // Aggregator keyed by material element id
            var materialData = new Dictionary<ElementId, MaterialQuantityModel>();

            ICollection<Element> elements;
            if (_selectedElementsOnly)
            {
                elements = uiDoc.Selection.GetElementIds()
                    .Select(id => doc.GetElement(id))
                    .Where(e => e != null)
                    .ToList()!;
            }
            else
            {
                var collector = new FilteredElementCollector(doc).WhereElementIsNotElementType();

                if (_categoryFilters is { Count: > 0 })
                {
                    var builtInCategories = new List<BuiltInCategory>();
                    foreach (var catName in _categoryFilters)
                    {
                        if (Enum.TryParse<BuiltInCategory>(catName, out var cat))
                            builtInCategories.Add(cat);
                    }

                    if (builtInCategories.Count > 0)
                    {
                        collector = collector.WherePasses(new ElementMulticategoryFilter(builtInCategories));
                    }
                }

                elements = collector.ToElements();
            }

            foreach (var element in elements)
            {
                var materialIds = element.GetMaterialIds(false);

                foreach (var matId in materialIds)
                {
                    if (doc.GetElement(matId) is not Material material) continue;

                    if (!materialData.TryGetValue(matId, out var entry))
                    {
                        entry = new MaterialQuantityModel
                        {
#if REVIT2024_OR_GREATER
                            MaterialId = matId.Value,
#else
                            MaterialId = matId.IntegerValue,
#endif
                            MaterialName = material.Name,
                            MaterialClass = material.MaterialClass
                        };
                        materialData[matId] = entry;
                    }

                    entry.Area += element.GetMaterialArea(matId, false);
                    entry.Volume += element.GetMaterialVolume(matId);

#if REVIT2024_OR_GREATER
                    var elemIdValue = element.Id.Value;
#else
                    var elemIdValue = (long)element.Id.IntegerValue;
#endif
                    if (!entry.ElementIds.Contains(elemIdValue))
                    {
                        entry.ElementIds.Add(elemIdValue);
                        entry.ElementCount++;
                    }
                }
            }

            var materials = materialData.Values.ToList();
            ResultInfo = new GetMaterialQuantitiesResult
            {
                TotalMaterials = materials.Count,
                TotalArea = materials.Sum(m => m.Area),
                TotalVolume = materials.Sum(m => m.Volume),
                Materials = materials,
                Success = true,
                Message = $"Successfully calculated quantities for {materials.Count} materials"
            };
        }
        catch (Exception ex)
        {
            ResultInfo = new GetMaterialQuantitiesResult
            {
                Success = false,
                Message = $"Error calculating material quantities: {ex.Message}"
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public string GetName() => "ZeManage Get Material Quantities";
}
