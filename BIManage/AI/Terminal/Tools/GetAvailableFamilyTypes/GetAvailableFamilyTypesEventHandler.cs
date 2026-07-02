using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.Models;

namespace BIManage.AI.Terminal.Tools.GetAvailableFamilyTypes;

/// <summary>
/// External event handler for the <c>get_available_family_types</c> MCP tool.
/// Enumerates loadable families + system family types, with optional category and name filters.
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
internal sealed class GetAvailableFamilyTypesEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);

    public List<string>? CategoryList { get; set; }
    public string? FamilyNameFilter { get; set; }
    public int? Limit { get; set; }

    public List<FamilyTypeInfo> ResultFamilyTypes { get; private set; } = new();

    public bool WaitForCompletion(int timeoutMilliseconds = 12500)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null) { ResultFamilyTypes = new(); return; }

            // Loadable families
            var familySymbols = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .Cast<FamilySymbol>()
                .Cast<ElementType>();

            // System family types (walls, floors, roofs, ceilings, curtain systems)
            var systemTypes = new List<ElementType>();
            systemTypes.AddRange(new FilteredElementCollector(doc).OfClass(typeof(WallType)).Cast<ElementType>());
            systemTypes.AddRange(new FilteredElementCollector(doc).OfClass(typeof(FloorType)).Cast<ElementType>());
            systemTypes.AddRange(new FilteredElementCollector(doc).OfClass(typeof(RoofType)).Cast<ElementType>());
            systemTypes.AddRange(new FilteredElementCollector(doc).OfClass(typeof(CeilingType)).Cast<ElementType>());
            systemTypes.AddRange(new FilteredElementCollector(doc).OfClass(typeof(CurtainSystemType)).Cast<ElementType>());

            IEnumerable<ElementType> filtered = familySymbols.Concat(systemTypes);

            // Category filter — accepts BuiltInCategory enum names
            if (CategoryList != null && CategoryList.Count > 0)
            {
                var validCategoryIds = new HashSet<long>();
                foreach (var categoryName in CategoryList)
                {
                    if (Enum.TryParse<BuiltInCategory>(categoryName, out var bic))
                    {
                        validCategoryIds.Add((long)bic);
                    }
                }

                if (validCategoryIds.Count > 0)
                {
                    filtered = filtered.Where(et =>
                    {
                        if (et.Category == null) return false;
#if REVIT2024_OR_GREATER
                        return validCategoryIds.Contains(et.Category.Id.Value);
#else
                        return validCategoryIds.Contains(et.Category.Id.IntegerValue);
#endif
                    });
                }
            }

            // Name filter — fuzzy match against family name OR type name
            if (!string.IsNullOrEmpty(FamilyNameFilter))
            {
                var needle = FamilyNameFilter!;
                filtered = filtered.Where(et =>
                {
                    var familyName = et is FamilySymbol fs
                        ? fs.FamilyName
                        : et.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM)?.AsString() ?? "";

                    return (familyName?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                        || et.Name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
                });
            }

            if (Limit.HasValue && Limit.Value > 0)
            {
                filtered = filtered.Take(Limit.Value);
            }

            ResultFamilyTypes = filtered.Select(et =>
            {
                string? familyName;
                if (et is FamilySymbol fs)
                {
                    familyName = fs.FamilyName;
                }
                else
                {
                    var param = et.get_Parameter(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
                    familyName = param?.AsString() ?? et.GetType().Name.Replace("Type", "");
                }

                return new FamilyTypeInfo
                {
#if REVIT2024_OR_GREATER
                    FamilyTypeId = et.Id.Value,
#else
                    FamilyTypeId = et.Id.IntegerValue,
#endif
                    UniqueId = et.UniqueId,
                    FamilyName = familyName,
                    TypeName = et.Name,
                    Category = et.Category?.Name
                };
            }).ToList();
        }
        catch
        {
            ResultFamilyTypes = new();
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public string GetName() => "ZeManage Get Available Family Types";
}
