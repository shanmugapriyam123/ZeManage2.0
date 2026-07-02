using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using BIManage.AI.Interfaces;
using BIManage.AI.Knowledge.Models;
using BIManage.AI.Knowledge.Retrieval;
using BIManage.Infrastructure.Logging;

namespace BIManage.AI.Knowledge
{
    /// <summary>
    /// Loads knowledge from local JSON files shipped alongside the plugin DLL.
    /// To migrate to API endpoints, implement IKnowledgeProvider as ApiKnowledgeProvider.
    /// </summary>
    public class JsonKnowledgeProvider : IKnowledgeProvider
    {
        private readonly string _knowledgeFolder;
        private readonly ILogger? _logger;

        private List<KnowledgeEntry>? _errors;
        private List<BestPracticeEntry>? _bestPractices;
        private List<PerformanceEntry>? _performanceTips;
        private List<WorkflowEntry>? _workflows;
        private List<ProductEntry>? _productKnowledge;
        private OffTopicConfig? _offTopicConfig;
        private string? _systemPrompt;
        private readonly Random _random = new();

        // Mini-RAG: BM25 index built lazily on first query, kept for the lifetime of this
        // provider instance. Replaces the per-section substring scan with relevance ranking.
        private readonly KnowledgeRetriever _retriever = new();
        private bool _retrieverBuilt;

        /// <summary>
        /// When true, product knowledge entries marked "admin" are included.
        /// Set this based on IUserService.HasAdminPrivileges before queries are made.
        /// Defaults to false (normal user — restricted access).
        /// </summary>
        public bool IsAdmin { get; set; }

        public JsonKnowledgeProvider(ILogger? logger = null)
        {
            _logger = logger;

            var dllDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)
                         ?? AppDomain.CurrentDomain.BaseDirectory;
            var baseDir = AppDomain.CurrentDomain.BaseDirectory;

            var candidates = new[]
            {
                Path.Combine(dllDir, "BIManage", "AI", "Knowledge", "Data"),
                Path.Combine(dllDir, "knowledge"),
                Path.Combine(baseDir, "BIManage", "AI", "Knowledge", "Data"),
                Path.Combine(baseDir, "knowledge"),
            };

            _knowledgeFolder = candidates.FirstOrDefault(Directory.Exists)
                ?? Path.Combine(dllDir, "BIManage", "AI", "Knowledge", "Data");

            _logger?.LogDebug($"[Knowledge] DLL dir: {dllDir}");
            _logger?.LogDebug($"[Knowledge] Using folder: {_knowledgeFolder}");
            _logger?.LogDebug($"[Knowledge] Folder exists: {Directory.Exists(_knowledgeFolder)}");
        }

        public Task<string> GetSystemPromptAsync()
        {
            if (_systemPrompt != null)
                return Task.FromResult(_systemPrompt);

            var path = Path.Combine(_knowledgeFolder, "system_prompt.txt");
            _systemPrompt = File.Exists(path)
                ? File.ReadAllText(path)
                : FallbackSystemPrompt();

            return Task.FromResult(_systemPrompt);
        }

        public Task<string> GetRelevantKnowledgeAsync(string query, HashSet<KnowledgeSection>? sections = null)
        {
            // Empty set = no knowledge needed for this query (e.g. pure metrics question)
            if (sections != null && sections.Count == 0)
                return Task.FromResult(string.Empty);

            EnsureLoaded();
            EnsureRetrieverBuilt();

            // BM25 retrieval replaces the old per-section substring scan. The retriever knows
            // about role-based filtering (admin-only product entries), per-section TopK, and
            // a minimum relevance cutoff so unrelated entries don't slip into the prompt.
            var knowledge = _retriever.RetrieveFormatted(query, sections, IsAdmin);

            if (_logger != null)
            {
                var topScore = _retriever.TopScore(query);
                _logger.LogDebug($"[Knowledge/BM25] query='{Truncate(query, 60)}' topScore={topScore:F2} resultChars={knowledge.Length}");
            }

            return Task.FromResult(knowledge);
        }

