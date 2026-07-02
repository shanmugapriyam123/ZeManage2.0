using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.GetMaterialQuantities;

/// <summary>
/// MCP tool: <c>get_material_quantities</c> — material take-off (area + volume per material)
/// across either the current selection or the whole active document.
/// </summary>
/// <remarks>
/// Optional parameters:
/// <list type="bullet">
///   <item><c>categoryFilters</c>: array of <c>BuiltInCategory</c> enum names to scope the search</item>
///   <item><c>selectedElementsOnly</c> (default false): restrict to current selection</item>
/// </list>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
public sealed class GetMaterialQuantitiesCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "get_material_quantities";

    public override string Description =>
        "Material take-off: aggregates area (sq.ft) and volume (cu.ft) per material across " +
        "either the user's current selection or the whole document. Use for 'how much " +
        "concrete', 'material schedule', 'quantity of X material', etc.";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Properties = new()
        {
            ["categoryFilters"] = new OpenAiPropertySchema
            {
                Type = "array",
                Description = "Optional list of BuiltInCategory enum names to scope the search.",
                Items = new OpenAiPropertySchema { Type = "string" }
            },
            ["selectedElementsOnly"] = new OpenAiPropertySchema
            {
                Type = "boolean",
                Description = "When true, restrict the take-off to the current selection. Default false (whole document)."
            }
        }
    };

    public GetMaterialQuantitiesCommand(UIApplication uiApp)
        : base(new GetMaterialQuantitiesEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var handler = (GetMaterialQuantitiesEventHandler)Handler;
                handler.SetParameters(
                    categoryFilters: TryReadStringArray(parameters, "categoryFilters"),
                    selectedElementsOnly: TryReadBool(parameters, "selectedElementsOnly") ?? false);

                if (IsCalledFromConstructionThread())
                {
                    ExecuteOnUiThread();
                }
                else if (!RaiseAndWaitForCompletion(120_000))
                {
                    throw new TimeoutException("get_material_quantities timed out after 120 seconds");
                }

                return handler.ResultInfo
                    ?? throw new InvalidOperationException("Material quantity calculation did not produce a result");
            }
            catch (Exception ex)
            {
                throw new Exception($"get_material_quantities failed: {ex.Message}", ex);
            }
        }
    }

    private static List<string>? TryReadStringArray(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Array) return null;
        var result = new List<string>();
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrEmpty(s)) result.Add(s!);
            }
        }
        return result.Count > 0 ? result : null;
    }

    private static bool? TryReadBool(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null
        };
    }
}
