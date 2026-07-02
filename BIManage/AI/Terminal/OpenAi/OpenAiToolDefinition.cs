using System.Text.Json.Serialization;

namespace BIManage.AI.Terminal.OpenAi;

/// <summary>
/// Wire-format tool definition for OpenAI's chat completions <c>tools</c> array.
/// Matches the schema documented at https://platform.openai.com/docs/guides/function-calling.
/// </summary>
public sealed class OpenAiToolDefinition
{
    /// <summary>Always <c>"function"</c> for the function-calling API.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "function";

    [JsonPropertyName("function")]
    public OpenAiFunctionDefinition Function { get; set; } = new();
}

/// <summary>The function-shaped portion of an OpenAI tool definition.</summary>
public sealed class OpenAiFunctionDefinition
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>JSON-schema describing the function's parameters object.</summary>
    [JsonPropertyName("parameters")]
    public OpenAiParametersSchema Parameters { get; set; } = new();
}

/// <summary>JSON-schema 'object' shape for OpenAI function parameters.</summary>
public sealed class OpenAiParametersSchema
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "object";

    [JsonPropertyName("properties")]
    public Dictionary<string, OpenAiPropertySchema> Properties { get; set; } = new();

    /// <summary>Names of required properties (omitted from JSON when null).</summary>
    /// <remarks>
    /// Uses <see cref="JsonIgnoreCondition.WhenWritingNull"/> rather than WhenWritingDefault
    /// because OpenAI rejects <c>"required": null</c> with HTTP 400 BadRequest. WhenWritingNull
    /// is the universally-honored skip condition across all System.Text.Json versions.
    /// </remarks>
    [JsonPropertyName("required")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Required { get; set; }
}

/// <summary>A single property in a parameters schema.</summary>
public sealed class OpenAiPropertySchema
{
    /// <summary>JSON-schema type: <c>"string"</c>, <c>"integer"</c>, <c>"number"</c>, <c>"boolean"</c>, <c>"array"</c>, <c>"object"</c>.</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "string";

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    /// <summary>For <c>type: "array"</c>, describes the element type.</summary>
    [JsonPropertyName("items")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public OpenAiPropertySchema? Items { get; set; }

    /// <summary>For <c>type: "object"</c>, describes nested properties.</summary>
    [JsonPropertyName("properties")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, OpenAiPropertySchema>? Properties { get; set; }
}
