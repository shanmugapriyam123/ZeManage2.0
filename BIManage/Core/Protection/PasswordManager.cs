using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Data.SQLite;
using System.Threading.Tasks;
using BIManage.Core.Protection.Models;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;

namespace BIManage.Core.Protection
{
    /// <summary>
    /// Manages password storage and validation for protection override
    /// Extended with structured override support for granular control
    /// </summary>
    public class PasswordManager
    {
        private readonly string _connectionString;
        private readonly ILogger _logger;
        private ProtectionSettings _cachedSettings;
        private readonly OverrideRepository _overrideRepository;
        private readonly OtpRepository _otpRepository;
        private readonly DatabaseIntegrityService? _integrityService;

        public PasswordManager(string databasePath, ILogger logger, DatabaseIntegrityService? integrityService = null)
        {
            _connectionString = SqliteConnectionHelper.BuildConnectionString(databasePath);
            _logger = logger;
            _integrityService = integrityService;
            _overrideRepository = new OverrideRepository(databasePath, logger);
            _otpRepository = new OtpRepository(logger);
            EnsureSchemaExists();
            LoadSettings();
        }

        private void EnsureSchemaExists()
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                connection.Open();

                var createTableSql = @"
                    CREATE TABLE IF NOT EXISTS protection_passwords (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        password_hash TEXT NOT NULL,
                        salt TEXT NOT NULL,
                        created_date TEXT NOT NULL DEFAULT (datetime('now')),
                        updated_date TEXT NOT NULL DEFAULT (datetime('now'))
                    );
                ";

