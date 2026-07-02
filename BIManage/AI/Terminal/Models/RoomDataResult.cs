using System.Text.Json.Serialization;

namespace BIManage.AI.Terminal.Models;

/// <summary>
/// Single-room projection used by the <c>export_room_data</c> MCP tool.
/// All length/area/volume values are in Revit's internal units (feet / sq.ft / cu.ft).
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </summary>
public sealed class RoomDataModel
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("uniqueId")]
    public string? UniqueId { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("number")]
    public string? Number { get; set; }

    [JsonPropertyName("level")]
    public string? Level { get; set; }

    /// <summary>Square feet (Revit internal units).</summary>
    [JsonPropertyName("area")]
    public double Area { get; set; }

    /// <summary>Cubic feet (Revit internal units).</summary>
    [JsonPropertyName("volume")]
    public double Volume { get; set; }

    /// <summary>Feet (Revit internal units).</summary>
    [JsonPropertyName("perimeter")]
    public double Perimeter { get; set; }

    /// <summary>Feet (Revit internal units).</summary>
    [JsonPropertyName("unboundedHeight")]
    public double UnboundedHeight { get; set; }

    [JsonPropertyName("department")]
    public string? Department { get; set; }

    [JsonPropertyName("comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("occupancy")]
    public string? Occupancy { get; set; }
}

/// <summary>Wrapped result of <c>export_room_data</c>.</summary>
public sealed class ExportRoomDataResult
{
    [JsonPropertyName("totalRooms")]
    public int TotalRooms { get; set; }

    [JsonPropertyName("totalArea")]
    public double TotalArea { get; set; }

    [JsonPropertyName("rooms")]
    public List<RoomDataModel> Rooms { get; set; } = new();

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
