using System.Text.Json;
using Autodesk.Revit.UI;
using BIManage.AI.Terminal.Execution;
using BIManage.AI.Terminal.OpenAi;

namespace BIManage.AI.Terminal.Tools.SendCodeToRevit;

/// <summary>
/// MCP tool: <c>send_code_to_revit</c> — execute AI-generated C# against the active Revit document.
/// </summary>
/// <remarks>
/// <para>
/// <b>Phase 3b status: SAFE STUB.</b> The tool is registered and discoverable, but execution
/// returns a "Tier 2 — disabled" result. The full Roslyn-backed implementation lands in
/// <b>Phase 3d</b> alongside the safety layer (AST analyzer, namespace allowlist, audit log).
/// </para>
/// <para>Future parameters (already accepted, persisted on the handler):
/// <list type="bullet">
///   <item><c>code</c> (required): C# snippet body that becomes the body of
///         <c>public static object Execute(Document document, object[] parameters)</c></item>
///   <item><c>parameters</c>: array of values passed to the Execute method</item>
///   <item><c>transactionMode</c>: <c>"auto"</c> wraps execution in a Transaction,
///         <c>"none"</c> leaves transaction handling to the user code</item>
/// </list>
/// </para>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
public sealed class SendCodeToRevitCommand : ExternalEventCommandBase
{
    private static readonly object ExecutionLock = new();

    public override string CommandName => "send_code_to_revit";

    public override string Description =>
        "Compiles and executes a small C# snippet against the active Revit document, returning " +
        "the snippet's return value as JSON. Use this when no other tool fits — e.g. for custom " +
        "filters, parameter aggregations, or geometry queries that aren't covered by " +
        "ai_element_filter / analyze_model_statistics.\n\n" +

        "STRICTLY READ-ONLY: the safety layer rejects any code that creates Transactions, " +
        "calls Delete or Parameter.Set, touches the file system or network, uses reflection, " +
        "or imports namespaces outside the allowlist.\n\n" +

        "Your snippet runs as the body of `public static object Run(Document doc, UIApplication uiApp)` — " +
        "use `doc` for the active document and END with a `return` statement that produces a " +
        "JSON-serializable value (anonymous types work great).\n\n" +

        "Available namespaces (the wrapper imports these — DO NOT add 'using' statements): " +
        "System, System.Collections.Generic, System.Linq, Autodesk.Revit.DB, Autodesk.Revit.UI. " +
        "MEP-specific types like Duct/Pipe live in sub-namespaces — refer to them as " +
        "`Autodesk.Revit.DB.Mechanical.Duct`, `Autodesk.Revit.DB.Plumbing.Pipe`, " +
        "`Autodesk.Revit.DB.Electrical.Wire`, `Autodesk.Revit.DB.Architecture.Room`, " +
        "`Autodesk.Revit.DB.Structure.AnalyticalModel`.\n\n" +

        "REVIT API CHEAT SHEET — use these EXACT names, do not guess:\n\n" +

        "  Length of a duct/pipe/conduit (and other MEPCurve elements): " +
        "`elem.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH).AsDouble()` — " +
        "NOT CURVE_LENGTH, NOT DUCT_LENGTH. Result is in feet (Revit internal units).\n\n" +

        "  Diameter of a round duct/pipe: " +
        "`elem.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM).AsDouble()` in feet, OR " +
        "`elem.get_Parameter(BuiltInParameter.RBS_CURVE_DIAMETER_PARAM).AsValueString()` for the " +
        "display string (\"200 mm\", etc.).\n\n" +

        "  Width/height of a rectangular duct: `RBS_CURVE_WIDTH_PARAM`, `RBS_CURVE_HEIGHT_PARAM`.\n\n" +

        "  Wall height: `BuiltInParameter.WALL_USER_HEIGHT_PARAM` (in feet).\n\n" +

        "  Door/window dimensions: `FAMILY_WIDTH_PARAM`, `FAMILY_HEIGHT_PARAM`.\n\n" +

        "  Room data: `ROOM_AREA`, `ROOM_NUMBER`, `ROOM_NAME` (note: `room.Name` returns the same).\n\n" +

        "  Level info: cast an Element's `LevelId` via `doc.GetElement(elem.LevelId) as Level`. " +
        "Level elevation: `level.Elevation` (feet). Level name: `level.Name`.\n\n" +

        "  Element location geometry: cast `elem.Location` to `LocationCurve` for curve-based elements " +
        "(walls, ducts, pipes) — then `((LocationCurve)elem.Location).Curve` for the Curve object. " +
        "For point elements (doors, family instances) cast to `LocationPoint`.\n\n" +

        "  Curve length: `curve.Length` (a property, not a method — NO parentheses). " +
        "There is NO `GetCurve()` method on CurveElement.\n\n" +

        "  Generic 'read any string parameter by name': " +
        "`elem.LookupParameter(\"Parameter Name\")?.AsString()` — handles both built-in and shared/custom parameters.\n\n" +

        "  Generic 'read any double parameter by name': " +
        "`elem.LookupParameter(\"Length\")?.AsDouble()` (in internal units = feet).\n\n" +

        "  Element category check: `elem.Category?.Id.IntegerValue == (int)BuiltInCategory.OST_Walls`.\n\n" +

        "  Convert feet to mm: multiply by 304.8. Convert sq ft to sq m: multiply by 0.0929.\n\n" +

