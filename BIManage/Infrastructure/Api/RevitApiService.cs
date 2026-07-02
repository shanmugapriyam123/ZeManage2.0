using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.Network;

namespace BIManage.Infrastructure.Api
{
    /// <summary>
    /// Service for fetching Revit categories and commands from the backend API
    /// </summary>
    public class RevitApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly ILogger? _logger;
        private bool _disposed;

        public RevitApiService(string baseUrl, ILogger? logger = null, SslValidationPolicy? sslPolicy = null)
        {
            _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
            _logger = logger;

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = sslPolicy != null
                    ? sslPolicy.Validate
                    : (message, cert, chain, errors) => true
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            _logger?.LogInfo($"RevitApiService initialized with base URL: {_baseUrl}");
        }

        /// <summary>
        /// Fetches all available Revit commands from the API
        /// API returns: { "message": "...", "data": { "commands": [{ "commandName": "...", "memberName": "...", "description": "..." }, ...] } }
        /// </summary>
        public async Task<List<RevitCommand>> GetCommandsAsync()
        {
            try
            {
                var url = $"{_baseUrl}/api/v1/Revit/commands";
                _logger?.LogInfo($"Fetching Revit commands from: {url}");

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Commands API response length: {json?.Length ?? 0}");

                var commands = new List<RevitCommand>();

                // API returns: { "message": "...", "data": { "commands": [...] } }
                try
                {
                    var result = JsonSerializer.Deserialize<CommandsApiResponse>(json, GetJsonOptions());
                    if (result?.Data?.Commands != null && result.Data.Commands.Count > 0)
                    {
                        commands = result.Data.Commands.Where(c => !string.IsNullOrEmpty(c.CommandName)).ToList();
                        _logger?.LogInfo($"Fetched {commands.Count} Revit commands from API");
                        return commands;
                    }
                }
                catch (JsonException ex)
                {
                    _logger?.LogDebug($"Commands response parsing error: {ex.Message}");
                }

                // Fallback: Try direct array format
                try
                {
                    var commandList = JsonSerializer.Deserialize<List<RevitCommand>>(json, GetJsonOptions());
                    if (commandList != null && commandList.Count > 0)
                    {
                        commands = commandList.Where(c => !string.IsNullOrEmpty(c.CommandName)).ToList();
                        _logger?.LogInfo($"Fetched {commands.Count} Revit commands from API (direct array)");
                        return commands;
                    }
                }
                catch (JsonException)
                {
                    _logger?.LogDebug("Commands response is not direct array");
                }

                _logger?.LogWarning("No commands found in API response");
                return commands;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error fetching Revit commands from API: {ex.Message}", ex);
                return new List<RevitCommand>();
            }
        }

        /// <summary>
        /// Fetches all available Revit categories from the API
        /// Supports multiple response formats:
        /// - { "data": { "categories": [...] } } - wrapped format
        /// - { "categories": [...] } - direct categories property
        /// - [{ "categoryName": "...", "categoryCode": "..." }, ...] - direct array
        /// </summary>
        public async Task<List<RevitCategory>> GetCategoriesAsync()
        {
            try
            {
                var url = $"{_baseUrl}/api/v1/Revit/categories";
                _logger?.LogInfo($"Fetching Revit categories from: {url}");

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Categories API response length: {json?.Length ?? 0}");

                var categories = new List<RevitCategory>();

                // Try 1: Wrapped format { "data": { "categories": [...] } } or { "categories": [...] }
                try
                {
                    var result = JsonSerializer.Deserialize<CategoriesApiResponse>(json, GetJsonOptions());

                    // Check data.categories first
                    if (result?.Data?.Categories != null && result.Data.Categories.Count > 0)
                    {
                        categories = result.Data.Categories.Where(c => !string.IsNullOrEmpty(c.CategoryName)).ToList();
                        _logger?.LogInfo($"Fetched {categories.Count} Revit categories from API (data.categories)");
                        return categories;
                    }

                    // Check data.categoryNames (string array)
                    if (result?.Data?.CategoryNames != null && result.Data.CategoryNames.Count > 0)
                    {
                        foreach (var name in result.Data.CategoryNames)
                        {
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                categories.Add(new RevitCategory { CategoryName = name });
                            }
                        }
                        _logger?.LogInfo($"Fetched {categories.Count} Revit categories from API (data.categoryNames)");
                        return categories;
                    }

                    // Check direct categories property
                    if (result?.Categories != null && result.Categories.Count > 0)
                    {
                        categories = result.Categories.Where(c => !string.IsNullOrEmpty(c.CategoryName)).ToList();
                        _logger?.LogInfo($"Fetched {categories.Count} Revit categories from API (categories property)");
                        return categories;
                    }
                }
                catch (JsonException ex)
                {
                    _logger?.LogDebug($"Categories response is not wrapped format: {ex.Message}");
                }

                // Try 2: Direct array of category objects [{ categoryName, categoryCode }, ...]
                try
                {
                    var categoryList = JsonSerializer.Deserialize<List<RevitCategory>>(json, GetJsonOptions());
                    if (categoryList != null && categoryList.Count > 0)
                    {
                        categories = categoryList.Where(c => !string.IsNullOrEmpty(c.CategoryName)).ToList();
                        _logger?.LogInfo($"Fetched {categories.Count} Revit categories from API (object array)");
                        return categories;
                    }
                }
                catch (JsonException ex)
                {
                    _logger?.LogDebug($"Categories response is not object array: {ex.Message}");
                }

                // Try 3: Simple string array ["Category1", "Category2", ...]
                try
                {
                    var categoryNames = JsonSerializer.Deserialize<List<string>>(json, GetJsonOptions());
                    if (categoryNames != null && categoryNames.Count > 0)
                    {
                        foreach (var name in categoryNames)
                        {
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                categories.Add(new RevitCategory { CategoryName = name });
                            }
                        }
                        _logger?.LogInfo($"Fetched {categories.Count} Revit categories from API (string array)");
                        return categories;
                    }
                }
                catch (JsonException)
                {
                    _logger?.LogDebug("Categories response is not a simple array");
                }

