using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;

namespace BIManage.Infrastructure.Security
{
    /// <summary>
    /// Provides AES-256 encryption for sensitive database columns (OTP codes, credentials, etc.)
    /// Uses machine-specific key derivation for hardware-bound encryption
    ///
    /// Security Model:
    /// - Key derived from: Machine ID (motherboard) + Current User SID + Application Salt
    /// - Algorithm: AES-256-CBC with random IV per encryption
    /// - Protection: Data encrypted on this machine can only be decrypted on same machine by same user
    ///
    /// IMPORTANT: If hardware changes (motherboard replacement), encrypted data becomes unrecoverable.
    /// Consider implementing key backup/escrow for production scenarios.
    /// </summary>
    public static class ColumnEncryption
    {
        private const string APPLICATION_SALT = "BIManageRevit_v1.0_ColumnEncryption";
        private const int KEY_SIZE_BITS = 256;
        private const int KEY_SIZE_BYTES = KEY_SIZE_BITS / 8; // 32 bytes
        private const int IV_SIZE_BYTES = 16; // AES block size
        private const int PBKDF2_ITERATIONS = 100000; // OWASP recommended minimum

        // Cache for machine-specific key (computed once per session)
        private static byte[]? _cachedEncryptionKey;
        private static readonly object _keyLock = new object();

