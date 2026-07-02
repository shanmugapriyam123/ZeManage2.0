using System.Text.Json.Serialization;

namespace BIManage.AI.Terminal.Models;

/// <summary>
/// Per-material area + volume aggregation across all participating elements.
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </summary>
public sealed class MaterialQuantityModel
{
    [JsonPropertyName("materialId")]
    public long MaterialId { get; set; }

    [JsonPropertyName("materialName")]
    public string? MaterialName { get; set; }

    [JsonPropertyName("materialClass")]
    public string? MaterialClass { get; set; }

    /// <summary>Square feet (Revit internal units).</summary>
    [JsonPropertyName("area")]
    public double Area { get; set; }

    /// <summary>Cubic feet (Revit internal units).</summary>
    [JsonPropertyName("volume")]
    public double Volume { get; set; }

    [JsonPropertyName("elementCount")]
    public int ElementCount { get; set; }

    [JsonPropertyName("elementIds")]
    public List<long> ElementIds { get; set; } = new();
}

/// <summary>Wrapped result of <c>get_material_quantities</c>.</summary>
public sealed class GetMaterialQuantitiesResult
{
    [JsonPropertyName("totalMaterials")]
    public int TotalMaterials { get; set; }

    [JsonPropertyName("totalArea")]
    public double TotalArea { get; set; }

    [JsonPropertyName("totalVolume")]
    public double TotalVolume { get; set; }

    [JsonPropertyName("materials")]
    public List<MaterialQuantityModel> Materials { get; set; } = new();

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}
