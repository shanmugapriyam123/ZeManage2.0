using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.SayHello;

/// <summary>
/// MCP tool: <c>say_hello</c> — connection/health check.
/// Optional <c>message</c> parameter; defaults to a friendly greeting.
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
public sealed class SayHelloCommand : ExternalEventCommandBase
{
    private const string DefaultMessage = "Hello from ZeManage AI Terminal!";
    private static readonly object ExecutionLock = new();

    public override string CommandName => "say_hello";

    public override string Description =>
        "Connection / health check. Pops a TaskDialog in Revit confirming the AI Terminal " +
        "pipeline is reachable. Use only when explicitly asked to test connectivity.";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Properties = new()
        {
            ["message"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "Optional custom message to display in the dialog. Defaults to a friendly greeting."
            }
        }
    };

    public SayHelloCommand(UIApplication uiApp)
        : base(new SayHelloEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        // Serialize concurrent calls — Revit's UI thread can only run one command at a time.
        lock (ExecutionLock)
        {
            try
            {
                var handler = (SayHelloEventHandler)Handler;
                handler.Message = TryReadMessage(parameters) ?? DefaultMessage;

                // UI-thread caller (ribbon button) → call handler directly.
                // Background-thread caller (MCP server) → raise external event and wait.
                if (IsCalledFromConstructionThread())
                {
                    ExecuteOnUiThread();
                    return new { success = true, message = handler.Message };
                }

                if (RaiseAndWaitForCompletion(15_000))
                {
                    return new { success = true, message = handler.Message };
                }

                throw new TimeoutException("say_hello timed out after 15 seconds");
            }
            catch (Exception ex)
            {
                throw new Exception($"say_hello failed: {ex.Message}", ex);
            }
        }
    }

    private static string? TryReadMessage(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty("message", out var messageProp)) return null;
        return messageProp.ValueKind == JsonValueKind.String ? messageProp.GetString() : null;
    }
}
