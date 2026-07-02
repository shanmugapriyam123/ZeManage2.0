using System.Text.Json.Serialization;

namespace BIManage.AI.Terminal.Models;

/// <summary>
/// Result of the <c>analyze_model_statistics</c> MCP tool — totals, per-category breakdown,
/// and per-level breakdown for the active document.
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </summary>
public sealed class AnalyzeModelStatisticsResult
{
    [JsonPropertyName("projectName")]
    public string? ProjectName { get; set; }

    [JsonPropertyName("totalElements")]
    public int TotalElements { get; set; }

    [JsonPropertyName("totalTypes")]
    public int TotalTypes { get; set; }

    [JsonPropertyName("totalFamilies")]
    public int TotalFamilies { get; set; }

    [JsonPropertyName("totalViews")]
    public int TotalViews { get; set; }

    [JsonPropertyName("totalSheets")]
    public int TotalSheets { get; set; }

    [JsonPropertyName("categories")]
    public List<CategoryStatistics> Categories { get; set; } = new();

    [JsonPropertyName("levels")]
    public List<LevelStatistics> Levels { get; set; } = new();

    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }
}

/// <summary>Per-category element/type/family counts.</summary>
public sealed class CategoryStatistics
{
    [JsonPropertyName("categoryName")]
    public string? CategoryName { get; set; }

    [JsonPropertyName("elementCount")]
    public int ElementCount { get; set; }

    [JsonPropertyName("typeCount")]
    public int TypeCount { get; set; }

    [JsonPropertyName("familyCount")]
    public int FamilyCount { get; set; }

    [JsonPropertyName("types")]
    public List<TypeStatistics> Types { get; set; } = new();
}

/// <summary>Per-type instance count within a category.</summary>
public sealed class TypeStatistics
{
    [JsonPropertyName("typeName")]
    public string? TypeName { get; set; }

    [JsonPropertyName("familyName")]
    public string? FamilyName { get; set; }

    [JsonPropertyName("instanceCount")]
    public int InstanceCount { get; set; }
}

/// <summary>Per-level element count. Elevations are exposed in BOTH feet (Revit's
/// internal unit) and millimetres — never ask the AI to convert one to the other.</summary>
public sealed class LevelStatistics
{
    [JsonPropertyName("levelName")]
    public string? LevelName { get; set; }

    /// <summary>Elevation in Revit internal units (feet). Kept for backwards-compat.</summary>
    [JsonPropertyName("elevationFeet")]
    public double ElevationFeet { get; set; }

    /// <summary>Elevation in millimetres. Pre-converted server-side so the AI never has to
    /// multiply by 304.8 — that arithmetic has been observed to hallucinate (see
    /// get_view_range comments for the same fix).</summary>
    [JsonPropertyName("elevationMm")]
    public double ElevationMm { get; set; }

    /// <summary>Legacy alias for ElevationFeet. Will be removed once the AI prompt
    /// is updated to expect the unit-suffixed fields.</summary>
    [JsonPropertyName("elevation")]
    public double Elevation { get; set; }

    [JsonPropertyName("elementCount")]
    public int ElementCount { get; set; }
}
