using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using BIManage.Infrastructure.Auth.Models;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.Network;

namespace BIManage.Infrastructure.Auth
{
    /// <summary>
    /// HTTP client for the 5 authentication API endpoints.
    /// Follows the existing RevitApiService pattern (SSL bypass, System.Text.Json, ILogger).
    /// </summary>
    public class AuthApiService : IDisposable
    {
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly ILogger? _logger;
        private bool _disposed;

        public AuthApiService(string baseUrl, ILogger? logger = null, SslValidationPolicy? sslPolicy = null)
        {
            _baseUrl = baseUrl?.TrimEnd('/') ?? throw new ArgumentNullException(nameof(baseUrl));
            _logger = logger;

            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = sslPolicy != null
                    ? sslPolicy.Validate
                    : (message, cert, chain, errors) => true
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            _logger?.LogInfo($"AuthApiService initialized with base URL: {_baseUrl}");
        }

        /// <summary>
        /// POST /api/v1/tenant/company-auth/login
        /// Authenticates a user with email and password
        /// </summary>
        public async Task<LoginResponse?> LoginAsync(string email, string password)
        {
            try
            {
                var url = $"{_baseUrl}/api/v1/tenant/company-auth/login";
                _logger?.LogInfo($"Attempting company login for: {email}");

                var request = new LoginRequest { Email = email, Password = password };
                var response = await PostAsync<LoginRequest, LoginResponse>(url, request);

                if (response?.AccessToken != null)
                {
                    _logger?.LogInfo($"Login successful for: {email}");
                }
                else
                {
                    _logger?.LogWarning($"Login returned no access token for: {email}");
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Login failed for {email}: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// POST /api/v1/tenant/company-auth/select-company
        /// Exchanges the post-login session token for a company-scoped token whose
        /// JWT carries the `profileId` claim. This step is mandatory: protection
        /// writes (events / commands / rules) reject the post-login token with
        /// "Token does not contain a valid profile ID" and only accept the
        /// post-select-company token.
        /// </summary>
        public async Task<LoginResponse?> SelectCompanyAsync(string sessionAccessToken, string companyId)
        {
            try
            {
                var url = $"{_baseUrl}/api/v1/tenant/company-auth/select-company";
                _logger?.LogInfo($"Selecting company: {companyId}");

                var request = new SelectCompanyRequest
                {
                    AccessToken = sessionAccessToken,
                    CompanyId = companyId
                };
                var response = await PostAsync<SelectCompanyRequest, LoginResponse>(url, request);

                if (response?.AccessToken != null)
                {
                    var hasProfile = !string.IsNullOrWhiteSpace(response.User?.UserId)
                                     || !string.IsNullOrWhiteSpace(response.ProfileId);
                    _logger?.LogInfo($"Company selected: {companyId} (token has profileId: {hasProfile})");
                }
                else
                {
                    _logger?.LogWarning($"Select-company returned no access token for companyId: {companyId}");
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Select-company failed for {companyId}: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// POST /api/v1/tenant/device/auth/register-device
        /// Registers a device with license key and machine ID
        /// </summary>
        public async Task<DeviceAuthResponse?> RegisterDeviceAsync(string licenseKey, string machineId)
        {
            try
            {
                var url = $"{_baseUrl}/api/v1/tenant/device/auth/register-device";
                _logger?.LogInfo($"Registering device: {machineId}");

                string? sid = null;
                try { sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value; } catch { }

                var request = new RegisterDeviceRequest
                {
                    LicenseKey = licenseKey,
                    MachineId = machineId,
                    MachineName = Environment.MachineName,
                    Sid = sid,
                    OsVersion = Environment.OSVersion.VersionString
                };
                var response = await PostAsync<RegisterDeviceRequest, DeviceAuthResponse>(url, request);

                if (response?.AccessToken != null)
                {
                    _logger?.LogInfo($"Device registered successfully: {machineId}");
                }
                else
                {
                    _logger?.LogWarning($"Device registration returned no access token: {machineId}");
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Device registration failed for {machineId}: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// POST /api/v1/tenant/device/auth/validate-device
        /// Validates an already-registered device by machine ID
        /// </summary>
        public async Task<DeviceAuthResponse?> ValidateDeviceAsync(string machineId)
        {
            try
            {
                var url = $"{_baseUrl}/api/v1/tenant/device/auth/validate-device";
                _logger?.LogInfo($"Validating device: {machineId}");

                var request = new ValidateDeviceRequest { MachineId = machineId };
                var response = await PostAsync<ValidateDeviceRequest, DeviceAuthResponse>(url, request);

                if (response?.AccessToken != null)
                {
                    _logger?.LogInfo($"Device validated successfully: {machineId}");
                }
                else
                {
                    _logger?.LogWarning($"Device validation returned no access token: {machineId}");
                }

                return response;
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Device validation failed for {machineId}: {ex.Message}", ex);
                return null;
            }
        }

        /// <summary>
        /// POST /api/v1/tenant/device/auth/refresh
        /// Refreshes device tokens using a device refresh token.
        /// Uses PostRefreshAsync to distinguish server rejections (400/401/403 → throw
        /// TokenRejectedException) from network errors (HttpRequestException propagates).
        /// </summary>
        public async Task<RefreshTokenResponse?> RefreshTokenAsync(string refreshToken)
        {
            var url = $"{_baseUrl}/api/v1/tenant/device/auth/refresh";
            _logger?.LogInfo("Attempting device token refresh");

            var request = new RefreshTokenRequest { RefreshToken = refreshToken };
            var response = await PostRefreshAsync<RefreshTokenRequest, RefreshTokenResponse>(url, request);

            if (response?.AccessToken != null)
            {
                _logger?.LogInfo("Device token refresh successful");
            }
            else
            {
                _logger?.LogWarning("Device token refresh returned no access token");
            }

            return response;
        }

        /// <summary>
        /// POST /api/v1/tenant/device/auth/admin-refresh
        /// Refreshes admin tokens using an admin refresh token (contains accessId).
        /// Same response shape as device refresh — reuses RefreshTokenResponse.
        /// </summary>
        public async Task<RefreshTokenResponse?> AdminRefreshTokenAsync(string refreshToken)
        {
            var url = $"{_baseUrl}/api/v1/tenant/device/auth/admin-refresh";
            _logger?.LogInfo("Attempting admin token refresh");

            var request = new RefreshTokenRequest { RefreshToken = refreshToken };
            var response = await PostRefreshAsync<RefreshTokenRequest, RefreshTokenResponse>(url, request);

            if (response?.AccessToken != null)
            {
                _logger?.LogInfo("Admin token refresh successful");
            }
            else
            {
                _logger?.LogWarning("Admin token refresh returned no access token");
            }

            return response;
        }

        /// <summary>
        /// POST /api/v1/tenant/device/auth/admin-logout
        /// Revokes the admin session on the server. Called from SignOut so the
        /// server-side session is invalidated alongside the local token wipe.
        /// Fire-and-forget friendly: never throws, returns false on any failure so
        /// sign-out flow can proceed to clear local state regardless.
        /// Per the Swagger AdminLogoutDto schema, the body must be { "sessionId": "<uuid>" }
        /// (additionalProperties: false — extra fields like refreshToken cause 400).
        ///
        /// Requires the admin Bearer token in the Authorization header — the server
        /// rejects unauthenticated calls with 401. Caller must fetch the token from
        /// AuthTokenManager.GetAdminAccessTokenAsync() before invoking, BEFORE clearing
        /// local tokens.
        /// </summary>
        public async Task<bool> AdminLogoutAsync(string sessionId, string? adminAccessToken)
        {
            if (string.IsNullOrEmpty(sessionId))
            {
                _logger?.LogDebug("AdminLogoutAsync: no sessionId provided — skipping server call.");
                return false;
            }

            var url = $"{_baseUrl}/api/v1/tenant/device/auth/admin-logout";
            _logger?.LogInfo($"Notifying server of admin sign-out (POST /admin-logout, sessionId={sessionId}, hasToken={!string.IsNullOrEmpty(adminAccessToken)})");

            try
            {
                var request = new AdminLogoutRequest { SessionId = sessionId };
                var jsonContent = JsonSerializer.Serialize(request, GetJsonOptions());
                using var httpRequest = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrEmpty(adminAccessToken))
                {
                    httpRequest.Headers.Authorization =
                        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", adminAccessToken);
                }

                var response = await _httpClient.SendAsync(httpRequest);
                if (response.IsSuccessStatusCode)
                {
                    _logger?.LogInfo("Admin logout acknowledged by server (session revoked).");
                    return true;
                }

                var responseJson = await response.Content.ReadAsStringAsync();
                _logger?.LogWarning($"Admin logout returned {(int)response.StatusCode}: {responseJson}");
                return false;
            }
            catch (Exception ex)
            {
                // Non-critical: server unreachable, etc. Local sign-out still proceeds.
                _logger?.LogWarning($"Admin logout call failed (non-critical, local sign-out continues): {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// POST /api/v1/tenant/device/auth/admin-login
        /// Authenticates an admin user on a registered device. The body must include
        /// the current Revit session id (per Swagger AdminLoginDto: machineId,
        /// sessionId, username, password) so the server can bind the admin login
        /// to the correct active session.
        /// Throws HttpRequestException with user-friendly messages on API errors.
        /// </summary>
        public async Task<LoginResponse?> AdminLoginAsync(string machineId, string sessionId, string username, string password)
        {
            var url = $"{_baseUrl}/api/v1/tenant/device/auth/admin-login";
            _logger?.LogInfo($"Attempting admin login for: {username} on device: {machineId} (session: {sessionId})");

            var request = new AdminLoginRequest
            {
                MachineId = machineId,
                SessionId = sessionId,
                Username = username,
                Password = password
            };
            var response = await PostAsync<AdminLoginRequest, LoginResponse>(url, request);

            if (response?.AccessToken != null)
            {
                _logger?.LogInfo($"Admin login successful for: {username}");
            }
            else
            {
                _logger?.LogWarning($"Admin login returned no access token for: {username}");
            }

            return response;
        }

        /// <summary>
        /// Generic POST method for sending JSON requests and receiving typed responses.
        /// Parses API error responses to throw user-friendly HttpRequestException messages.
        /// </summary>
        private async Task<TResponse?> PostAsync<TRequest, TResponse>(string url, TRequest request)
            where TResponse : class
        {
            var jsonContent = JsonSerializer.Serialize(request, GetJsonOptions());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);

            var responseJson = await response.Content.ReadAsStringAsync();
            _logger?.LogDebug($"API Response [{response.StatusCode}]: {responseJson}");

            if (!response.IsSuccessStatusCode)
            {
                var apiError = TryParseApiError(responseJson);
                var statusCode = (int)response.StatusCode;

                string friendlyMessage;
                if (statusCode == 401)
                    friendlyMessage = apiError ?? "Invalid email or password.";
                else if (statusCode == 403)
                    friendlyMessage = apiError ?? "Access denied. Your account may be restricted.";
                else if (statusCode == 404)
                    friendlyMessage = apiError ?? "Service not available. Please contact your administrator.";
                else if (statusCode >= 500)
                    friendlyMessage = apiError ?? "Server is temporarily unavailable. Please try again later.";
                else
                    friendlyMessage = apiError ?? $"Request failed ({statusCode}).";

                _logger?.LogWarning($"API error {statusCode}: {friendlyMessage}");
                throw new HttpRequestException(friendlyMessage);
            }

            return JsonSerializer.Deserialize<TResponse>(responseJson, GetJsonOptions());
        }

        /// <summary>
        /// POST method for token refresh endpoints that distinguishes server rejections
        /// from network errors. Throws TokenRejectedException for 400/401/403 so
        /// AuthTokenManager can clear the stored refresh token instead of preserving it.
        /// Network errors (HttpRequestException from actual connection failures) propagate as-is.
        /// </summary>
        private async Task<TResponse?> PostRefreshAsync<TRequest, TResponse>(string url, TRequest request)
            where TResponse : class
        {
            var jsonContent = JsonSerializer.Serialize(request, GetJsonOptions());
            var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync(url, content);

            var responseJson = await response.Content.ReadAsStringAsync();
            _logger?.LogDebug($"API Response [{response.StatusCode}]: {responseJson}");

            var statusCode = (int)response.StatusCode;

            // 400/401/403 = server explicitly rejected the refresh token — it's invalid
            if (statusCode == 400 || statusCode == 401 || statusCode == 403)
            {
                throw new TokenRejectedException(
                    $"Token refresh rejected by server ({response.StatusCode}): {responseJson}",
                    statusCode);
            }

            response.EnsureSuccessStatusCode();

            return JsonSerializer.Deserialize<TResponse>(responseJson, GetJsonOptions());
        }

        /// <summary>
        /// Tries to extract a user-friendly error message from an API JSON error response.
        /// Checks common fields: message, error, title, detail.
        /// </summary>
        private static string? TryParseApiError(string responseJson)
        {
            if (string.IsNullOrWhiteSpace(responseJson)) return null;
            try
            {
                using var doc = JsonDocument.Parse(responseJson);
                var root = doc.RootElement;

                // Try common error message fields
                foreach (var field in new[] { "message", "Message", "error", "Error", "detail", "Detail", "title", "Title" })
                {
                    if (root.TryGetProperty(field, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        var val = prop.GetString();
                        if (!string.IsNullOrWhiteSpace(val))
                            return val;
                    }
                }

                // ASP.NET validation errors: { errors: { Field: ["msg"] } }
                if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Object)
                {
                    var messages = new System.Collections.Generic.List<string>();
                    foreach (var field in errors.EnumerateObject())
                    {
                        if (field.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var err in field.Value.EnumerateArray())
                            {
                                var msg = err.GetString();
                                if (!string.IsNullOrWhiteSpace(msg))
                                    messages.Add(msg);
                            }
                        }
                    }
                    if (messages.Count > 0)
                        return string.Join(" ", messages);
                }
            }
            catch { /* JSON parse failed — return null */ }
            return null;
        }

        private static JsonSerializerOptions GetJsonOptions()
        {
            return new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _httpClient?.Dispose();
            _disposed = true;
        }
    }

    /// <summary>
    /// Thrown when the server explicitly rejects a refresh token (400/401/403).
    /// Signals to AuthTokenManager that the stored refresh token should be cleared,
    /// unlike network errors where the token should be preserved for retry.
    /// </summary>
    public class TokenRejectedException : Exception
    {
        public int StatusCode { get; }

        public TokenRejectedException(string message, int statusCode)
            : base(message)
        {
            StatusCode = statusCode;
        }
    }
}