        "EXAMPLE — longest duct with length and level (canonical pattern):\n" +
        "```\n" +
        "var ducts = new FilteredElementCollector(doc)\n" +
        "    .OfCategory(BuiltInCategory.OST_DuctCurves)\n" +
        "    .WhereElementIsNotElementType()\n" +
        "    .ToList();\n" +
        "var longest = ducts\n" +
        "    .OrderByDescending(d => d.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH)?.AsDouble() ?? 0)\n" +
        "    .FirstOrDefault();\n" +
        "if (longest == null) return new { found = false };\n" +
        "var level = doc.GetElement(longest.LevelId) as Level;\n" +
        "var lengthFeet = longest.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH).AsDouble();\n" +
        "return new {\n" +
        "    found = true,\n" +
        "    name = longest.Name,\n" +
        "    id = longest.Id.IntegerValue,\n" +
        "    lengthFeet = lengthFeet,\n" +
        "    lengthMm = lengthFeet * 304.8,\n" +
        "    level = level?.Name\n" +
        "};\n" +
        "```\n\n" +

        "IF THE COMPILER REJECTS YOUR CODE: read the error message carefully. CS0117 means the enum " +
        "value doesn't exist — try a similar name from the cheat sheet above. CS0234 means the namespace " +
        "doesn't exist — check the sub-namespace list (Mechanical, Plumbing, etc.). CS1061 means the " +
        "method/property doesn't exist on that type — try `LookupParameter(\"name\")` as a fallback " +
        "for unknown parameters, or check the cheat sheet for the canonical property.";

    public override OpenAiParametersSchema ParameterSchema => new()
    {
        Required = new() { "code" },
        Properties = new()
        {
            ["code"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "C# snippet body. Becomes the body of `public static object Execute(Document document, object[] parameters)`."
            },
            ["parameters"] = new OpenAiPropertySchema
            {
                Type = "array",
                Description = "Optional values passed to the Execute method's parameters argument. " +
                              "Items can be any JSON-serializable scalar (string, number, boolean) or a JSON string for complex objects.",
                // OpenAI's schema validator requires every array type to declare its element shape
                // via `items`. We accept arbitrary primitives, so the most permissive schema is
                // a oneOf-style description. The simplest valid declaration that matches OpenAI's
                // accepted shapes is `items: { type: "string" }` — the LLM will JSON-stringify
                // non-string values when needed and our dispatcher handles the conversion.
                Items = new OpenAiPropertySchema { Type = "string" }
            },
            ["transactionMode"] = new OpenAiPropertySchema
            {
                Type = "string",
                Description = "\"auto\" (default) wraps execution in a Transaction. \"none\" leaves transaction handling to user code."
            }
        }
    };

    public SendCodeToRevitCommand(UIApplication uiApp)
        : base(new SendCodeToRevitEventHandler(), uiApp)
    {
    }

    public override object Execute(JsonElement parameters, string requestId)
    {
        lock (ExecutionLock)
        {
            try
            {
                var code = TryReadString(parameters, "code")
                    ?? throw new ArgumentException("'code' parameter is required");

                // GPT sometimes double-escapes its code argument, sending literal "\\n" instead
                // of a real newline. The JSON deserializer decodes one layer (\\n → \n), leaving
                // a literal backslash-n in the C# source that causes CS1056 "Unexpected character"
                // on every line. Defensively un-escape these common sequences before compile.
                code = code
                    .Replace("\\r\\n", "\n")
                    .Replace("\\n", "\n")
                    .Replace("\\t", "\t")
                    .Replace("\\\"", "\"");

                var handler = (SendCodeToRevitEventHandler)Handler;
                handler.Code = code;
                handler.ExecutionParameters = TryReadParametersArray(parameters);
                handler.TransactionMode = TryReadString(parameters, "transactionMode")
                    ?? SendCodeToRevitEventHandler.TransactionModeAuto;

                if (IsCalledFromConstructionThread())
                {
                    ExecuteOnUiThread();
                }
                else if (!RaiseAndWaitForCompletion(60_000))
                {
                    throw new TimeoutException("send_code_to_revit timed out after 60 seconds");
                }

                return handler.ResultInfo;
            }
            catch (Exception ex)
            {
                throw new Exception($"send_code_to_revit failed: {ex.Message}", ex);
            }
        }
    }

    private static string? TryReadString(JsonElement parameters, string name)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return null;
        if (!parameters.TryGetProperty(name, out var prop)) return null;
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }

    /// <summary>
    /// Reads the optional <c>parameters</c> array, converting each JSON value to the
    /// most natural .NET type (string / long / double / bool / null). Complex objects are
    /// passed through as raw JSON strings — Phase 3d may add typed mapping.
    /// </summary>
    private static object[] TryReadParametersArray(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object) return Array.Empty<object>();
        if (!parameters.TryGetProperty("parameters", out var arrayProp)
            || arrayProp.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<object>();
        }

        var result = new List<object>();
        foreach (var item in arrayProp.EnumerateArray())
        {
            result.Add(item.ValueKind switch
            {
                JsonValueKind.String => item.GetString()!,
                JsonValueKind.Number when item.TryGetInt64(out var l) => l,
                JsonValueKind.Number => item.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null!,
                _ => item.GetRawText()
            });
        }
        return result.ToArray();
    }
}