        private static string Truncate(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        public Task<bool> IsRedirectedAsync(string question)
        {
            EnsureOffTopicLoaded();
            var lower = question.ToLower();
            var config = _offTopicConfig!;

            var isRedirect = config.RedirectKeywords.Any(k => lower.Contains(k));
            return Task.FromResult(isRedirect);
        }

        public Task<string> GetRedirectReplyAsync()
        {
            EnsureOffTopicLoaded();
            var replies = _offTopicConfig!.RedirectReplies;
            var reply = replies.Count > 0
                ? replies[_random.Next(replies.Count)]
                : "I'm not able to help with that. Please contact your project or company administrator for assistance.";
            return Task.FromResult(reply);
        }

        public Task<bool> IsOffTopicAsync(string question)
        {
            EnsureOffTopicLoaded();
            var lower = question.ToLower();
            var config = _offTopicConfig!;

            if (config.OffTopicKeywords.Any(k => lower.Contains(k)))
                return Task.FromResult(true);

            if (question.Length < config.MinimumQuestionLength)
                return Task.FromResult(false);

            // ZeManage product questions are on-topic even when RevitKeywords misses them.
            if (IntentClassifier.Classify(question).Contains(KnowledgeSection.Product))
                return Task.FromResult(false);

            // Baseline Revit/BIM safety net. See ApiKnowledgeProvider.BaselineRevitKeywords
            // for the rationale — local floor for cases where the off-topic config has gaps.
            if (BaselineRevitKeywords.Any(k => lower.Contains(k)))
                return Task.FromResult(false);

            var hasRevitKeyword = config.RevitKeywords.Any(k => lower.Contains(k));
            var isOffTopic = !hasRevitKeyword && question.Length > config.OffTopicMinimumLength;

            return Task.FromResult(isOffTopic);
        }

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

        public Task<string> GetOffTopicReplyAsync()
        {
            EnsureOffTopicLoaded();
            var replies = _offTopicConfig!.OffTopicReplies;
            var reply = replies.Count > 0
                ? replies[_random.Next(replies.Count)]
                : "I can only help with Revit and BIM questions!";
            return Task.FromResult(reply);
        }

        private void EnsureLoaded()
        {
            if (_errors != null) return;

            _errors = LoadJson<ErrorsFile>("errors.json")?.Entries ?? new List<KnowledgeEntry>();
            _bestPractices = LoadJson<BestPracticesFile>("best_practices.json")?.Entries ?? new List<BestPracticeEntry>();
            _performanceTips = LoadJson<PerformanceTipsFile>("performance_tips.json")?.Entries ?? new List<PerformanceEntry>();
            _workflows = LoadJson<WorkflowsFile>("workflows.json")?.Entries ?? new List<WorkflowEntry>();
            _productKnowledge = LoadJson<ProductFile>("product_knowledge.json")?.Entries ?? new List<ProductEntry>();

            _logger?.LogInfo($"[Knowledge] Loaded: {_errors.Count} errors, {_bestPractices.Count} best practices, {_performanceTips.Count} performance tips, {_workflows.Count} workflows, {_productKnowledge.Count} product entries");
        }

        private void EnsureRetrieverBuilt()
        {
            if (_retrieverBuilt) return;
            _retriever.Build(_errors, _bestPractices, _performanceTips, _workflows, _productKnowledge);
            _retrieverBuilt = true;
            _logger?.LogInfo("[Knowledge] BM25 retrieval index built from local JSON files");
        }

        private void EnsureOffTopicLoaded()
        {
            if (_offTopicConfig != null) return;
            _offTopicConfig = LoadJson<OffTopicConfig>("off_topic_config.json") ?? new OffTopicConfig();
        }

        private T? LoadJson<T>(string fileName) where T : class
        {
            try
            {
                var path = Path.Combine(_knowledgeFolder, fileName);
                if (!File.Exists(path))
                {
                    _logger?.LogWarning($"[Knowledge] File not found: {path}");
                    return default;
                }

                var json = File.ReadAllText(path);
                var options = new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                };
                return JsonSerializer.Deserialize<T>(json, options);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[Knowledge] Error loading {fileName}: {ex.Message}", ex);
                return default;
            }
        }

        private static string FallbackSystemPrompt() =>
            "You are a helpful Revit AI Assistant with expertise in BIM, Revit workflows, and model optimization. " +
            "Strictly answer only questions related to Revit, Building Information Modeling (BIM), and Engineering. " +
            "If a question is not related to these topics, politely decline to answer. " +
            "Provide practical, actionable answers using bullet points for lists. " +
            "Do NOT include any follow-up questions or 'You might also want to know' sections in your response.";
    }
}
