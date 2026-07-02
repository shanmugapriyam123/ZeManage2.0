namespace BIManage.AI.Terminal.Models;

/// <summary>
/// Snapshot of the active Revit view returned by the <c>get_current_view_info</c> MCP tool.
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </summary>
public sealed class CurrentViewInfo
{
    public long Id { get; set; }
    public string? UniqueId { get; set; }
    public string? Name { get; set; }
    public string? ViewType { get; set; }
    public bool IsTemplate { get; set; }
    public int Scale { get; set; }
    public string? DetailLevel { get; set; }
}
