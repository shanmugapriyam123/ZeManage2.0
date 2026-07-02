using System.Text.Json.Serialization;

namespace BIManage.AI.Terminal.Tools.SendCodeToRevit;

/// <summary>
/// Result envelope returned by the <c>send_code_to_revit</c> MCP tool.
/// Adapted from mcp-servers-for-revit (MIT). See THIRD_PARTY_NOTICES.md.
/// </summary>
public sealed class ExecutionResultInfo
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    /// <summary>JSON-serialised return value of the user's <c>Execute</c> method.</summary>
    [JsonPropertyName("result")]
    public string? Result { get; set; }

    [JsonPropertyName("errorMessage")]
    public string ErrorMessage { get; set; } = string.Empty;
}
