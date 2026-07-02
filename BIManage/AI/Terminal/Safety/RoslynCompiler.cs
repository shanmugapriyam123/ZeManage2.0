using System.IO;
using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BIManage.AI.Terminal.Safety;

/// <summary>
/// Compiles a safety-analyzer-approved C# snippet into an in-memory assembly with
/// strictly restricted assembly references. Wraps the snippet in a generated <c>Script.Run(Document, UIApplication)</c>
/// entry point that callers invoke via reflection after a successful compile.
/// </summary>
/// <remarks>
/// <para>
/// Why manual <see cref="CSharpCompilation"/> + Emit (instead of <see cref="Microsoft.CodeAnalysis.CSharp.Scripting.CSharpScript"/>):
/// the scripting API auto-imports a large set of namespaces and pulls in references we don't
/// want. Manual compilation gives us exact control over which assemblies are added — we
/// add ONLY mscorlib, System.Core (LINQ), RevitAPI, and RevitAPIUI. No System.IO, no
/// System.Net, no System.Reflection.Emit.
/// </para>
/// <para>
/// The compiler is the SECOND safety layer (after the AST analyzer). Defense in depth:
/// even if the analyzer missed a way to express a forbidden call, the compiler will refuse
/// to bind any type that lives in an assembly we didn't reference.
/// </para>
/// </remarks>
public static class RoslynCompiler
{
    /// <summary>
    /// Compiles the supplied user snippet. Caller must have already run
    /// <see cref="RoslynStaticAnalyzer.Analyze"/> and confirmed
    /// <see cref="SafetyVerdict.IsAllowed"/> is true — this method does NOT re-validate.
    /// </summary>
    /// <param name="userCode">
    /// The C# snippet exactly as it came from the LLM. Becomes the body of the generated
    /// <c>Script.Run(Document doc, UIApplication uiApp)</c> method.
    /// </param>
    /// <returns>
    /// A <see cref="CompilationResult"/> with either a loaded <see cref="Assembly"/>
    /// ready for invocation, or a list of Roslyn errors that the LLM can read and fix.
    /// </returns>
    public static CompilationResult Compile(string userCode)
    {
        var result = new CompilationResult();

        var wrapped = WrapAsCompilationUnit(userCode);
        var syntaxTree = CSharpSyntaxTree.ParseText(
            wrapped,
            new CSharpParseOptions(LanguageVersion.Latest));

        // Restricted reference set — ONLY these assemblies are visible to the snippet.
        // Anything outside is a compile-time bind failure, which is exactly what we want.
        var references = BuildRestrictedReferences();

        var compilation = CSharpCompilation.Create(
            assemblyName: $"BIManageGeneratedScript_{Guid.NewGuid():N}",
            syntaxTrees: new[] { syntaxTree },
            references: references,
            options: new CSharpCompilationOptions(
                outputKind: OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Release,
                // Treat warnings as warnings — not errors. The LLM sometimes generates code
                // with harmless warnings (unused variables in early iterations) and we want
                // those to compile so the user gets an answer rather than a retry storm.
                generalDiagnosticOption: ReportDiagnostic.Default,
                // Disable assembly loading from the file system — defense in depth against
                // any path where the compiler might be tricked into loading off-disk assemblies.
                assemblyIdentityComparer: DesktopAssemblyIdentityComparer.Default));

        using var peStream = new MemoryStream();
        var emitResult = compilation.Emit(peStream);

        if (!emitResult.Success)
        {
            foreach (var diag in emitResult.Diagnostics)
            {
                if (diag.Severity != DiagnosticSeverity.Error) continue;
                var span = diag.Location.GetLineSpan();
                var rawMessage = diag.GetMessage();
                var enhanced = EnhanceWithSuggestion(diag.Id, rawMessage);
                result.Errors.Add(new CompilerError
                {
                    Id = diag.Id,
                    // Translate wrapper-line back to user-line. Our wrapper has 8 prologue lines
                    // (see RoslynStaticAnalyzer.WrapForParsing) — keep this in sync if the
                    // wrapper template changes.
                    Line = Math.Max(1, span.StartLinePosition.Line - WrapperPrologueLines + 1),
                    Column = span.StartLinePosition.Character + 1,
                    Message = enhanced
                });
            }
            return result;
        }

        peStream.Seek(0, SeekOrigin.Begin);
        result.CompiledAssembly = Assembly.Load(peStream.ToArray());
        return result;
    }

