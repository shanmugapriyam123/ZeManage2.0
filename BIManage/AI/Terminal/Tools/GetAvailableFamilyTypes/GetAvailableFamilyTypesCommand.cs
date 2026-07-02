using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.GetAvailableFamilyTypes;

/// <summary>
/// MCP tool: <c>get_available_family_types</c> — returns loadable families and system family types.
/// </summary>
/// <remarks>
/// Parameters (all optional):
/// <list type="bullet">
///   <item><c>categoryList</c>: array of <c>BuiltInCategory</c> enum names (e.g. <c>["OST_Walls", "OST_Doors"]</c>)</item>
///   <item><c>familyNameFilter</c>: case-insensitive substring matched against family AND type name</item>
///   <item><c>limit</c>: cap on returned count</item>
/// </list>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
public sealed class GetAvailableFamilyTypesCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "get_available_family_types";

    public override string Description =>
        "Lists loadable families and system family types in the project, with optional " +
        "category and name filters. Use when the user asks 'what families do I have', " +
        "'what wall types are loaded', 'is family X in the project', etc.";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Properties = new()
        {
            ["categoryList"] = new OpenAiPropertySchema
            {
                Type = "array",
                Description = "Optional list of BuiltInCategory enum names to filter by, e.g. [\"OST_Walls\", \"OST_Doors\"].",
                Items = new OpenAiPropertySchema { Type = "string" }
            },
            ["familyNameFilter"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "Optional case-insensitive substring matched against both family and type name."
            },
            ["limit"] = new OpenAiPropertySchema
            {
                Type = "integer",
                Description = "Optional cap on the number of results."
            }
        }
    };

    public GetAvailableFamilyTypesCommand(UIApplication uiApp)
        : base(new GetAvailableFamilyTypesEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var handler = (GetAvailableFamilyTypesEventHandler)Handler;
                handler.CategoryList = TryReadStringArray(parameters, "categoryList");
                handler.FamilyNameFilter = TryReadString(parameters, "familyNameFilter");
                handler.Limit = TryReadPositiveInt(parameters, "limit");

                if (IsCalledFromConstructionThread())
                {
                    ExecuteOnUiThread();
                }
                else if (!RaiseAndWaitForCompletion(15_000))
                {
                    throw new TimeoutException("get_available_family_types timed out after 15 seconds");
                }

                return new
                {
                    count = handler.ResultFamilyTypes.Count,
                    familyTypes = handler.ResultFamilyTypes
                };
            }
            catch (Exception ex)
            {
                throw new Exception($"get_available_family_types failed: {ex.Message}", ex);
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

    private static string? TryReadString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    private static int? TryReadPositiveInt(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number) return null;
        return prop.TryGetInt32(out var v) && v > 0 ? v : null;
    }
}
