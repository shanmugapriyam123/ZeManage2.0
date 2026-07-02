using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.Auth
{
    /// <summary>
    /// Securely stores refresh tokens using Windows DPAPI (Data Protection API).
    /// Tokens are encrypted with CurrentUser scope - only the same Windows user
    /// on the same machine can decrypt them.
    /// </summary>
    public class SecureTokenStorage
    {
        // Serializes all reads/writes to auth.dat and identity.dat. Both files are written
        // by multiple call sites (device validation, admin refresh, SignalR token provider)
        // that can race when the dual-ALC load surfaces concurrent callers — the symptom in
        // User 6's R2024 log was IOException "auth.dat ... being used by another process".
        // One lock is enough: contention is rare and the two files do not co-update.
        private static readonly object _fileLock = new object();

        private readonly string _storagePath;
        private readonly string _identityPath;
        // Separate slot for the admin refresh token. The single-slot design (auth.dat shared
        // between admin/device sessions, routed by an in-memory flag) breaks across process
        // restarts: at startup _isAdminSession defaults to false, so a periodic device
        // validation that races ahead of RestoreAdminSessionFlag overwrites the admin
        // refresh token with the device refresh token. The next /admin-refresh call then
        // 401s because we're sending a device token. Keep the admin refresh token here so
        // device-side writes can never clobber it.
        private readonly string _adminStoragePath;
        private readonly ILogger? _logger;

        public SecureTokenStorage(ILogger? logger = null)
        {
            _logger = logger;

            // Store in %APPDATA%\BIManage\auth.dat
            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BIManage");

            if (!Directory.Exists(appDataPath))
            {
                Directory.CreateDirectory(appDataPath);
            }

            _storagePath = Path.Combine(appDataPath, "auth.dat");
            _identityPath = Path.Combine(appDataPath, "identity.dat");
            _adminStoragePath = Path.Combine(appDataPath, "admin-auth.dat");
            _logger?.LogInfo($"SecureTokenStorage initialized: {_storagePath}");
        }

        /// <summary>
        /// Saves the admin refresh token (issued by /admin-login) to its own DPAPI-encrypted
        /// slot at admin-auth.dat. Kept separate from the device refresh token in auth.dat so
        /// device-side flows (validation, heartbeats, periodic re-auth) cannot overwrite it.
        /// </summary>
        public void SaveAdminRefreshToken(string refreshToken)
        {
            try
            {
                if (string.IsNullOrEmpty(refreshToken))
                {
                    _logger?.LogWarning("Attempted to save empty admin refresh token");
                    return;
                }

                var tokenBytes = Encoding.UTF8.GetBytes(refreshToken);
                var encryptedBytes = ProtectedData.Protect(
                    tokenBytes,
                    null,
                    DataProtectionScope.CurrentUser);

                WriteAllBytesWithRetry(_adminStoragePath, encryptedBytes, "admin refresh token");
                _logger?.LogInfo("Admin refresh token saved securely (DPAPI)");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to save admin refresh token: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Loads and decrypts the stored admin refresh token. Returns null if not present.
        /// </summary>
        public string? LoadAdminRefreshToken()
        {
            try
            {
                byte[] encryptedBytes;
                lock (_fileLock)
                {
                    if (!File.Exists(_adminStoragePath))
                        return null;
                    encryptedBytes = File.ReadAllBytes(_adminStoragePath);
                }

                var tokenBytes = ProtectedData.Unprotect(
                    encryptedBytes,
                    null,
                    DataProtectionScope.CurrentUser);

                return Encoding.UTF8.GetString(tokenBytes);
            }
            catch (CryptographicException ex)
            {
                _logger?.LogError($"Failed to decrypt admin refresh token (user/machine changed?): {ex.Message}", ex);
                ClearAdminRefreshToken();
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load admin refresh token: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Deletes the stored admin refresh token file. Called on logout and on server-side
        /// session downgrade. The device refresh token at auth.dat is untouched.
        /// </summary>
        public void ClearAdminRefreshToken()
        {
            try
            {
                lock (_fileLock)
                {
                    if (File.Exists(_adminStoragePath))
                    {
                        File.Delete(_adminStoragePath);
                        _logger?.LogInfo("Stored admin refresh token cleared");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to clear admin refresh token: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Whether a stored admin refresh token exists on disk.
        /// </summary>
        public bool HasStoredAdminRefreshToken => File.Exists(_adminStoragePath);

        /// <summary>
        /// Saves the refresh token encrypted with DPAPI (CurrentUser scope)
        /// </summary>
        public void SaveRefreshToken(string refreshToken)
        {
            try
            {
                if (string.IsNullOrEmpty(refreshToken))
                {
                    _logger?.LogWarning("Attempted to save empty refresh token");
                    return;
                }

                var tokenBytes = Encoding.UTF8.GetBytes(refreshToken);
                var encryptedBytes = ProtectedData.Protect(
                    tokenBytes,
                    null, // optional entropy
                    DataProtectionScope.CurrentUser);

                WriteAllBytesWithRetry(_storagePath, encryptedBytes, "refresh token");
                _logger?.LogInfo("Refresh token saved securely (DPAPI)");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to save refresh token: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Loads and decrypts the stored refresh token
        /// </summary>
        /// <returns>The decrypted refresh token, or null if not found or decryption fails</returns>
        public string? LoadRefreshToken()
        {
            try
            {
                byte[] encryptedBytes;
                lock (_fileLock)
                {
                    if (!File.Exists(_storagePath))
                    {
                        _logger?.LogInfo("No stored refresh token found");
                        return null;
                    }

                    encryptedBytes = File.ReadAllBytes(_storagePath);
                }

                var tokenBytes = ProtectedData.Unprotect(
                    encryptedBytes,
                    null, // optional entropy
                    DataProtectionScope.CurrentUser);

                var token = Encoding.UTF8.GetString(tokenBytes);
                _logger?.LogInfo("Refresh token loaded from secure storage");
                return token;
            }
            catch (CryptographicException ex)
            {
                _logger?.LogError($"Failed to decrypt refresh token (user/machine changed?): {ex.Message}", ex);
                // Token was encrypted by a different user or on a different machine
                ClearTokens();
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load refresh token: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Deletes the stored refresh token file
        /// </summary>
        public void ClearTokens()
        {
            try
            {
                lock (_fileLock)
                {
                    if (File.Exists(_storagePath))
                    {
                        File.Delete(_storagePath);
                        _logger?.LogInfo("Stored refresh token cleared");
                    }
                    if (File.Exists(_adminStoragePath))
                    {
                        File.Delete(_adminStoragePath);
                        _logger?.LogInfo("Stored admin refresh token cleared");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to clear stored tokens: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Checks if a refresh token is stored
        /// </summary>
        public bool HasStoredToken => File.Exists(_storagePath);

        /// <summary>
        /// Saves user identity data encrypted with DPAPI (CurrentUser scope).
        /// Stored separately from refresh token in identity.dat.
        /// </summary>
        public void SaveUserIdentity(StoredUserIdentity identity)
        {
            try
            {
                if (identity == null) return;

                var json = JsonSerializer.Serialize(identity);
                var bytes = Encoding.UTF8.GetBytes(json);
                var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);

                WriteAllBytesWithRetry(_identityPath, encrypted, "user identity");
                _logger?.LogInfo("User identity saved securely (DPAPI)");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to save user identity: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Loads and decrypts stored user identity data.
        /// </summary>
        public StoredUserIdentity? LoadUserIdentity()
        {
            try
            {
                byte[] encrypted;
                lock (_fileLock)
                {
                    if (!File.Exists(_identityPath)) return null;
                    encrypted = File.ReadAllBytes(_identityPath);
                }

                var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                var json = Encoding.UTF8.GetString(bytes);

                var identity = JsonSerializer.Deserialize<StoredUserIdentity>(json);
                _logger?.LogInfo("User identity loaded from secure storage");
                return identity;
            }
            catch (CryptographicException ex)
            {
                _logger?.LogError($"Failed to decrypt user identity: {ex.Message}", ex);
                ClearUserIdentity();
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load user identity: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// Deletes the stored user identity file.
        /// </summary>
        public void ClearUserIdentity()
        {
            try
            {
                lock (_fileLock)
                {
                    if (File.Exists(_identityPath))
                    {
                        File.Delete(_identityPath);
                        _logger?.LogInfo("Stored user identity cleared");
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to clear user identity: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Persists the company name to stored identity. Used by the device auth flows
        /// (register-device / validate-device / device-refresh) which return companyName
        /// in the response. Without this, a device-only user (never went through admin
        /// login) had no stored identity at all and the Device Status dialog showed an
        /// empty Company row. If no stored identity exists yet, creates a minimal one
        /// holding just the company name.
        /// </summary>
        public void UpdateStoredCompanyName(string? companyName, string? companyId = null)
        {
            try
            {
                if (string.IsNullOrEmpty(companyName)) return;

                var identity = LoadUserIdentity() ?? new StoredUserIdentity();
                bool changed = false;
                if (!string.Equals(identity.CompanyName, companyName, StringComparison.Ordinal))
                {
                    identity.CompanyName = companyName;
                    changed = true;
                }
                if (!string.IsNullOrEmpty(companyId) && !string.Equals(identity.CompanyId, companyId, StringComparison.Ordinal))
                {
                    identity.CompanyId = companyId;
                    changed = true;
                }
                if (changed)
                {
                    SaveUserIdentity(identity);
                    _logger?.LogInfo($"Stored company name updated from device auth response: {companyName}");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to update stored company name: {ex.Message}");
            }
        }

        /// <summary>
        /// Clears the ProfileId from stored identity (used when switching from admin to device session).
        /// Prevents the admin refresh endpoint from being used with a device refresh token.
        /// </summary>
        public void ClearProfileId()
        {
            try
            {
                var identity = LoadUserIdentity();
                if (identity != null && !string.IsNullOrEmpty(identity.ProfileId))
                {
                    identity.ProfileId = null;
                    // Keep IsCompanyAdmin/IsProjectAdmin — they reflect the user's role, not the token type.
                    // ClearProfileId only affects token refresh routing (admin vs device endpoint).
                    SaveUserIdentity(identity);
                    _logger?.LogInfo("Cleared ProfileId from stored identity (token routing reset, role preserved)");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to clear ProfileId: {ex.Message}");
            }
        }

        /// <summary>
        /// Checks if user identity data is stored.
        /// </summary>
        public bool HasStoredIdentity => File.Exists(_identityPath);

        // Bounded retry for transient IOException ("being used by another process") on
        // top of the lock — covers the case where an external process (Explorer, AV
        // scanner, prior crashed Revit) briefly holds the file.
        private void WriteAllBytesWithRetry(string path, byte[] data, string label)
        {
            const int maxAttempts = 3;
            int[] backoffMs = { 50, 100, 200 };

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    lock (_fileLock)
                    {
                        File.WriteAllBytes(path, data);
                    }
                    return;
                }
                catch (IOException ex) when (attempt < maxAttempts)
                {
                    _logger?.LogWarning(
                        $"[SecureTokenStorage] Write retry {attempt}/{maxAttempts} after IOException for {label}: {ex.Message}");
                    System.Threading.Thread.Sleep(backoffMs[attempt - 1]);
                }
            }
        }
    }

    /// <summary>
    /// Serializable user identity for secure storage.
    /// Contains only the fields needed to restore role-based ribbon visibility.
    /// </summary>
    public class StoredUserIdentity
    {
        public string? UserId { get; set; }
        public string? UserName { get; set; }
        public string? Email { get; set; }
        public string? CompanyId { get; set; }
        public string? CompanyName { get; set; }
        public bool IsCompanyAdmin { get; set; }
        public bool IsProjectAdmin { get; set; }
        public string? RoleName { get; set; }
        public string? ProfileId { get; set; }
    }
}