        /// <summary>
        /// Encrypts plaintext using machine-bound AES-256 encryption
        /// </summary>
        /// <param name="plaintext">Text to encrypt</param>
        /// <returns>Base64-encoded string: [IV:16 bytes][Ciphertext:variable length]</returns>
        /// <exception cref="ArgumentNullException">If plaintext is null</exception>
        /// <exception cref="CryptographicException">If encryption fails</exception>
        public static string Encrypt(string plaintext)
        {
            if (plaintext == null)
                throw new ArgumentNullException(nameof(plaintext));

            if (string.IsNullOrEmpty(plaintext))
                return string.Empty;

            try
            {
                using (var aes = Aes.Create())
                {
                    aes.KeySize = KEY_SIZE_BITS;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = GetEncryptionKey();
                    aes.GenerateIV(); // Random IV for each encryption

                    using (var encryptor = aes.CreateEncryptor())
                    using (var ms = new MemoryStream())
                    {
                        // Write IV first (needed for decryption)
                        ms.Write(aes.IV, 0, IV_SIZE_BYTES);

                        // Encrypt plaintext
                        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
                        using (var writer = new StreamWriter(cs, Encoding.UTF8))
                        {
                            writer.Write(plaintext);
                        }

                        // Return Base64-encoded [IV + Ciphertext]
                        return Convert.ToBase64String(ms.ToArray());
                    }
                }
            }
            catch (Exception ex)
            {
                throw new CryptographicException($"Encryption failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Decrypts ciphertext encrypted with Encrypt() method
        /// </summary>
        /// <param name="ciphertext">Base64-encoded [IV + Ciphertext]</param>
        /// <returns>Original plaintext</returns>
        /// <exception cref="ArgumentNullException">If ciphertext is null</exception>
        /// <exception cref="CryptographicException">If decryption fails (wrong machine, corrupted data, etc.)</exception>
        public static string Decrypt(string ciphertext)
        {
            if (ciphertext == null)
                throw new ArgumentNullException(nameof(ciphertext));

            if (string.IsNullOrEmpty(ciphertext))
                return string.Empty;

            try
            {
                var encryptedBytes = Convert.FromBase64String(ciphertext);

                if (encryptedBytes.Length < IV_SIZE_BYTES)
                    throw new CryptographicException("Invalid ciphertext: too short to contain IV");

                using (var aes = Aes.Create())
                {
                    aes.KeySize = KEY_SIZE_BITS;
                    aes.Mode = CipherMode.CBC;
                    aes.Padding = PaddingMode.PKCS7;
                    aes.Key = GetEncryptionKey();

                    // Extract IV from beginning of encrypted data
                    var iv = new byte[IV_SIZE_BYTES];
                    Array.Copy(encryptedBytes, 0, iv, 0, IV_SIZE_BYTES);
                    aes.IV = iv;

                    // Decrypt remaining bytes
                    using (var decryptor = aes.CreateDecryptor())
                    using (var ms = new MemoryStream(encryptedBytes, IV_SIZE_BYTES, encryptedBytes.Length - IV_SIZE_BYTES))
                    using (var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read))
                    using (var reader = new StreamReader(cs, Encoding.UTF8))
                    {
                        return reader.ReadToEnd();
                    }
                }
            }
            catch (FormatException ex)
            {
                throw new CryptographicException($"Decryption failed: Invalid Base64 encoding: {ex.Message}", ex);
            }
            catch (CryptographicException)
            {
                throw; // Re-throw cryptographic exceptions as-is
            }
            catch (Exception ex)
            {
                throw new CryptographicException($"Decryption failed: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Gets or derives the machine-specific encryption key
        /// Key is cached for performance (derivation is computationally expensive)
        /// </summary>
        private static byte[] GetEncryptionKey()
        {
            if (_cachedEncryptionKey != null)
                return _cachedEncryptionKey;

            lock (_keyLock)
            {
                // Double-check after acquiring lock
                if (_cachedEncryptionKey != null)
                    return _cachedEncryptionKey;

                // Derive key from machine identity
                var machineId = GetMachineIdentifier();
                var userSid = GetCurrentUserSid();
                var keyMaterial = $"{machineId}|{userSid}|{APPLICATION_SALT}";

                // Use PBKDF2 to derive strong encryption key
                using (var pbkdf2 = new Rfc2898DeriveBytes(
                    keyMaterial,
                    Encoding.UTF8.GetBytes(APPLICATION_SALT),
                    PBKDF2_ITERATIONS,
                    HashAlgorithmName.SHA256))
                {
                    _cachedEncryptionKey = pbkdf2.GetBytes(KEY_SIZE_BYTES);
                    return _cachedEncryptionKey;
                }
            }
        }

        /// <summary>
        /// Gets a unique machine identifier (motherboard serial number)
        /// Falls back to MAC address if motherboard serial unavailable
        /// </summary>
        private static string GetMachineIdentifier()
        {
            try
            {
                // Try motherboard serial number (most stable hardware identifier)
                using (var searcher = new ManagementObjectSearcher("SELECT SerialNumber FROM Win32_BaseBoard"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        var serial = obj["SerialNumber"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(serial) && serial != "None")
                            return serial;
                    }
                }

                // Fallback: MAC address of first network adapter
                using (var searcher = new ManagementObjectSearcher("SELECT MACAddress FROM Win32_NetworkAdapter WHERE MACAddress IS NOT NULL"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        var mac = obj["MACAddress"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(mac))
                            return mac;
                    }
                }

                // Last resort: CPU ID
                using (var searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor"))
                {
                    foreach (var obj in searcher.Get())
                    {
                        var cpuId = obj["ProcessorId"]?.ToString();
                        if (!string.IsNullOrWhiteSpace(cpuId))
                            return cpuId;
                    }
                }
            }
            catch
            {
                // If WMI fails, fall back to machine name (less secure but better than nothing)
            }

            return Environment.MachineName;
        }

        /// <summary>
        /// Gets current Windows user SID (Security Identifier)
        /// Ensures data can only be decrypted by the same user who encrypted it
        /// </summary>
        private static string GetCurrentUserSid()
        {
            try
            {
                return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                    ?? Environment.UserName;
            }
            catch
            {
                return Environment.UserName;
            }
        }

        /// <summary>
        /// Clears the cached encryption key (force re-derivation on next use)
        /// Useful for testing or security-sensitive operations
        /// </summary>
        public static void ClearKeyCache()
        {
            lock (_keyLock)
            {
                if (_cachedEncryptionKey != null)
                {
                    Array.Clear(_cachedEncryptionKey, 0, _cachedEncryptionKey.Length);
                    _cachedEncryptionKey = null;
                }
            }
        }

        /// <summary>
        /// Tests if encryption/decryption is working correctly
        /// </summary>
        /// <returns>True if roundtrip encryption works</returns>
        public static bool TestEncryption()
        {
            try
            {
                const string testData = "Test123!@#$%^&*()_+-=[]{}|;:',.<>?/~`";
                var encrypted = Encrypt(testData);
                var decrypted = Decrypt(encrypted);
                return testData == decrypted;
            }
            catch
            {
                return false;
            }
        }
    }
}
