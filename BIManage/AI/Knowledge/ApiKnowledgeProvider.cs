using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using BIManage.AI.Interfaces;
using BIManage.AI.Knowledge.Models;
using BIManage.AI.Knowledge.Retrieval;
using BIManage.Infrastructure.Auth;
using BIManage.Infrastructure.Logging;

namespace BIManage.AI.Knowledge
{
    /// <summary>
    /// Fetches AI knowledge from the backend API endpoint.
    /// Falls back to the provided JsonKnowledgeProvider if the API call fails.
    /// Caches the API response for the lifetime of this instance (session-scoped).
    /// </summary>
    public class ApiKnowledgeProvider : IKnowledgeProvider
    {
        private readonly AuthenticatedHttpClient _httpClient;
        private readonly JsonKnowledgeProvider _fallback;
        private readonly ILogger? _logger;

        private const string Endpoint = "/api/v1/master/ai-training";

        // Session-scoped cache — populated on first access, reused thereafter
        private bool _loaded;
        private bool _apiSucceeded;
        private List<KnowledgeEntry> _errors = new();
        private List<BestPracticeEntry> _bestPractices = new();
        private List<PerformanceEntry> _performanceTips = new();
        private List<WorkflowEntry> _workflows = new();
        private List<ProductEntry> _productKnowledge = new();
        private OffTopicConfig? _offTopicConfig;
        private string? _systemPrompt;
        private readonly Random _random = new();

        // Mini-RAG: BM25 retriever over API-loaded entries. Local-only computation, no extra
        // network calls. Rebuilt the first time GetRelevantKnowledgeAsync runs after a successful
        // EnsureLoadedAsync, then reused for the rest of the session.
        private readonly KnowledgeRetriever _retriever = new();
        private bool _retrieverBuilt;

        /// <summary>
        /// When true, product knowledge entries marked "admin" are included.
        /// Set this based on IUserService.HasAdminPrivileges before queries are made.
        /// Defaults to false (normal user — restricted access).
        /// </summary>
        public bool IsAdmin { get; set; }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip
        };

        public ApiKnowledgeProvider(
            AuthenticatedHttpClient httpClient,
            JsonKnowledgeProvider fallback,
            ILogger? logger = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
            _logger = logger;
        }

        // ──────────────────────────────────────────────────────────────────────
        // IKnowledgeProvider implementation
        // ──────────────────────────────────────────────────────────────────────

        public async Task<string> GetSystemPromptAsync()
        {
            await EnsureLoadedAsync();

            if (_apiSucceeded && _systemPrompt != null)
                return _systemPrompt;

            return await _fallback.GetSystemPromptAsync();
        }

        public async Task<string> GetRelevantKnowledgeAsync(string query, HashSet<KnowledgeSection>? sections = null)
        {
            // Empty set = no knowledge needed for this query
            if (sections != null && sections.Count == 0)
                return string.Empty;

            await EnsureLoadedAsync();

            if (!_apiSucceeded)
                return await _fallback.GetRelevantKnowledgeAsync(query, sections);

            EnsureRetrieverBuilt();

            // BM25 retrieval over API-loaded entries. If the API returned zero product entries
            // (common \u2014 backend's product corpus is often empty), we still augment with the
            // local fallback so ZeManage feature questions get answered.
            var apiResults = _retriever.RetrieveFormatted(query, sections, IsAdmin);

            if (_logger != null)
            {
                var topScore = _retriever.TopScore(query);
                var q = query.Length <= 60 ? query : query.Substring(0, 60) + "\u2026";
                _logger.LogDebug($"[Knowledge/BM25-API] query='{q}' topScore={topScore:F2} resultChars={apiResults.Length}");
            }

            var needsLocalProductFallback = _productKnowledge.Count == 0 &&
                (sections == null || sections.Contains(KnowledgeSection.Product));

            if (needsLocalProductFallback)
            {
                var productOnly = new HashSet<KnowledgeSection> { KnowledgeSection.Product };
                _fallback.IsAdmin = this.IsAdmin;
                var fallbackProduct = await _fallback.GetRelevantKnowledgeAsync(query, productOnly);
                if (!string.IsNullOrEmpty(fallbackProduct))
                {
                    return string.IsNullOrEmpty(apiResults)
                        ? fallbackProduct
                        : apiResults + "\n\n---\n\n" + fallbackProduct;
                }
            }

            return apiResults;
        }

