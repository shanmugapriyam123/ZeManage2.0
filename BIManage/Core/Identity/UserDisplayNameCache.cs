using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BIManage.Core.Identity
{
    /// <summary>
    /// Thread-safe cache for mapping profileIds to display names.
    /// Populated from sign-in responses and API data.
    /// Persists to a local JSON file so display names survive across sessions.
    /// </summary>
    public static class UserDisplayNameCache
    {
        private static readonly ConcurrentDictionary<string, string> _cache = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;
        private static readonly object _fileLock = new object();

        private static string PersistPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BIManageRevit", "user_display_names.json");

        /// <summary>
        /// Add or update a profileId → displayName mapping
        /// </summary>
        public static void Set(string profileId, string displayName)
        {
            if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(displayName))
                return;

            EnsureLoaded();

            // Only persist if actually changed
            if (_cache.TryGetValue(profileId, out var existing) && existing == displayName)
                return;

            _cache[profileId] = displayName;
            PersistAsync();
        }

        /// <summary>
        /// Get display name for a profileId. Returns:
        ///   1. The cached display name if a mapping exists.
        ///   2. A friendly placeholder "User &lt;first 8 chars&gt;" when the input looks like
        ///      a GUID but isn't in the cache (covers the case where the backend returns
        ///      a profileId but no companion displayName — the dialog shows raw GUIDs).
        ///   3. The input itself unchanged when it's not GUID-shaped (e.g. usernames
        ///      already in plain text).
        /// Never returns null.
        /// </summary>
        public static string GetDisplayName(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return profileId ?? string.Empty;

            EnsureLoaded();

            if (_cache.TryGetValue(profileId, out var displayName))
                return displayName;

            // Friendly fallback for unresolved GUIDs. The raw "f9f0b246-f92f-4319-..."
            // form in the Modified By / Created By column is ugly AND not actionable —
            // an admin can't tell who that is. Truncating to "User f9f0b246" keeps the
            // value distinguishable (different users get different prefixes) without
            // leaking the full ID to the UI. Once the backend starts returning
            // modifiedByDisplayName for that user, the cache will be filled and this
            // fallback stops firing for them.
            if (LooksLikeGuid(profileId))
            {
                // Use the first 8 hex chars — enough to disambiguate between users in
                // practice (1-in-4-billion collision) while keeping the label short.
                var prefix = profileId.Length >= 8 ? profileId.Substring(0, 8) : profileId;
                return "User " + prefix;
            }

            return profileId;
        }

        /// <summary>
        /// Loose GUID-shape detector — accepts both hyphenated (8-4-4-4-12) and bare
        /// 32-char hex forms. Used by <see cref="GetDisplayName"/> to decide whether
        /// an unresolved value should be shown as "User &lt;prefix&gt;" rather than raw.
        /// </summary>
        private static bool LooksLikeGuid(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return false;
            // Standard GUID: 36 chars including 4 hyphens.
            if (s.Length == 36 && s[8] == '-' && s[13] == '-' && s[18] == '-' && s[23] == '-')
                return true;
            // Compact GUID: 32 hex chars, no hyphens.
            if (s.Length == 32)
            {
                foreach (var ch in s)
                {
                    if (!IsHex(ch)) return false;
                }
                return true;
            }
            return false;
        }

        private static bool IsHex(char ch)
            => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f') || (ch >= 'A' && ch <= 'F');

        /// <summary>
        /// Check if a profileId is in the cache
        /// </summary>
        public static bool Contains(string profileId)
        {
            if (string.IsNullOrWhiteSpace(profileId))
                return false;

            EnsureLoaded();

            return _cache.ContainsKey(profileId);
        }

        /// <summary>
        /// Bulk add multiple profileId → displayName mappings
        /// </summary>
        public static void SetMany(Dictionary<string, string> mappings)
        {
            if (mappings == null || mappings.Count == 0)
                return;

            EnsureLoaded();

            var changed = false;
            foreach (var kvp in mappings)
            {
                if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                {
                    if (!_cache.TryGetValue(kvp.Key, out var existing) || existing != kvp.Value)
                    {
                        _cache[kvp.Key] = kvp.Value;
                        changed = true;
                    }
                }
            }

            if (changed) PersistAsync();
        }

        /// <summary>
        /// Clear all cached mappings (in-memory and persisted file)
        /// </summary>
        public static void Clear()
        {
            _cache.Clear();
            try
            {
                var path = PersistPath;
                if (File.Exists(path)) File.Delete(path);
            }
            catch { /* ignore cleanup errors */ }
        }

        /// <summary>
        /// Get all cached mappings (for debugging)
        /// </summary>
        public static IReadOnlyDictionary<string, string> GetAll()
        {
            EnsureLoaded();
            return _cache;
        }

        /// <summary>
        /// Load persisted mappings from disk on first access
        /// </summary>
        private static void EnsureLoaded()
        {
            if (_loaded) return;

            lock (_fileLock)
            {
                if (_loaded) return;
                _loaded = true;

                try
                {
                    var path = PersistPath;
                    if (!File.Exists(path)) return;

                    var json = File.ReadAllText(path);
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            if (!string.IsNullOrWhiteSpace(kvp.Key) && !string.IsNullOrWhiteSpace(kvp.Value))
                                _cache.TryAdd(kvp.Key, kvp.Value);
                        }
                    }
                }
                catch { /* ignore load errors — cache starts empty */ }
            }
        }

        /// <summary>
        /// Persist current cache to disk (fire-and-forget, non-blocking)
        /// </summary>
        private static void PersistAsync()
        {
            // Fire-and-forget write on thread pool
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (_fileLock)
                {
                    try
                    {
                        var path = PersistPath;
                        var dir = Path.GetDirectoryName(path);
                        if (dir != null) Directory.CreateDirectory(dir);

                        var snapshot = new Dictionary<string, string>(_cache, StringComparer.OrdinalIgnoreCase);
                        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                        File.WriteAllText(path, json);
                    }
                    catch { /* ignore write errors — cache still works in-memory */ }
                }
            });
        }
    }
}
