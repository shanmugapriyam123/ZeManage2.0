using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.QueryAuditLog;

/// <summary>
/// MCP tool: <c>query_audit_log</c> — reads ZeManage's protection audit log so the AI can
/// answer questions like "who tried to delete grids today" or "how many Protect-mode blocks
/// fired this week". Reads the local SQLite <c>audit_log</c> table; no Revit API access needed,
/// so the handler is a thin no-op shim that satisfies the base class.
/// </summary>
public sealed class QueryAuditLogCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "query_audit_log";

    public override string Description =>
        "Reads ZeManage's protection audit log (who triggered which command, when, and whether " +
        "it was blocked/assisted/notified). Use this for questions about user activity in ZeManage: " +
        "'who tried to delete X', 'what got blocked yesterday', 'how many Protect-mode events this " +
        "week', 'show me audit events for command Y'. Filters: dayRange (1-90 days back), mode " +
        "(Notify/Assist/Protect), commandName (Delete, Move, etc.), userName, limit (max rows).";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Properties = new()
        {
            ["dayRange"] = new OpenAiPropertySchema
            {
                Type = "integer",
                Description = "Number of days back from today to search. Default 7. Max 90."
            },
            ["mode"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "Filter by protection mode: 'Notify', 'Assist', or 'Protect'. Omit for all."
            },
            ["commandName"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "Filter by Revit command name (e.g. 'Delete', 'Move'). Case-insensitive substring match. Omit for all."
            },
            ["userName"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "Filter by user name (case-insensitive substring match). Omit for all users."
            },
            ["limit"] = new OpenAiPropertySchema
            {
                Type = "integer",
                Description = "Maximum number of rows to return. Default 50, max 200."
            }
        }
    };

    public QueryAuditLogCommand(UIApplication uiApp)
        : base(new QueryAuditLogEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var handler = (QueryAuditLogEventHandler)Handler;
                handler.SetParameters(
                    dayRange:    TryReadInt(parameters, "dayRange") ?? 7,
                    mode:        TryReadString(parameters, "mode"),
                    commandName: TryReadString(parameters, "commandName"),
                    userName:    TryReadString(parameters, "userName"),
                    limit:       TryReadInt(parameters, "limit") ?? 50);

                // Pure DB read — runs synchronously on the calling thread; no Revit API needed.
                handler.Execute(UiApp);

                return handler.Result
                    ?? throw new InvalidOperationException("query_audit_log produced no result");
            }
            catch (Exception ex)
            {
                throw new Exception($"query_audit_log failed: {ex.Message}", ex);
            }
        }
    }

    private static int? TryReadInt(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.Number && prop.TryGetInt32(out var v) ? v : null;
    }

    private static string? TryReadString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind != JsonValueKind.String) return null;
        var s = prop.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }
}
