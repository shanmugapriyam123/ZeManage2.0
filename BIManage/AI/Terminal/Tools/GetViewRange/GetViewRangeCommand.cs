using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.GetViewRange;

/// <summary>
/// MCP tool: <c>get_view_range</c> — returns the view-range configuration of a plan view
/// (top / cut plane / bottom / view depth as level IDs and offsets, plus the underlay range).
/// Created in response to repeated tester compile-failures where GPT hallucinated
/// non-existent PlanViewRange members like GetCutPlane(), GetBottom(), GetUpperLimit(),
/// and PlanViewPlane.Top/Bottom/Cut — the real API uses GetLevelId(PlanViewPlane.TopClipPlane)
/// and PlanViewPlane.{TopClipPlane,CutPlane,BottomClipPlane,ViewDepthPlane,UnderlayBottom,UnderlayTop}.
/// </summary>
public sealed class GetViewRangeCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "get_view_range";

    public override string Description =>
        "Returns the view range of a Revit plan view: top, cut plane, bottom, and view depth — " +
        "each as a level name + offset. Also reports the underlay range. Use this for any " +
        "'view range', 'cut plane', 'why can't I see X in this plan', or 'fix view range' " +
        "question. Falls back to the active view when viewId is omitted. Returns an error if " +
        "the view isn't a plan view (this tool is plan-only — Section/Elevation/3D views don't " +
        "have a view range).";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Properties = new()
        {
            ["viewId"] = new OpenAiPropertySchema
            {
                Type = "integer",
                Description = "Optional. ElementId of the plan view to inspect. Omit to use the active view."
            }
        }
    };

    public GetViewRangeCommand(UIApplication uiApp)
        : base(new GetViewRangeEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var handler = (GetViewRangeEventHandler)Handler;
                handler.SetParameters(viewId: TryReadInt(parameters, "viewId"));

                if (IsCalledFromConstructionThread())
                {
                    ExecuteOnUiThread();
                }
                else if (!RaiseAndWaitForCompletion(10_000))
                {
                    throw new TimeoutException("get_view_range timed out after 10 seconds");
                }

                return handler.Result
                    ?? throw new InvalidOperationException("get_view_range produced no result");
            }
            catch (Exception ex)
            {
                throw new Exception($"get_view_range failed: {ex.Message}", ex);
            }
        }
    }

    private static int? TryReadInt(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) ? v : null;
    }
}
