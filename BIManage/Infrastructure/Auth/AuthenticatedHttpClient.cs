
using System;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.Network;

namespace BIManage.Infrastructure.Auth
{
    /// <summary>
    /// HTTP client wrapper that automatically attaches Bearer token to every request.
    /// If a request returns 401, triggers token refresh and retries once.
    /// </summary>
    public class AuthenticatedHttpClient : IDisposable
    {
        private readonly AuthTokenManager _tokenManager;
        private readonly HttpClient _httpClient;
        private readonly string _baseUrl;
        private readonly ILogger? _logger;
        private bool _disposed;

        // Global "persistent 401 after refresh" counter — shared across every endpoint
        // this client serves. Increments when a request returns 401, we successfully
        // obtain a NEW token via refresh, and the retry still 401s. Resets to 0 on any
        // successful response.
        //
        // When this counter hits _persistentAuth401Threshold, NotifyAuthExhausted is
        // fired regardless of whether refresh tokens are still in storage. That covers
        // the "blacklisted AccessId" failure mode where refresh keeps succeeding but
        // every issued token inherits the same blacklisted AccessId and gets rejected
        // — the ONLY recovery is for the user to sign in fresh so a NEW AccessId is
        // minted. Without this global escalation, each subsystem (mail dispatch, audit
        // logs, evidence upload, SignalR negotiate) had its own per-subsystem detection
        // and the user saw "some things work, some don't" until they manually signed
        // out + back in. Now: first two persistent-401s anywhere → force re-sign-in →
        // everything recovers together.
        private static int _persistentAuth401Count;
        private const int _persistentAuth401Threshold = 2;

        public AuthenticatedHttpClient(AuthTokenManager tokenManager, string baseUrl, ILogger? logger = null, SslValidationPolicy? sslPolicy = null)
        {
            _tokenManager = tokenManager ?? throw new ArgumentNullException(nameof(tokenManager));
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

            _logger?.LogInfo($"AuthenticatedHttpClient initialized with base URL: {_baseUrl}");
        }

        /// <summary>
        /// Returns true if an access token is currently available or can be obtained via refresh.
        /// Use this to check if authenticated requests can be made.
        /// </summary>
        public bool IsAuthenticated => _tokenManager.IsAuthenticated || _tokenManager.HasStoredRefreshToken;

        /// <summary>
        /// The backend base URL this client posts to (trailing slash stripped). Exposed so
        /// callers that need to build full URLs themselves — e.g. the AI provider's raw
        /// HttpClient streaming path — can stay consistent with the configured base.
        /// </summary>
        public string BaseUrl => _baseUrl;

        /// <summary>
        /// Returns the current access token (for Swagger paste / debugging).
        /// </summary>
        public Task<string?> GetCurrentAccessTokenAsync() => _tokenManager.GetAccessTokenAsync();

        /// <summary>
        /// Signals that auth is unrecoverable from the plugin's side — e.g. consecutive 401s
        /// keep firing despite successful refresh because the server has blacklisted the
        /// AccessId (verified in field: refresh returns 200 + new bearer, but server still
        /// 401s with "Rejected token for blacklisted (AccessId=...)"). The only fix is for
        /// the user to sign in again so a new AccessId is issued. Background services
        /// (mail dispatch, audit-log push) call this when their own circuit breaker trips
        /// so the user gets a Sign In prompt instead of silent failure.
        /// </summary>
        public void NotifyAuthExhausted(bool wasAdminAttempt)
            => _tokenManager.NotifyAuthExhausted(wasAdminAttempt);

        /// <summary>
        /// Sends a GET request with Bearer token attached
        /// </summary>
        public async Task<HttpResponseMessage> GetAsync(string relativeUrl, bool requireAdminToken = false)
        {
            return await SendWithAuthAsync(HttpMethod.Get, relativeUrl, null, requireAdminToken);
        }

