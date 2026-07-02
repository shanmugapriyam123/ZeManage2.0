using System;
using System.Collections.Generic;
using System.Linq;
using BIManage.AI.Knowledge.Models;

namespace BIManage.AI.Knowledge.Retrieval
{
    /// <summary>
    /// Builds a BM25 index over the four knowledge categories (errors, best practices,
    /// performance tips, workflows, product entries) and exposes a single
    /// <see cref="RetrieveFormatted"/> method that returns the same prompt-ready strings
    /// the old substring scan produced — but ranked by BM25 relevance instead of brittle
    /// keyword contains.
    /// </summary>
    /// <remarks>
    /// Title tokens are duplicated 3x and explicit Keywords 5x at index time. This gives
    /// curated metadata more weight than free-form Content, which is the BM25 equivalent
    /// of saying "if it's tagged with this keyword, that's a strong signal."
    /// </remarks>
    public sealed class KnowledgeRetriever
    {
        // Per-section minimum BM25 score. Anything below the cutoff is treated as "irrelevant".
        // Empirically tuned: scores for irrelevant entries on this corpus cluster around 0.5-1.5,
        // genuine matches typically score 3+. Keeping the cutoff conservative avoids surfacing
        // unrelated entries when the query has common words like "what" or "model".
        private const double RelevanceCutoff = 2.0;

        // Top-K per section. The old substring scan returned every match; that's wasteful and
        // dilutes the prompt. 3 is plenty for a single section to inform the LLM.
        private const int TopK = 3;

        private Bm25Index<IndexedEntry> _index = new();
        private bool _built;

        /// <summary>
        /// Builds the index from the provided entry collections. Safe to pass nulls.
        /// Call once after JSON loading completes. Subsequent calls rebuild the index from
        /// scratch — useful if a hot-reload of the knowledge files is added later.
        /// </summary>
        public void Build(
            IReadOnlyList<KnowledgeEntry>? errors,
            IReadOnlyList<BestPracticeEntry>? bestPractices,
            IReadOnlyList<PerformanceEntry>? performanceTips,
            IReadOnlyList<WorkflowEntry>? workflows,
            IReadOnlyList<ProductEntry>? productKnowledge)
        {
            // Reset by reassigning — Seal is one-shot, can't re-finalize an existing index.
            var fresh = new Bm25Index<IndexedEntry>();
            _index = fresh;

            if (errors != null)
            {
                foreach (var e in errors)
                {
                    fresh.Add(new IndexedEntry
                    {
                        Section = KnowledgeSection.Errors,
                        Title = e.Title,
                        Body = e.Solution,
                        Tag = e.Title,
                        Formatted = $"[{e.Title}]\n{e.Solution}"
                    }, BuildTokens(e.Title, e.Solution, e.Keywords));
                }
            }

            if (bestPractices != null)
            {
                foreach (var e in bestPractices)
                {
                    var body = "• " + string.Join("\n• ", e.Practices);
                    fresh.Add(new IndexedEntry
                    {
                        Section = KnowledgeSection.BestPractices,
                        Title = e.Title,
                        Body = body,
                        Tag = $"Best Practices: {e.Title}",
                        Formatted = $"[Best Practices: {e.Title}]\n{body}"
                    }, BuildTokens(e.Title, string.Join(" ", e.Practices), e.Keywords));
                }
            }

            if (performanceTips != null)
            {
                foreach (var e in performanceTips)
                {
                    var body = string.Join("\n", e.Steps.Select((s, i) => $"{i + 1}. {s}"));
                    fresh.Add(new IndexedEntry
                    {
                        Section = KnowledgeSection.Performance,
                        Title = e.Title,
                        Body = body,
                        Tag = $"Performance: {e.Title}",
                        Formatted = $"[Performance: {e.Title}]\n{body}"
                    }, BuildTokens(e.Title, string.Join(" ", e.Steps), e.Keywords));
                }
            }

            if (workflows != null)
            {
                foreach (var e in workflows)
                {
                    var body = string.Join("\n", e.Steps.Select((s, i) => $"{i + 1}. {s}"));
                    fresh.Add(new IndexedEntry
                    {
                        Section = KnowledgeSection.Workflows,
                        Title = e.Title,
                        Body = body,
                        Tag = $"Workflow: {e.Title}",
                        Formatted = $"[Workflow: {e.Title}]\n{body}"
                    }, BuildTokens(e.Title, string.Join(" ", e.Steps), e.Keywords));
                }
            }

            if (productKnowledge != null)
            {
                foreach (var e in productKnowledge)
                {
                    fresh.Add(new IndexedEntry
                    {
                        Section = KnowledgeSection.Product,
                        Title = e.Title,
                        Body = e.Content,
                        Tag = $"ZeManage: {e.Title}",
                        IsAdminOnly = e.Access == "admin",
                        Formatted = $"[ZeManage: {e.Title}]\n{e.Content}"
                    }, BuildTokens(e.Title, e.Content, e.Keywords));
                }
            }

            fresh.Seal();
            _built = true;
        }

