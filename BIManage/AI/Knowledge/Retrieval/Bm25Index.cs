using System;
using System.Collections.Generic;
using System.Linq;

namespace BIManage.AI.Knowledge.Retrieval
{
    /// <summary>
    /// In-memory BM25 inverted index. Build once with <see cref="Add"/> + <see cref="Finalize"/>,
    /// then call <see cref="Search"/> as many times as needed. Thread-safe for concurrent reads
    /// once finalized; not safe to mutate after the first <see cref="Search"/> call.
    /// </summary>
    /// <remarks>
    /// BM25 parameters use the Lucene defaults (k1=1.2, b=0.75) which empirically work well on
    /// short documents like our knowledge entries. We don't expose them as knobs because tuning
    /// them on an 80-entry corpus isn't measurable.
    /// </remarks>
    /// <typeparam name="TDoc">Document type — usually an entry object the caller wants back.</typeparam>
    public sealed class Bm25Index<TDoc> where TDoc : class
    {
        private const double K1 = 1.2;
        private const double B = 0.75;

        private readonly List<IndexedDoc> _docs = new();

        // term → list of (docIndex, termFrequency)
        private readonly Dictionary<string, List<Posting>> _postings = new(StringComparer.Ordinal);

        private double _avgDocLength;
        private bool _finalized;

        /// <summary>
        /// Adds a document to the index. <paramref name="tokens"/> are the already-tokenized
        /// terms (call <see cref="KnowledgeTokenizer.Tokenize"/> beforehand). The same document
        /// can be split across multiple fields by passing extra weight via duplicate tokens —
        /// e.g. duplicate the title tokens 3x to boost title matches.
        /// </summary>
        public void Add(TDoc document, IReadOnlyList<string> tokens)
        {
            if (_finalized)
                throw new InvalidOperationException("Cannot add documents after Seal() — build a new index.");

            var docIndex = _docs.Count;
            var docLength = tokens.Count;
            var termCounts = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var token in tokens)
            {
                termCounts[token] = termCounts.TryGetValue(token, out var c) ? c + 1 : 1;
            }

            foreach (var pair in termCounts)
            {
                if (!_postings.TryGetValue(pair.Key, out var list))
                {
                    list = new List<Posting>();
                    _postings[pair.Key] = list;
                }
                list.Add(new Posting { DocIndex = docIndex, TermFreq = pair.Value });
            }

            _docs.Add(new IndexedDoc { Document = document, Length = docLength });
        }

        /// <summary>
        /// Computes the average document length (needed by BM25's length-normalization term).
        /// Call once after all <see cref="Add"/> calls, before any <see cref="Search"/>.
        /// Named <c>Seal</c> rather than <c>Finalize</c> to avoid clashing with <c>Object.Finalize</c>.
        /// </summary>
        public void Seal()
        {
            if (_docs.Count == 0)
            {
                _avgDocLength = 0;
            }
            else
            {
                long total = 0;
                foreach (var d in _docs) total += d.Length;
                _avgDocLength = (double)total / _docs.Count;
            }
            _finalized = true;
        }

        public int DocumentCount => _docs.Count;
        public int VocabularySize => _postings.Count;

        /// <summary>
        /// Returns the top-K documents matching the query tokens, with BM25 scores.
        /// Empty list if the index has no documents or no query token matches anything.
        /// Caller can filter on a minimum score threshold if desired.
        /// </summary>
        public List<ScoredResult> Search(IReadOnlyList<string> queryTokens, int k = 10)
        {
            if (!_finalized)
                throw new InvalidOperationException("Call Seal() before searching.");
            if (_docs.Count == 0 || queryTokens.Count == 0)
                return new List<ScoredResult>(0);

            var scores = new Dictionary<int, double>();
            var totalDocs = _docs.Count;

            // Deduplicate query terms — BM25 doesn't benefit from repeating them.
            var uniqueQueryTerms = new HashSet<string>(queryTokens, StringComparer.Ordinal);

            foreach (var term in uniqueQueryTerms)
            {
                if (!_postings.TryGetValue(term, out var postings)) continue;

                // IDF = log((N - df + 0.5) / (df + 0.5) + 1), the Lucene-style smoothed form.
                var df = postings.Count;
                var idf = Math.Log(((double)(totalDocs - df) + 0.5) / (df + 0.5) + 1.0);
                if (idf <= 0) continue;

                foreach (var p in postings)
                {
                    var doc = _docs[p.DocIndex];
                    var lengthNorm = _avgDocLength > 0 ? doc.Length / _avgDocLength : 1.0;
                    var tfComponent = (p.TermFreq * (K1 + 1)) / (p.TermFreq + K1 * (1 - B + B * lengthNorm));
                    var contribution = idf * tfComponent;

                    scores[p.DocIndex] = scores.TryGetValue(p.DocIndex, out var existing)
                        ? existing + contribution
                        : contribution;
                }
            }

            return scores
                .OrderByDescending(kvp => kvp.Value)
                .Take(k)
                .Select(kvp => new ScoredResult
                {
                    Document = _docs[kvp.Key].Document,
                    Score = kvp.Value
                })
                .ToList();
        }

        public sealed class ScoredResult
        {
            public TDoc Document { get; init; } = default!;
            public double Score { get; init; }
        }

        private struct Posting
        {
            public int DocIndex;
            public int TermFreq;
        }

        private sealed class IndexedDoc
        {
            public TDoc Document { get; init; } = default!;
            public int Length { get; init; }
        }
    }
}
