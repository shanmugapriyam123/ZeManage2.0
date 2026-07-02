using System.Reflection;
using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;

namespace BIManage.AI.Terminal;

/// <summary>
/// Single source of truth for the AI Terminal's available tools.
/// Built once at plugin startup, queried by both the OpenAI function-calling adapter (Phase 3c-NEW)
/// and a future MCP server (Phase 5). The same <see cref="ExternalEventCommandBase"/> instances
/// satisfy both clients — only the adapter layer differs.
/// </summary>
/// <remarks>
/// Tool instances are constructed eagerly with the supplied <see cref="UIApplication"/> so the
/// inherited <see cref="ExternalEventCommandBase.IsCalledFromConstructionThread"/> threading
/// detection works correctly from background callers (function-call dispatcher, MCP socket).
/// </remarks>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ExternalEventCommandBase> _tools = new(StringComparer.Ordinal);

    /// <summary>All tool names registered, in registration order.</summary>
    public IReadOnlyCollection<string> ToolNames => _tools.Keys;

    /// <summary>All registered tool instances.</summary>
    public IReadOnlyCollection<ExternalEventCommandBase> Tools => _tools.Values;

    public int Count => _tools.Count;

    /// <summary>
    /// Registers a single tool by instance. Throws if a tool with the same
    /// <see cref="ExternalEventCommandBase.CommandName"/> is already registered.
    /// </summary>
    public void Register(ExternalEventCommandBase tool)
    {
        if (tool == null) throw new ArgumentNullException(nameof(tool));

        if (_tools.ContainsKey(tool.CommandName))
        {
            throw new InvalidOperationException(
                $"Tool '{tool.CommandName}' is already registered " +
                $"(existing: {_tools[tool.CommandName].GetType().FullName}, new: {tool.GetType().FullName})");
        }

        _tools[tool.CommandName] = tool;
    }

    /// <summary>Returns the registered tool, or null if not found.</summary>
    public ExternalEventCommandBase? TryGet(string commandName)
        => _tools.TryGetValue(commandName, out var tool) ? tool : null;

    /// <summary>
    /// Invokes a tool by name with JSON-encoded parameters. Returns the tool's raw result object.
    /// Caller is responsible for serializing the result if needed.
    /// </summary>
    /// <exception cref="KeyNotFoundException">Tool not registered.</exception>
    public object Invoke(string commandName, JsonElement parameters, string requestId)
    {
        var tool = TryGet(commandName)
            ?? throw new KeyNotFoundException($"No tool registered with name '{commandName}'");
        return tool.Execute(parameters, requestId);
    }

    /// <summary>
    /// Discovers and registers every concrete <see cref="ExternalEventCommandBase"/> subclass
    /// in the calling assembly that has a public single-argument constructor accepting
    /// <see cref="UIApplication"/>. This is the standard registration path used at plugin startup.
    /// </summary>
    /// <remarks>
    /// Why reflection: the registry should pick up tools as they're added under
    /// <c>BIManage/AI/Terminal/Tools/</c> without anyone having to remember to wire them in
    /// <c>Application.cs</c>. The tradeoff is one-time startup cost (~5-20ms) and the inability
    /// to detect missing UIApplication constructors at compile time — both acceptable for our scale.
    /// </remarks>
    public static ToolRegistry DiscoverAndCreate(UIApplication uiApp)
    {
        if (uiApp == null) throw new ArgumentNullException(nameof(uiApp));

        var registry = new ToolRegistry();
        var commandBaseType = typeof(ExternalEventCommandBase);

        // Scan our own assembly only — third-party tool packages would register themselves explicitly.
        var toolTypes = commandBaseType.Assembly.GetTypes()
            .Where(t => t.IsClass
                && !t.IsAbstract
                && commandBaseType.IsAssignableFrom(t))
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        foreach (var toolType in toolTypes)
        {
            var ctor = toolType.GetConstructor(new[] { typeof(UIApplication) });
            if (ctor == null) continue; // Tool authors who need a different signature register manually.

            var instance = (ExternalEventCommandBase)ctor.Invoke(new object[] { uiApp });
            registry.Register(instance);
        }

        return registry;
    }
}