                _logger?.LogWarning("No categories found in API response");
                return categories;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error fetching Revit categories from API: {ex.Message}", ex);
                return new List<RevitCategory>();
            }
        }

        /// <summary>
        /// Fetches category codes for a specific category name
        /// </summary>
        public async Task<RevitCategoryCode?> GetCategoryCodesAsync(string categoryName)
        {
            try
            {
                var encodedName = Uri.EscapeDataString(categoryName);
                var url = $"{_baseUrl}/api/v1/Revit/categories/codes?categoryName={encodedName}";
                _logger?.LogInfo($"Fetching category codes for: {categoryName}");

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var result = JsonSerializer.Deserialize<CategoryCodesApiResponse>(json, GetJsonOptions());

                return result?.CategoryCode;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error fetching category codes: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Fetches category codes for multiple category names
        /// API endpoint: /api/v1/Revit/categories/codes?names=Category1,Category2
        /// Returns: { "data": { "categoryCodes": { "Category1": "OST_Code1", "Category2": "OST_Code2" } } }
        /// </summary>
        public async Task<Dictionary<string, string>> GetCategoryCodesBatchAsync(IEnumerable<string> categoryNames)
        {
            var result = new Dictionary<string, string>();

            try
            {
                var names = string.Join(",", categoryNames.Select(Uri.EscapeDataString));
                var url = $"{_baseUrl}/api/v1/Revit/categories/codes?names={names}";
                _logger?.LogDebug($"Fetching category codes for: {string.Join(", ", categoryNames)}");

                var response = await _httpClient.GetAsync(url);

                // Handle 404 gracefully - endpoint may not exist in this API version
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger?.LogDebug($"Category codes endpoint not available (404) - using local data only");
                    return result;
                }

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Category codes API response: {json}");

                var apiResponse = JsonSerializer.Deserialize<CategoryCodesBatchResponse>(json, GetJsonOptions());

                if (apiResponse?.Data?.CategoryCodes != null)
                {
                    foreach (var kvp in apiResponse.Data.CategoryCodes)
                    {
                        result[kvp.Key] = kvp.Value;
                    }
                    _logger?.LogInfo($"Fetched {result.Count} category codes from API");
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Category codes fetch skipped: {ex.Message}");
                return result;
            }
        }

        /// <summary>
        /// Fetches command details (member name and description) for a specific command
        /// API endpoint: /api/v1/Revit/commands/{commandName}
        /// Returns: { "data": { "commandName": "Area", "memberName": "Area", "description": "..." } }
        /// </summary>
        public async Task<RevitCommandDetails?> GetCommandDetailsAsync(string commandName)
        {
            try
            {
                var encodedName = Uri.EscapeDataString(commandName);
                var url = $"{_baseUrl}/api/v1/Revit/commands/{encodedName}";
                _logger?.LogDebug($"Fetching command details for: {commandName}");

                var response = await _httpClient.GetAsync(url);

                // Handle 404 gracefully - endpoint may not exist in this API version
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    _logger?.LogDebug($"Command details endpoint not available (404) for: {commandName}");
                    return null;
                }

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                _logger?.LogDebug($"Command details API response: {json}");

                var apiResponse = JsonSerializer.Deserialize<CommandDetailsApiResponse>(json, GetJsonOptions());

                if (apiResponse?.Data != null)
                {
                    _logger?.LogInfo($"Fetched command details: {apiResponse.Data.CommandName} -> {apiResponse.Data.MemberName}");
                    return apiResponse.Data;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Command details fetch skipped for {commandName}: {ex.Message}");
                return null;
            }
        }

        private static JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _httpClient?.Dispose();
            _disposed = true;
        }
    }

    #region API Response Models

    /// <summary>
    /// Response from /api/v1/Revit/commands
    /// Structure: { "message": "...", "data": { "commandNames": [...] } }
    /// </summary>
    public class CommandsApiResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("data")]
        public CommandsData? Data { get; set; }
    }

    public class CommandsData
    {
        [JsonPropertyName("commandNames")]
        public List<string>? CommandNames { get; set; }

        [JsonPropertyName("commands")]
        public List<RevitCommand>? Commands { get; set; }
    }

    /// <summary>
    /// Represents a Revit command from API
    /// API returns: { commandName, memberName, description, canHaveBinding, needBinding, canWorkWithSelection }
    /// </summary>
    public class RevitCommand
    {
        [JsonPropertyName("commandName")]
        public string CommandName { get; set; } = string.Empty;

        [JsonPropertyName("memberName")]
        public string? MemberName { get; set; }

        [JsonPropertyName("description")]
        public string? Description { get; set; }

        [JsonPropertyName("canHaveBinding")]
        public bool CanHaveBinding { get; set; }

        [JsonPropertyName("needBinding")]
        public bool NeedBinding { get; set; }

        [JsonPropertyName("canWorkWithSelection")]
        public bool CanWorkWithSelection { get; set; }

        [JsonPropertyName("code")]
        public string? Code { get; set; }

        public override string ToString() => CommandName;
    }

    /// <summary>
    /// Response from /api/v1/Revit/categories
    /// Supports multiple structures:
    /// - { "data": { "categoryNames": [...] } }
    /// - { "data": { "categories": [...] } }
    /// - { "categories": [...] }
    /// </summary>
    public class CategoriesApiResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("data")]
        public CategoriesData? Data { get; set; }

        [JsonPropertyName("categories")]
        public List<RevitCategory>? Categories { get; set; }
    }

    public class CategoriesData
    {
        [JsonPropertyName("categoryNames")]
        public List<string>? CategoryNames { get; set; }

        [JsonPropertyName("categories")]
        public List<RevitCategory>? Categories { get; set; }
    }

    public class RevitCategory
    {
        [JsonPropertyName("categoryId")]
        public int CategoryId { get; set; }

        [JsonPropertyName("categoryName")]
        public string CategoryName { get; set; } = string.Empty;

        [JsonPropertyName("categoryCode")]
        public string? CategoryCode { get; set; }

        public override string ToString() => CategoryName;
    }

    public class CategoryCodesApiResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("categoryCode")]
        public RevitCategoryCode? CategoryCode { get; set; }
    }

    public class RevitCategoryCode
    {
        [JsonPropertyName("categoryName")]
        public string CategoryName { get; set; } = string.Empty;

        [JsonPropertyName("builtInCategory")]
        public string BuiltInCategory { get; set; } = string.Empty;

        [JsonPropertyName("categoryId")]
        public int CategoryId { get; set; }
    }

    /// <summary>
    /// Response from /api/v1/Revit/categories/codes?names=...
    /// Structure: { "data": { "categoryCodes": { "CategoryName": "OST_Code", ... } } }
    /// </summary>
    public class CategoryCodesBatchResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("data")]
        public CategoryCodesBatchData? Data { get; set; }
    }

    public class CategoryCodesBatchData
    {
        [JsonPropertyName("categoryCodes")]
        public Dictionary<string, string>? CategoryCodes { get; set; }
    }

    /// <summary>
    /// Response from /api/v1/Revit/commands/{commandName}
    /// Structure: { "data": { "commandName": "Area", "memberName": "Area", "description": "..." } }
    /// </summary>
    public class CommandDetailsApiResponse
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }

        [JsonPropertyName("data")]
        public RevitCommandDetails? Data { get; set; }
    }

    /// <summary>
    /// Represents detailed command information from API
    /// </summary>
    public class RevitCommandDetails
    {
        [JsonPropertyName("commandName")]
        public string CommandName { get; set; } = string.Empty;

        [JsonPropertyName("memberName")]
        public string MemberName { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }

    #endregion
}
