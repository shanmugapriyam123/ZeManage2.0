using System.Collections.Generic;

namespace BIManage.AI.Knowledge
{
    /// <summary>
    /// Locally classifies a user query into the knowledge sections it needs.
    /// No API call — pure keyword matching for zero-cost, zero-latency routing.
    ///
    /// Three outcomes:
    ///   Non-empty set  → only those sections are injected into the prompt
    ///   Empty set      → no knowledge sections needed (e.g. pure metrics question)
    ///   (never null)   → callers can safely treat null as "use all" via fallback
    /// </summary>
    public static class IntentClassifier
    {
        // ── Keyword tables ─────────────────────────────────────────────────────

        private static readonly string[] ErrorKeywords =
        {
            "error", "warning", "failed", "fail", "crash", "corrupt",
            "issue", "problem", "fix", "broken", "not working", "doesn't work",
            "cannot", "can't", "unable", "missing", "lost", "deleted",
            "unresolved", "conflict", "inconsistent"
        };

        private static readonly string[] BestPracticeKeywords =
        {
            "best practice", "recommend", "should i", "tip", "standard",
            "proper", "correct way", "right way", "good practice",
            "advice", "guideline", "convention", "naming", "template",
            "approach", "strategy", "better way"
        };

        private static readonly string[] PerformanceKeywords =
        {
            "slow", "performance", "optimiz", "speed", "large", "heavy",
            "purge", "file size", "memory", "lag", "freeze", "hang",
            "bloat", "overloaded", "purgeable", "reduce", "cleanup",
            "audit", "shrink", "compact", "lightweight"
        };

        private static readonly string[] WorkflowKeywords =
        {
            "how to", "how do", "workflow", "steps", "process",
            "procedure", "export", "link", "coordinate", "publish",
            "set up", "setup", "create", "add", "place", "insert",
            "copy", "move", "manage", "organiz", "sheet", "view",
            "schedule", "tag", "annotate", "dimension", "align"
        };

        private static readonly string[] LocalDbKeywords =
        {
            "last sync", "when synced", "sync history", "sync log", "sync logs",
            "sync record", "sync records", "sync count", "how many sync",
            "synced times", "sync times", "synced time", "synced",
            "synced model", "model synced", "models synced", "models i synced",
            "history of sync", "all my sync", "all the sync", "all syncs",
            "list of sync", "list my sync", "summary of sync", "sync summary",
            "syncs per model", "syncs by model", "different models",
            "sessions today", "how many session", "active session", "current session",
            "who is online", "crash history", "crash count", "how many crash",
            "audit log", "protection log", "recent audit",
            "pinned element", "how many pin", "active pin", "protected element",
            "metrics history", "warning trend", "file size history",
            "registered model", "model list", "what model",
            "sync setting", "sync interval", "sync config",
            "who opened", "model opened by", "document history",
            "rule list", "active rule", "protection rule",
            "evidence", "screenshot history", "capture history",
            "heartbeat", "cpu usage", "memory usage", "resource usage",
            "session history", "user activity", "who modified",
            "query database", "check database", "show me data",
            "how many warnings over", "warning count over"
        };

        private static readonly string[] ProductKeywords =
        {
            "zemanage", "ze manage", "zestine", "bimanage", "bi manage",
            // Common typos / phonetic spellings observed in tester logs. Cheaper than
            // hauling in a fuzzy-match library — these cover ~all observed mis-spellings
            // of "zemanage" without needing per-token edit-distance scoring.
            "zemnage", "zemange", "zemnage", "ze-manage", "zee manage", "zee-manage",
            "ze manag", "zemanag", "z manage",
            "protection module", "activity tracker", "health monitor", "sync control",
            "support module", "support", "help module", "contact support", "get help",
            "pin protection", "command protection", "event restriction", "rule-based",
            "rule based", "admin override", "otp", "one-time password",
            "zedai", "zed ai", "zeai", "ze ai", "ai assistant",
            "nwc export", "link remapper", "addon",
            "company admin", "project admin", "normal user", "user role",
            "audit trail", "evidence capture", "compliance",
            "background sync", "relinquish", "compact model", "idle exit",
            "crash register", "session info", "model activities",
            "health score", "complexity score", "health dashboard",
            "license", "device registration", ".ze file",
            "what is zemanage", "what does zemanage", "zemanage feature",
            "tell me about", "about the", "what modules", "list modules", "modules in",
            "protection mode", "notify mode", "assist mode", "protect mode",
            "green mode", "yellow mode", "red mode",
            "this plugin", "this tool", "this addin", "this add-in"
        };

