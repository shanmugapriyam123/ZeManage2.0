using System.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;

namespace BIManage.AI.Terminal.Tools.ListCategoriesWithElements;

/// <summary>
/// External event handler for the <c>list_categories_with_elements</c> MCP tool.
/// Returns every category that has at least one element in the host document, with
/// the matching BuiltInCategory enum name and the live element count.
/// </summary>
/// <remarks>
/// Designed as a discovery aid for the LLM: when a user asks about a category whose
/// official enum name the model doesn't know (e.g. "ducts" vs OST_DuctCurves), it can
/// call this first to learn what's actually in the model and what the canonical names are.
/// </remarks>
internal sealed class ListCategoriesWithElementsEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);

    /// <summary>If non-null, restrict the response to categories whose name contains this substring.</summary>
    public string? NameFilter { get; set; }

    /// <summary>Max categories to return. 0 = unlimited.</summary>
    public int Limit { get; set; } = 200;

    public List<CategorySummary> Result { get; private set; } = new();
    public string? HostDocumentTitle { get; private set; }
    public int HostLinkedRevitCount { get; private set; }
    public string? Message { get; private set; }

    /// <summary>True when the walk completed normally. False when an exception interrupted it.</summary>
    public bool Success { get; private set; }

    public bool WaitForCompletion(int timeoutMilliseconds = 30000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        Success = false; // default to false; flipped to true only after the walk completes
        try
        {
            var doc = app.ActiveUIDocument?.Document;
            if (doc == null)
            {
                Result = new();
                Message = "No active document";
                return;
            }

            HostDocumentTitle = doc.Title;
            HostLinkedRevitCount = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_RvtLinks)
                .WhereElementIsNotElementType()
                .GetElementCount();

            // Walk every non-type element once and bucket by category id. One pass beats
            // hundreds of separate FilteredElementCollector queries (one per BuiltInCategory).
            var counts = new Dictionary<long, int>();
            var sampleNames = new Dictionary<long, string>();

            foreach (var element in new FilteredElementCollector(doc).WhereElementIsNotElementType())
            {
                var cat = element.Category;
                if (cat == null) continue;
                var catId = cat.Id.GetValue();
                counts.TryGetValue(catId, out var prev);
                counts[catId] = prev + 1;
                if (!sampleNames.ContainsKey(catId))
                {
                    sampleNames[catId] = cat.Name ?? "(unnamed)";
                }
            }

            // Map back from id → BuiltInCategory enum name where possible. Custom (non-built-in)
            // categories return -1 from the cast and we report them with displayName only.
            var summaries = new List<CategorySummary>(counts.Count);
            foreach (var kvp in counts)
            {
                var enumName = TryGetBuiltInCategoryName(kvp.Key);
                summaries.Add(new CategorySummary
                {
                    BuiltInCategoryName = enumName,
                    DisplayName = sampleNames[kvp.Key],
                    ElementCount = kvp.Value
                });
            }

            // Apply optional name filter (case-insensitive substring against either name).
            if (!string.IsNullOrEmpty(NameFilter))
            {
                var needle = NameFilter!;
                summaries = summaries.Where(s =>
                    (s.BuiltInCategoryName?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                    || (s.DisplayName?.IndexOf(needle, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0
                ).ToList();
            }

            // Sort by count descending so the LLM sees the most-populated categories first.
            summaries.Sort((a, b) => b.ElementCount.CompareTo(a.ElementCount));

            if (Limit > 0 && summaries.Count > Limit)
                summaries = summaries.Take(Limit).ToList();

            Result = summaries;
            Message = HostLinkedRevitCount > 0
                ? $"Found {summaries.Count} categor(y/ies) with elements in the host document " +
                  $"'{HostDocumentTitle}'. NOTE: this document also has {HostLinkedRevitCount} " +
                  $"linked Revit model(s) — elements inside links are NOT counted here."
                : $"Found {summaries.Count} categor(y/ies) with elements in '{HostDocumentTitle}'.";
            Success = true;
        }
        catch (Exception ex)
        {
            Result = new();
            Message = $"List categories failed: {ex.Message}";
            Success = false;
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public string GetName() => "ZeManage List Categories With Elements";

    /// <summary>
    /// Returns the <see cref="BuiltInCategory"/> enum name for the given category id when it
    /// matches a built-in, or null for user-defined categories.
    /// </summary>
    /// <remarks>
    /// The <see cref="BuiltInCategory"/> enum's underlying type differs across Revit versions:
    /// it's <see cref="int"/> on Revit 2023 and earlier, and <see cref="long"/> on Revit 2024+.
    /// <see cref="Enum.IsDefined"/> throws <see cref="ArgumentException"/> when the supplied
    /// value type doesn't match the enum's underlying type — which is exactly what bit us on
    /// the first run against a 2024 model. The fix is to convert to the correct underlying
    /// type per TFM via a compile-time switch.
    /// </remarks>
    private static string? TryGetBuiltInCategoryName(long catId)
    {
        try
        {
#if REVIT2024_OR_GREATER
            // 2024+: underlying type is long. Cast through the enum directly.
            var asEnum = (BuiltInCategory)catId;
            return Enum.IsDefined(typeof(BuiltInCategory), asEnum)
                ? asEnum.ToString()
                : null;
#else
            // 2023 and earlier: underlying type is int. Range-check first so the cast is safe.
            if (catId < int.MinValue || catId > int.MaxValue) return null;
            var asInt = (int)catId;
            return Enum.IsDefined(typeof(BuiltInCategory), asInt)
                ? Enum.GetName(typeof(BuiltInCategory), asInt)
                : null;
#endif
        }
        catch
        {
            // Defensive: any malformed id just returns null rather than failing the whole walk.
            return null;
        }
    }
}

/// <summary>Single category entry returned by <see cref="ListCategoriesWithElementsEventHandler"/>.</summary>
public sealed class CategorySummary
{
    /// <summary>The <c>BuiltInCategory</c> enum name (e.g. <c>"OST_DuctCurves"</c>), or null for custom categories.</summary>
    public string? BuiltInCategoryName { get; set; }

    /// <summary>Human-readable category name as shown in Revit's UI (e.g. <c>"Ducts"</c>).</summary>
    public string? DisplayName { get; set; }

    /// <summary>Count of non-type element instances in this category.</summary>
    public int ElementCount { get; set; }
}
