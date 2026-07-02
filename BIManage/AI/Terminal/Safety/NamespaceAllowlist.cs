namespace BIManage.AI.Terminal.Safety;

/// <summary>
/// Static allowlist of namespaces AI-generated scripts may reference. Used by both
/// <see cref="RoslynStaticAnalyzer"/> (rejects <c>using</c> directives outside this list)
/// and by <see cref="RoslynCompiler"/> (controls which assemblies are added to ScriptOptions).
/// </summary>
/// <remarks>
/// <para>
/// Allowlist (NOT blocklist) is the deliberate design choice: we know exactly what a
/// read-only Revit query needs, and anything outside that list is presumed unsafe until
/// proven otherwise. New surface area lands here only after an explicit safety review.
/// </para>
/// <para>
/// Specifically NOT allowed (and the rejection rule id):
/// <list type="bullet">
///   <item><c>System.IO</c> — SAFE010_NoFileIO</item>
///   <item><c>System.Net</c> / <c>System.Net.Http</c> — SAFE011_NoNetwork</item>
///   <item><c>System.Diagnostics</c> (Process etc.) — SAFE012_NoProcess</item>
///   <item><c>System.Reflection</c> / <c>System.Reflection.Emit</c> — SAFE013_NoReflection</item>
///   <item><c>System.Runtime.InteropServices</c> (Marshal) — SAFE014_NoInterop</item>
///   <item><c>Microsoft.Win32</c> (Registry) — SAFE015_NoRegistry</item>
/// </list>
/// </para>
/// </remarks>
public static class NamespaceAllowlist
{
    /// <summary>
    /// Exact namespace strings that are allowed to appear in <c>using</c> directives.
    /// Comparison is ordinal, case-sensitive — Revit's API uses PascalCase consistently.
    /// </summary>
    /// <remarks>
    /// Exposed as <see cref="HashSet{T}"/> rather than IReadOnlySet because the latter is
    /// .NET 5+ only and we cross-compile to net48. The set is constructed once at type init
    /// — callers shouldn't mutate it but the type system won't enforce that. <see cref="IsAllowed"/>
    /// is the supported access path.
    /// </remarks>
    public static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        // ── Core BCL (just what a read-only Revit query needs) ─────────────────
        "System",
        "System.Collections",
        "System.Collections.Generic",
        "System.Linq",
        "System.Text",

        // ── Revit API ──────────────────────────────────────────────────────────
        // We allow the whole DB and UI namespaces. Within those, individual member
        // calls are gated by RoslynStaticAnalyzer (e.g. Transaction.Start is rejected
        // even though Autodesk.Revit.DB is allowed at the namespace level).
        "Autodesk.Revit.DB",
        "Autodesk.Revit.DB.Architecture",
        "Autodesk.Revit.DB.Mechanical",
        "Autodesk.Revit.DB.Electrical",
        "Autodesk.Revit.DB.Plumbing",
        "Autodesk.Revit.DB.Structure",
        "Autodesk.Revit.UI"
    };

    /// <summary>
    /// Returns true if the given <c>using</c> directive target is on the allowlist.
    /// Empty / null returns false (defensive — empty namespace shouldn't appear in legitimate code).
    /// </summary>
    public static bool IsAllowed(string? namespaceName)
        => !string.IsNullOrEmpty(namespaceName) && Allowed.Contains(namespaceName!);

    /// <summary>
    /// Pretty-printed comma-separated list of allowed namespaces. Used in rejection messages
    /// so the LLM can see exactly what it's allowed to import without us inflating every
    /// rejection's message with the full list.
    /// </summary>
    public static string AllowedAsCommaList()
        => string.Join(", ", Allowed.OrderBy(s => s, StringComparer.Ordinal));
}
