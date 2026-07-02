using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.GetSelectedElements;

/// <summary>
/// MCP tool: <c>get_selected_elements</c> — returns the active UIDocument's current selection
/// projected to <see cref="Models.ElementInfo"/>. Optional <c>limit</c> caps the response size.
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
public sealed class GetSelectedElementsCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "get_selected_elements";

    public override string Description =>
        "Returns the elements the user currently has selected in the active Revit view, " +
        "with id, uniqueId, name, and category. Use this when the user refers to " +
        "'this', 'these', 'the selected', 'what I picked', etc.";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Properties = new()
        {
            ["limit"] = new OpenAiPropertySchema
            {
                Type = "integer",
                Description = "Optional cap on the number of elements returned. Useful when the user has selected hundreds and we only need the first few."
            }
        }
    };

    public GetSelectedElementsCommand(UIApplication uiApp)
        : base(new GetSelectedElementsEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var handler = (GetSelectedElementsEventHandler)Handler;
                handler.Limit = TryReadLimit(parameters);

                if (IsCalledFromConstructionThread())
                {
                    ExecuteOnUiThread();
                }
                else if (!RaiseAndWaitForCompletion(15_000))
                {
                    throw new TimeoutException("get_selected_elements timed out after 15 seconds");
                }

                return new
                {
                    count = handler.ResultElements.Count,
                    elements = handler.ResultElements
                };
            }
            catch (Exception ex)
            {
                throw new Exception($"get_selected_elements failed: {ex.Message}", ex);
            }
        }
    }

    private static int? TryReadLimit(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty("limit", out var limitProp)) return null;
        if (limitProp.ValueKind != JsonValueKind.Number) return null;
        return limitProp.TryGetInt32(out var v) && v > 0 ? v : null;
    }
}
