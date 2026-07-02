using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.ColorSplash;

/// <summary>
/// MCP tool: <c>color_splash</c> — colors elements in the active view by parameter value.
/// Modifies view overrides only; the underlying model elements and their parameters are untouched.
/// </summary>
/// <remarks>
/// <para>
/// Required parameters:
/// <list type="bullet">
///   <item><c>categoryName</c>: Revit category name (e.g. <c>"Walls"</c>)</item>
///   <item><c>parameterName</c>: parameter to group elements by</item>
/// </list>
/// Optional:
/// <list type="bullet">
///   <item><c>useGradient</c> (default false): blue→red ramp instead of random colors</item>
///   <item><c>customColors</c>: array of <c>{r,g,b}</c> objects for explicit per-group colors</item>
/// </list>
/// </para>
/// <para>
/// <b>Tier classification:</b> writes view overrides inside a Transaction. Counts as a "safe write" —
/// no model elements or parameters are modified — so this tool is allowed under Tier 1.
/// </para>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
public sealed class ColorSplashCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "color_splash";

    public override string Description =>
        "Colors elements in the active view by parameter value, grouping each unique value " +
        "into its own color. Modifies VIEW OVERRIDES only — model elements and parameters " +
        "are untouched. Use for 'color walls by fire rating', 'highlight rooms by " +
        "department', 'visualize X by Y', etc.";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Required = new() { "categoryName", "parameterName" },
        Properties = new()
        {
            ["categoryName"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "Revit category name to color (e.g. \"Walls\", \"Doors\", \"Rooms\"). Case-insensitive."
            },
            ["parameterName"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "Parameter to group elements by (instance or type parameter)."
            },
            ["useGradient"] = new OpenAiPropertySchema
            {
                Type = "boolean",
                Description = "When true, blend a blue→red gradient across groups instead of random colors. Default false."
            },
            ["customColors"] = new OpenAiPropertySchema
            {
                Type = "array",
                Description = "Optional explicit per-group RGB colors. Each item: { r, g, b } with 0-255 ints.",
                Items = new OpenAiPropertySchema
                {
                    Type = "object",
                    Properties = new()
                    {
                        ["r"] = new OpenAiPropertySchema { Type = "integer" },
                        ["g"] = new OpenAiPropertySchema { Type = "integer" },
                        ["b"] = new OpenAiPropertySchema { Type = "integer" }
                    }
                }
            }
        }
    };

    public ColorSplashCommand(UIApplication uiApp)
        : base(new ColorSplashEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var categoryName = TryReadString(parameters, "categoryName")
                    ?? throw new ArgumentException("categoryName is required");
                var parameterName = TryReadString(parameters, "parameterName")
                    ?? throw new ArgumentException("parameterName is required");

                var handler = (ColorSplashEventHandler)Handler;
                handler.SetParameters(
                    categoryName,
                    parameterName,
                    useGradient: TryReadBool(parameters, "useGradient") ?? false,
                    customColors: TryReadColorArray(parameters, "customColors"));

                if (IsCalledFromConstructionThread())
                {
                    ExecuteOnUiThread();
                }
                else if (!RaiseAndWaitForCompletion(20_000))
                {
                    throw new TimeoutException("color_splash timed out after 20 seconds");
                }

                return handler.ColoringResults
                    ?? throw new InvalidOperationException("Color splash did not produce a result");
            }
            catch (Exception ex)
            {
                throw new Exception($"color_splash failed: {ex.Message}", ex);
            }
        }
    }

    private static string? TryReadString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
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

    private static List<int[]>? TryReadColorArray(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var arrayProp) || arrayProp.ValueKind != JsonValueKind.Array)
            return null;

        var colors = new List<int[]>();
        foreach (var item in arrayProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            if (!item.TryGetProperty("r", out var r) || !item.TryGetProperty("g", out var g) ||
                !item.TryGetProperty("b", out var b)) continue;

            if (r.TryGetInt32(out var ri) && g.TryGetInt32(out var gi) && b.TryGetInt32(out var bi))
            {
                colors.Add(new[] { Clamp(ri), Clamp(gi), Clamp(bi) });
            }
        }
        return colors.Count > 0 ? colors : null;

        static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;
    }
}