        /// <summary>
        /// Sends a POST request with Bearer token and JSON body
        /// </summary>
        public async Task<HttpResponseMessage> PostAsync(string relativeUrl, object? body = null, bool requireAdminToken = false)
        {
            return await SendWithAuthAsync(HttpMethod.Post, relativeUrl, body, requireAdminToken);
        }

        /// <summary>
        /// Sends a PUT request with Bearer token and JSON body
        /// </summary>
        public async Task<HttpResponseMessage> PutAsync(string relativeUrl, object? body = null, bool requireAdminToken = false)
        {
            return await SendWithAuthAsync(HttpMethod.Put, relativeUrl, body, requireAdminToken);
        }

        /// <summary>
        /// Sends a DELETE request with Bearer token
        /// </summary>
        public async Task<HttpResponseMessage> DeleteAsync(string relativeUrl, bool requireAdminToken = false)
        {
            return await SendWithAuthAsync(HttpMethod.Delete, relativeUrl, null, requireAdminToken);
        }

        /// <summary>
        /// Sends a PATCH request with Bearer token and JSON body
        /// </summary>
        public async Task<HttpResponseMessage> PatchAsync(string relativeUrl, object? body = null, bool requireAdminToken = false)
        {
            return await SendWithAuthAsync(new HttpMethod("PATCH"), relativeUrl, body, requireAdminToken);
        }

        /// <summary>
        /// Sends a GET request and deserializes the response to the specified type
        /// </summary>
        public async Task<T?> GetAsync<T>(string relativeUrl) where T : class
        {
            var response = await GetAsync(relativeUrl);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, GetJsonOptions());
        }

        /// <summary>
        /// Sends a POST request and deserializes the response to the specified type
        /// </summary>
        public async Task<T?> PostAsync<T>(string relativeUrl, object? body = null) where T : class
        {
            var response = await PostAsync(relativeUrl, body);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<T>(json, GetJsonOptions());
        }

        /// <summary>
        /// Sends a POST request with multipart form data and Bearer token.
        /// Retries once on 401 after token refresh.
        /// </summary>
        public async Task<HttpResponseMessage> PostMultipartAsync(string relativeUrl, MultipartFormDataContent formData)
        {
            var url = $"{_baseUrl}/{relativeUrl.TrimStart('/')}";

            // First attempt
            var response = await SendMultipartRequestAsync(url, formData);

            // If 401, refresh token and retry
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger?.LogInfo("Multipart upload received 401, attempting token refresh...");

                var newToken = await _tokenManager.GetAccessTokenAsync();
                if (newToken == null)
                {
                    _logger?.LogWarning("Token refresh failed, returning 401 response");
                    // Multipart path uses the device token (no requireAdminToken option),
                    // so wasAdminAttempt is always false here. See SendWithAuthAsync for the
                    // detailed comment on the no-stored-refresh-token recovery rationale.
                    if (!_tokenManager.HasStoredRefreshToken)
                    {
                        _tokenManager.NotifyAuthExhausted(wasAdminAttempt: false);
                    }
                    return response;
                }

                _logger?.LogInfo("Retrying multipart upload with refreshed token");
                response = await SendMultipartRequestAsync(url, formData);
            }

