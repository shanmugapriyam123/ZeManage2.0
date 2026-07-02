using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using BIManage.Data.SQLite;

namespace BIManage.Core.Identity
{
    /// <summary>
    /// Resolves a display username for a remote SignalR session, walking a
    /// preference chain so the UI never has to fall back to "Unknown user":
    ///
    ///   1. RevitUsername / Username supplied by the SignalR payload
    ///   2. In-memory cache from a previous successful resolve
    ///   3. SessionRepository.GetSessionAsync(sessionId) — local sessions table
    ///   4. ComputerName supplied by the SignalR payload (last-resort hint)
    ///
    /// Returns null when all four fail; callers should defer rendering until
    /// a follow-up event with usable identity arrives, rather than stamping a
    /// placeholder that turns the User column into "Unknown user".
    ///
    /// The in-memory cache is keyed by sessionId so repeated SignalR events
    /// for the same remote user don't hammer SQLite on every emit.
    /// </summary>
    public static class UsernameResolver
    {
        private static readonly ConcurrentDictionary<string, string> _cache =
            new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static async Task<string?> ResolveAsync(
            string? sessionId,
            string? revitUsername,
            string? username,
            string? computerName,
            SessionRepository? sessionRepo)
        {
            // 1. SignalR payload — fastest path, takes precedence over cache so
            //    a freshly-renamed user picks up the new name immediately.
            var fromPayload = !string.IsNullOrWhiteSpace(revitUsername) ? revitUsername
                            : !string.IsNullOrWhiteSpace(username) ? username
                            : null;
            if (fromPayload != null)
            {
                if (!string.IsNullOrWhiteSpace(sessionId))
                    _cache[sessionId!] = fromPayload!;
                return fromPayload;
            }

            // 2. Previously-resolved value for this session.
            if (!string.IsNullOrWhiteSpace(sessionId)
                && _cache.TryGetValue(sessionId!, out var cached))
                return cached;

            // 3. Sessions table — the canonical local source of truth for any
            //    session this client has observed (started or heartbeat-seen).
            if (sessionRepo != null && !string.IsNullOrWhiteSpace(sessionId))
            {
                try
                {
                    var s = await sessionRepo.GetSessionAsync(sessionId!).ConfigureAwait(false);
                    var resolved = !string.IsNullOrWhiteSpace(s?.RevitUsername) ? s!.RevitUsername
                                 : !string.IsNullOrWhiteSpace(s?.Username) ? s!.Username
                                 : null;
                    if (!string.IsNullOrWhiteSpace(resolved))
                    {
                        _cache[sessionId!] = resolved!;
                        return resolved;
                    }
                }
                catch
                {
                    // DB lookup failure isn't fatal — fall through to computer name.
                }
            }

            // 4. Computer name beats "Unknown user" as a last hint.
            if (!string.IsNullOrWhiteSpace(computerName))
                return computerName;

            return null;
        }

        /// <summary>Synchronous best-effort lookup — uses the cache only, no DB call.
        /// Returns null on miss so callers can decide between blocking with the
        /// async variant or deferring.</summary>
        public static string? TryGetCached(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId)) return null;
            return _cache.TryGetValue(sessionId!, out var v) ? v : null;
        }

        /// <summary>Seed the cache when a name is learned through another channel
        /// (e.g. SessionActivityListener publishing user-joined events).</summary>
        public static void Seed(string? sessionId, string? displayName)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(displayName))
                return;
            _cache[sessionId!] = displayName!;
        }
    }
}