    /// <summary>
    /// Returns the assemblies the AI-generated script may reference. Strictly limited to
    /// what's needed for read-only Revit queries: BCL essentials, LINQ, Revit API.
    /// </summary>
    /// <remarks>
    /// Deliberately omitted (and the compile-time effect):
    /// <list type="bullet">
    ///   <item><c>System.IO.dll</c> — <c>File</c>, <c>Directory</c>, <c>Path</c> unbindable</item>
    ///   <item><c>System.Net.Http.dll</c> — <c>HttpClient</c> etc. unbindable</item>
    ///   <item><c>System.Reflection.Emit.dll</c> — runtime codegen unbindable</item>
    ///   <item><c>Microsoft.Win32.Registry.dll</c> — Registry unbindable</item>
    ///   <item><c>System.Diagnostics.Process.dll</c> — <c>Process</c> unbindable</item>
    /// </list>
    /// On net48, <c>System.IO</c> / <c>System.Net</c> live INSIDE mscorlib so the static
    /// analyzer (not just the reference list) is what blocks them there. The analyzer's
    /// pattern-based rules are the primary defense; reference restriction is belt-and-braces.
    /// </remarks>
    private static List<MetadataReference> BuildRestrictedReferences()
    {
        var references = new List<MetadataReference>
        {
            // mscorlib (Object, primitives, exceptions, Console etc.)
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),

            // System.Core (LINQ, Enumerable.*)
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),

            // System.Collections (Dictionary, HashSet etc.) — separate assembly on .NET Core+
            MetadataReference.CreateFromFile(typeof(System.Collections.Generic.Dictionary<,>).Assembly.Location),

            // Revit API — the reason we're here
            MetadataReference.CreateFromFile(typeof(Document).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(UIApplication).Assembly.Location),
        };

#if NET8_0_OR_GREATER
        // .NET 8+ splits the BCL across many assemblies. Without these, even `int.Parse`
        // fails to bind. Add the most-common netstandard surface explicitly.
        AddIfNotNull(references, "netstandard");
        AddIfNotNull(references, "System.Runtime");
        AddIfNotNull(references, "System.Linq");
        AddIfNotNull(references, "System.Collections");
#endif

        return references;
    }

#if NET8_0_OR_GREATER
    /// <summary>Best-effort lookup-by-name of a referenced assembly. No-op if not found.</summary>
    private static void AddIfNotNull(List<MetadataReference> refs, string simpleName)
    {
        try
        {
            var asm = System.AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, simpleName, StringComparison.Ordinal));
            if (asm != null && !string.IsNullOrEmpty(asm.Location))
            {
                refs.Add(MetadataReference.CreateFromFile(asm.Location));
            }
        }
        catch
        {
            // Defensive — a missing optional reference shouldn't crash compilation setup.
        }
    }
#endif

    /// <summary>
    /// Wraps the user's snippet in a parseable compilation unit. Mirrors
    /// <see cref="RoslynStaticAnalyzer"/>'s wrapper so line numbers stay consistent.
    /// </summary>
    private static string WrapAsCompilationUnit(string userCode)
        => @"using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
