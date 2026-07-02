using System.Text.Json;
using BIManage.Infrastructure.Logging;

namespace BIManage.AI.Terminal.OpenAi;

/// <summary>
/// Routes an OpenAI <c>tool_call</c> (function name + JSON arguments string) to the
/// matching tool in a <see cref="ToolRegistry"/>, executes it, and serializes the
/// result to a JSON string suitable for sending back to OpenAI as the <c>tool</c>
/// role message content.
/// </summary>
public sealed class ToolDispatcher
{
    private readonly ToolRegistry _registry;
    private readonly ILogger? _logger;

    /// <summary>
    /// Serializer options shared across all tool result encodings. <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>
    /// is used so non-ASCII characters (e.g. em-dashes in tool messages) appear naturally
    /// instead of as <c>—</c> escapes, which OpenAI tokenises less efficiently.
    /// </summary>
    private static readonly JsonSerializerOptions ResultSerializerOptions = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public ToolDispatcher(ToolRegistry registry, ILogger? logger = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _logger = logger;
    }

    /// <summary>
    /// Dispatches a single OpenAI tool call to its registered handler.
    /// </summary>
    /// <param name="functionName">Tool name as returned in the OpenAI <c>tool_calls[].function.name</c> field.</param>
    /// <param name="argumentsJson">Raw JSON string of arguments — OpenAI returns these as a string, not a parsed object.</param>
    /// <param name="toolCallId">OpenAI's per-call id, used here only for logging and for the request id passed to tools.</param>
    /// <returns>
    /// JSON string ready to be used as the <c>content</c> of a <c>tool</c> role message in
    /// the next chat completions call. Errors are returned as a structured JSON object
    /// so the LLM can read the failure and recover, rather than throwing.
    /// </returns>
    public string Dispatch(string functionName, string argumentsJson, string toolCallId)
    {
        _logger?.LogDebug($"[ToolDispatcher] Dispatching '{functionName}' (call={toolCallId}) with args: {Truncate(argumentsJson, 200)}");

        try
        {
            var tool = _registry.TryGet(functionName);
            if (tool == null)
            {
                _logger?.LogWarning($"[ToolDispatcher] Unknown tool '{functionName}' — returning structured error");
                return JsonSerializer.Serialize(new
                {
                    error = $"Tool '{functionName}' is not registered. Known tools: {string.Join(", ", _registry.ToolNames)}"
                }, ResultSerializerOptions);
            }

            // OpenAI sometimes sends an empty string when no arguments are needed; treat that as {}.
            var argsElement = string.IsNullOrWhiteSpace(argumentsJson)
                ? JsonDocument.Parse("{}").RootElement
                : JsonDocument.Parse(argumentsJson).RootElement;

            var result = tool.Execute(argsElement, toolCallId);
            var resultJson = JsonSerializer.Serialize(result, ResultSerializerOptions);

            _logger?.LogDebug($"[ToolDispatcher] '{functionName}' completed ({resultJson.Length} chars)");
            return resultJson;
        }
        catch (JsonException jsonEx)
        {
            _logger?.LogError($"[ToolDispatcher] Invalid JSON arguments for '{functionName}': {jsonEx.Message}");
            return JsonSerializer.Serialize(new
            {
                error = $"Tool arguments were not valid JSON: {jsonEx.Message}",
                receivedArguments = argumentsJson
            }, ResultSerializerOptions);
        }
        catch (Exception ex)
        {
            _logger?.LogError($"[ToolDispatcher] Tool '{functionName}' threw {ex.GetType().Name}: {ex.Message}");
            // Return the failure as a structured JSON object so the LLM can read it and explain
            // the failure to the user, rather than crashing the chat turn.
            return JsonSerializer.Serialize(new
            {
                error = $"{ex.GetType().Name}: {ex.Message}",
                tool = functionName
            }, ResultSerializerOptions);
        }
    }

    private static string Truncate(string s, int max)
        => s == null ? "" : s.Length <= max ? s : s.Substring(0, max) + "…";
}