        // Warning resolution queries — when matched, NO knowledge sections are loaded
        // (the live warning diagnosis block from Revit is the only useful context)
        private static readonly string[] WarningKeywords =
        {
            "warning", "warnings", "resolve warning", "fix warning", "model warning",
            "model warnings", "errors in model", "model issues", "model errors",
            "health check", "model health", "duplicate", "unenclosed",
            "not connected", "disconnected", "overlap", "room not enclosed",
            "how many warnings", "list warnings", "show warnings"
        };

        // Visibility queries — when matched, NO knowledge sections are loaded
        // (the live diagnosis block from Revit is the only useful context)
        private static readonly string[] VisibilityKeywords =
        {
            "not visible", "not showing", "not show", "can't see", "cant see",
            "cannot see", "invisible", "hidden", "not found", "missing element",
            "element missing", "why is", "where is element", "find element",
            "visibility", "why can", "why can't", "why cant",
            "address why", "not visib", "not shown", "element.*visible",
            "visible.*element"
        };

        // ── Public API ─────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the query is a data question that should be answered
        /// from the local SQLite database (sessions, syncs, audit, metrics, etc.).
        /// </summary>
        public static bool IsLocalDbQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return false;
            var lower = query.ToLowerInvariant();
            return Matches(lower, LocalDbKeywords);
        }

        /// <summary>
        /// Returns true if the query is about model warnings/errors.
        /// When true, the caller should use WarningResolutionService for live diagnosis.
        /// </summary>
        public static bool IsWarningQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return false;
            var lower = query.ToLowerInvariant();
            return Matches(lower, WarningKeywords);
        }

        /// <summary>
        /// Returns true if the query is a visibility/element-lookup question.
        /// When true, the caller should skip knowledge section injection entirely —
        /// the live Revit diagnosis block is the only relevant context.
        /// </summary>
        public static bool IsVisibilityQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return false;
            var lower = query.ToLowerInvariant();
            return Matches(lower, VisibilityKeywords);
        }

        /// <summary>
        /// Returns the set of knowledge sections relevant to <paramref name="query"/>.
        /// Returns an empty set for visibility queries (no knowledge sections needed).
        /// An empty set also means no training-data sections are needed for this query.
        /// </summary>
        public static HashSet<KnowledgeSection> Classify(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return new HashSet<KnowledgeSection>();

            // Visibility and warning queries: the live diagnosis block IS the answer.
            // Injecting knowledge sections only confuses the model away from the diagnosis.
            if (IsVisibilityQuery(query) || IsWarningQuery(query))
                return new HashSet<KnowledgeSection>();

            var lower = query.ToLowerInvariant();
            var sections = new HashSet<KnowledgeSection>();

            if (Matches(lower, ErrorKeywords))
                sections.Add(KnowledgeSection.Errors);

            if (Matches(lower, BestPracticeKeywords))
                sections.Add(KnowledgeSection.BestPractices);

            if (Matches(lower, PerformanceKeywords))
                sections.Add(KnowledgeSection.Performance);

            if (Matches(lower, WorkflowKeywords))
                sections.Add(KnowledgeSection.Workflows);

            if (Matches(lower, ProductKeywords))
                sections.Add(KnowledgeSection.Product);

            return sections;
        }

        private static bool Matches(string lower, string[] keywords)
        {
            foreach (var kw in keywords)
                if (lower.Contains(kw))
                    return true;
            return false;
        }
    }

    /// <summary>
    /// Knowledge sections that can be independently loaded and injected.
    /// </summary>
    public enum KnowledgeSection
    {
        Errors,
        BestPractices,
        Performance,
        Workflows,
        Product
    }
}
