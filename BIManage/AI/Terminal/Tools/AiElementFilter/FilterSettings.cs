namespace BIManage.AI.Terminal.Tools.AiElementFilter;

/// <summary>
/// Parameters for the <c>ai_element_filter</c> MCP tool.
/// </summary>
/// <remarks>
/// This is a focused port of Sparx's <c>FilterSetting</c> — it supports the most useful
/// 80% of filters (category, element class, visibility in current view, instance/type
/// inclusion, max element cap) but intentionally omits bounding-box / family-symbol-id
/// filters that depend on Sparx's JZPoint/Outline machinery. For the more exotic queries,
/// the LLM should use <c>send_code_to_revit</c> with a custom FilteredElementCollector.
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </remarks>
internal sealed class FilterSettings
{
    /// <summary>
    /// BuiltInCategory enum name (e.g. <c>"OST_Walls"</c>). Optional — null/empty = no
    /// category filter.
    /// </summary>
    public string? FilterCategory { get; set; }

    /// <summary>
    /// Revit API element class name (e.g. <c>"Wall"</c>, <c>"Floor"</c>, <c>"FamilyInstance"</c>).
    /// Resolved against the <c>Autodesk.Revit.DB</c> namespace. Optional.
    /// </summary>
    public string? FilterElementType { get; set; }

    /// <summary>If true, restrict to elements visible in the active view.</summary>
    public bool FilterVisibleInCurrentView { get; set; }

    /// <summary>Include element instances. Default true.</summary>
    public bool IncludeInstances { get; set; } = true;

    /// <summary>Include element types. Default false.</summary>
    public bool IncludeTypes { get; set; }

    /// <summary>Maximum elements to return. 0 = no cap.</summary>
    public int MaxElements { get; set; } = 100;

    public bool Validate(out string errorMessage)
    {
        if (!IncludeInstances && !IncludeTypes)
        {
            errorMessage = "At least one of includeInstances / includeTypes must be true";
            return false;
        }
        errorMessage = string.Empty;
        return true;
    }
}
