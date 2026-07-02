using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BIManage.AI.Knowledge.Retrieval
{
    /// <summary>
    /// Tokenizer + light stemmer + Revit/BIM/ZeManage synonym expansion. The output is the
    /// vocabulary unit fed into <see cref="Bm25Index"/>. Designed for a small corpus (~80
    /// entries) where recall matters more than precision — we err toward generating extra
    /// tokens via synonym expansion so "synced" / "synchronize" / "syncing" all retrieve the
    /// same entries.
    /// </summary>
    /// <remarks>
    /// This is intentionally not Porter or Snowball — those are tuned for general English and
    /// over-stem domain terms like "ducts" → "duct" (good) but also "warnings" → "warn" (bad
    /// for our exact-match against parameter names). We hand-roll a conservative suffix rule
    /// set instead, plus an explicit synonyms map for the high-value Revit/ZeManage terms.
    /// </remarks>
    public static class KnowledgeTokenizer
    {
        // Split on anything that isn't a letter, digit, or underscore. Keeps numbers
        // (element IDs, version numbers, parameter codes like "OST_Walls") intact.
        private static readonly Regex TokenSplit = new(@"[^a-z0-9_]+", RegexOptions.Compiled);

        // English stopwords — high frequency, low information. Removed from both queries
        // and documents so they don't dominate IDF calculations. Conservative list — we
        // keep words like "how", "what", "why" because they distinguish question types.
        private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
        {
            "a", "an", "the", "and", "or", "but", "if", "then", "is", "are", "was", "were",
            "be", "been", "being", "in", "on", "at", "to", "of", "for", "with", "by", "from",
            "as", "this", "that", "these", "those", "it", "its", "my", "your", "our", "their",
            "i", "you", "we", "they", "he", "she", "him", "her", "us", "them",
            "do", "does", "did", "doing", "done", "have", "has", "had", "having",
            "will", "would", "could", "should", "may", "might", "must", "can",
            "not", "no", "yes", "so", "too", "very", "also"
        };

        // Synonyms expand a single token into many. Used at index time AND query time so
        // a search for "sync" hits documents indexed under "synchronize" and vice-versa.
        // Keep this list TIGHT — every synonym increases noise. Only add when a tester
        // demonstrably types the variant and misses the doc.
        private static readonly Dictionary<string, string[]> Synonyms = new(StringComparer.Ordinal)
        {
            // Sync/save
            ["sync"]       = new[] { "synchronize", "synchronization", "syncing", "synced", "synchronized" },
            ["save"]       = new[] { "saving", "saved", "stl" },
            ["central"]    = new[] { "centralfile", "centralmodel" },

            // Protection / governance
            ["protect"]    = new[] { "protection", "protecting", "protected", "guard", "guarding" },
            ["lock"]       = new[] { "locked", "locking", "pinned", "pin" },
            ["delete"]     = new[] { "deleting", "deleted", "remove", "removing", "removed", "erase", "wipe" },
            ["move"]       = new[] { "moving", "moved", "shift", "shifting", "shifted", "translate" },

            // ZeManage features (must mirror IntentClassifier.ProductKeywords)
            ["zemanage"]   = new[] { "zemanager", "ze", "zemange", "zemnage", "zemanag" }, // common typos included
            ["activity"]   = new[] { "tracker", "tracking", "audit", "log", "history" },
            ["health"]     = new[] { "monitor", "monitoring", "metrics", "diagnostic", "diagnostics" },
            ["otp"]        = new[] { "onetime", "password", "passcode" },

            // Revit elements (common plural/singular)
            ["wall"]       = new[] { "walls" },
            ["duct"]       = new[] { "ducts", "ductwork" },
            ["pipe"]       = new[] { "pipes", "piping", "pipework" },
            ["door"]       = new[] { "doors" },
            ["window"]     = new[] { "windows" },
            ["room"]       = new[] { "rooms" },
            ["floor"]      = new[] { "floors", "slab", "slabs" },
            ["ceiling"]    = new[] { "ceilings" },
            ["roof"]       = new[] { "roofs", "roofing" },
            ["column"]     = new[] { "columns" },
            ["beam"]       = new[] { "beams" },
            ["grid"]       = new[] { "grids", "gridline", "gridlines" },
            ["level"]      = new[] { "levels", "story", "storey", "stories", "storeys" },
            ["family"]     = new[] { "families", "rfa" },
            ["sheet"]      = new[] { "sheets", "drawing", "drawings" },
            ["view"]       = new[] { "views", "viewport", "viewports" },
            ["warning"]    = new[] { "warnings", "warn", "warned", "warning" },
            ["element"]    = new[] { "elements", "item", "items", "entity", "entities" },
            ["parameter"]  = new[] { "parameters", "param", "params" },
            ["category"]   = new[] { "categories" },
            ["type"]       = new[] { "types", "typology" },
            ["link"]       = new[] { "links", "linked", "linking", "reference", "referenced", "xref" },
            ["import"]     = new[] { "imports", "imported", "importing" },
            ["export"]     = new[] { "exports", "exported", "exporting" },
            ["purge"]      = new[] { "purged", "purging", "cleanup", "cleanups", "clean" },
            ["schedule"]   = new[] { "schedules", "schedule" },
            ["worksharing"] = new[] { "workset", "worksets", "workshared" },

            // Revit disciplines / acronyms
            ["mep"]        = new[] { "mechanical", "electrical", "plumbing" },
            ["bim"]        = new[] { "buildinginformationmodeling", "buildinginformationmodel" },
            ["cad"]        = new[] { "dwg", "autocad" },

            // Action verbs the LLM cares about
            ["fix"]        = new[] { "fixing", "fixed", "resolve", "resolving", "resolved", "repair", "repairing", "repaired", "correct", "correcting", "corrected" },
            ["show"]       = new[] { "showing", "showed", "display", "displaying", "displayed", "list", "listing", "listed", "view", "viewing", "see" },
            ["create"]     = new[] { "creating", "created", "add", "adding", "added", "place", "placing", "placed", "insert", "inserting", "inserted", "make", "making", "made" },
            ["find"]       = new[] { "finding", "found", "locate", "locating", "located", "search", "searching", "searched", "lookup"  },
            ["hide"]       = new[] { "hiding", "hidden", "conceal", "concealed", "concealing" },
            ["unhide"]     = new[] { "unhiding", "unhid", "reveal", "revealing", "revealed", "show", "showing" },

            // Visibility / Graphics
            ["visibility"] = new[] { "vg", "graphics", "visible", "invisible" },
            ["model"]      = new[] { "models", "project", "projects", "file", "files", "rvt" }
        };

        /// <summary>
        /// Tokenizes raw text into normalized terms, with synonyms expanded. The expanded
        /// token set is what gets indexed (at build time) or matched (at query time).
        /// Returns an empty array for null/whitespace input. Duplicate tokens are preserved
        /// (BM25 uses term frequency).
        /// </summary>
        public static List<string> Tokenize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return new List<string>(0);

            var raw = TokenSplit.Split(text.ToLowerInvariant())
                .Where(t => t.Length > 0 && !Stopwords.Contains(t));

            var result = new List<string>();
            foreach (var token in raw)
            {
                var stem = Stem(token);
                result.Add(stem);

                // Expand synonyms in BOTH directions: if the token is a key, add its values;
                // if the token is a value in any group, add the canonical key + siblings.
                if (Synonyms.TryGetValue(stem, out var direct))
                {
                    result.AddRange(direct);
                }
                else
                {
                    foreach (var pair in Synonyms)
                    {
                        if (Array.IndexOf(pair.Value, stem) >= 0)
                        {
                            result.Add(pair.Key);
                            result.AddRange(pair.Value.Where(v => v != stem));
                            break;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Conservative suffix-stripping stemmer. Only removes inflections that don't change
        /// meaning in our corpus. We INTENTIONALLY don't strip "-tion", "-ment", "-ity", etc.
        /// because those distinctions matter (e.g. "synchronize" vs "synchronization").
        /// </summary>
        private static string Stem(string token)
        {
            if (token.Length <= 3) return token;

            // -ies → -y (e.g. "categories" → "category"... but we also keep both via synonyms)
            if (token.EndsWith("ies", StringComparison.Ordinal) && token.Length > 4)
                return token.Substring(0, token.Length - 3) + "y";

            // -es → "" (e.g. "boxes" → "box")  — only when removing "es" leaves a valid stem ending
            if (token.EndsWith("es", StringComparison.Ordinal) && token.Length > 4)
            {
                var stripped = token.Substring(0, token.Length - 2);
                if (stripped.EndsWith("s") || stripped.EndsWith("x") || stripped.EndsWith("z")
                 || stripped.EndsWith("ch") || stripped.EndsWith("sh"))
                    return stripped;
            }

            // -s → "" (only on words > 4 chars, to avoid "is", "as", "us")
            if (token.EndsWith("s", StringComparison.Ordinal)
                && !token.EndsWith("ss", StringComparison.Ordinal)
                && token.Length > 4)
                return token.Substring(0, token.Length - 1);

            // -ing → "" (e.g. "linking" → "link"). Only when stem would be ≥ 3 chars.
            if (token.EndsWith("ing", StringComparison.Ordinal) && token.Length > 5)
                return token.Substring(0, token.Length - 3);

            // -ed → "" (e.g. "linked" → "link"). Avoid breaking "need", "bed".
            if (token.EndsWith("ed", StringComparison.Ordinal) && token.Length > 4)
                return token.Substring(0, token.Length - 2);

            return token;
        }
    }
}
