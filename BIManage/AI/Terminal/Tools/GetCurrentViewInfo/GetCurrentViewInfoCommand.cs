using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.GetCurrentViewInfo;

/// <summary>
/// MCP tool: <c>get_current_view_info</c> — returns viewport, scale, detail level, type, etc.
/// for the user's active Revit view. Takes no parameters.
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
public sealed class GetCurrentViewInfoCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "get_current_view_info";

    public override string Description =>
        "Returns the active Revit view's id, name, view type, scale, detail level, and " +
        "isTemplate flag. Use when the user asks about 'this view', 'the current view', " +
        "or wants to know what they're looking at.";

    // No parameters — the default empty schema from the base class is correct.

    public GetCurrentViewInfoCommand(UIApplication uiApp)
        : base(new GetCurrentViewInfoEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var handler = (GetCurrentViewInfoEventHandler)Handler;

                if (IsCalledFromConstructionThread())
                {
                    ExecuteOnUiThread();
                }
                else if (!RaiseAndWaitForCompletion(10_000))
                {
                    throw new TimeoutException("get_current_view_info timed out after 10 seconds");
                }

                if (handler.ResultInfo == null)
                {
                    throw new InvalidOperationException("No active view available");
                }

                return handler.ResultInfo;
            }
            catch (Exception ex)
            {
                throw new Exception($"get_current_view_info failed: {ex.Message}", ex);
            }
        }
    }
}