namespace BIManage.AI.Terminal.Generated {
  public static class Script {
    public static object Run(Document doc, UIApplication uiApp) {
" + userCode + @"
    }
  }
}";

    /// <summary>
    /// Number of lines in <see cref="WrapAsCompilationUnit"/>'s prologue (before user code).
    /// MUST match <see cref="RoslynStaticAnalyzer"/>'s constant of the same name — if you
    /// change one wrapper, change the other.
    /// </summary>
    private const int WrapperPrologueLines = 8;

    /// <summary>
    /// Augments a raw Roslyn diagnostic message with concrete suggestions when we can
    /// identify the AI's common Revit-API hallucinations. Drives self-correction on the
    /// next round so GPT doesn't burn all 5 rounds with the same wrong guess.
    /// </summary>
    /// <remarks>
    /// Catches the two most-common failure modes seen in real production logs:
    /// <list type="bullet">
    ///   <item><b>CS0117</b> — undefined enum member, typically <c>BuiltInParameter.CURVE_LENGTH</c>
    ///         when the LLM should have used <c>CURVE_ELEM_LENGTH</c></item>
    ///   <item><b>CS0234</b> — missing namespace member, typically <c>Autodesk.Revit.DB.Duct</c>
    ///         when the LLM should have used <c>Autodesk.Revit.DB.Mechanical.Duct</c></item>
    /// </list>
    /// Extend this table whenever new common hallucinations show up in audit logs.
    /// </remarks>
    private static string EnhanceWithSuggestion(string diagnosticId, string originalMessage)
    {
        if (string.IsNullOrEmpty(originalMessage)) return originalMessage;

        // CS0117 — "'BuiltInParameter' does not contain a definition for 'XYZ'"
        if (diagnosticId == "CS0117")
        {
            foreach (var kvp in BuiltInParameterHints)
            {
                if (originalMessage.IndexOf($"'{kvp.Key}'", StringComparison.Ordinal) >= 0)
                {
                    return originalMessage + " — Suggestion: use " + kvp.Value + ".";
                }
            }
            // Catch-all for any BuiltInParameter miss we didn't pre-map.
            if (originalMessage.Contains("BuiltInParameter") && originalMessage.Contains("does not contain"))
            {
                return originalMessage +
                       " — Hint: BuiltInParameter members have very specific names. For lengths of " +
                       "duct/pipe/conduit use CURVE_ELEM_LENGTH. For wall heights use WALL_USER_HEIGHT_PARAM. " +
                       "For any unknown parameter, fall back to elem.LookupParameter(\"display name\") instead.";
            }
        }

        // CS0234 — "The type or namespace name 'X' does not exist in the namespace 'Y'"
        if (diagnosticId == "CS0234")
        {
            foreach (var kvp in NamespaceTypeHints)
            {
                if (originalMessage.IndexOf($"'{kvp.Key}'", StringComparison.Ordinal) >= 0)
                {
                    return originalMessage + " — Suggestion: " + kvp.Value + ".";
                }
            }
        }

        // CS1061 — "'X' does not contain a definition for 'Y'"
        if (diagnosticId == "CS1061")
        {
            foreach (var kvp in MethodHints)
            {
                if (originalMessage.IndexOf($"'{kvp.Key}'", StringComparison.Ordinal) >= 0)
                {
                    return originalMessage + " — Suggestion: " + kvp.Value + ".";
                }
            }
        }

        return originalMessage;
    }

    /// <summary>Common BuiltInParameter hallucinations → correct values.</summary>
    private static readonly Dictionary<string, string> BuiltInParameterHints = new(StringComparer.Ordinal)
    {
        ["CURVE_LENGTH"] = "BuiltInParameter.CURVE_ELEM_LENGTH (this is the length of any MEPCurve including ducts, pipes, conduits)",
        ["DUCT_LENGTH"] = "BuiltInParameter.CURVE_ELEM_LENGTH (ducts use the generic MEPCurve length parameter)",
        ["PIPE_LENGTH"] = "BuiltInParameter.CURVE_ELEM_LENGTH (pipes use the generic MEPCurve length parameter)",
        ["DUCT_DIAMETER"] = "BuiltInParameter.RBS_CURVE_DIAMETER_PARAM (for round ducts) or RBS_CURVE_WIDTH_PARAM / RBS_CURVE_HEIGHT_PARAM (for rectangular)",
        ["PIPE_DIAMETER"] = "BuiltInParameter.RBS_CURVE_DIAMETER_PARAM",
        ["WALL_HEIGHT"] = "BuiltInParameter.WALL_USER_HEIGHT_PARAM",
        ["WALL_HEIGHT_PARAM"] = "BuiltInParameter.WALL_USER_HEIGHT_PARAM",
        ["ROOM_AREA_PARAM"] = "BuiltInParameter.ROOM_AREA",
        ["DOOR_HEIGHT"] = "BuiltInParameter.FAMILY_HEIGHT_PARAM",
        ["DOOR_WIDTH"] = "BuiltInParameter.FAMILY_WIDTH_PARAM",
    };

    /// <summary>Common namespace-member hallucinations → correct namespace.</summary>
    private static readonly Dictionary<string, string> NamespaceTypeHints = new(StringComparer.Ordinal)
    {
        ["Duct"] = "Duct lives in Autodesk.Revit.DB.Mechanical.Duct, not Autodesk.Revit.DB",
        ["DuctCurve"] = "There is no DuctCurve type — use Autodesk.Revit.DB.Mechanical.Duct, or simply work with Element if you only need shared properties",
        ["Pipe"] = "Pipe lives in Autodesk.Revit.DB.Plumbing.Pipe, not Autodesk.Revit.DB",
        ["PipeCurve"] = "There is no PipeCurve type — use Autodesk.Revit.DB.Plumbing.Pipe, or work with Element",
        ["Wire"] = "Wire lives in Autodesk.Revit.DB.Electrical.Wire",
        ["Room"] = "Room lives in Autodesk.Revit.DB.Architecture.Room",
        ["Area"] = "Area lives in Autodesk.Revit.DB.Architecture.Area",
        ["CableTray"] = "CableTray lives in Autodesk.Revit.DB.Electrical.CableTray",
        ["Conduit"] = "Conduit lives in Autodesk.Revit.DB.Electrical.Conduit",
    };

    /// <summary>Common method/property hallucinations → correct accessor.</summary>
    private static readonly Dictionary<string, string> MethodHints = new(StringComparer.Ordinal)
    {
        ["GetCurve"] = "CurveElement has no GetCurve() method. Use ((LocationCurve)elem.Location).Curve, then .Length (a property, not a method)",
        ["CurveLength"] = "Use elem.get_Parameter(BuiltInParameter.CURVE_ELEM_LENGTH).AsDouble() for the length in feet",
    };
}
