using Autodesk.Revit.DB;

namespace BIManage.AI.Terminal.Execution;

/// <summary>
/// Cross-version helpers for <see cref="ElementId"/>.
/// Revit 2024+ exposes <c>Value</c> as <see cref="long"/>; earlier versions expose
/// <c>IntegerValue</c> as <see cref="int"/>. Use these to write version-agnostic code.
/// </summary>
/// <remarks>
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
public static class ElementIdExtensions
{
    /// <summary>Returns the numeric value of an ElementId as a long.</summary>
#if REVIT2024_OR_GREATER
    public static long GetValue(this ElementId id) => id.Value;
#else
    public static long GetValue(this ElementId id) => id.IntegerValue;
#endif

    /// <summary>Returns the numeric value of an ElementId as an int.</summary>
#if REVIT2024_OR_GREATER
    public static int GetIntValue(this ElementId id) => (int)id.Value;
#else
    public static int GetIntValue(this ElementId id) => id.IntegerValue;
#endif
}
