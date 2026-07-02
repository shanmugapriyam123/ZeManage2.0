using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.CheckProtectionRules;

/// <summary>
/// MCP tool: <c>check_protection_rules</c> — lists ZeManage's active command-protection rules
/// so the AI can answer questions like "what rules are protecting this model" or
/// "what's blocking the Delete command".
/// </summary>
public sealed class CheckProtectionRulesCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "check_protection_rules";

    public override string Description =>
        "Lists ZeManage's active command-protection rules, optionally filtered to the current model. " +
        "Use for questions about what governance is in place: 'what rules apply to this model', " +
        "'is Delete protected', 'what commands are restricted', 'show me the protection setup'. " +
        "Each rule reports its command, mode (Notify/Assist/Protect), scope (Company/Project/Model), " +
        "and whether it's enabled.";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Properties = new()
        {
            ["modelGuid"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "If supplied, returns rules applicable to this model only " +
                              "(model-scope rules with this GUID, plus all company/project rules). " +
                              "Omit to return every rule across all scopes."
            },
            ["commandCode"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "Filter by Revit command code (e.g. 'ID_OBJECTS_DELETE'). " +
                              "Case-insensitive substring match. Omit for all commands."
            },
            ["enabledOnly"] = new OpenAiPropertySchema
            {
                Type = "boolean",
                Description = "When true (default), only return enabled rules. Set false to include disabled ones."
            }
        }
    };

    public CheckProtectionRulesCommand(UIApplication uiApp)
        : base(new CheckProtectionRulesEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var handler = (CheckProtectionRulesEventHandler)Handler;
                handler.SetParameters(
                    modelGuid:   TryReadString(parameters, "modelGuid"),
                    commandCode: TryReadString(parameters, "commandCode"),
                    enabledOnly: TryReadBool(parameters, "enabledOnly") ?? true);

                handler.Execute(UiApp);

                return handler.Result
                    ?? throw new InvalidOperationException("check_protection_rules produced no result");
            }
            catch (Exception ex)
            {
                throw new Exception($"check_protection_rules failed: {ex.Message}", ex);
            }
        }
    }

    private static string? TryReadString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        if (prop.ValueKind != JsonValueKind.String) return null;
        var s = prop.GetString();
        return string.IsNullOrWhiteSpace(s) ? null : s;
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
