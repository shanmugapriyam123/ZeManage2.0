using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.AiElementFilter;

/// <summary>
/// MCP tool: <c>ai_element_filter</c> — filter elements in the active document by category,
/// element class, instance/type inclusion, and current-view visibility.
/// </summary>
/// <remarks>
/// Parameters (all optional, wrapped in a <c>data</c> object to match Sparx's API shape):
/// <list type="bullet">
///   <item><c>filterCategory</c>: BuiltInCategory enum name (e.g. <c>"OST_Walls"</c>)</item>
///   <item><c>filterElementType</c>: Revit API class name (e.g. <c>"Wall"</c>)</item>
///   <item><c>filterVisibleInCurrentView</c> (default false)</item>
///   <item><c>includeInstances</c> (default true)</item>
///   <item><c>includeTypes</c> (default false)</item>
///   <item><c>maxElements</c> (default 100, 0 = unlimited)</item>
/// </list>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
public sealed class AiElementFilterCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "ai_element_filter";

    public override string Description =>
        "Filters elements in the active document by category, element class, instance/type " +
        "inclusion, and current-view visibility, returning matched elements as " +
        "{id, uniqueId, name, category}. Use for 'find all X', 'list every Y in the view', " +
        "'how many Z of type T', etc. The response always includes hostDocumentContext with " +
        "linked-Revit-model count — when count is 0 in a federated/MEP/coordination model, " +
        "the elements likely live inside a linked model rather than the host. " +
        "Common BuiltInCategory names (use these EXACT names — they are case-insensitive but " +
        "the spelling matters): " +
        "OST_Walls, OST_Floors, OST_Roofs, OST_Ceilings, OST_Doors, OST_Windows, " +
        "OST_StructuralColumns, OST_StructuralFraming, OST_Stairs, OST_Railings, " +
        "OST_Rooms, OST_Areas, OST_Levels, OST_Grids, OST_Sheets, OST_Views, " +
        "OST_DuctCurves (NOT OST_Ducts), OST_DuctFitting, OST_DuctTerminal, " +
        "OST_PipeCurves (NOT OST_Pipe), OST_PipeFitting, OST_PlumbingFixtures, " +
        "OST_ElectricalEquipment, OST_LightingFixtures, OST_ElectricalFixtures, " +
        "OST_MechanicalEquipment, OST_GenericModel, OST_RvtLinks, OST_Furniture. " +
        "If you guess wrong, the tool will suggest valid alternatives.";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Properties = new()
        {
            ["filterCategory"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "BuiltInCategory enum name (e.g. \"OST_Walls\", \"OST_Doors\", \"OST_StructuralColumns\")."
            },
            ["filterElementType"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "Revit API class name (e.g. \"Wall\", \"FamilyInstance\", \"Floor\"). Resolved against Autodesk.Revit.DB."
            },
            ["filterVisibleInCurrentView"] = new OpenAiPropertySchema
            {
                Type = "boolean",
                Description = "When true, restrict to elements visible in the active view. Default false."
            },
            ["includeInstances"] = new OpenAiPropertySchema
            {
                Type = "boolean",
                Description = "Include element instances. Default true."
            },
            ["includeTypes"] = new OpenAiPropertySchema
            {
                Type = "boolean",
                Description = "Include element types. Default false."
            },
            ["maxElements"] = new OpenAiPropertySchema
            {
                Type = "integer",
                Description = "Cap on returned count. Default 100. Use 0 for unlimited (avoid on large models)."
            }
        }
    };

    public AiElementFilterCommand(UIApplication uiApp)
        : base(new AiElementFilterEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                // Sparx wraps inputs under a "data" key for this tool.
                // Accept either {data: {...}} or just {...} for caller convenience.
                var dataElement = parameters.ValueKind == JsonValueKind.Object
                                  && parameters.TryGetProperty("data", out var d)
                                  && d.ValueKind == JsonValueKind.Object
                    ? d
                    : parameters;

                var settings = new FilterSettings
                {
                    FilterCategory = TryReadString(dataElement, "filterCategory"),
                    FilterElementType = TryReadString(dataElement, "filterElementType"),
                    FilterVisibleInCurrentView = TryReadBool(dataElement, "filterVisibleInCurrentView") ?? false,
                    IncludeInstances = TryReadBool(dataElement, "includeInstances") ?? true,
                    IncludeTypes = TryReadBool(dataElement, "includeTypes") ?? false,
                    MaxElements = TryReadInt(dataElement, "maxElements") ?? 100
                };

                var handler = (AiElementFilterEventHandler)Handler;
                handler.SetParameters(settings);

                if (IsCalledFromConstructionThread())
                {
                    ExecuteOnUiThread();
                }
                else if (!RaiseAndWaitForCompletion(15_000))
                {
                    throw new TimeoutException("ai_element_filter timed out after 15 seconds");
                }

                return new
                {
                    success = handler.ResultSuccess,
                    message = handler.ResultMessage,
                    // totalMatched is the true count — when truncated > 0, this is the number
                    // to quote in the user-facing answer. count is just elements.Length.
                    totalMatched = handler.TotalMatched,
                    count = handler.Result.Count,
                    truncated = handler.IsTruncated,
                    elements = handler.Result,
                    // Always-included context so the LLM can reason about empty results
                    // without needing extra tool calls. Especially useful for federated/
                    // MEP/coordination models where the host doc has few native elements.
                    hostDocumentContext = new
                    {
                        title = handler.HostDocumentTitle,
                        totalNativeElements = handler.HostTotalElementCount,
                        linkedRevitModels = handler.HostLinkedRevitCount
                    }
                };
            }
            catch (Exception ex)
            {
                throw new Exception($"ai_element_filter failed: {ex.Message}", ex);
            }
        }
    }

    private static string? TryReadString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
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

    private static int? TryReadInt(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Number)
            return null;
        return prop.TryGetInt32(out var v) && v >= 0 ? v : null;
    }
}
