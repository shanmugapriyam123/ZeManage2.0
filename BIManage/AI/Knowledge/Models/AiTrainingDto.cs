using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BIManage.AI.Knowledge.Models
{
    /// <summary>
    /// DTO for a single item from GET /api/v1/master/ai-training.
    /// The 'content' field is a raw JsonElement because its structure
    /// varies by category and must be deserialized separately.
    /// </summary>
    public class AiTrainingItemDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("content")]
        public JsonElement Content { get; set; }

        [JsonPropertyName("createdAt")]
        public DateTime? CreatedAt { get; set; }

        [JsonPropertyName("createdBy")]
        public string? CreatedBy { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTime? UpdatedAt { get; set; }

        [JsonPropertyName("updatedBy")]
        public string? UpdatedBy { get; set; }
    }

    /// <summary>
    /// Represents the content field structure for the "System Prompt" category.
    /// </summary>
    public class SystemPromptContent
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }

    /// <summary>
    /// Wrapper for entry-based categories (errors, best practices, performance, workflows).
    /// </summary>
    public class EntriesContent<T>
    {
        [JsonPropertyName("entries")]
        public List<T>? Entries { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }
    }
}
