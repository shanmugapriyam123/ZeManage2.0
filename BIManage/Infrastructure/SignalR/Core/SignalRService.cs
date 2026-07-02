using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.Network;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR.Core
{
    /// <summary>
    /// SignalR service using raw WebSocket + JSON Hub Protocol.
    /// Eliminates dependency on Microsoft.AspNetCore.SignalR.Client and all Microsoft.Extensions.*
    /// packages, avoiding assembly version conflicts with Revit's CLR host.
    /// Compatible with both net48 (Revit 2021-2024) and net8.0-windows (Revit 2025-2026).
    /// </summary>
    public class SignalRService : ISignalRService
    {
        // SignalR JSON Hub Protocol uses 0x1E (Record Separator) as message delimiter
        private const byte RecordSeparator = 0x1E;
        private const int ReceiveBufferSize = 4096;
        private const int MaxReconnectAttempts = 10;
        private const int MaxMessageBufferSize = 10 * 1024 * 1024; // 10 MB

        private readonly SignalRConfiguration _config;
        private readonly ILogger _logger;
        private readonly object _lock = new object();
        private readonly HashSet<string> _joinedGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HttpClient _httpClient;
        private readonly SemaphoreSlim _connectLock = new SemaphoreSlim(1, 1);
        private static readonly Random _jitterRng = new Random();

        private ClientWebSocket _ws;
        private string _accessToken;
        private string _connectionId;
        private volatile SignalRConnectionState _state = SignalRConnectionState.Disconnected;
        private int _reconnectAttempt = 0;
        private int _slowReconnectAttempt = 0;
        private volatile bool _isDisposed = false;
        private volatile bool _intentionalClose = false;
        private CancellationTokenSource _receiveCts;
        private CancellationTokenSource _reconnectCts;
        private CancellationTokenSource _keepAliveCts;
        private Timer _slowReconnectTimer;
        private DateTime _rateLimitedUntil = DateTime.MinValue;
        private DateTime? _lastConnectedAt;
        private string _lastError = null;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);

        // Business method names we route to listeners via MessageReceived
        private static readonly HashSet<string> BusinessMethods = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            SignalRMethods.ModelRegistered, SignalRMethods.ModelDeregistered, SignalRMethods.ModelSettingsChanged,
            SignalRMethods.RevitSessionCreated, SignalRMethods.RevitSessionUpdated, SignalRMethods.RevitSessionEnded,
            SignalRMethods.RuleUpdate, SignalRMethods.ProtectionSettingsChange,
            SignalRMethods.CommandBlockedNotify, SignalRMethods.PinProtectionChange,
            SignalRMethods.SyncQueueUpdate, SignalRMethods.SyncStarting,
            SignalRMethods.SyncCompleted, SignalRMethods.SyncCancelled, SignalRMethods.SyncRequest,
            // Cloud sync gate (Option D) replies — without these the server's
            // RequestSyncSlot reply is silently dropped here before it reaches
            // SyncQueueListener, and the cloud gate awaiter always times out.
            SignalRMethods.SyncSlotGranted, SignalRMethods.SyncSlotDenied,
            SignalRMethods.UserJoined, SignalRMethods.UserLeft, SignalRMethods.ActiveUsersUpdate,
            SignalRMethods.AdminBroadcast, SignalRMethods.ConfigurationUpdate, SignalRMethods.MaintenanceNotice,
            SignalRMethods.ForceLogout, SignalRMethods.ForceTokenRefresh,
            SignalRMethods.ChatMessageReceived
        };

        public event EventHandler<SignalRConnectionStateChangedEventArgs> ConnectionStateChanged;
        public event EventHandler<SignalRMessageInfo> MessageReceived;

        public bool IsConnected => _state == SignalRConnectionState.Connected;
        public SignalRConnectionState State => _state;
        public string ConnectionId => _connectionId;
        public bool HasReachedMaxReconnectAttempts => Thread.VolatileRead(ref _reconnectAttempt) >= MaxReconnectAttempts;

        // Diagnostic properties
        public int ReconnectAttemptCount => Thread.VolatileRead(ref _reconnectAttempt);
        public DateTime RateLimitedUntil => _rateLimitedUntil;
        public string HubUrl => _config?.HubUrl;
        public IReadOnlyList<string> JoinedGroups
        {
            get { lock (_lock) { return _joinedGroups.ToList(); } }
        }
        public string LastError => _lastError;
        public DateTime? LastConnectedAt => _lastConnectedAt;

        /// <inheritdoc />
        public Func<Task<string>> AccessTokenProvider { get; set; }

        /// <summary>
        /// Optional callback fired when SignalR negotiate keeps returning 401 even after
        /// AccessTokenProvider issues fresh tokens — i.e. the server is rejecting every
        /// token (AccessId blacklisted). Wired to AuthTokenManager.NotifyAuthExhausted in
        /// the bootstrapper so a force-logout fires after 2 consecutive 401s instead of
        /// looping for ~30 s on the 5-s slow-reconnect cadence.
        /// </summary>
        public Action? OnAuthExhausted { get; set; }

        // Tracks consecutive SignalR negotiate 401s. Resets on successful connect.
        // Threshold 2: first 401 might be a transient token mid-refresh; the second
        // 401 after a fresh token from AccessTokenProvider proves the server is
        // rejecting every token → force re-auth.
        private int _consecutiveNegotiate401;
        private const int _negotiate401Threshold = 2;

        private readonly SslValidationPolicy? _sslPolicy;

        public SignalRService(SignalRConfiguration config, ILogger logger, SslValidationPolicy? sslPolicy = null)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = logger;
            _sslPolicy = sslPolicy;
            _httpClient = CreateHttpClient();
        }

        public void ResetReconnectAttempts()
        {
            Interlocked.Exchange(ref _reconnectAttempt, 0);
            _logger?.LogInfo("SignalR: Reconnect attempts reset");
        }

        public async Task ConnectAsync(string accessToken = null)
        {
            if (!string.IsNullOrEmpty(accessToken))
                _accessToken = accessToken;
            await ConnectInternalAsync(isReconnect: false);
        }

        private async Task ConnectInternalAsync(bool isReconnect)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(SignalRService));

            if (_state == SignalRConnectionState.Connected || _state == SignalRConnectionState.Connecting)
            {
                _logger?.LogDebug("SignalR: Already connected or connecting");
                return;
            }

            if (!_config.IsValid)
            {
                _logger?.LogError("SignalR: Invalid configuration - HubUrl and MachineId are required");
                return;
            }

            // Serialize connection attempts to prevent concurrent reconnects
            await _connectLock.WaitAsync();
            try
            {
                // Re-check state after acquiring lock
                if (_state == SignalRConnectionState.Connected || _state == SignalRConnectionState.Connecting)
                {
                    _logger?.LogDebug("SignalR: Already connected or connecting (after lock)");
                    return;
                }

                _intentionalClose = false;
                SetState(SignalRConnectionState.Connecting);

                if (!isReconnect)
                    Interlocked.Exchange(ref _reconnectAttempt, 0);

                try
                {
                    await CreateAndStartConnection();
                }
                catch (Exception ex)
                {
                    // Log the innermost exception message — HttpRequestException wraps the actual
                    // cause (SSL error, connection refused, DNS failure, etc.) in InnerException
                    var rootCause = ex;
                    while (rootCause.InnerException != null)
                        rootCause = rootCause.InnerException;
                    var reason = rootCause == ex
                        ? ex.Message
                        : $"{ex.Message} → {rootCause.GetType().Name}: {rootCause.Message}";
                    _logger?.LogError($"SignalR: Connection failed - {reason}", ex);
                    SetState(SignalRConnectionState.Disconnected, ex);

                    // Stop reconnecting on permanent auth failures (401/403) — retrying won't help
                    // until the user re-authenticates or the token is refreshed by another path
                    var isPermanentAuthFailure = reason.Contains("401") || reason.Contains("Unauthorized")
                                              || reason.Contains("403") || reason.Contains("Forbidden");

                    if (isPermanentAuthFailure)
                    {
                        var hits = System.Threading.Interlocked.Increment(ref _consecutiveNegotiate401);
                        if (hits >= _negotiate401Threshold && OnAuthExhausted != null)
                        {
                            _logger?.LogWarning($"SignalR: {hits} consecutive negotiate 401(s) — every refreshed token is rejected (likely blacklisted AccessId). Firing OnAuthExhausted to force re-sign-in (skipping the {_config.AuthRetryDelayMs / 1000}s retry loop).");
                            System.Threading.Interlocked.Exchange(ref _consecutiveNegotiate401, 0);
                            try { OnAuthExhausted.Invoke(); }
                            catch (Exception cbEx) { _logger?.LogWarning($"SignalR: OnAuthExhausted callback threw: {cbEx.Message}"); }
                            return;
                        }
                        var retryDelayMs = _config.AuthRetryDelayMs;
                        _logger?.LogWarning($"SignalR: Auth failure (401/403) — scheduling delayed retry in {retryDelayMs / 1000}s to allow token refresh to complete. ({hits}/{_negotiate401Threshold} before force re-sign-in.)");
                        // Don't give up permanently — token refresh may complete in a few seconds.
                        // Schedule a single delayed retry so SignalR recovers automatically.
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await Task.Delay(retryDelayMs);
                                if (AccessTokenProvider != null)
                                {
                                    var token = await AccessTokenProvider();
                                    if (!string.IsNullOrEmpty(token))
                                    {
                                        _logger?.LogInfo("SignalR: Token now available after 401 — reconnecting...");
                                        await ConnectAsync();
                                    }
                                    else
                                    {
                                        _logger?.LogWarning("SignalR: Token still unavailable after delayed retry — will retry on next slow reconnect cycle.");
                                        StartSlowReconnect();
                                    }
                                }
                            }
                            catch (Exception retryEx)
                            {
                                _logger?.LogDebug($"SignalR: Delayed auth retry failed: {retryEx.Message}");
                                StartSlowReconnect();
                            }
                        });
                    }
                    else if (_config.AutoReconnect)
                    {
                        // On 429 rate limiting, skip fast reconnect and go directly to slow reconnect
                        if (_rateLimitedUntil > DateTime.UtcNow)
                        {
                            var backoffMs = (int)(_rateLimitedUntil - DateTime.UtcNow).TotalMilliseconds;
                            _logger?.LogWarning($"SignalR: Rate-limited (429) — slow reconnect in {backoffMs / 1000}s");
                            StartSlowReconnect();
                        }
                        else
                        {
                            ScheduleReconnect();
                        }
                    }
                }
            }
            finally
            {
                _connectLock.Release();
            }
        }

        private async Task CreateAndStartConnection()
        {
            // Obtain token from provider if needed
            if (string.IsNullOrEmpty(_accessToken) && AccessTokenProvider != null)
            {
                _logger?.LogInfo("SignalR: No explicit token — requesting from AccessTokenProvider...");
                try
                {
                    _accessToken = await AccessTokenProvider();
                    if (!string.IsNullOrEmpty(_accessToken))
                        _logger?.LogInfo("SignalR: Obtained access token from provider");
                    else
                        _logger?.LogWarning("SignalR: AccessTokenProvider returned null/empty token");
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning($"SignalR: AccessTokenProvider failed: {ex.Message}");
                }
            }

            var hubUrl = _config.HubUrl.TrimEnd('/');

            // Step 1: Negotiate to get connectionId/connectionToken
            _logger?.LogInfo("SignalR: Negotiating...");
            var (connectionId, connectionToken) = await NegotiateAsync(hubUrl);
            _connectionId = connectionId;

            // Step 2: Open WebSocket
            var wsUrl = BuildWebSocketUrl(hubUrl, connectionToken);
            _logger?.LogInfo($"SignalR: Connecting to {hubUrl}...");

            _ws = CreateWebSocket();
            using var connectCts = new CancellationTokenSource(_config.ConnectionTimeout);
            await _ws.ConnectAsync(new Uri(wsUrl), connectCts.Token);

            // Step 3: JSON Hub Protocol handshake
            await SendHandshakeAsync();
            await ReceiveHandshakeResponseAsync();

            _logger?.LogInfo($"SignalR: Connected with ID {_connectionId}");
            // Successful negotiate clears the persistent-401 counter — server now
            // accepts our token, so a future single 401 (e.g. transient mid-refresh)
            // shouldn't immediately trip force-logout.
            System.Threading.Interlocked.Exchange(ref _consecutiveNegotiate401, 0);
            SetState(SignalRConnectionState.Connected);
            Interlocked.Exchange(ref _reconnectAttempt, 0);
            Interlocked.Exchange(ref _slowReconnectAttempt, 0);
            StopSlowReconnect();

            // Step 4: Start receive loop + keep-alive
            _receiveCts = new CancellationTokenSource();
            _keepAliveCts = new CancellationTokenSource();
            _ = Task.Run(() => ReceiveLoopAsync(_receiveCts.Token));
            _ = Task.Run(() => KeepAliveLoopAsync(_keepAliveCts.Token));

            // Step 5: Rejoin any previously joined groups
            await RejoinGroups();
        }

        #region Negotiate

        private async Task<(string connectionId, string connectionToken)> NegotiateAsync(string hubUrl)
        {
            // Respect rate limit backoff from previous 429 response
            if (_rateLimitedUntil > DateTime.UtcNow)
            {
                var waitSeconds = (int)(_rateLimitedUntil - DateTime.UtcNow).TotalSeconds;
                throw new HttpRequestException(
                    $"SignalR negotiate rate-limited — backing off for {waitSeconds}s");
            }

            var negotiateUrl = $"{hubUrl}/negotiate?negotiateVersion=1";

            using var request = new HttpRequestMessage(HttpMethod.Post, negotiateUrl);
            if (!string.IsNullOrEmpty(_accessToken))
                request.Headers.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _accessToken);

            var response = await _httpClient.SendAsync(request);

            // Handle 429 Too Many Requests — read Retry-After and back off
            if (response.StatusCode == (HttpStatusCode)429)
            {
                var retryAfterSeconds = 120; // default 2 min backoff
                if (response.Headers.TryGetValues("Retry-After", out var values))
                {
                    var retryValue = values.FirstOrDefault();
                    if (int.TryParse(retryValue, out var parsed))
                        retryAfterSeconds = Math.Max(parsed, 30); // minimum 30s
                }
                _rateLimitedUntil = DateTime.UtcNow.AddSeconds(retryAfterSeconds);
                throw new HttpRequestException(
                    $"Response status code does not indicate success: 429 (Too Many Requests). Backing off for {retryAfterSeconds}s.");
            }

            response.EnsureSuccessStatusCode();
            _rateLimitedUntil = DateTime.MinValue; // clear rate limit on success

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var connId = root.TryGetProperty("connectionId", out var idProp) ? idProp.GetString() : null;
            var connToken = root.TryGetProperty("connectionToken", out var tokenProp) ? tokenProp.GetString() : connId;

            if (string.IsNullOrEmpty(connId))
                throw new InvalidOperationException("SignalR negotiate returned no connectionId");

            _logger?.LogDebug($"SignalR: Negotiate OK — connectionId={connId}");
            return (connId, connToken);
        }

        #endregion

        #region WebSocket Communication

        private string BuildWebSocketUrl(string hubUrl, string connectionToken)
        {
            // Convert scheme: https → wss, http → ws
            string wsUrl;
            if (hubUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                wsUrl = "wss://" + hubUrl.Substring(8);
            else if (hubUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
                wsUrl = "ws://" + hubUrl.Substring(7);
            else
                wsUrl = hubUrl;

            var parts = new List<string>();
            if (!string.IsNullOrEmpty(connectionToken))
                parts.Add($"id={Uri.EscapeDataString(connectionToken)}");
            if (!string.IsNullOrEmpty(_accessToken))
                parts.Add($"access_token={Uri.EscapeDataString(_accessToken)}");
            if (!string.IsNullOrEmpty(_config.CompanyId))
                parts.Add($"companyId={Uri.EscapeDataString(_config.CompanyId)}");
            if (!string.IsNullOrEmpty(_config.MachineId))
                parts.Add($"machineId={Uri.EscapeDataString(_config.MachineId)}");
            if (!string.IsNullOrEmpty(_config.Username))
                parts.Add($"username={Uri.EscapeDataString(_config.Username)}");

            return parts.Count > 0 ? $"{wsUrl}?{string.Join("&", parts)}" : wsUrl;
        }

        private async Task SendHandshakeAsync()
        {
            // SignalR JSON Hub Protocol handshake: {"protocol":"json","version":1}\x1E
            var handshake = "{\"protocol\":\"json\",\"version\":1}" + (char)RecordSeparator;
            var bytes = Encoding.UTF8.GetBytes(handshake);
            await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }

        private async Task ReceiveHandshakeResponseAsync()
        {
            var buffer = new byte[ReceiveBufferSize];
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);

            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException(
                    $"SignalR: Server closed during handshake: {_ws.CloseStatusDescription}");

            var response = Encoding.UTF8.GetString(buffer, 0, result.Count).TrimEnd((char)RecordSeparator);

            if (!string.IsNullOrEmpty(response))
            {
                try
                {
                    using var doc = JsonDocument.Parse(response);
                    if (doc.RootElement.TryGetProperty("error", out var errorProp))
                        throw new InvalidOperationException($"SignalR handshake error: {errorProp.GetString()}");
                }
                catch (JsonException)
                {
                    // Empty {} response is expected on success
                }
            }

            _logger?.LogDebug("SignalR: Handshake completed");
        }

        /// <summary>
        /// Sends a raw JSON message with the record separator appended.
        /// </summary>
        private async Task SendHubMessageAsync(string json)
        {
            if (_ws?.State != WebSocketState.Open)
                return;

            var bytes = Encoding.UTF8.GetBytes(json + (char)RecordSeparator);
            await _sendLock.WaitAsync();
            try
            {
                if (_ws?.State != WebSocketState.Open)
                    return;
                await _ws.SendAsync(
                    new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Send failed - {ex.Message}");
            }
            finally
            {
                _sendLock.Release();
            }
        }

        /// <summary>
        /// Sends a Hub Invocation message (type 1, fire-and-forget).
        /// </summary>
        private Task SendInvocationAsync(string target, params object[] args)
        {
            var msg = JsonSerializer.Serialize(new
            {
                type = 1,
                target,
                arguments = args ?? Array.Empty<object>()
            });
            return SendHubMessageAsync(msg);
        }

        private Task SendPingAsync()
        {
            return SendHubMessageAsync("{\"type\":6}");
        }

        #endregion

        #region Receive Loop

        private async Task ReceiveLoopAsync(CancellationToken ct)
        {
            var buffer = new byte[ReceiveBufferSize];
            var messageBuffer = new MemoryStream();
            // Timeout if no data arrives within server timeout + heartbeat interval
            var receiveTimeoutMs = (int)(_config.ServerTimeout + _config.HeartbeatInterval).TotalMilliseconds;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var ws = _ws;
                    if (ws == null || ws.State != WebSocketState.Open)
                        break;

                    WebSocketReceiveResult result;
                    try
                    {
                        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeoutCts.CancelAfter(receiveTimeoutMs);
                        result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeoutCts.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        _logger?.LogWarning($"SignalR: Receive timed out after {receiveTimeoutMs / 1000}s — no data from server");
                        break;
                    }
                    catch (OperationCanceledException) { break; }
                    catch (WebSocketException)
                    {
                        _logger?.LogWarning("SignalR: WebSocket closed unexpectedly");
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"SignalR: Receive error - {ex.Message}");
                        break;
                    }

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        _logger?.LogInfo($"SignalR: Server closed connection: {ws.CloseStatusDescription}");
                        break;
                    }

                    messageBuffer.Write(buffer, 0, result.Count);

                    // Guard against unbounded buffer growth
                    if (messageBuffer.Length > MaxMessageBufferSize)
                    {
                        _logger?.LogError($"SignalR: Message buffer exceeded {MaxMessageBufferSize / (1024 * 1024)}MB limit — dropping and reconnecting");
                        messageBuffer.SetLength(0);
                        break;
                    }

                    if (result.EndOfMessage)
                    {
                        var data = Encoding.UTF8.GetString(messageBuffer.ToArray());
                        messageBuffer.SetLength(0);

                        // Split by record separator — multiple messages can arrive in one frame
                        var messages = data.Split(new[] { (char)RecordSeparator }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var msg in messages)
                        {
                            ProcessMessage(msg);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Receive loop error - {ex.Message}", ex);
            }
            finally
            {
                messageBuffer.Dispose();
            }

            // Connection lost — schedule reconnect (unless intentionally closed or disposed)
            if (!ct.IsCancellationRequested && !_isDisposed && !_intentionalClose)
            {
                _logger?.LogWarning("SignalR: Connection lost");
                SetState(SignalRConnectionState.Disconnected);

                if (_config.AutoReconnect)
                    ScheduleReconnect();
            }
        }

        private void ProcessMessage(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("type", out var typeElement))
                    return;

                var type = typeElement.GetInt32();

                switch (type)
                {
                    case 1: // Invocation (server → client method call)
                        HandleInvocation(root);
                        break;
                    case 3: // Completion (response to an invoke-with-id)
                        HandleCompletion(root);
                        break;
                    case 6: // Ping
                        _ = SendPingAsync();
                        break;
                    case 7: // Close
                        HandleClose(root);
                        break;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Failed to process message - {ex.Message}");
            }
        }

        private void HandleInvocation(JsonElement root)
        {
            if (!root.TryGetProperty("target", out var targetProp))
                return;

            var method = targetProp.GetString();
            if (string.IsNullOrEmpty(method))
                return;

            root.TryGetProperty("arguments", out var argumentsElement);

            // --- Lifecycle events (log only, no routing) ---
            switch (method)
            {
                case "Connected":
                    _logger?.LogInfo("SignalR: Server confirmed connection");
                    return;
                case "Subscribed":
                    _logger?.LogDebug("SignalR: Server confirmed subscription");
                    return;
                case "Unsubscribed":
                    _logger?.LogDebug("SignalR: Server confirmed unsubscription");
                    return;
                case "Pong":
                    _logger?.LogDebug("SignalR: Pong received");
                    return;
            }

            // --- Business messages → route to listeners via MessageReceived ---
            if (BusinessMethods.Contains(method))
            {
                _logger?.LogInfo($"SignalR: Received invocation '{method}' (arg count: {(argumentsElement.ValueKind == JsonValueKind.Array ? argumentsElement.GetArrayLength() : 0)})");

                string payload = null;
                string modelGuid = null;
                string sessionId = null;
                string username = null;

                if (argumentsElement.ValueKind == JsonValueKind.Array && argumentsElement.GetArrayLength() > 0)
                {
                    var arg = argumentsElement[0];
                    payload = arg.GetRawText();

                    // Extract common fields from payload for top-level access
                    if (arg.ValueKind == JsonValueKind.Object)
                    {
                        if (arg.TryGetProperty("modelGuid", out var mg) || arg.TryGetProperty("ModelGuid", out mg))
                            modelGuid = mg.GetString();
                        if (arg.TryGetProperty("sessionId", out var si) || arg.TryGetProperty("SessionId", out si))
                            sessionId = si.GetString();
                        if (arg.TryGetProperty("username", out var un) || arg.TryGetProperty("Username", out un))
                            username = un.GetString();
                    }
                }

                RaiseMessageReceived(new SignalRMessageInfo
                {
                    Method = method,
                    Payload = payload,
                    ModelGuid = modelGuid,
                    SenderSessionId = sessionId,
                    SenderUsername = username,
                    Timestamp = DateTime.UtcNow
                });
                return;
            }

            // --- Generic messages (GroupMessage, DirectMessage, Broadcast) ---
            if (method == "GroupMessage" || method == "DirectMessage" || method == "Broadcast")
            {
                if (argumentsElement.ValueKind == JsonValueKind.Array && argumentsElement.GetArrayLength() > 0)
                {
                    try
                    {
                        var msgInfo = JsonSerializer.Deserialize<SignalRMessageInfo>(
                            argumentsElement[0].GetRawText(),
                            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (msgInfo != null)
                        {
                            _logger?.LogDebug($"SignalR: Received {method}");
                            RaiseMessageReceived(msgInfo);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"SignalR: Failed to deserialize {method} - {ex.Message}");
                    }
                }
                return;
            }

            _logger?.LogDebug($"SignalR: Unhandled method '{method}'");
        }

        private void HandleCompletion(JsonElement root)
        {
            if (root.TryGetProperty("error", out var errorProp))
            {
                var error = errorProp.GetString();
                _logger?.LogWarning($"SignalR: Invocation error: {error}");
            }
        }

        private void HandleClose(JsonElement root)
        {
            var error = root.TryGetProperty("error", out var errorProp) ? errorProp.GetString() : null;
            _logger?.LogWarning($"SignalR: Server sent Close{(error != null ? $" - {error}" : "")}");
        }

        #endregion

        #region Keep-Alive

        private async Task KeepAliveLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(_config.HeartbeatInterval, ct);
                    var ws = _ws;
                    if (ws != null && ws.State == WebSocketState.Open)
                        await SendPingAsync();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Keep-alive error - {ex.Message}");
            }
        }

        #endregion

        #region Group Management

        public async Task JoinGroupAsync(string groupName)
        {
            if (string.IsNullOrEmpty(groupName))
                return;

            if (!IsConnected)
            {
                _logger?.LogWarning($"SignalR: Cannot join group '{groupName}' - not connected");
                // Store for later when connected
                lock (_lock) { _joinedGroups.Add(groupName); }
                return;
            }

            try
            {
                await InvokeSubscribeAsync(groupName);
                lock (_lock) { _joinedGroups.Add(groupName); }
                _logger?.LogInfo($"SignalR: Joined group '{groupName}'");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Failed to join group '{groupName}' - {ex.Message}", ex);
            }
        }

        public async Task LeaveGroupAsync(string groupName)
        {
            if (string.IsNullOrEmpty(groupName))
                return;

            lock (_lock) { _joinedGroups.Remove(groupName); }

            if (!IsConnected)
                return;

            try
            {
                await InvokeUnsubscribeAsync(groupName);
                _logger?.LogInfo($"SignalR: Left group '{groupName}'");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Failed to leave group '{groupName}' - {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Routes a group subscribe to the correct hub method.
        /// Identity groups (company, machine, admin) are auto-joined server-side via JWT claims.
        /// </summary>
        private async Task InvokeSubscribeAsync(string groupName)
        {
            var (prefix, id) = ParseGroupName(groupName);
            switch (prefix)
            {
                case "model": await SendInvocationAsync("SubscribeToModel", id); break;
                case "project": await SendInvocationAsync("SubscribeToProject", id); break;
                case "sync": await SendInvocationAsync("SubscribeToSync", id); break;
                case "conversation": await SendInvocationAsync("SubscribeToConversation", id); break;
                default:
                    _logger?.LogDebug(
                        $"SignalR: Group '{groupName}' — identity groups auto-joined via JWT");
                    break;
            }
        }

        private async Task InvokeUnsubscribeAsync(string groupName)
        {
            var (prefix, id) = ParseGroupName(groupName);
            switch (prefix)
            {
                case "model": await SendInvocationAsync("UnsubscribeFromModel", id); break;
                case "project": await SendInvocationAsync("UnsubscribeFromProject", id); break;
                case "sync": await SendInvocationAsync("UnsubscribeFromSync", id); break;
                case "conversation": await SendInvocationAsync("UnsubscribeFromConversation", id); break;
                default:
                    _logger?.LogDebug($"SignalR: Group '{groupName}' — no unsubscribe method");
                    break;
            }
        }

        private static (string prefix, string id) ParseGroupName(string groupName)
        {
            var colonIndex = groupName.IndexOf(':');
            return colonIndex < 0
                ? (groupName, string.Empty)
                : (groupName.Substring(0, colonIndex), groupName.Substring(colonIndex + 1));
        }

        private async Task RejoinGroups()
        {
            List<string> groupsToJoin;
            lock (_lock) { groupsToJoin = new List<string>(_joinedGroups); }

            foreach (var groupName in groupsToJoin)
            {
                try
                {
                    await InvokeSubscribeAsync(groupName);
                    _logger?.LogDebug($"SignalR: Rejoined group '{groupName}'");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"SignalR: Failed to rejoin group '{groupName}' - {ex.Message}", ex);
                }
            }
        }

        #endregion

        public async Task SendAsync(string method, params object[] args)
        {
            if (!IsConnected)
            {
                _logger?.LogDebug($"SignalR: Cannot send '{method}' - not connected");
                return;
            }

            try
            {
                await SendInvocationAsync(method, args);

                // Log presence and sync-related methods at INFO so they appear in production logs
                if (method == SignalRMethods.UserJoined || method == SignalRMethods.UserLeft ||
                    method == "RequestActiveUsers" ||
                    method == SignalRMethods.SyncStarting || method == SignalRMethods.SyncCompleted ||
                    method == SignalRMethods.SyncCancelled || method == SignalRMethods.SyncQueueJoin ||
                    method == SignalRMethods.SyncQueueLeave ||
                    // Cloud sync gate (v100): log RequestSyncSlot at INFO so production logs
                    // confirm whether the request actually went out. Without this, debugging
                    // a no-reply scenario relies on guesswork.
                    method == SignalRMethods.RequestSyncSlot)
                    _logger?.LogInfo($"SignalR: Sent '{method}' successfully");
                else
                    _logger?.LogDebug($"SignalR: Sent '{method}'");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Failed to send '{method}' - {ex.Message}", ex);
            }
        }

        public async Task DisconnectAsync()
        {
            if (_state == SignalRConnectionState.Disconnected || _state == SignalRConnectionState.Disconnecting)
                return;

            _intentionalClose = true;
            SetState(SignalRConnectionState.Disconnecting);
            _reconnectCts?.Cancel();
            StopSlowReconnect();

            StopBackgroundTasks();

            try
            {
                if (_ws?.State == WebSocketState.Open)
                {
                    _logger?.LogInfo("SignalR: Disconnecting...");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", cts.Token);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Error during disconnect - {ex.Message}");
            }

            DisposeWebSocket();

            lock (_lock) { _joinedGroups.Clear(); }

            SetState(SignalRConnectionState.Disconnected);
            _logger?.LogInfo("SignalR: Disconnected");
        }

        #region Reconnection Logic

        private void ScheduleReconnect()
        {
            if (_isDisposed || _state == SignalRConnectionState.Connecting)
                return;

            var currentAttempt = Thread.VolatileRead(ref _reconnectAttempt);
            if (currentAttempt >= MaxReconnectAttempts)
            {
                _logger?.LogWarning(
                    $"SignalR: Max fast reconnect attempts ({MaxReconnectAttempts}) reached — switching to slow reconnect (every {_config.SlowReconnectIntervalMs / 1000}s)");
                StartSlowReconnect();
                return;
            }

            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            _reconnectCts = new CancellationTokenSource();

            var delay = GetReconnectDelay();
            _logger?.LogInfo(
                $"SignalR: Scheduling reconnect attempt {currentAttempt + 1}/{MaxReconnectAttempts} in {delay}ms");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(delay, _reconnectCts.Token);

                    if (_reconnectCts.Token.IsCancellationRequested || _isDisposed)
                        return;

                    Interlocked.Increment(ref _reconnectAttempt);

                    // Refresh token before reconnecting
                    if (AccessTokenProvider != null)
                    {
                        try
                        {
                            var newToken = await AccessTokenProvider();
                            if (!string.IsNullOrEmpty(newToken))
                                _accessToken = newToken;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogWarning($"SignalR: Token refresh failed before reconnect: {ex.Message}");
                        }
                    }

                    StopBackgroundTasks();
                    DisposeWebSocket();

                    await ConnectInternalAsync(isReconnect: true);
                }
                catch (TaskCanceledException) { }
                catch (Exception ex)
                {
                    _logger?.LogError($"SignalR: Reconnect failed - {ex.Message}", ex);
                }
            });
        }

        private int GetReconnectDelay()
        {
            if (_config.ReconnectDelays == null || _config.ReconnectDelays.Length == 0)
                return 5000;

            var index = Math.Min(Thread.VolatileRead(ref _reconnectAttempt), _config.ReconnectDelays.Length - 1);
            var baseDelay = _config.ReconnectDelays[index];

            // Add ±20% jitter to prevent thundering herd on server recovery
            var jitterRange = (int)(baseDelay * 0.2);
            if (jitterRange > 0)
            {
                lock (_jitterRng)
                {
                    baseDelay += _jitterRng.Next(-jitterRange, jitterRange + 1);
                }
            }

            return Math.Max(baseDelay, 1000); // minimum 1s delay
        }

        /// <summary>
        /// Starts a slow periodic reconnect timer after fast reconnect attempts are exhausted.
        /// Retries at configurable intervals until success, disposal, or max attempts reached.
        /// </summary>
        private void StartSlowReconnect()
        {
            StopSlowReconnect();
            Interlocked.Exchange(ref _slowReconnectAttempt, 0);
            _slowReconnectTimer = new Timer(async _ =>
            {
                if (_isDisposed || _state == SignalRConnectionState.Connected ||
                    _state == SignalRConnectionState.Connecting)
                    return;

                // Respect rate limit backoff
                if (_rateLimitedUntil > DateTime.UtcNow)
                {
                    _logger?.LogDebug($"SignalR: Slow reconnect skipped — rate-limited for {(_rateLimitedUntil - DateTime.UtcNow).TotalSeconds:F0}s");
                    return;
                }

                // Check max slow reconnect attempts
                var attempt = Interlocked.Increment(ref _slowReconnectAttempt);
                if (attempt > _config.MaxSlowReconnectAttempts)
                {
                    _logger?.LogWarning($"SignalR: Max slow reconnect attempts ({_config.MaxSlowReconnectAttempts}) reached — giving up.");
                    StopSlowReconnect();
                    return;
                }

                // Non-blocking: skip if another connection attempt is in progress
                if (!await _connectLock.WaitAsync(0))
                {
                    _logger?.LogDebug("SignalR: Slow reconnect skipped — connection attempt already in progress");
                    return;
                }
                _connectLock.Release(); // Release immediately — ConnectInternalAsync will re-acquire

                _logger?.LogInfo($"SignalR: Attempting slow reconnect ({attempt}/{_config.MaxSlowReconnectAttempts})...");
                try
                {
                    // Refresh token before reconnecting
                    if (AccessTokenProvider != null)
                    {
                        try
                        {
                            var newToken = await AccessTokenProvider();
                            if (!string.IsNullOrEmpty(newToken))
                                _accessToken = newToken;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogDebug($"SignalR: Token refresh failed before slow reconnect: {ex.Message}");
                        }
                    }

                    StopBackgroundTasks();
                    DisposeWebSocket();
                    Interlocked.Exchange(ref _reconnectAttempt, 0);
                    await ConnectInternalAsync(isReconnect: true);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug($"SignalR: Slow reconnect failed — {ex.Message}");
                }
            }, null, _config.SlowReconnectIntervalMs, _config.SlowReconnectIntervalMs);
        }

        private void StopSlowReconnect()
        {
            _slowReconnectTimer?.Dispose();
            _slowReconnectTimer = null;
        }

        #endregion

        #region Helpers

        private void RaiseMessageReceived(SignalRMessageInfo message)
        {
            try
            {
                MessageReceived?.Invoke(this, message);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Error in message handler - {ex.Message}", ex);
            }
        }

        private void SetState(SignalRConnectionState newState, Exception error = null)
        {
            var oldState = _state;
            if (oldState == newState)
                return;

            _state = newState;

            if (newState == SignalRConnectionState.Connected)
                _lastConnectedAt = DateTime.UtcNow;

            if (error != null)
                _lastError = error.Message;
            else if (newState == SignalRConnectionState.Connected)
                _lastError = null;

            _logger?.LogInfo($"SignalR: Connection state changed from {oldState} to {newState}");

            try
            {
                ConnectionStateChanged?.Invoke(this,
                    new SignalRConnectionStateChangedEventArgs(oldState, newState, error));
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Error in state change handler - {ex.Message}", ex);
            }
        }

        private void StopBackgroundTasks()
        {
            _keepAliveCts?.Cancel();
            _receiveCts?.Cancel();
        }

        private void DisposeWebSocket()
        {
            try { _ws?.Dispose(); }
            catch { }
            _ws = null;

            _keepAliveCts?.Dispose();
            _keepAliveCts = null;
            _receiveCts?.Dispose();
            _receiveCts = null;
        }

        #endregion

        #region Factory Methods

        // Shared cookie jar: Azure App Service / SignalR Service issues sticky-session cookies
        // (e.g. ARRAffinity) on /negotiate that MUST be replayed on the WebSocket upgrade,
        // otherwise the WS request can land on a different backend that doesn't know the connection.
        private readonly System.Net.CookieContainer _cookieJar = new System.Net.CookieContainer();

        private HttpClient CreateHttpClient()
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = _sslPolicy != null
                    ? _sslPolicy.Validate
                    : (_, _, _, _) => true,
                // Use system proxy (corporate networks) and forward credentials
                UseProxy = true,
                Proxy = System.Net.WebRequest.GetSystemWebProxy(),
                UseDefaultCredentials = true,
                // Capture sticky-session cookies from /negotiate
                CookieContainer = _cookieJar,
                UseCookies = true,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
            };
            return new HttpClient(handler) { Timeout = _config.ConnectionTimeout };
        }

        private ClientWebSocket CreateWebSocket()
        {
            var ws = new ClientWebSocket();

            // Forward sticky-session cookies from /negotiate so the WS upgrade lands on the same backend
            ws.Options.Cookies = _cookieJar;

            // TCP-level keep-alive — corporate firewalls and home routers drop idle TCP connections
            // after 30s–5min. App-level pings (every HeartbeatInterval) keep the JSON Hub Protocol
            // alive but ALSO need TCP frames or NAT entries time out. Use half the heartbeat interval.
            var keepAlive = TimeSpan.FromSeconds(Math.Max(_config.HeartbeatInterval.TotalSeconds / 2, 15));
            ws.Options.KeepAliveInterval = keepAlive;

            // Proxy: ClientWebSocket does NOT inherit HttpClient proxy settings, must be set explicitly.
            // Use system proxy (WPAD/PAC/registry) so corporate environments work without configuration.
            try
            {
                var systemProxy = System.Net.WebRequest.GetSystemWebProxy();
                var hubUri = new Uri(_config.HubUrl);
                var proxyUri = systemProxy?.GetProxy(hubUri);
                if (proxyUri != null && proxyUri != hubUri)
                {
                    ws.Options.Proxy = systemProxy;
                    _logger?.LogDebug($"SignalR: Using system proxy {proxyUri} for WebSocket");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"SignalR: Proxy detection failed (continuing without proxy): {ex.Message}");
            }

#if !NETFRAMEWORK
            // .NET 5+ has per-socket SSL callback; .NET Framework relies on ServicePointManager
            ws.Options.RemoteCertificateValidationCallback = _sslPolicy != null
                ? _sslPolicy.ValidateLegacy
                : (_, _, _, _) => true;
#endif
            return ws;
        }

        #endregion

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            _reconnectCts?.Cancel();
            _reconnectCts?.Dispose();
            StopSlowReconnect();

            StopBackgroundTasks();

            try { _ws?.Dispose(); }
            catch { }
            _ws = null;

            _keepAliveCts?.Dispose();
            _receiveCts?.Dispose();
            _sendLock?.Dispose();
            _connectLock?.Dispose();

            try { _httpClient?.Dispose(); }
            catch { }

            _logger?.LogInfo("SignalR: Service disposed");
        }
    }
}