        public async Task<bool> IsRedirectedAsync(string question)
        {
            await EnsureLoadedAsync();

            if (!_apiSucceeded || _offTopicConfig == null)
                return await _fallback.IsRedirectedAsync(question);

            var lower = question.ToLower();
            return _offTopicConfig.RedirectKeywords.Any(k => lower.Contains(k));
        }

        public async Task<string> GetRedirectReplyAsync()
        {
            await EnsureLoadedAsync();

            if (!_apiSucceeded || _offTopicConfig == null)
                return await _fallback.GetRedirectReplyAsync();

            var replies = _offTopicConfig.RedirectReplies;
            return replies.Count > 0
                ? replies[_random.Next(replies.Count)]
                : "I'm not able to help with that. Please contact your project or company administrator for assistance.";
        }

        public async Task<bool> IsOffTopicAsync(string question)
        {
            await EnsureLoadedAsync();

            if (!_apiSucceeded || _offTopicConfig == null)
                return await _fallback.IsOffTopicAsync(question);

            var lower = question.ToLower();
            var config = _offTopicConfig;

            if (config.OffTopicKeywords.Any(k => lower.Contains(k)))
                return true;

            if (question.Length < config.MinimumQuestionLength)
                return false;

            // ZeManage product questions ("what does pin protection do", "how does zemanage work")
            // are on-topic even when the backend's RevitKeywords list doesn't mention them.
            if (IntentClassifier.Classify(question).Contains(KnowledgeSection.Product))
                return false;

            // Baseline Revit/BIM safety net. The backend's RevitKeywords list has been observed
            // to be empty or missing obvious terms ("revit", "duct", "wall"), causing legitimate
            // questions like "Performance optimization tips for Revit" to be flagged off-topic.
            // This local list is the floor — anything matching here is on-topic regardless of
            // what the backend ships. Keep in sync with the engineeringKeywords list in
            // ZestAiViewModel.GetPreformedAnswerIfOffTopic so all guardrails agree.
            if (BaselineRevitKeywords.Any(k => lower.Contains(k)))
                return false;

            var hasRevitKeyword = config.RevitKeywords.Any(k => lower.Contains(k));
            return !hasRevitKeyword && question.Length > config.OffTopicMinimumLength;
        }

        // Local floor — never gets out of sync with the backend, never gets emptied accidentally.
        private static readonly string[] BaselineRevitKeywords =
        {
            "revit", "bim", "model", "parameter", "sheet", "view", "wall", "pipe", "duct", "family",
            "warning", "purge", "link", "import", "export", "schedule", "workset", "phasing",
            "design", "structural", "mechanical", "electrical", "plumbing", "mep", "coordination",
            "clash", "navisworks", "bim360", "acc", "construction", "architecture", "element",
            "floor", "ceiling", "roof", "door", "window", "column", "beam", "level", "grid",
            "annotation", "dimension", "tag", "detail", "section", "elevation", "rendering",
            "phase", "category", "type", "instance", "shared parameter", "project parameter",
            "central", "local", "sync", "synchronize", "load", "unload", "audit", "compact",
            "ifc", "dwg", "rvt", "rfa", "rte", "rft", "ucs", "north"
        };

        public async Task<string> GetOffTopicReplyAsync()
        {
            await EnsureLoadedAsync();

            if (!_apiSucceeded || _offTopicConfig == null)
                return await _fallback.GetOffTopicReplyAsync();

            var replies = _offTopicConfig.OffTopicReplies;
            return replies.Count > 0
                ? replies[_random.Next(replies.Count)]
                : "I can only help with Revit and BIM questions!";
        }

        // ──────────────────────────────────────────────────────────────────────
        // Data loading
        // ──────────────────────────────────────────────────────────────────────

        private void EnsureRetrieverBuilt()
        {
            if (_retrieverBuilt) return;
            _retriever.Build(_errors, _bestPractices, _performanceTips, _workflows, _productKnowledge);
            _retrieverBuilt = true;
            _logger?.LogInfo("[Knowledge] BM25 retrieval index built from API-loaded entries");
        }

