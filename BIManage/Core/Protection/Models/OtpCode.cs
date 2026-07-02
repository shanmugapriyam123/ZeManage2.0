using System;

namespace BIManage.Core.Protection.Models
{
    /// <summary>
    /// Represents a one-time password (OTP) code for temporary override access
    /// Time-bound, single-use codes with metadata for audit trail
    /// </summary>
    public class OtpCode
    {
        /// <summary>
        /// Unique identifier for this OTP
        /// </summary>
        public string OtpId { get; set; } = string.Empty;

        /// <summary>
        /// The actual OTP code (6-digit numeric)
        /// </summary>
        public string Code { get; set; } = string.Empty;

        /// <summary>
        /// When this OTP was generated (UTC)
        /// </summary>
        public DateTime GeneratedAt { get; set; }

        /// <summary>
        /// When this OTP expires (UTC)
        /// </summary>
        public DateTime ExpiresAt { get; set; }

        /// <summary>
        /// Whether this OTP has been used
        /// </summary>
        public bool IsUsed { get; set; }

        /// <summary>
        /// Whether this OTP has expired (computed based on ExpiresAt)
        /// </summary>
        public bool IsExpired
        {
            get => DateTime.UtcNow > ExpiresAt;
            set { /* Setter for database mapping, actual value is computed */ }
        }

        /// <summary>
        /// Project ID this OTP is associated with (null for global/all projects)
        /// </summary>
        public string ProjectId { get; set; } = string.Empty;

        /// <summary>
        /// When this OTP was used (UTC), null if not used
        /// </summary>
        public DateTime? UsedAt { get; set; }

        /// <summary>
        /// User who used this OTP
        /// </summary>
        public string UsedBy { get; set; } = string.Empty;

        /// <summary>
        /// Rule ID this OTP grants access to (null for global)
        /// </summary>
        public string RuleId { get; set; } = string.Empty;

        /// <summary>
        /// Command ID this OTP grants access to (null for any command)
        /// </summary>
        public string CommandId { get; set; } = string.Empty;

        /// <summary>
        /// Scope of this OTP override
        /// </summary>
        public OverrideScope Scope { get; set; }

        /// <summary>
        /// Email of the administrator who generated this OTP
        /// </summary>
        public string GeneratedBy { get; set; } = string.Empty;

        /// <summary>
        /// Reason for generating this OTP
        /// </summary>
        public string Reason { get; set; } = string.Empty;

        /// <summary>
        /// Maximum number of times this OTP can be used (default: 1)
        /// </summary>
        public int MaxUses { get; set; } = 1;

        /// <summary>
        /// Current use count
        /// </summary>
        public int UseCount { get; set; }

        /// <summary>
        /// Check if OTP is valid for use
        /// </summary>
        public bool IsValid()
        {
            return !IsUsed
                   && DateTime.UtcNow < ExpiresAt
                   && UseCount < MaxUses;
        }

        /// <summary>
        /// Check if OTP applies to specific rule/command context
        /// </summary>
        public bool AppliesTo(string ruleId, string commandId)
        {
            return Scope switch
            {
                OverrideScope.Global => true,
                OverrideScope.Rule => RuleId == ruleId,
                OverrideScope.Command => CommandId == commandId,
                OverrideScope.RuleAndCommand => RuleId == ruleId && CommandId == commandId,
                _ => false
            };
        }

        /// <summary>
        /// Generate a new OTP code with specified parameters
        /// </summary>
        public static OtpCode Generate(
            OverrideScope scope,
            string generatedBy,
            string reason,
            TimeSpan validityDuration,
            string ruleId = null,
            string commandId = null,
            int maxUses = 1)
        {
            var code = GenerateSecureCode();
            var now = DateTime.UtcNow;

            return new OtpCode
            {
                OtpId = Guid.NewGuid().ToString(),
                Code = code,
                GeneratedAt = now,
                ExpiresAt = now.Add(validityDuration),
                IsUsed = false,
                UsedAt = null,
                UsedBy = string.Empty,
                RuleId = ruleId,
                CommandId = commandId,
                Scope = scope,
                GeneratedBy = generatedBy,
                Reason = reason,
                MaxUses = maxUses,
                UseCount = 0
            };
        }

        /// <summary>
        /// Generate a cryptographically secure 6-digit OTP code
        /// Uses System.Security.Cryptography.RandomNumberGenerator for true randomness
        /// </summary>
        private static string GenerateSecureCode()
        {
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                var bytes = new byte[4];
                rng.GetBytes(bytes);

                // Convert to integer and take modulo 1000000 to get 6 digits
                var number = Math.Abs(BitConverter.ToInt32(bytes, 0)) % 1000000;

                // Format with leading zeros
                return number.ToString("D6");
            }
        }

        /// <summary>
        /// Mark this OTP as used
        /// </summary>
        public void MarkAsUsed(string userName)
        {
            UseCount++;
            UsedAt = DateTime.UtcNow;
            UsedBy = userName;

            if (UseCount >= MaxUses)
            {
                IsUsed = true;
            }
        }

        public override string ToString()
        {
            var status = IsValid() ? "Valid" : "Invalid";
            var scopeDesc = Scope switch
            {
                OverrideScope.Global => "Global",
                OverrideScope.Rule => $"Rule: {RuleId}",
                OverrideScope.Command => $"Command: {CommandId}",
                OverrideScope.RuleAndCommand => $"Rule: {RuleId}, Command: {CommandId}",
                _ => "Unknown"
            };

            return $"OTP {Code} ({status}) - {scopeDesc} - Expires: {ExpiresAt:yyyy-MM-dd HH:mm:ss} UTC - Uses: {UseCount}/{MaxUses}";
        }
    }
}