            return response;
        }

        private async Task<HttpResponseMessage> SendMultipartRequestAsync(string url, MultipartFormDataContent formData)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, url);

            var token = await _tokenManager.GetAccessTokenAsync();
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }

            request.Content = formData;
            _logger?.LogDebug($"Sending multipart POST {url}");
            return await _httpClient.SendAsync(request);
        }

        /// <summary>
        /// Core method: sends request with Bearer token, retries once on 401.
        /// When requireAdminToken=true, attaches the admin-login token specifically (which carries
        /// profileId). Admin-scoped endpoints (command/event/rule protections, audit-log writes)
        /// reject device tokens with a misleading 400 — always pass requireAdminToken=true for those.
        /// </summary>
        private async Task<HttpResponseMessage> SendWithAuthAsync(HttpMethod method, string relativeUrl, object? body = null, bool requireAdminToken = false)
        {
            var url = $"{_baseUrl}/{relativeUrl.TrimStart('/')}";

            var response = await SendRequestAsync(method, url, body, requireAdminToken);

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                _logger?.LogInfo("Received 401 Unauthorized, attempting token refresh...");

                // Force-expire the in-memory token slots BEFORE asking for a new one.
                // Without this, GetAdminAccessTokenAsync / GetAccessTokenAsync see a token
                // that is "still valid by the local clock" and return the SAME cached token
                // — the very token the server just rejected. The retry then fails with the
                // same 401 in an infinite loop (observed in the field 2026-05-11: every
                // admin endpoint returned 401 for 4+ minutes after a single revocation,
                // because the cached admin_expires_at was hours in the future).
                // The server (not the local clock) is authoritative on token validity, so
                // a 401 means our cached "is valid" belief is wrong — invalidate and refresh.
                _tokenManager.ForceExpireAccessToken();

                var newToken = requireAdminToken
                    ? await _tokenManager.GetAdminAccessTokenAsync()
                    : await _tokenManager.GetAccessTokenAsync();
                if (newToken == null)
                {
                    _logger?.LogWarning("Token refresh failed, returning 401 response");
                    // ADMIN-SLOT-EMPTY ESCALATION (added 2026-05-26): if this was an
                    // admin-scoped call AND we couldn't even obtain a new admin token,
                    // the admin-auth.dat slot is empty (verified in field: log shows
                    // "Admin refresh requested but admin-auth.dat slot is empty —
                    // demoting session to device-only"). The user is signed in as
                    // Company Admin in-memory but has no admin refresh token in
                    // storage, so EVERY admin write 401s and nothing in this method's
                    // existing branches (HasStoredRefreshToken / persistent-401
                    // counter) trips — both bail out BEFORE this path. Forcing
                    // NotifyAuthExhausted here is the only way out — the user
                    // re-signs and the admin slot gets repopulated.
                    if (requireAdminToken)
                    {
                        _logger?.LogWarning("Admin token refresh returned null — admin-auth.dat slot is empty. Forcing re-sign-in.");
                        _tokenManager.NotifyAuthExhausted(wasAdminAttempt: true);
                        return response;
                    }
                    // Re-sign-in is the ONLY recovery path when there's no stored refresh
                    // token to retry with. Notify the token manager so a single throttled
                    // logout flow runs instead of leaving the user stranded with silent
                    // 401s on every admin operation. Suppressed for transient failures
                    // (network, 5xx) because RefreshAccessTokenAsync preserves the stored
                    // refresh token in those cases — HasStoredRefreshToken stays true.
                    if (!_tokenManager.HasStoredRefreshToken)
                    {
                        _tokenManager.NotifyAuthExhausted(wasAdminAttempt: requireAdminToken);
                    }
                    return response;
                }

                // If the "refreshed" token is byte-identical to what the server just rejected,
                // the refresh path is broken (e.g., refresh-token endpoint returned the same
                // token). Don't bother retrying — surface the 401 so the caller can stop
                // hammering the server. NotifyAuthExhausted will fire if the underlying refresh
                // token is also gone.
                if (string.Equals(newToken, response.RequestMessage?.Headers.Authorization?.Parameter, StringComparison.Ordinal))
                {
                    _logger?.LogWarning("Token refresh returned the same token the server just rejected — giving up to avoid 401 loop. User must re-authenticate.");
                    if (!_tokenManager.HasStoredRefreshToken)
                        _tokenManager.NotifyAuthExhausted(wasAdminAttempt: requireAdminToken);
                    return response;
                }

                _logger?.LogInfo("Retrying request with refreshed token");
                response = await SendRequestAsync(method, url, body, requireAdminToken);

                if (response.StatusCode == HttpStatusCode.Unauthorized)
                {
                    // Retry-after-refresh still 401. This is the "persistent auth failure"
                    // case — either:
                    //   (a) no refresh token in storage → truly exhausted, force re-sign-in
                    //   (b) refresh kept succeeding but server still rejects → AccessId
                    //       blacklisted, force re-sign-in to mint a new AccessId
                    //   (c) a one-off endpoint-specific 401 (token-type mismatch, transient
                    //       state) — surface to caller, don't force logout for a single
                    //       endpoint
                    // We distinguish (a)/(b) from (c) by counting GLOBALLY across all
                    // endpoints: a single persistent-401 might be benign (c); two in a row
                    // across any endpoints means the session itself is broken (a or b).
                    if (!_tokenManager.HasStoredRefreshToken)
                    {
                        _logger?.LogWarning("Retry after token refresh ALSO returned 401 AND no refresh token in storage — session truly exhausted.");
                        System.Threading.Interlocked.Exchange(ref _persistentAuth401Count, 0);
                        _tokenManager.NotifyAuthExhausted(wasAdminAttempt: requireAdminToken);
                    }
                    else
                    {
                        var hits = System.Threading.Interlocked.Increment(ref _persistentAuth401Count);
                        if (hits >= _persistentAuth401Threshold)
                        {
                            _logger?.LogWarning($"Retry after token refresh returned 401 — {hits} consecutive persistent-auth failures across all endpoints. Server is rejecting every refreshed token (likely AccessId blacklisted). Forcing re-sign-in to mint a new AccessId.");
                            System.Threading.Interlocked.Exchange(ref _persistentAuth401Count, 0);
                            _tokenManager.NotifyAuthExhausted(wasAdminAttempt: requireAdminToken);
                        }
                        else
                        {
                            _logger?.LogWarning($"Retry after token refresh returned 401 — surfacing to caller ({hits}/{_persistentAuth401Threshold} persistent failures; will force re-sign-in at threshold).");
                        }
                    }
                }
                else
                {
                    // Retry succeeded — refresh actually fixed it. Clear the global counter
                    // so a future first 401-after-refresh starts from zero.
                    System.Threading.Interlocked.Exchange(ref _persistentAuth401Count, 0);
                }
            }
            else if (response.IsSuccessStatusCode)
            {
                // Any successful response anywhere clears the persistent-401 counter so
                // intermittent transient failures don't gradually accumulate to threshold
                // over a long-running session.
                if (System.Threading.Volatile.Read(ref _persistentAuth401Count) > 0)
                    System.Threading.Interlocked.Exchange(ref _persistentAuth401Count, 0);
            }

            return response;
        }

        private async Task<HttpResponseMessage> SendRequestAsync(HttpMethod method, string url, object? body = null, bool requireAdminToken = false)
        {
            var request = new HttpRequestMessage(method, url);

            var token = requireAdminToken
                ? await _tokenManager.GetAdminAccessTokenAsync()
                : await _tokenManager.GetAccessTokenAsync();
            if (!string.IsNullOrEmpty(token))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            else
            {
                _logger?.LogWarning(requireAdminToken
                    ? "No admin access token available for admin-scoped request (user not signed in as admin?)"
                    : "No access token available for request");
            }

            if (body != null)
            {
                var jsonContent = JsonSerializer.Serialize(body, GetJsonOptions());
                request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
            }

            _logger?.LogDebug($"Sending {method} {url}{(requireAdminToken ? " [admin-token]" : "")}");
            return await _httpClient.SendAsync(request);
        }

        private static JsonSerializerOptions GetJsonOptions()
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                ReadCommentHandling = JsonCommentHandling.Skip,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            options.Converters.Add(new UtcDateTimeConverter());
            return options;
        }

        /// <summary>
        /// Ensures all DateTime values are serialized as UTC (Z suffix).
        /// Converts local/unspecified DateTimes to UTC before writing.
        /// </summary>
        private class UtcDateTimeConverter : JsonConverter<DateTime>
        {
            public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            {
                return reader.GetDateTime().ToUniversalTime();
            }

            public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
            {
                // Always write as UTC with Z suffix
                var utc = value.Kind == DateTimeKind.Utc
                    ? value
                    : value.ToUniversalTime();
                writer.WriteStringValue(utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _httpClient?.Dispose();
            _disposed = true;
        }
    }
}