        /// <summary>
        /// Returns up to <see cref="TopK"/> entries per requested section, ranked by BM25,
        /// joined with the same separator the old code used. Returns empty when the query is
        /// blank, the index isn't built, or no entry crosses <see cref="RelevanceCutoff"/>.
        /// </summary>
        /// <param name="query">The raw user prompt.</param>
        /// <param name="allowedSections">
        /// If non-null, only entries in these sections are considered. Pass null to allow all.
        /// An empty set returns an empty string (matches the existing "no knowledge needed" contract).
        /// </param>
        /// <param name="isAdmin">When false, admin-only product entries are filtered out.</param>
        public string RetrieveFormatted(string query, HashSet<KnowledgeSection>? allowedSections, bool isAdmin)
        {
            if (!_built || string.IsNullOrWhiteSpace(query))
                return string.Empty;
            if (allowedSections != null && allowedSections.Count == 0)
                return string.Empty;

            var tokens = KnowledgeTokenizer.Tokenize(query);
            if (tokens.Count == 0) return string.Empty;

            // Fetch a generous over-pull then group by section, so per-section TopK is honored
            // even when one section has many close-ranked entries.
            var raw = _index.Search(tokens, k: 30);
            if (raw.Count == 0) return string.Empty;

            var bySection = new Dictionary<KnowledgeSection, List<Bm25Index<IndexedEntry>.ScoredResult>>();
            foreach (var r in raw)
            {
                if (r.Score < RelevanceCutoff) continue;
                var entry = r.Document;
                if (allowedSections != null && !allowedSections.Contains(entry.Section)) continue;
                if (entry.IsAdminOnly && !isAdmin) continue;

                if (!bySection.TryGetValue(entry.Section, out var bucket))
                {
                    bucket = new List<Bm25Index<IndexedEntry>.ScoredResult>();
                    bySection[entry.Section] = bucket;
                }
                if (bucket.Count < TopK) bucket.Add(r);
            }

            if (bySection.Count == 0) return string.Empty;

            // Section ordering: Product first (the most authoritative for ZeManage questions),
            // then Errors / Performance (action-oriented), Workflows, BestPractices last.
            var sectionOrder = new[]
            {
                KnowledgeSection.Product,
                KnowledgeSection.Errors,
                KnowledgeSection.Performance,
                KnowledgeSection.Workflows,
                KnowledgeSection.BestPractices,
            };

            var flat = new List<string>();
            foreach (var section in sectionOrder)
            {
                if (!bySection.TryGetValue(section, out var bucket)) continue;
                foreach (var r in bucket) flat.Add(r.Document.Formatted);
            }

            return string.Join("\n\n---\n\n", flat);
        }

        /// <summary>
        /// Diagnostic: returns the top-scoring entry's score for a query, regardless of section
        /// filters. Use this to decide whether the query is on-topic for the knowledge corpus.
        /// Returns 0 when no entry matches.
        /// </summary>
        public double TopScore(string query)
        {
            if (!_built || string.IsNullOrWhiteSpace(query)) return 0;
            var tokens = KnowledgeTokenizer.Tokenize(query);
            if (tokens.Count == 0) return 0;
            var raw = _index.Search(tokens, k: 1);
            return raw.Count > 0 ? raw[0].Score : 0;
        }

        // Combines title (3x weight) + keywords (5x weight) + body (1x weight) into the
        // token stream fed to BM25. Weighting via repetition is the standard trick — BM25
        // length normalization will partially counteract it but the IDF boost on
        // rare-in-corpus terms (which titles and keywords tend to be) still wins.
        private static IReadOnlyList<string> BuildTokens(string title, string body, IReadOnlyList<string>? keywords)
        {
            var all = new List<string>();

            var titleTokens = KnowledgeTokenizer.Tokenize(title);
            for (int i = 0; i < 3; i++) all.AddRange(titleTokens);

            if (keywords != null)
            {
                var keywordText = string.Join(" ", keywords);
                var keywordTokens = KnowledgeTokenizer.Tokenize(keywordText);
                for (int i = 0; i < 5; i++) all.AddRange(keywordTokens);
            }

            all.AddRange(KnowledgeTokenizer.Tokenize(body));
            return all;
        }

        private sealed class IndexedEntry
        {
            public KnowledgeSection Section { get; init; }
            public string Title { get; init; } = "";
            public string Body { get; init; } = "";
            public string Tag { get; init; } = "";
            public string Formatted { get; init; } = "";
            public bool IsAdminOnly { get; init; }
        }
    }
}
