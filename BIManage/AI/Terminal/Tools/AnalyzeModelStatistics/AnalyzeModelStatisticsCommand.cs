using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.AnalyzeModelStatistics;

/// <summary>
/// MCP tool: <c>analyze_model_statistics</c> — element/type/view/sheet counts
/// plus per-category and per-level breakdown for the active document.
/// </summary>
/// <remarks>
/// Optional parameter <c>includeDetailedTypes</c> (default true) controls whether
/// per-type instance counts are included for each category. Disabling it speeds up
/// very large models. Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
public sealed class AnalyzeModelStatisticsCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "analyze_model_statistics";

    public override string Description =>
        "Walks the live Revit model and returns total element/type/family/view/sheet counts, " +
        "a per-category breakdown, and a per-level element distribution. Use this for " +
        "'how big is my model', 'what's the breakdown', 'how many X are there', or any " +
        "question that needs current model totals (NOT cached snapshot data).";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Properties = new()
        {
            ["includeDetailedTypes"] = new OpenAiPropertySchema
            {
                Type = "boolean",
                Description = "When true (default), each category includes per-type instance counts. Set false on very large models to speed up the response."
            }
        }
    };

    public AnalyzeModelStatisticsCommand(UIApplication uiApp)
        : base(new AnalyzeModelStatisticsEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var handler = (AnalyzeModelStatisticsEventHandler)Handler;
                handler.SetParameters(TryReadBool(parameters, "includeDetailedTypes") ?? true);

                if (IsCalledFromConstructionThread())
                {
                    ExecuteOnUiThread();
                }
                else if (!RaiseAndWaitForCompletion(120_000))
                {
                    throw new TimeoutException("analyze_model_statistics timed out after 120 seconds");
                }

                return handler.ResultInfo
                    ?? throw new InvalidOperationException("Statistics analysis did not produce a result");
            }
            catch (Exception ex)
            {
                throw new Exception($"analyze_model_statistics failed: {ex.Message}", ex);
            }
        }
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
