using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BIManage.Infrastructure.Logging;

namespace BIManage.Licensing
{
    /// <summary>
    /// In-memory + DPAPI-persisted cache for license validation results.
    /// Persists to %APPDATA%\BIManage\license.dat so offline grace period can
    /// restore the last known license state after a restart.
    /// Follows the same DPAPI pattern as SecureTokenStorage.
    /// </summary>
    public class LicenseCache
    {
        private readonly string _cachePath;
        private readonly ILogger? _logger;

        private LicenseInfo _currentInfo;
        private DateTime _lastChecked = DateTime.MinValue;

        public LicenseCache(ILogger? logger = null)
        {
            _logger = logger;
            _currentInfo = LicenseInfo.CreateDefault();

            var appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BIManage");

            if (!Directory.Exists(appDataPath))
                Directory.CreateDirectory(appDataPath);

            _cachePath = Path.Combine(appDataPath, "license.dat");
        }

        /// <summary>Current license status.</summary>
        public LicenseStatus Status => _currentInfo.Status;

        /// <summary>Current operational mode (Licensed/Passive/Breached).</summary>
        public LicenseMode Mode => _currentInfo.Mode;

        /// <summary>When the license was last checked (in-memory).</summary>
        public DateTime LastChecked => _lastChecked;

        /// <summary>Full license info from last successful server response.</summary>
        public LicenseInfo CurrentLicenseInfo => _currentInfo;

        /// <summary>
        /// Updates the in-memory cache with a fresh server response and persists to disk.
        /// </summary>
        public void SetLicenseInfo(LicenseInfo info)
        {
            if (info == null) return;
            _currentInfo = info;
            _lastChecked = DateTime.UtcNow;
            PersistToDisk(info);
        }

        /// <summary>
        /// Updates only the license status (for grace period transitions without a server response).
        /// </summary>
        public void SetStatus(LicenseStatus status)
        {
            _currentInfo.Status = status;
            _lastChecked = DateTime.UtcNow;
        }

        /// <summary>
        /// Checks if a specific module is enabled under the current license.
        /// </summary>
        public bool IsModuleEnabled(LicenseModule module)
        {
            return _currentInfo.IsModuleEnabled(module);
        }

        /// <summary>
        /// Loads the last persisted license info from DPAPI-encrypted file.
        /// Returns null if no cache exists or decryption fails.
        /// </summary>
        public LicenseInfo? LoadPersistedCache()
        {
            try
            {
                if (!File.Exists(_cachePath))
                {
                    _logger?.LogDebug("No persisted license cache found");
                    return null;
                }

                var encryptedBytes = File.ReadAllBytes(_cachePath);
                var bytes = ProtectedData.Unprotect(
                    encryptedBytes,
                    null,
                    DataProtectionScope.CurrentUser);

                var json = Encoding.UTF8.GetString(bytes);
                var info = JsonSerializer.Deserialize<LicenseInfo>(json, GetJsonOptions());

                if (info != null)
                {
                    _logger?.LogInfo($"License cache loaded (status={info.Status}, mode={info.Mode}, fetched={info.FetchedAt:u})");
                }

                return info;
            }
            catch (CryptographicException ex)
            {
                _logger?.LogWarning($"Failed to decrypt license cache (user/machine changed?): {ex.Message}");
                ClearCache();
                return null;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to load license cache: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Deletes the persisted license cache file.
        /// </summary>
        public void ClearCache()
        {
            try
            {
                if (File.Exists(_cachePath))
                {
                    File.Delete(_cachePath);
                    _logger?.LogInfo("License cache cleared");
                }

                _currentInfo = LicenseInfo.CreateDefault();
                _lastChecked = DateTime.MinValue;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to clear license cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Saves the license info encrypted with DPAPI (CurrentUser scope).
        /// </summary>
        private void PersistToDisk(LicenseInfo info)
        {
            try
            {
                var json = JsonSerializer.Serialize(info, GetJsonOptions());
                var bytes = Encoding.UTF8.GetBytes(json);
                var encrypted = ProtectedData.Protect(
                    bytes,
                    null,
                    DataProtectionScope.CurrentUser);

                File.WriteAllBytes(_cachePath, encrypted);
                _logger?.LogDebug("License cache persisted to disk");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"Failed to persist license cache: {ex.Message}");
            }
        }

        private static JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                WriteIndented = false
            };
        }
    }
}
