using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.ListCategoriesWithElements;

/// <summary>
/// MCP tool: <c>list_categories_with_elements</c> — returns every BuiltInCategory that has
/// at least one element instance in the host document, with the canonical enum name and a
/// live element count. Designed as a discovery aid before more targeted filtering queries.
/// </summary>
public sealed class ListCategoriesWithElementsCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "list_categories_with_elements";

    public override string Description =>
        "Lists every Revit category that has at least one element in the host document, " +
        "with its canonical BuiltInCategory enum name (e.g. 'OST_DuctCurves'), the display " +
        "name as shown in Revit's UI (e.g. 'Ducts'), and the live element count. Sorted by " +
        "count, descending. Use this FIRST when you're unsure of the correct " +
        "BuiltInCategory name for a user's question (e.g. they said 'ducts' but the actual " +
        "enum is 'OST_DuctCurves'). Then use ai_element_filter with the exact name from this list.";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Properties = new()
        {
            ["nameFilter"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "Optional substring matched against both the BuiltInCategory enum name and the display name (case-insensitive)."
            },
            ["limit"] = new OpenAiPropertySchema
            {
                Type = "integer",
                Description = "Maximum categories to return. Default 200 (effectively no limit for typical models). Use 0 for unlimited."
            }
        }
    };

    public ListCategoriesWithElementsCommand(UIApplication uiApp)
        : base(new ListCategoriesWithElementsEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var handler = (ListCategoriesWithElementsEventHandler)Handler;
                handler.NameFilter = TryReadString(parameters, "nameFilter");
                handler.Limit = TryReadInt(parameters, "limit") ?? 200;

                if (IsCalledFromConstructionThread())
                {
                    ExecuteOnUiThread();
                }
                else if (!RaiseAndWaitForCompletion(30_000))
                {
                    throw new TimeoutException("list_categories_with_elements timed out after 30 seconds");
                }

                return new
                {
                    success = handler.Success,
                    message = handler.Message,
                    count = handler.Result.Count,
                    categories = handler.Result,
                    hostDocumentContext = new
                    {
                        title = handler.HostDocumentTitle,
                        linkedRevitModels = handler.HostLinkedRevitCount
                    }
                };
            }
            catch (Exception ex)
            {
                throw new Exception($"list_categories_with_elements failed: {ex.Message}", ex);
            }
        }
    }

    private static string? TryReadString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static int? TryReadInt(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
            return null;
        return prop.TryGetInt32(out var v) && v >= 0 ? v : null;
    }
}
