using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BIManage.Infrastructure.Auth.Models
{
    #region Request Models

    /// <summary>
    /// POST /api/v1/tenant/company-auth/login
    /// </summary>
    public class LoginRequest
    {
        [JsonPropertyName("email")]
        public string Email { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;
    }

    /// <summary>
    /// POST /api/v1/tenant/device/auth/register-device
    /// </summary>
    public class RegisterDeviceRequest
    {
        [JsonPropertyName("licenseKey")]
        public string LicenseKey { get; set; } = string.Empty;

        [JsonPropertyName("machineId")]
        public string MachineId { get; set; } = string.Empty;

        [JsonPropertyName("machineName")]
        public string MachineName { get; set; } = string.Empty;

        [JsonPropertyName("sid")]
        public string? Sid { get; set; }

        [JsonPropertyName("osVersion")]
        public string? OsVersion { get; set; }
    }

    /// <summary>
    /// POST /api/v1/tenant/device/auth/validate-device
    /// </summary>
    public class ValidateDeviceRequest
    {
        [JsonPropertyName("machineId")]
        public string MachineId { get; set; } = string.Empty;
    }

    /// <summary>
    /// POST /api/v1/tenant/device/auth/refresh
    /// </summary>
    public class RefreshTokenRequest
    {
        [JsonPropertyName("refreshToken")]
        public string RefreshToken { get; set; } = string.Empty;
    }

    /// <summary>
    /// POST /api/v1/tenant/device/auth/admin-logout
    /// Per Swagger AdminLogoutDto: required sessionId (uuid), additionalProperties: false.
    /// </summary>
    public class AdminLogoutRequest
    {
        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;
    }

    /// <summary>
    /// POST /api/v1/tenant/company-auth/select-company
    /// Exchanges the post-login session token for a company-scoped token whose JWT
    /// payload includes the `profileId` claim. Without this step, the server's
    /// protection-write endpoints reject every PUT/POST with
    /// "Token does not contain a valid profile ID".
    /// </summary>
    public class SelectCompanyRequest
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; } = string.Empty;

        [JsonPropertyName("companyId")]
        public string CompanyId { get; set; } = string.Empty;
    }

    /// <summary>
    /// POST /api/v1/tenant/device/auth/admin-login
    /// Per Swagger: { machineId, sessionId (uuid), username, password }
    /// </summary>
    public class AdminLoginRequest
    {
        [JsonPropertyName("machineId")]
        public string MachineId { get; set; } = string.Empty;

        [JsonPropertyName("sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("username")]
        public string Username { get; set; } = string.Empty;

        [JsonPropertyName("password")]
        public string Password { get; set; } = string.Empty;
    }

    #endregion

    #region Response Models

    /// <summary>
    /// Response from /api/v1/tenant/company-auth/login and /api/v1/tenant/device/auth/admin-login
    /// </summary>
    public class LoginResponse
    {
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("tokenExpiry")]
        public DateTime? TokenExpiry { get; set; }

        [JsonPropertyName("companies")]
        public List<CompanyInfo>? Companies { get; set; }

        [JsonPropertyName("user")]
        public UserInfo? User { get; set; }

        // Top-level fields from device auth admin-login response
        [JsonPropertyName("roleName")]
        public string? RoleName { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("companyId")]
        public string? CompanyId { get; set; }

        [JsonPropertyName("companyName")]
        public string? CompanyName { get; set; }

        [JsonPropertyName("profileId")]
        public string? ProfileId { get; set; }
    }

    public class CompanyInfo
    {
        [JsonPropertyName("companyId")]
        public string? CompanyId { get; set; }

        [JsonPropertyName("companyName")]
        public string? CompanyName { get; set; }

        // Tenant company-auth/login returns the user's role per company in the
        // `companies[]` array. This is the only place roleName appears at login
        // time (before select-company is called).
        [JsonPropertyName("roleId")]
        public string? RoleId { get; set; }

        [JsonPropertyName("roleName")]
        public string? RoleName { get; set; }

        // Permissions is a 64-bit bitmask the API sometimes serializes as a JSON
        // number (e.g. 9223372036854775808) and sometimes as a quoted string. Using
        // JsonElement lets the deserializer accept either shape without throwing
        // "The JSON value could not be converted to System.String" — the plugin
        // doesn't read this value, it's kept only so the deserializer doesn't drop
        // it on round-trip.
        [JsonPropertyName("permissions")]
        public System.Text.Json.JsonElement? Permissions { get; set; }
    }

    /// <summary>
    /// User info returned inside the `user` object on the
    /// POST /api/v1/tenant/company-auth/select-company response. Carries the
    /// company-scoped identity (profileId, roleName, companyId, etc.) — the
    /// authoritative source for role-based UI gating.
    /// </summary>
    public class UserInfo
    {
        [JsonPropertyName("userId")]
        public string? UserId { get; set; }

        [JsonPropertyName("email")]
        public string? Email { get; set; }

        [JsonPropertyName("username")]
        public string? Username { get; set; }

        [JsonPropertyName("role")]
        public string? Role { get; set; }

        // Fields actually returned by select-company per the API spec.
        [JsonPropertyName("profileId")]
        public string? ProfileId { get; set; }

        [JsonPropertyName("accessId")]
        public string? AccessId { get; set; }

        [JsonPropertyName("firstName")]
        public string? FirstName { get; set; }

        [JsonPropertyName("lastName")]
        public string? LastName { get; set; }

        [JsonPropertyName("fullName")]
        public string? FullName { get; set; }

        [JsonPropertyName("companyId")]
        public string? CompanyId { get; set; }

        [JsonPropertyName("companyName")]
        public string? CompanyName { get; set; }

        [JsonPropertyName("roleId")]
        public string? RoleId { get; set; }

        [JsonPropertyName("roleName")]
        public string? RoleName { get; set; }
    }

    /// <summary>
    /// Response from /api/v1/tenant/device/auth/register-device and /api/v1/tenant/device/auth/validate-device
    /// </summary>
    public class DeviceAuthResponse
    {
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("machineId")]
        public string? MachineId { get; set; }

        [JsonPropertyName("companyId")]
        public string? CompanyId { get; set; }

        // The device-registration and validate-device endpoints include the tenant's
        // company name in the response. Captured so device-only users (who never go
        // through admin-login) still see the company in Device Status — see
        // AuthTokenManager.SetTokensFromDevice.
        [JsonPropertyName("companyName")]
        public string? CompanyName { get; set; }

        [JsonPropertyName("tokenExpiry")]
        public DateTime? TokenExpiry { get; set; }
    }

    /// <summary>
    /// Response from /api/v1/tenant/device/auth/refresh
    /// </summary>
    public class RefreshTokenResponse
    {
        [JsonPropertyName("accessToken")]
        public string? AccessToken { get; set; }

        [JsonPropertyName("refreshToken")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("tokenExpiry")]
        public DateTime? TokenExpiry { get; set; }

        // Device-refresh response carries companyName too — kept fresh on every refresh
        // so a server-side company rename eventually propagates without requiring an
        // admin-login round-trip.
        [JsonPropertyName("companyName")]
        public string? CompanyName { get; set; }
    }

    /// <summary>
    /// Response from GET /api/v1/tenant/device/auth/license-status
    /// Contains per-module licensing, passive mode flag, and seat information.
    /// </summary>
    public class LicenseStatusResponse
    {
        [JsonPropertyName("status")]
        public string? Status { get; set; }

        [JsonPropertyName("isPassiveMode")]
        public bool IsPassiveMode { get; set; }

        [JsonPropertyName("enabledModules")]
        public List<int>? EnabledModules { get; set; }

        [JsonPropertyName("expiration")]
        public DateTime? Expiration { get; set; }

        [JsonPropertyName("companyId")]
        public string? CompanyId { get; set; }

        [JsonPropertyName("companyName")]
        public string? CompanyName { get; set; }

        [JsonPropertyName("totalSeats")]
        public int TotalSeats { get; set; }

        [JsonPropertyName("activeSeats")]
        public int ActiveSeats { get; set; }
    }

    #endregion

    #region Internal Models

    /// <summary>
    /// Internal token information held in memory
    /// </summary>
    public class TokenInfo
    {
        public string AccessToken { get; set; } = string.Empty;
        public string RefreshToken { get; set; } = string.Empty;
        public DateTime ExpiresAt { get; set; }

        public bool IsExpired => DateTime.UtcNow >= ExpiresAt;
        public bool IsNearExpiry => DateTime.UtcNow >= ExpiresAt.AddSeconds(-60);
    }

    #endregion
}
