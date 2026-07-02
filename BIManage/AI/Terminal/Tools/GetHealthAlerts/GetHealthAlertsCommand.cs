using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.GetHealthAlerts;

/// <summary>
/// MCP tool: <c>get_health_alerts</c> — returns the health alerts the ZeManage Health Monitor
/// has computed for the currently-open Revit model. The alerts come from the ViewModel's
/// startup health check (via ZeManageRuntimeContext) so this tool is instant and offline.
/// </summary>
public sealed class GetHealthAlertsCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "get_health_alerts";

    public override string Description =>
        "Returns ZeManage's Health Monitor alerts for the currently-open Revit model — things " +
        "like 'too many warnings', 'oversized families', 'unenclosed rooms', 'large file size'. " +
        "Use this for questions about model health, why a model is slow, or what needs cleaning up: " +
        "'why is my model unhealthy', 'what are the health issues', 'what should I fix first', " +
        "'how healthy is this model'. Each alert reports its severity (Critical / Warning / Info), " +
        "metric name, message, threshold, and actual value.";

    // No parameters — always returns alerts for the active model.
    public override OpenAiParametersSchema ParameterSchema => new();

    public GetHealthAlertsCommand(UIApplication uiApp)
        : base(new GetHealthAlertsEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var handler = (GetHealthAlertsEventHandler)Handler;
                handler.Execute(UiApp);

                return handler.Result
                    ?? throw new InvalidOperationException("get_health_alerts produced no result");
            }
            catch (Exception ex)
            {
                throw new Exception($"get_health_alerts failed: {ex.Message}", ex);
            }
        }
    }
}
