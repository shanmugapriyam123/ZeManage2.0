using BIManage.AI.Terminal.Execution;

namespace BIManage.AI.Terminal.OpenAi;

/// <summary>
/// Builds the <c>tools</c> array we pass to OpenAI's chat completions endpoint
/// from the contents of a <see cref="ToolRegistry"/>. Each tool's
/// <see cref="ExternalEventCommandBase.CommandName"/>,
/// <see cref="ExternalEventCommandBase.Description"/>, and
/// <see cref="ExternalEventCommandBase.ParameterSchema"/> become a single function definition.
/// </summary>
public static class ToolSchemaBuilder
{
    /// <summary>
    /// Returns one <see cref="OpenAiToolDefinition"/> per registered tool.
    /// Order matches <see cref="ToolRegistry.Tools"/> order (alphabetical by class FullName).
    /// </summary>
    public static List<OpenAiToolDefinition> Build(ToolRegistry registry)
    {
        if (registry == null) throw new ArgumentNullException(nameof(registry));

        var defs = new List<OpenAiToolDefinition>(registry.Count);
        foreach (var tool in registry.Tools)
        {
            defs.Add(new OpenAiToolDefinition
            {
                Type = "function",
                Function = new OpenAiFunctionDefinition
                {
                    Name = tool.CommandName,
                    Description = tool.Description,
                    Parameters = tool.ParameterSchema
                }
            });
        }
        return defs;
    }
}