        private async Task EnsureLoadedAsync()
        {
            if (_loaded) return;

            try
            {
                _logger?.LogInfo($"[ApiKnowledgeProvider] Fetching training data from {Endpoint}");

                var response = await _httpClient.GetAsync(Endpoint);
                var json = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    _logger?.LogWarning($"[ApiKnowledgeProvider] API returned {response.StatusCode} — falling back to JSON files");
                    _apiSucceeded = false;
                    _loaded = true;
                    return;
                }

                var items = TryDeserializeItems(json);
                if (items == null || items.Count == 0)
                {
                    _logger?.LogWarning("[ApiKnowledgeProvider] API returned no training items — falling back to JSON files");
                    _apiSucceeded = false;
                    _loaded = true;
                    return;
                }

                foreach (var item in items)
                {
                    ParseTrainingItem(item);
                }

                _apiSucceeded = true;
                _loaded = true;

                _logger?.LogInfo($"[ApiKnowledgeProvider] Loaded from API: " +
                    $"{_errors.Count} errors, {_bestPractices.Count} best practices, " +
                    $"{_performanceTips.Count} performance tips, {_workflows.Count} workflows, " +
                    $"{_productKnowledge.Count} product entries, " +
                    $"systemPrompt={_systemPrompt != null}, offTopic={_offTopicConfig != null}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[ApiKnowledgeProvider] Failed to fetch training data: {ex.Message}", ex);
                _apiSucceeded = false;
                _loaded = true;
            }
        }

        // ──────────────────────────────────────────────────────────────────────
        // Parsing helpers
        // ──────────────────────────────────────────────────────────────────────

        private List<AiTrainingItemDto>? TryDeserializeItems(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;

            // Try direct array first
            try
            {
                var list = JsonSerializer.Deserialize<List<AiTrainingItemDto>>(json, JsonOpts);
                if (list != null && list.Count > 0) return list;
            }
            catch { /* try wrapped format */ }

            // Try wrapped { "data": [...] }
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var dataProp))
                {
                    var inner = dataProp.GetRawText();
                    return JsonSerializer.Deserialize<List<AiTrainingItemDto>>(inner, JsonOpts);
                }
            }
            catch { /* give up */ }

            return null;
        }

        /// <summary>
        /// Gets the content JSON string from an API item, handling both
        /// object content (returned as-is) and string content (escaped JSON that needs unwrapping).
        /// </summary>
        private static string GetContentJson(AiTrainingItemDto item)
        {
            if (item.Content.ValueKind == JsonValueKind.String)
                return item.Content.GetString() ?? "{}";

            return item.Content.GetRawText();
        }

        private void ParseTrainingItem(AiTrainingItemDto item)
        {
            if (string.IsNullOrEmpty(item.Category)) return;

            try
            {
                var contentJson = GetContentJson(item);

                switch (item.Category)
                {
                    case "System Prompt":
                    {
                        var sp = JsonSerializer.Deserialize<SystemPromptContent>(contentJson, JsonOpts);
                        if (!string.IsNullOrEmpty(sp?.Content))
                            _systemPrompt = sp.Content;
                        break;
                    }

                    case "off_topic":
                    case "Off-Topic Configuration":
                    {
                        _offTopicConfig = JsonSerializer.Deserialize<OffTopicConfig>(contentJson, JsonOpts);
                        break;
                    }

                    case "Common Errors & Solutions":
                    {
                        var wrapper = JsonSerializer.Deserialize<EntriesContent<KnowledgeEntry>>(contentJson, JsonOpts);
                        if (wrapper?.Entries != null)
                            _errors.AddRange(wrapper.Entries);
                        break;
                    }

                    case "Best Practices":
                    {
                        var wrapper = JsonSerializer.Deserialize<EntriesContent<BestPracticeEntry>>(contentJson, JsonOpts);
                        if (wrapper?.Entries != null)
                            _bestPractices.AddRange(wrapper.Entries);
                        break;
                    }

                    case "Performance Optimization":
                    {
                        var wrapper = JsonSerializer.Deserialize<EntriesContent<PerformanceEntry>>(contentJson, JsonOpts);
                        if (wrapper?.Entries != null)
                            _performanceTips.AddRange(wrapper.Entries);
                        break;
                    }

                    case "Workflows":
                    {
                        var wrapper = JsonSerializer.Deserialize<EntriesContent<WorkflowEntry>>(contentJson, JsonOpts);
                        if (wrapper?.Entries != null)
                            _workflows.AddRange(wrapper.Entries);
                        break;
                    }

                    case "Product Knowledge":
                    {
                        var wrapper = JsonSerializer.Deserialize<EntriesContent<ProductEntry>>(contentJson, JsonOpts);
                        if (wrapper?.Entries != null)
                            _productKnowledge.AddRange(wrapper.Entries);
                        break;
                    }

                    default:
                        _logger?.LogDebug($"[ApiKnowledgeProvider] Unknown category: {item.Category}");
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ApiKnowledgeProvider] Failed to parse category '{item.Category}': {ex.Message}");
            }
        }
    }
}