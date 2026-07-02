#nullable enable

using System;
using System.Text;
using System.Text.Json;

namespace BIManage.Common.Helpers
{
    /// <summary>
    /// Tiny JWT payload reader. We never validate signatures here — that's the server's
    /// job and the token has already been issued/refreshed by the auth pipeline. This is
    /// purely for extracting claim values the client legitimately needs to read (e.g.
    /// profileId, role) when local cached identity has been cleared but the access token
    /// is still valid.
    /// </summary>
    public static class JwtHelper
    {
        /// <summary>
        /// Returns the string value of the named claim from a JWT's payload, or null when
        /// the token is malformed, the claim is missing, or the claim isn't a string.
        /// Safe to call on a null/empty token (returns null without throwing).
        /// </summary>
        public static string? GetClaimValue(string? jwt, string claimName)
        {
            if (string.IsNullOrWhiteSpace(jwt) || string.IsNullOrEmpty(claimName)) return null;
            try
            {
                var parts = jwt!.Split('.');
                if (parts.Length < 2) return null;
                var payload = Base64UrlDecodeToUtf8(parts[1]);
                if (payload == null) return null;

                using var doc = JsonDocument.Parse(payload);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;

                if (doc.RootElement.TryGetProperty(claimName, out var v))
                {
                    return v.ValueKind switch
                    {
                        JsonValueKind.String => v.GetString(),
                        JsonValueKind.Number => v.GetRawText(),
                        _ => null
                    };
                }
            }
            catch
            {
                // Malformed token — caller treats as "claim not found".
            }
            return null;
        }

        /// <summary>
        /// Convenience: tries multiple claim names in order, returns the first non-empty
        /// value. Useful because different identity providers / token versions emit the
        /// same logical id under different claim names (e.g. "profileId", "profile_id",
        /// "sub", "uid").
        /// </summary>
        public static string? GetFirstClaim(string? jwt, params string[] claimNames)
        {
            foreach (var name in claimNames)
            {
                var value = GetClaimValue(jwt, name);
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }
            return null;
        }

        private static string? Base64UrlDecodeToUtf8(string base64Url)
        {
            try
            {
                var padded = base64Url.Replace('-', '+').Replace('_', '/');
                switch (padded.Length % 4)
                {
                    case 2: padded += "=="; break;
                    case 3: padded += "="; break;
                    case 0: break;
                    default: return null; // Invalid base64url length
                }
                var bytes = Convert.FromBase64String(padded);
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return null;
            }
        }
    }
}
