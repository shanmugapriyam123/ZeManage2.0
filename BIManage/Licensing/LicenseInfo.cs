using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BIManage.Licensing
{
    /// <summary>
    /// Complete license state returned by the server API and cached locally.
    /// Contains module permissions, passive mode flag, seat counts, and expiration.
    /// </summary>
    public class LicenseInfo
    {
        /// <summary>License validity status (Valid, Expired, Revoked, GracePeriod, etc.).</summary>
        [JsonPropertyName("status")]
        public LicenseStatus Status { get; set; } = LicenseStatus.Unknown;

        /// <summary>Server-decided: true when active seats exceed the license seat limit.</summary>
        [JsonPropertyName("isPassiveMode")]
        public bool IsPassiveMode { get; set; }

        /// <summary>List of module IDs the company has purchased/enabled.</summary>
        [JsonPropertyName("enabledModules")]
        public List<int> EnabledModules { get; set; } = new List<int>();

        /// <summary>License expiration date (UTC). Null if perpetual or unknown.</summary>
        [JsonPropertyName("expiration")]
        public DateTime? Expiration { get; set; }

        /// <summary>Company identifier for tenant isolation.</summary>
        [JsonPropertyName("companyId")]
        public string? CompanyId { get; set; }

        /// <summary>Company display name.</summary>
        [JsonPropertyName("companyName")]
        public string? CompanyName { get; set; }

        /// <summary>Total licensed seat count for this company.</summary>
        [JsonPropertyName("totalSeats")]
        public int TotalSeats { get; set; }

        /// <summary>Currently active seats (devices with active sessions).</summary>
        [JsonPropertyName("activeSeats")]
        public int ActiveSeats { get; set; }

        /// <summary>Timestamp of when this license info was fetched from the server.</summary>
        [JsonIgnore]
        public DateTime FetchedAt { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Computed operational mode based on license status and passive flag.
        /// </summary>
        [JsonIgnore]
        public LicenseMode Mode
        {
            get
            {
                if (Status != LicenseStatus.Valid && Status != LicenseStatus.GracePeriod)
                    return LicenseMode.Breached;
                return IsPassiveMode ? LicenseMode.Passive : LicenseMode.Licensed;
            }
        }

        /// <summary>
        /// Checks whether a specific module is enabled under the current license mode.
        /// - Breached: all modules disabled.
        /// - Passive: only Protection + ActivityTracker remain active.
        /// - Licensed: module must be in the EnabledModules list from server.
        /// </summary>
        public bool IsModuleEnabled(LicenseModule module)
        {
            if (Mode == LicenseMode.Breached)
                return false;

            if (Mode == LicenseMode.Passive)
                return module == LicenseModule.Protection || module == LicenseModule.ActivityTracker;

            // Licensed mode — if no modules specified, enable all (server hasn't configured modules yet)
            if (EnabledModules == null || EnabledModules.Count == 0)
                return true;

            return EnabledModules.Contains((int)module);
        }

        /// <summary>
        /// Returns a display-friendly summary of the seat usage.
        /// </summary>
        [JsonIgnore]
        public string SeatUsageDisplay => $"{ActiveSeats} / {TotalSeats} seats active";

        /// <summary>
        /// Creates a default LicenseInfo for when no server response is available.
        /// </summary>
        public static LicenseInfo CreateDefault() => new LicenseInfo
        {
            Status = LicenseStatus.Unknown,
            IsPassiveMode = false,
            EnabledModules = new List<int>(),
            FetchedAt = DateTime.MinValue
        };
    }
}
