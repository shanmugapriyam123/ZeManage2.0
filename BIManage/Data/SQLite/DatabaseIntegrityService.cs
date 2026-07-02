using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using BIManage.Infrastructure.Logging;

namespace BIManage.Data.SQLite
{
    /// <summary>
    /// Provides row-level HMAC-SHA256 integrity verification for critical SQLite tables.
    /// Uses DPAPI (CurrentUser scope) with assembly-embedded entropy to protect the HMAC key.
    /// Thread-safe: creates a new HMACSHA256 instance per computation.
    /// </summary>
    public class DatabaseIntegrityService
    {
        private byte[]? _hmacKey;
        private readonly string _keyPath;
        private readonly ILogger? _logger;

        // Assembly-embedded entropy — raises the bar from "open DB Browser" to "decompile .NET assembly"
        private static readonly byte[] Entropy =
        {
            0x4B, 0x49, 0x2D, 0x4D, 0x61, 0x6E, 0x61, 0x67,
            0x65, 0x2D, 0x49, 0x6E, 0x74, 0x65, 0x67, 0x72,
            0x69, 0x74, 0x79, 0x2D, 0x56, 0x31, 0x2D, 0x53,
            0xA7, 0x3F, 0xC8, 0x91, 0x0E, 0xD5, 0x6A, 0xB2
        };

        /// <summary>
        /// Indicates whether the HMAC key is loaded and integrity verification is active.
        /// When false, verification is skipped (degraded mode) but new writes still attempt HMAC.
        /// </summary>
        public bool IsIntegrityAvailable => _hmacKey != null;

        public DatabaseIntegrityService(ILogger? logger = null)
        {
            _logger = logger;

            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BIManage");

            if (!Directory.Exists(appDataPath))
                Directory.CreateDirectory(appDataPath);

            _keyPath = Path.Combine(appDataPath, "integrity.key");
        }

        /// <summary>
        /// Loads existing HMAC key or generates a new one. Call once during bootstrap.
        /// </summary>
        public void InitializeKey()
        {
            try
            {
                _hmacKey = LoadKey();

                if (_hmacKey == null)
                {
                    _logger?.LogInfo("No integrity key found — generating new HMAC key");
                    var newKey = new byte[32]; // 256-bit key
                    using (var rng = RandomNumberGenerator.Create())
                    {
                        rng.GetBytes(newKey);
                    }

                    SaveKey(newKey);
                    _hmacKey = newKey;
                    _logger?.LogInfo("HMAC integrity key generated and stored securely");
                }
                else
                {
                    _logger?.LogInfo("HMAC integrity key loaded from secure storage");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"CRITICAL: Failed to initialize integrity key — HMAC verification suspended: {ex.Message}", ex);
                _hmacKey = null;
            }
        }

        /// <summary>
        /// Computes HMAC-SHA256 over table name, row ID, and column values.
        /// Returns empty string if key is unavailable (degraded mode).
        /// Thread-safe: creates a new HMACSHA256 instance per call.
        /// </summary>
        public string ComputeHmac(string tableName, string rowId, params string?[] values)
        {
            if (_hmacKey == null)
                return string.Empty;

            try
            {
                var input = BuildHmacInput(tableName, rowId, values);
                using var hmac = new HMACSHA256(_hmacKey);
                var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
                return Convert.ToBase64String(hash);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to compute HMAC for {tableName}.{rowId}: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Verifies a stored HMAC against recomputed values.
        /// Returns true if: HMAC matches, key unavailable (degraded), or storedHmac is null/empty (pre-migration).
        /// </summary>
        public bool VerifyHmac(string tableName, string rowId, string? storedHmac, params string?[] values)
        {
            // Degraded mode — skip verification
            if (_hmacKey == null)
                return true;

            // Pre-migration row — needs backfill, not rejection
            if (string.IsNullOrEmpty(storedHmac))
                return true;

            try
            {
                var expected = ComputeHmac(tableName, rowId, values);
                if (string.IsNullOrEmpty(expected))
                    return true; // ComputeHmac failed — don't reject

                return string.Equals(storedHmac, expected, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"HMAC verification error for {tableName}.{rowId}: {ex.Message}");
                return true; // Don't reject on internal error
            }
        }

        /// <summary>
        /// Computes a SHA-256 hash over sorted child record values for inclusion in parent HMAC.
        /// Used by RuleRepository to protect rule_parameters, rule_builtin_parameters, rule_commands.
        /// </summary>
        public string ComputeChildHash(params string[] sortedChildValues)
        {
            if (sortedChildValues == null || sortedChildValues.Length == 0)
                return string.Empty;

            try
            {
                var input = string.Join("|", sortedChildValues);
                using var sha256 = SHA256.Create();
                var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(input));
                return Convert.ToBase64String(hash);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// Checks if a row_hmac value indicates a pre-migration row that needs backfill.
        /// </summary>
        public bool NeedsBackfill(string? storedHmac)
        {
            return _hmacKey != null && string.IsNullOrEmpty(storedHmac);
        }

        private static string BuildHmacInput(string tableName, string rowId, string?[] values)
        {
            // Format: "tableName|rowId|val1|val2|...|valN"
            // Null values are represented as empty strings for deterministic output
            var sb = new StringBuilder();
            sb.Append(tableName);
            sb.Append('|');
            sb.Append(rowId ?? string.Empty);

            for (int i = 0; i < values.Length; i++)
            {
                sb.Append('|');
                sb.Append(values[i] ?? string.Empty);
            }

            return sb.ToString();
        }

        private byte[]? LoadKey()
        {
            try
            {
                if (!File.Exists(_keyPath))
                    return null;

                var encryptedBytes = File.ReadAllBytes(_keyPath);
                if (encryptedBytes.Length == 0)
                    return null;

                var keyBytes = ProtectedData.Unprotect(
                    encryptedBytes,
                    Entropy,
                    DataProtectionScope.CurrentUser);

                return keyBytes;
            }
            catch (CryptographicException ex)
            {
                _logger?.LogError($"Failed to decrypt integrity key (user/machine changed?): {ex.Message}", ex);
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load integrity key: {ex.Message}", ex);
                return null;
            }
        }

        private void SaveKey(byte[] key)
        {
            var encryptedBytes = ProtectedData.Protect(
                key,
                Entropy,
                DataProtectionScope.CurrentUser);

            File.WriteAllBytes(_keyPath, encryptedBytes);
        }
    }
}
