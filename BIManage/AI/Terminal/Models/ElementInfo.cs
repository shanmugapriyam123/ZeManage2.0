namespace BIManage.AI.Terminal.Models;

/// <summary>
/// Lightweight projection of a Revit element for MCP tool responses.
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </summary>
public sealed class ElementInfo
{
    /// <summary>
    /// Element ID. On Revit 2024+ this is the long Value; on older versions it's the IntegerValue.
    /// </summary>
    public long Id { get; set; }

    public string? UniqueId { get; set; }

    public string? Name { get; set; }

    public string? Category { get; set; }

    /// <summary>Optional parameter map populated by tools that read element properties.</summary>
    public Dictionary<string, string> Properties { get; set; } = new();
}