                using var command = new SQLiteCommand(createTableSql, connection);
                command.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to create protection_passwords table: {ex.Message}", ex);
            }
        }

        private void LoadSettings()
        {
            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                connection.Open();

                var sql = "SELECT id, password_hash, salt, created_date, updated_date, row_hmac FROM protection_passwords ORDER BY id DESC LIMIT 1";
                using var command = new SQLiteCommand(sql, connection);
                using var reader = command.ExecuteReader();

                if (reader.Read())
                {
                    var id = reader.GetInt32(0);
                    var passwordHash = reader.GetString(1);
                    var salt = reader.GetString(2);
                    string? storedHmac = null;
                    try { storedHmac = reader.IsDBNull(5) ? null : reader.GetString(5); }
                    catch (IndexOutOfRangeException) { /* row_hmac column may not exist yet */ }

                    // Verify HMAC integrity
                    if (_integrityService?.IsIntegrityAvailable == true &&
                        !string.IsNullOrEmpty(storedHmac) &&
                        !_integrityService.VerifyHmac("protection_passwords", id.ToString(), storedHmac, passwordHash, salt))
                    {
                        _logger?.LogWarning("HMAC verification FAILED for protection_passwords — password data tampered");
                        _cachedSettings = new ProtectionSettings(); // Treat as no password set
                    }
                    else
                    {
                        _cachedSettings = new ProtectionSettings
                        {
                            Id = id,
                            PasswordHash = passwordHash,
                            Salt = salt,
                            CreatedDate = DateTime.Parse(reader.GetString(3)),
                            UpdatedDate = DateTime.Parse(reader.GetString(4))
                        };
                    }
                }
                else
                {
                    _cachedSettings = new ProtectionSettings();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load protection settings: {ex.Message}", ex);
                _cachedSettings = new ProtectionSettings();
            }
        }

        /// <summary>
        /// Check if a password is set
        /// </summary>
        public bool IsPasswordSet()
        {
            return _cachedSettings?.IsPasswordSet ?? false;
        }

        /// <summary>
        /// Validate a password against the stored hash
        /// </summary>
        public bool ValidatePassword(string password)
        {
            if (string.IsNullOrEmpty(password))
                return false;

            if (!IsPasswordSet())
            {
                _logger?.LogWarning("No password set, validation failed");
                return false;
            }

            try
            {
                var hash = HashPassword(password, _cachedSettings.Salt);
                var isValid = hash == _cachedSettings.PasswordHash;

                if (isValid)
                {
                    _logger?.LogInfo("Password validation successful");
                }
                else
                {
                    _logger?.LogWarning("Password validation failed");
                }

                return isValid;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Error validating password: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Set a new password
        /// </summary>
        public bool SetPassword(string password, string currentPassword = null)
        {
            if (string.IsNullOrEmpty(password))
            {
                _logger?.LogError("Cannot set empty password");
                return false;
            }

            // If password exists, validate current password
            if (IsPasswordSet() && !string.IsNullOrEmpty(currentPassword))
            {
                if (!ValidatePassword(currentPassword))
                {
                    _logger?.LogWarning("Current password validation failed");
                    return false;
                }
            }

            try
            {
                var salt = GenerateSalt();
                var hash = HashPassword(password, salt);

                using var connection = new SQLiteConnection(_connectionString);
                connection.Open();

                // Delete old passwords
                var deleteSql = "DELETE FROM protection_passwords";
                using (var deleteCommand = new SQLiteCommand(deleteSql, connection))
                {
                    deleteCommand.ExecuteNonQuery();
                }

                // Insert new password
                var insertSql = @"
                    INSERT INTO protection_passwords (password_hash, salt, created_date, updated_date)
                    VALUES (@hash, @salt, @now, @now)
                ";

                using var insertCommand = new SQLiteCommand(insertSql, connection);
                var now = DateTime.Now.ToString("O");
                insertCommand.Parameters.AddWithValue("@hash", hash);
                insertCommand.Parameters.AddWithValue("@salt", salt);
                insertCommand.Parameters.AddWithValue("@now", now);
                insertCommand.ExecuteNonQuery();

                // Compute and store HMAC for the new password row
                if (_integrityService?.IsIntegrityAvailable == true)
                {
                    // Get the auto-incremented id
                    var getIdSql = "SELECT id FROM protection_passwords ORDER BY id DESC LIMIT 1";
                    using var getIdCmd = new SQLiteCommand(getIdSql, connection);
                    var newId = getIdCmd.ExecuteScalar()?.ToString() ?? "0";

                    var hmac = _integrityService.ComputeHmac("protection_passwords", newId, hash, salt);
                    if (!string.IsNullOrEmpty(hmac))
                    {
                        using var hmacCmd = new SQLiteCommand(
                            "UPDATE protection_passwords SET row_hmac = @hmac WHERE id = @id", connection);
                        hmacCmd.Parameters.AddWithValue("@hmac", hmac);
                        hmacCmd.Parameters.AddWithValue("@id", int.Parse(newId));
                        hmacCmd.ExecuteNonQuery();
                    }
                }

                // Reload settings
                LoadSettings();

                _logger?.LogInfo("Password updated successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to set password: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Generate a cryptographically secure salt
        /// </summary>
        private string GenerateSalt()
        {
            var saltBytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(saltBytes);
            }
            return Convert.ToBase64String(saltBytes);
        }

        /// <summary>
        /// Hash a password with PBKDF2
        /// </summary>
        private string HashPassword(string password, string salt)
        {
            var saltBytes = Convert.FromBase64String(salt);
            
            using var pbkdf2 = new Rfc2898DeriveBytes(password, saltBytes, 10000);
            var hashBytes = pbkdf2.GetBytes(32);
            
            return Convert.ToBase64String(hashBytes);
        }

        /// <summary>
        /// Clear the password (for testing or reset)
        /// </summary>
        public bool ClearPassword(string currentPassword)
        {
            if (!IsPasswordSet())
                return true;

            if (!ValidatePassword(currentPassword))
            {
                _logger?.LogWarning("Cannot clear password: current password validation failed");
                return false;
            }

            try
            {
                using var connection = new SQLiteConnection(_connectionString);
                connection.Open();

                var sql = "DELETE FROM protection_passwords";
                using var command = new SQLiteCommand(sql, connection);
                command.ExecuteNonQuery();

                LoadSettings();

                _logger?.LogInfo("Password cleared successfully");
                return true;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to clear password: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Check if an active override exists for the given context
        /// </summary>
        public async Task<ProtectionOverride> GetActiveOverrideAsync(string ruleId, string commandId, string documentId)
        {
            try
            {
                var applicableOverrides = await _overrideRepository.GetApplicableOverridesAsync(ruleId, commandId, documentId);

                // Return the first valid override (most specific scope takes precedence)
                return applicableOverrides
                    .Where(o => o.IsValid())
                    .OrderBy(o => (int)o.Scope) // Global = 0, Rule = 1, etc.
                    .FirstOrDefault();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get active override: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Create a new structured override
        /// </summary>
        public async Task<ProtectionOverride> CreateOverrideAsync(
            string ruleId,
            string commandId,
            OverrideScope scope,
            string approverEmail,
            string approverName,
            string reason,
            TimeSpan? duration = null)
        {
            try
            {
                var protectionOverride = ProtectionOverride.Create(
                    ruleId,
                    commandId,
                    scope,
                    approverEmail,
                    approverName,
                    reason,
                    duration);

                var success = await _overrideRepository.SaveOverrideAsync(protectionOverride);

                if (success)
                {
                    _logger?.LogInfo($"Created override {protectionOverride.OverrideId} for {scope} scope by {approverName}");
                    return protectionOverride;
                }

                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to create override: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Revoke an active override
        /// </summary>
        public async Task<bool> RevokeOverrideAsync(string overrideId, string revokedBy)
        {
            try
            {
                var success = await _overrideRepository.RevokeOverrideAsync(overrideId, revokedBy);

                if (success)
                {
                    _logger?.LogInfo($"Revoked override {overrideId} by {revokedBy}");
                }

                return success;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to revoke override: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Get all active overrides
        /// </summary>
        public async Task<List<ProtectionOverride>> GetAllActiveOverridesAsync()
        {
            try
            {
                return await _overrideRepository.GetActiveOverridesAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to get all active overrides: {ex.Message}", ex);
                return new List<ProtectionOverride>();
            }
        }

        /// <summary>
        /// Cleanup expired overrides
        /// </summary>
        public async Task<int> CleanupExpiredOverridesAsync()
        {
            try
            {
                return await _overrideRepository.CleanupExpiredOverridesAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to cleanup expired overrides: {ex.Message}", ex);
                return 0;
            }
        }

        #region OTP Management

        /// <summary>
        /// Generate a new OTP code for temporary override access
        /// </summary>
        public async Task<OtpCode> GenerateOtpAsync(
            OverrideScope scope,
            string generatedBy,
            string reason,
            TimeSpan validityDuration,
            string ruleId = null,
            string commandId = null,
            int maxUses = 1)
        {
            try
            {
                var otp = OtpCode.Generate(scope, generatedBy, reason, validityDuration, ruleId, commandId, maxUses);
                var saved = await _otpRepository.SaveOtpAsync(otp);

                if (saved)
                {
                    _logger?.LogInfo($"OTP generated: {otp.Code} by {generatedBy} (Expires: {otp.ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC, Scope: {scope})");
                    return otp;
                }

                _logger?.LogError("Failed to save generated OTP");
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to generate OTP: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Validate an OTP code and mark it as used
        /// Returns true if OTP is valid and applicable to the context
        /// </summary>
        public async Task<bool> ValidateOtpAsync(string code, string userName, string ruleId, string commandId)
        {
            try
            {
                var otp = await _otpRepository.ValidateAndConsumeOtpAsync(code, userName, ruleId, commandId);

                if (otp != null)
                {
                    _logger?.LogInfo($"OTP validated: {code} by {userName} for Rule:{ruleId ?? "any"}, Command:{commandId ?? "any"}");
                    return true;
                }

                _logger?.LogWarning($"OTP validation failed: {code}");
                return false;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to validate OTP: {ex.Message}", ex);
                return false;
            }
        }

        /// <summary>
        /// Validate either password or OTP for override access
        /// Supports both permanent password and temporary OTP codes
        /// </summary>
        public async Task<bool> ValidateOverrideCredentialAsync(string credential, string userName, string ruleId, string commandId)
        {
            // Try OTP first (6 digits)
            if (credential?.Length == 6 && credential.All(char.IsDigit))
            {
                var otpValid = await ValidateOtpAsync(credential, userName, ruleId, commandId);
                if (otpValid)
                {
                    _logger?.LogInfo($"Override granted via OTP: {credential} by {userName}");
                    return true;
                }
            }

            // Fall back to password validation
            var passwordValid = ValidatePassword(credential);
            if (passwordValid)
            {
                _logger?.LogInfo($"Override granted via password by {userName}");
                return true;
            }

            _logger?.LogWarning($"Override credential validation failed for {userName}");
            return false;
        }

        /// <summary>
        /// Get all active OTP codes
        /// </summary>
        public async Task<List<OtpCode>> GetActiveOtpsAsync()
        {
            try
            {
                return await _otpRepository.GetActiveOtpsAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to retrieve active OTPs: {ex.Message}", ex);
                return new List<OtpCode>();
            }
        }

        /// <summary>
        /// Get OTP statistics
        /// </summary>
        public async Task<OtpStatistics> GetOtpStatisticsAsync()
        {
            try
            {
                return await _otpRepository.GetStatisticsAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to retrieve OTP statistics: {ex.Message}", ex);
                return new OtpStatistics();
            }
        }

        /// <summary>
        /// Cleanup old OTPs (expired and used)
        /// </summary>
        public async Task<int> CleanupOldOtpsAsync(TimeSpan retentionPeriod)
        {
            try
            {
                return await _otpRepository.CleanupOldOtpsAsync(retentionPeriod);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to cleanup old OTPs: {ex.Message}", ex);
                return 0;
            }
        }

        #endregion
    }
}
