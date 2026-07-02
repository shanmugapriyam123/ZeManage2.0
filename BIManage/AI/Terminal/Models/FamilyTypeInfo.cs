namespace BIManage.AI.Terminal.Models;

/// <summary>
/// Lightweight projection of a Revit family type (loadable family or system type)
/// returned by the <c>get_available_family_types</c> MCP tool.
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </summary>
public sealed class FamilyTypeInfo
{
    public long FamilyTypeId { get; set; }
    public string? UniqueId { get; set; }
    public string? FamilyName { get; set; }
    public string? TypeName { get; set; }
    public string? Category { get; set; }
}
