using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Groups;
using BIManage.Infrastructure.SignalR.Listeners;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR.Core
{
    /// <summary>
    /// Manages SignalR connection lifecycle and coordinates message routing to listeners
    /// </summary>
    public class SignalRConnectionManager : ISignalRConnectionManager
    {

        private readonly ISignalRService _signalRService;
        private readonly ISignalRGroupHelper _groupHelper;
        private readonly ILogger _logger;
        // Optional — only set when server-arbitrated cloud sync gate (Option D) is wired.
        // Used by RequestSyncSlotAsync to await a server reply; null when caller did not
        // register a coordinator (legacy bootstrap paths / tests). When null,
        // RequestSyncSlotAsync immediately returns Unavailable and the cloud-gate caller
        // falls back to the client-side STC gate.
        private readonly SyncSlotResponseCoordinator? _slotCoordinator;
        private readonly object _lock = new object();

        private readonly List<ISignalListener> _listeners = new List<ISignalListener>();
        private readonly HashSet<string> _activeGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DocumentContext> _openDocuments = new Dictionary<string, DocumentContext>(StringComparer.OrdinalIgnoreCase);

        private bool _isDisposed;

        public event EventHandler<SignalRConnectionStateChangedEventArgs> ConnectionStateChanged;

        public bool IsConnected => _signalRService?.IsConnected ?? false;
        public SignalRConnectionState State => _signalRService?.State ?? SignalRConnectionState.Disconnected;
        public IReadOnlyList<string> ActiveGroups
        {
            get
            {
                lock (_lock) { return _activeGroups.ToList(); }
            }
        }
        public int ListenerCount
        {
            get
            {
                lock (_lock) { return _listeners.Count; }
            }
        }

        public SignalRConnectionManager(
            ISignalRService signalRService,
            ISignalRGroupHelper groupHelper,
            ILogger logger,
            SyncSlotResponseCoordinator? slotCoordinator = null)
        {
            _signalRService = signalRService ?? throw new ArgumentNullException(nameof(signalRService));
            _groupHelper = groupHelper ?? throw new ArgumentNullException(nameof(groupHelper));
            _logger = logger;
            _slotCoordinator = slotCoordinator;

            // Subscribe to SignalR service events
            _signalRService.ConnectionStateChanged += OnServiceConnectionStateChanged;
            _signalRService.MessageReceived += OnMessageReceived;
        }

        public void RegisterListener(ISignalListener listener)
        {
            if (listener == null)
                throw new ArgumentNullException(nameof(listener));

            bool shouldConnect;
            lock (_lock)
            {
                if (_listeners.Contains(listener))
                {
                    _logger?.LogWarning($"SignalR: Listener already registered");
                    return;
                }

                _listeners.Add(listener);
                shouldConnect = _listeners.Count == 1 && _signalRService.AccessTokenProvider != null;
                _logger?.LogInfo($"SignalR: Registered listener (total: {_listeners.Count})");
            }

            // Start connection when first listener registers
            if (shouldConnect)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await ConnectAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"SignalR: Failed to auto-connect - {ex.Message}", ex);
                    }
                });
            }
        }

        public void UnregisterListener(ISignalListener listener)
        {
            if (listener == null)
                return;

            bool shouldDisconnect;
            lock (_lock)
            {
                if (!_listeners.Remove(listener))
                    return;

                shouldDisconnect = _listeners.Count == 0;
                _logger?.LogInfo($"SignalR: Unregistered listener (remaining: {_listeners.Count})");
            }

            // Stop connection when last listener unregisters
            if (shouldDisconnect)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        await DisconnectAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError($"SignalR: Failed to auto-disconnect - {ex.Message}", ex);
                    }
                });
            }
        }

        public async Task OnDocumentOpenedAsync(string modelGuid, string projectId)
        {
            if (string.IsNullOrEmpty(modelGuid))
                return;

            var context = new DocumentContext
            {
                ModelGuid = modelGuid,
                ProjectId = projectId
            };

            lock (_lock)
            {
                _openDocuments[modelGuid] = context;
            }

            _logger?.LogInfo($"SignalR: Document opened - Model: {modelGuid}, Project: {projectId}");

            // Join groups for this document
            var groupsToJoin = GetGroupsForContext(context);
            foreach (var group in groupsToJoin)
            {
                await JoinGroupAsync(group);
            }
        }

        public async Task OnDocumentClosedAsync(string modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid))
                return;

            DocumentContext context;
            lock (_lock)
            {
                if (!_openDocuments.TryGetValue(modelGuid, out context))
                    return;

                _openDocuments.Remove(modelGuid);
            }

            _logger?.LogInfo($"SignalR: Document closed - Model: {modelGuid}");

            // Determine which groups to leave (only if no other docs need them)
            var groupsToLeave = GetGroupsForContext(context);
            var groupsStillNeeded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            lock (_lock)
            {
                foreach (var doc in _openDocuments.Values)
                {
                    foreach (var group in GetGroupsForContext(doc))
                    {
                        groupsStillNeeded.Add(group);
                    }
                }
            }

            foreach (var group in groupsToLeave)
            {
                if (!groupsStillNeeded.Contains(group))
                {
                    await LeaveGroupAsync(group);
                }
            }
        }

        public async Task ConnectAsync(string accessToken = null)
        {
            if (_isDisposed)
                return;

            try
            {
                await _signalRService.ConnectAsync(accessToken);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Connection failed - {ex.Message}", ex);
                throw;
            }
        }

        public async Task DisconnectAsync()
        {
            try
            {
                await _signalRService.DisconnectAsync();

                lock (_lock)
                {
                    _activeGroups.Clear();
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Disconnect failed - {ex.Message}", ex);
            }
        }

        public async Task JoinGroupAsync(string groupName)
        {
            if (string.IsNullOrEmpty(groupName))
                return;

            lock (_lock)
            {
                if (_activeGroups.Contains(groupName))
                    return;

                _activeGroups.Add(groupName);
            }

            try
            {
                await _signalRService.JoinGroupAsync(groupName);
                _logger?.LogInfo($"SignalR: Joined group '{groupName}'");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Failed to join group '{groupName}' - {ex.Message}", ex);
                lock (_lock)
                {
                    _activeGroups.Remove(groupName);
                }
            }
        }

        public async Task LeaveGroupAsync(string groupName)
        {
            if (string.IsNullOrEmpty(groupName))
                return;

            lock (_lock)
            {
                if (!_activeGroups.Remove(groupName))
                    return;
            }

            try
            {
                await _signalRService.LeaveGroupAsync(groupName);
                _logger?.LogInfo($"SignalR: Left group '{groupName}'");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Failed to leave group '{groupName}' - {ex.Message}", ex);
            }
        }

        private List<string> GetGroupsForContext(DocumentContext context)
        {
            var groups = new List<string>();

            // Document-specific groups only.
            // Identity groups (company, machine) are auto-joined server-side via JWT claims.

            // Model-specific group
            if (!string.IsNullOrEmpty(context.ModelGuid))
            {
                groups.Add(_groupHelper.GetModelGroup(context.ModelGuid));
                // Sync coordination group — receives SyncQueueUpdate, SyncStarting, SyncCompleted, etc.
                groups.Add(_groupHelper.GetSyncGroup(context.ModelGuid));
            }

            // Project-level group
            if (!string.IsNullOrEmpty(context.ProjectId))
            {
                groups.Add(_groupHelper.GetProjectGroup(context.ProjectId));
            }

            return groups;
        }

        private void OnServiceConnectionStateChanged(object sender, SignalRConnectionStateChangedEventArgs e)
        {
            _logger?.LogInfo($"SignalR: Connection state changed from {e.OldState} to {e.NewState}");

            try
            {
                ConnectionStateChanged?.Invoke(this, e);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Error in connection state handler - {ex.Message}", ex);
            }

            // Notify listeners of state changes
            if (e.NewState == SignalRConnectionState.Connected)
            {
                NotifyListenersConnected();
                // Re-announce presence for all open documents
                // (groups are re-joined at the SignalRService level via RejoinGroups)
                _ = ReannouncePresenceAsync().ContinueWith(t =>
                    _logger?.LogError($"SignalR: Failed to re-announce presence - {t.Exception?.InnerException?.Message}", t.Exception?.InnerException),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
            else if (e.NewState == SignalRConnectionState.Disconnected)
            {
                NotifyListenersDisconnected();
            }
        }

        private void OnMessageReceived(object sender, SignalRMessageInfo message)
        {
            if (message == null)
                return;

            List<ISignalListener> listenersToNotify;
            lock (_lock)
            {
                listenersToNotify = _listeners
                    .Where(l => l.SupportedMethods.Contains(message.Method))
                    .ToList();
            }

            _logger?.LogInfo($"SignalR: Routing '{message.Method}' to {listenersToNotify.Count} of {ListenerCount} listeners");

            if (listenersToNotify.Count == 0)
            {
                _logger?.LogWarning($"SignalR: No listener subscribed to method '{message.Method}' — check SignalRMethods constants vs server contract");
            }

            foreach (var listener in listenersToNotify)
            {
                try
                {
                    // Fire and forget - listeners handle their own errors
                    Task.Run(async () =>
                    {
                        try
                        {
                            await listener.HandleMessageAsync(message);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError($"SignalR: Listener error handling {message.Method} - {ex.Message}", ex);
                        }
                    });
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"SignalR: Failed to dispatch message to listener - {ex.Message}", ex);
                }
            }
        }

        private async Task ReannouncePresenceAsync()
        {
            List<Messages.UserPresencePayload> payloads;
            lock (_lock)
            {
                payloads = _openDocuments.Values
                    .Where(d => d.PresencePayload != null)
                    .Select(d => d.PresencePayload)
                    .ToList();
            }

            if (payloads.Count == 0)
                return;

            _logger?.LogInfo($"SignalR: Re-announcing presence for {payloads.Count} open document(s)");

            foreach (var payload in payloads)
            {
                try
                {
                    await _signalRService.SendAsync(SignalRMethods.UserJoined, payload);
                    _logger?.LogInfo($"SignalR: Re-announced presence on model {payload.ModelGuid} — {payload.RevitUsername ?? payload.Username}");
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"SignalR: Failed to re-announce presence on model {payload.ModelGuid} - {ex.Message}", ex);
                }
            }
        }

        private void NotifyListenersConnected()
        {
            List<ISignalListener> listeners;
            lock (_lock)
            {
                listeners = _listeners.ToList();
            }

            foreach (var listener in listeners)
            {
                try
                {
                    listener.OnReconnected();
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"SignalR: Listener error on connect - {ex.Message}", ex);
                }
            }
        }

        private void NotifyListenersDisconnected()
        {
            List<ISignalListener> listeners;
            lock (_lock)
            {
                listeners = _listeners.ToList();
            }

            foreach (var listener in listeners)
            {
                try
                {
                    listener.OnDisconnected();
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"SignalR: Listener error on disconnect - {ex.Message}", ex);
                }
            }
        }

        public async Task AnnouncePresenceAsync(Messages.UserPresencePayload payload)
        {
            if (payload == null || string.IsNullOrEmpty(payload.ModelGuid))
                return;

            // Store payload for re-announcement on reconnect
            lock (_lock)
            {
                if (_openDocuments.TryGetValue(payload.ModelGuid, out var ctx))
                    ctx.PresencePayload = payload;
            }

            try
            {
                await _signalRService.SendAsync(SignalRMethods.UserJoined, payload);
                _logger?.LogInfo($"SignalR: Announced presence on model {payload.ModelGuid} — {payload.RevitUsername ?? payload.Username}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Failed to announce presence - {ex.Message}", ex);
            }
        }

        public async Task AnnounceLeaveAsync(string modelGuid, string sessionId)
        {
            if (string.IsNullOrEmpty(modelGuid))
                return;

            try
            {
                var payload = new Messages.UserLeftPayload
                {
                    ModelGuid = modelGuid,
                    SessionId = sessionId
                };
                await _signalRService.SendAsync(SignalRMethods.UserLeft, payload);
                _logger?.LogInfo($"SignalR: Announced leave from model {modelGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"SignalR: Failed to announce leave - {ex.Message}", ex);
            }
        }

        public Task SendAsync(string method, params object[] args)
        {
            return _signalRService.SendAsync(method, args);
        }

        public async Task<SyncSlotDecision> RequestSyncSlotAsync(
            string modelGuid, string sessionId, string username, string revitUsername,
            DateTime claimedAtUtc, bool isCloud, TimeSpan timeout,
            System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(modelGuid) || string.IsNullOrEmpty(sessionId))
                return SyncSlotDecision.Unavailable("invalid-key");

            if (_slotCoordinator == null)
            {
                _logger?.LogDebug("[SyncSlot] RequestSyncSlotAsync called but coordinator is null — falling back");
                return SyncSlotDecision.Unavailable("no-coordinator");
            }

            if (!IsConnected)
            {
                _logger?.LogDebug("[SyncSlot] RequestSyncSlotAsync — SignalR not connected, returning Unavailable");
                return SyncSlotDecision.Unavailable("disconnected");
            }

            // Pre-register awaiter BEFORE sending so a fast server reply isn't dropped.
            var awaitTask = _slotCoordinator.AwaitDecisionAsync(sessionId, modelGuid, timeout, cancellationToken);

            try
            {
                var payload = new Messages.RequestSyncSlotPayload
                {
                    ModelGuid         = modelGuid,
                    SessionId         = sessionId,
                    Username          = username,
                    RevitUsername     = revitUsername,
                    // Server uses (RevitUsername, ComputerName) to detect the
                    // crash-and-reopen case and evict stale slots immediately.
                    ComputerName      = Environment.MachineName,
                    ClaimedAtUtcTicks = claimedAtUtc.Ticks,
                    IsCloud           = isCloud
                };
                await _signalRService.SendAsync(SignalRMethods.RequestSyncSlot, payload).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[SyncSlot] RequestSyncSlot send failed: {ex.Message} — falling back");
                // Don't bother awaiting — the reply can't arrive if send failed.
                return SyncSlotDecision.Unavailable("send-failed");
            }

            return await awaitTask.ConfigureAwait(false);
        }

        public IReadOnlyList<(string ModelGuid, string ProjectId)> GetOpenDocuments()
        {
            lock (_lock)
            {
                return _openDocuments.Values
                    .Select(d => (d.ModelGuid, d.ProjectId))
                    .ToList();
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            _signalRService.ConnectionStateChanged -= OnServiceConnectionStateChanged;
            _signalRService.MessageReceived -= OnMessageReceived;

            lock (_lock)
            {
                _listeners.Clear();
                _activeGroups.Clear();
                _openDocuments.Clear();
            }

            _logger?.LogInfo("SignalR: Connection manager disposed");
        }

        /// <summary>
        /// Internal class to track document context
        /// </summary>
        private class DocumentContext
        {
            public string ModelGuid { get; set; }
            public string ProjectId { get; set; }
            public Messages.UserPresencePayload PresencePayload { get; set; }
        }
    }
}
