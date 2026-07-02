using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using BIManage.Core.Detection;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManageRevit.BIManage.ViewModels.ModelActivities
{
    public class ModelActivitiesViewModel : INotifyPropertyChanged
    {
        private readonly SessionRepository? _sessionRepository;
        private readonly UnmonitoredUserDetectionService? _unmonitoredDetectionService;
        private readonly ILogger? _logger;
        private readonly ISignalREventBus? _eventBus;
        private readonly ISignalRConnectionManager? _connectionManager;
        private readonly global::BIManage.Infrastructure.SignalR.PresenceCache? _presenceCache;
        private readonly Dispatcher _dispatcher;

        // Stored delegate references required for unsubscription
        private Action<UserJoinedEvent>? _onUserJoined;
        private Action<UserLeftEvent>? _onUserLeft;
        private Action<ActiveUsersUpdatedEvent>? _onActiveUsersUpdated;
        private Action<ChatMessageReceivedEvent>? _onChatMessageReceived;

        // Chat context
        private string _currentUsername = "";
        private string? _projectId;
        private string? _chatScope;           // null = closed, "model" / "project" / "direct"
        private string? _chatTargetUsername;   // set only for direct scope

        // Unread counts
        private int _unreadModelCount;
        private int _unreadProjectCount;

        public int UnreadModelCount
        {
            get => _unreadModelCount;
            set { _unreadModelCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUnreadModel)); }
        }
        public int UnreadProjectCount
        {
            get => _unreadProjectCount;
            set { _unreadProjectCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUnreadProject)); }
        }
        public bool HasUnreadModel => _unreadModelCount > 0;
        public bool HasUnreadProject => _unreadProjectCount > 0;

        private bool _isLoading;
        private int _activeSessionCount;
        private string _modelName = "";
        private string _modelGuid = "";
        private string? _centralModelPath;
        private string? _modelType;

        // Ticks every 30 s while the dialog is open and forces every ActiveUserItem to
        // recompute SessionDuration. Without this, the duration column froze at whatever
        // value WPF first read from the binding — so a user who opened the dialog 30 s
        // after joining a model saw "1m" indefinitely no matter how long they stayed.
        // 30 s is the right cadence for a "1m / 2m / 3m" display granularity; faster
        // would just waste CPU re-computing for the same string.
        private DispatcherTimer? _durationTickTimer;

        public ModelActivitiesViewModel(
            SessionRepository? sessionRepository,
            ILogger? logger,
            ISignalREventBus? eventBus = null,
            ISignalRConnectionManager? connectionManager = null,
            Dispatcher? dispatcher = null,
            UnmonitoredUserDetectionService? unmonitoredDetectionService = null,
            global::BIManage.Infrastructure.SignalR.PresenceCache? presenceCache = null)
        {
            _sessionRepository = sessionRepository;
            _unmonitoredDetectionService = unmonitoredDetectionService;
            _logger = logger;
            _eventBus = eventBus;
            _connectionManager = connectionManager;
            _presenceCache = presenceCache;
            _dispatcher = dispatcher ?? Dispatcher.CurrentDispatcher;

            ActiveUsers = new ObservableCollection<ActiveUserItem>();
            UnmonitoredUsers = new ObservableCollection<UnmonitoredUserItem>();
            ChatMessages = new ObservableCollection<ChatMessage>();

            // Start the duration-refresh tick. Runs on the UI dispatcher so the
            // PropertyChanged invocations land on the right thread for WPF bindings.
            _durationTickTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _durationTickTimer.Tick += (_, __) =>
            {
                foreach (var item in ActiveUsers)
                    item.RefreshSessionDuration();
            };
            _durationTickTimer.Start();
        }

        /// <summary>
        /// Stop the tick timer when the dialog closes. Without this the timer keeps
        /// firing on the dispatcher long after the dialog is gone — harmless but a
        /// slow leak across many open/close cycles.
        /// </summary>
        public void StopDurationTimer()
        {
            try { _durationTickTimer?.Stop(); }
            catch { /* best-effort */ }
            _durationTickTimer = null;
        }

        public ObservableCollection<ActiveUserItem> ActiveUsers { get; set; }
        public ObservableCollection<UnmonitoredUserItem> UnmonitoredUsers { get; set; }
        public ObservableCollection<ChatMessage> ChatMessages { get; set; }

        private int _unmonitoredCount;
        public int UnmonitoredCount
        {
            get => _unmonitoredCount;
            set { _unmonitoredCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUnmonitoredUsers)); }
        }
        public bool HasUnmonitoredUsers => _unmonitoredCount > 0;

        public string ModelName
        {
            get => _modelName;
            set { _modelName = value; OnPropertyChanged(); }
        }

        public string ModelGuid
        {
            get => _modelGuid;
            set { _modelGuid = value; OnPropertyChanged(); }
        }

        public string? CentralModelPath
        {
            get => _centralModelPath;
            set { _centralModelPath = value; OnPropertyChanged(); }
        }

        public string? ModelType
        {
            get => _modelType;
            set { _modelType = value; OnPropertyChanged(); }
        }

        public bool IsLoading
        {
            get => _isLoading;
            set { _isLoading = value; OnPropertyChanged(); }
        }

        public int ActiveSessionCount
        {
            get => _activeSessionCount;
            set { _activeSessionCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(ActiveSessionsDisplay)); }
        }

        public string ActiveSessionsDisplay => ActiveSessionCount == 0
            ? "No active sessions"
            : $"{ActiveSessionCount} active session{(ActiveSessionCount != 1 ? "s" : "")}";

        public void SetModelInfo(string modelGuid, string modelName, string? centralModelPath, string? modelType)
        {
            ModelGuid = modelGuid;
            ModelName = modelName;
            CentralModelPath = centralModelPath;
            ModelType = modelType;
        }

        public void SetChatContext(string currentUsername, string? projectId)
        {
            _currentUsername = currentUsername;
            _projectId = projectId;
        }

        public void SetChatScope(string? scope, string? targetUsername)
        {
            _chatScope = scope;
            _chatTargetUsername = targetUsername;

            if (scope == "model")        UnreadModelCount = 0;
            else if (scope == "project") UnreadProjectCount = 0;
            else if (scope == "direct" && targetUsername != null)
            {
                var user = ActiveUsers.FirstOrDefault(u =>
                    string.Equals(u.Username, targetUsername, StringComparison.OrdinalIgnoreCase));
                if (user != null) user.UnreadCount = 0;
            }
        }

        public async Task SendChatMessageAsync(string text)
        {
            if (_connectionManager == null || string.IsNullOrEmpty(_chatScope) || string.IsNullOrEmpty(text))
                return;

            var payload = new ChatMessagePayload
            {
                Scope = _chatScope,
                ModelGuid = ModelGuid,
                ProjectId = _projectId ?? "",
                TargetUsername = _chatTargetUsername ?? "",
                SenderUsername = _currentUsername,
                SenderDisplayName = _currentUsername,
                Text = text,
                Timestamp = DateTime.UtcNow
            };

            try
            {
                await _connectionManager.SendAsync(SignalRMethods.SendChatMessage, payload);
                _logger?.LogDebug($"[Chat] Sent {_chatScope} message to SignalR");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[Chat] Failed to send message via SignalR: {ex.Message}");
            }
        }

        public async Task LoadActiveUsersAsync()
        {
            if (_sessionRepository == null || string.IsNullOrEmpty(ModelGuid)) return;

            IsLoading = true;

            try
            {
                var count = await _sessionRepository.GetActiveSessionCountByModelGuidAsync(ModelGuid);
                ActiveSessionCount = count;

                var users = await _sessionRepository.GetActiveUsersByModelGuidAsync(ModelGuid);

                ActiveUsers.Clear();
                var seenSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int rowNum = 1;

                foreach (var user in users)
                {
                    ActiveUsers.Add(new ActiveUserItem
                    {
                        RowNumber = rowNum++,
                        SessionId = user.SessionId,
                        Username = user.Username,
                        RevitUsername = user.RevitUsername,
                        UserEmail = user.UserEmail,
                        ComputerName = user.ComputerName,
                        OpenedAt = user.OpenedAt
                    });

                    if (!string.IsNullOrEmpty(user.SessionId))
                        seenSessionIds.Add(user.SessionId);
                }

                // Merge with the global PresenceCache — captures users who joined via SignalR
                // UserJoined broadcasts before the dialog was opened. The local DB only tracks
                // THIS machine's sessions, and the server's RequestActiveUsers may omit users
                // who joined before the caller.
                if (_presenceCache != null)
                {
                    int cacheAdded = 0;
                    foreach (var cachedUser in _presenceCache.GetUsersForModel(ModelGuid))
                    {
                        if (cachedUser == null || string.IsNullOrEmpty(cachedUser.SessionId)) continue;
                        if (seenSessionIds.Contains(cachedUser.SessionId)) continue;

                        ActiveUsers.Add(new ActiveUserItem
                        {
                            RowNumber = rowNum++,
                            SessionId = cachedUser.SessionId,
                            Username = cachedUser.Username ?? "",
                            RevitUsername = cachedUser.RevitUsername,
                            UserEmail = cachedUser.UserEmail,
                            ComputerName = cachedUser.ComputerName ?? "",
                            OpenedAt = cachedUser.JoinedAt
                        });
                        seenSessionIds.Add(cachedUser.SessionId);
                        cacheAdded++;
                    }

                    if (cacheAdded > 0)
                        _logger?.LogInfo($"[ModelActivities] PresenceCache added {cacheAdded} user(s) missing from local DB");
                }

                ActiveSessionCount = ActiveUsers.Count;
                _logger?.LogInfo($"Loaded {ActiveUsers.Count} active users for model {ModelName}");

                // Load unmonitored users for this model
                await LoadUnmonitoredUsersAsync();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed to load active users: {ex.Message}", ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task LoadUnmonitoredUsersAsync()
        {
            if (_unmonitoredDetectionService == null || string.IsNullOrEmpty(ModelGuid)) return;

            try
            {
                var detections = await _unmonitoredDetectionService.GetDetectionsByModelAsync(ModelGuid);

                // Filter out users who are also in the active (monitored) users list
                var monitoredNames = ActiveUsers
                    .Select(u => u.RevitUsername ?? u.Username)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                UnmonitoredUsers.Clear();
                int rowNum = 1;
                foreach (var d in detections)
                {
                    if (monitoredNames.Contains(d.RevitUsername)) continue;

                    UnmonitoredUsers.Add(new UnmonitoredUserItem
                    {
                        RowNumber = rowNum++,
                        RevitUsername = d.RevitUsername,
                        DetectionSource = FormatDetectionSource(d.DetectionSource),
                        DetectedAt = d.DetectedAt
                    });
                }

                UnmonitoredCount = UnmonitoredUsers.Count;
                if (UnmonitoredUsers.Count > 0)
                    _logger?.LogInfo($"Found {UnmonitoredUsers.Count} unmonitored user(s) for model {ModelName}");
            }
            catch (Exception ex)
            {
                _logger?.LogDebug($"Failed to load unmonitored users: {ex.Message}");
            }
        }

        private static string FormatDetectionSource(string source)
        {
            return source switch
            {
                "workset_owner" => "Workset owner",
                "element_checkout" => "Element checked out",
                "last_changed_by" => "Last modified by",
                _ => source
            };
        }

        /// <summary>
        /// Subscribe to SignalR presence events for the current model.
        /// Call after SetModelInfo + LoadActiveUsersAsync.
        /// </summary>
        public void SubscribeSignalREvents()
        {
            if (_eventBus == null || string.IsNullOrEmpty(ModelGuid))
                return;

            _onUserJoined = OnUserJoined;
            _onUserLeft = OnUserLeft;
            _onActiveUsersUpdated = OnActiveUsersUpdated;
            _onChatMessageReceived = OnChatMessageReceived;

            _eventBus.Subscribe(_onUserJoined);
            _eventBus.Subscribe(_onUserLeft);
            _eventBus.Subscribe(_onActiveUsersUpdated);
            _eventBus.Subscribe(_onChatMessageReceived);

            _logger?.LogDebug($"[ModelActivities] Subscribed to SignalR presence events for model {ModelGuid}");

            // Request the full active users roster from the server
            // If the server supports it, it responds with ActiveUsersUpdate
            RequestActiveUsersFromServer();
        }

        /// <summary>
        /// Sends a RequestActiveUsers invocation to the server.
        /// The server responds with ActiveUsersUpdate containing all users on this model.
        /// </summary>
        private async void RequestActiveUsersFromServer()
        {
            if (_connectionManager == null || string.IsNullOrEmpty(ModelGuid))
                return;

            try
            {
                await _connectionManager.SendAsync("RequestActiveUsers", ModelGuid);
                _logger?.LogDebug($"[ModelActivities] Requested active users roster for model {ModelGuid}");
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[ModelActivities] Failed to request active users from server: {ex.Message}");
            }
        }

        /// <summary>
        /// Unsubscribe from SignalR presence events. Call when the dialog closes.
        /// </summary>
        public void UnsubscribeSignalREvents()
        {
            if (_eventBus == null)
                return;

            if (_onUserJoined != null)   { _eventBus.Unsubscribe(_onUserJoined);        _onUserJoined = null; }
            if (_onUserLeft != null)     { _eventBus.Unsubscribe(_onUserLeft);           _onUserLeft = null; }
            if (_onActiveUsersUpdated != null) { _eventBus.Unsubscribe(_onActiveUsersUpdated); _onActiveUsersUpdated = null; }
            if (_onChatMessageReceived != null) { _eventBus.Unsubscribe(_onChatMessageReceived); _onChatMessageReceived = null; }

            _logger?.LogDebug($"[ModelActivities] Unsubscribed from SignalR presence events for model {ModelGuid}");
        }

        private void OnUserJoined(UserJoinedEvent e)
        {
            if (e?.User == null || e.ModelGuid != ModelGuid)
                return;

            RunOnUiThread(() =>
            {
                // Avoid duplicates (reconnect scenario)
                foreach (var existing in ActiveUsers)
                {
                    if (existing.SessionId == e.User.SessionId)
                        return;
                }

                ActiveUsers.Add(new ActiveUserItem
                {
                    RowNumber = ActiveUsers.Count + 1,
                    SessionId = e.User.SessionId ?? "",
                    Username = e.User.Username ?? "",
                    RevitUsername = e.User.RevitUsername,
                    UserEmail = e.User.UserEmail,
                    ComputerName = e.User.ComputerName ?? "",
                    OpenedAt = e.User.JoinedAt
                });

                ActiveSessionCount = ActiveUsers.Count;
                _logger?.LogInfo($"[ModelActivities] UserJoined: {e.User.RevitUsername ?? e.User.Username}");

                // Newly joined user may have a stale detection row — re-filter.
                _ = LoadUnmonitoredUsersAsync();
            });
        }

        private void OnUserLeft(UserLeftEvent e)
        {
            if (e == null || e.ModelGuid != ModelGuid)
                return;

            RunOnUiThread(() =>
            {
                for (int i = ActiveUsers.Count - 1; i >= 0; i--)
                {
                    if (ActiveUsers[i].SessionId == e.SessionId)
                    {
                        ActiveUsers.RemoveAt(i);
                        break;
                    }
                }

                // Renumber rows
                for (int i = 0; i < ActiveUsers.Count; i++)
                    ActiveUsers[i].RowNumber = i + 1;

                ActiveSessionCount = ActiveUsers.Count;
                _logger?.LogInfo($"[ModelActivities] UserLeft: session {e.SessionId}");
            });
        }

        private void OnActiveUsersUpdated(ActiveUsersUpdatedEvent e)
        {
            if (e == null || e.ModelGuid != ModelGuid)
                return;

            RunOnUiThread(() =>
            {
                // If server returns 0 users but we already have local active users,
                // keep the local data (server may not track deactivated models)
                if ((e.Users == null || e.Users.Count == 0) && ActiveUsers.Count > 0)
                {
                    _logger?.LogInfo($"[ModelActivities] ActiveUsersUpdated: server returned 0 users but local has {ActiveUsers.Count} — keeping local data");
                    return;
                }

                // Preserve the current user's entry from the local list before clearing —
                // server-side RequestActiveUsers often omits the calling user, so without
                // this merge the current user disappears from Model Activities when another
                // user is online.
                ActiveUserItem? currentUserEntry = null;
                if (!string.IsNullOrEmpty(_currentUsername))
                {
                    foreach (var u in ActiveUsers)
                    {
                        if (string.Equals(u.RevitUsername, _currentUsername, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(u.Username, _currentUsername, StringComparison.OrdinalIgnoreCase))
                        {
                            currentUserEntry = u;
                            break;
                        }
                    }
                }

                ActiveUsers.Clear();

                int rowNum = 1;
                bool currentUserAdded = false;

                if (e.Users != null)
                {
                    foreach (var user in e.Users)
                    {
                        bool isCurrentUser =
                            !string.IsNullOrEmpty(_currentUsername) &&
                            (string.Equals(user.RevitUsername, _currentUsername, StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(user.Username, _currentUsername, StringComparison.OrdinalIgnoreCase));

                        // For the current user, prefer the locally-preserved OpenedAt — that
                        // value was set at the actual DocumentOpened moment via
                        // BuildPresencePayload(JoinedAt = DateTime.UtcNow). The server's copy
                        // can drift: SignalR reconnects (more frequent on Revit 2022 / net48)
                        // re-announce the same payload, but if the server treats each announce
                        // as a fresh join it refreshes the timestamp, and the duration column
                        // then collapses to "1m" each time we receive an ActiveUsersUpdate.
                        // Local clock is the authoritative source for "when did I open this
                        // model?" — always use it for ourselves; trust the server only for
                        // other users (where it's the only signal we have).
                        var openedAt = (isCurrentUser && currentUserEntry != null)
                            ? currentUserEntry.OpenedAt
                            : user.JoinedAt;

                        ActiveUsers.Add(new ActiveUserItem
                        {
                            RowNumber = rowNum++,
                            SessionId = user.SessionId ?? "",
                            Username = user.Username ?? "",
                            RevitUsername = user.RevitUsername,
                            UserEmail = user.UserEmail,
                            ComputerName = user.ComputerName ?? "",
                            OpenedAt = openedAt
                        });

                        if (isCurrentUser) currentUserAdded = true;
                    }
                }

                // If the server response did NOT include the current user, re-add them from the
                // local entry we preserved above. This ensures the user always sees their own session.
                if (!currentUserAdded && currentUserEntry != null)
                {
                    currentUserEntry.RowNumber = rowNum++;
                    ActiveUsers.Add(currentUserEntry);
                }

                ActiveSessionCount = ActiveUsers.Count;
                _logger?.LogInfo($"[ModelActivities] ActiveUsersUpdated: {ActiveUsers.Count} users for model {ModelGuid} (server: {e.Users?.Count ?? 0}, self re-added: {!currentUserAdded && currentUserEntry != null})");

                // Active-user roster just changed — re-run the unmonitored filter so
                // anyone the server just confirmed as active drops out of the
                // Unmonitored Users list (fixes first-open race).
                _ = LoadUnmonitoredUsersAsync();
            });
        }

        private void OnChatMessageReceived(ChatMessageReceivedEvent e)
        {
            if (e == null) return;

            // Never process own messages
            if (string.Equals(e.SenderUsername, _currentUsername, StringComparison.OrdinalIgnoreCase))
                return;

            bool panelOpen = !string.IsNullOrEmpty(_chatScope);
            bool matchesCurrent = panelOpen && IsRelevantForCurrentScope(e);

            if (!matchesCurrent)
            {
                if (IsRelevantToUser(e))
                    RunOnUiThread(() => IncrementUnreadCount(e));
                return;
            }

            RunOnUiThread(() => AddChatMessage(e.SenderDisplayName ?? e.SenderUsername, e.Text, isCurrentUser: false));
        }

        private bool IsRelevantForCurrentScope(ChatMessageReceivedEvent e) =>
            _chatScope switch
            {
                "model"   => e.Scope == "model"   && e.ModelGuid == ModelGuid,
                "project" => e.Scope == "project" && e.ProjectId == _projectId,
                "direct"  => e.Scope == "direct"  && e.ModelGuid == ModelGuid
                             && (string.Equals(e.SenderUsername, _chatTargetUsername, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(e.TargetUsername, _currentUsername, StringComparison.OrdinalIgnoreCase)),
                _ => false
            };

        private bool IsRelevantToUser(ChatMessageReceivedEvent e) =>
            (e.Scope == "model"   && e.ModelGuid == ModelGuid) ||
            (e.Scope == "project" && e.ProjectId == _projectId) ||
            (e.Scope == "direct"  && e.ModelGuid == ModelGuid
             && string.Equals(e.TargetUsername, _currentUsername, StringComparison.OrdinalIgnoreCase));

        private void IncrementUnreadCount(ChatMessageReceivedEvent e)
        {
            if (e.Scope == "model" && e.ModelGuid == ModelGuid)
                UnreadModelCount++;
            else if (e.Scope == "project" && e.ProjectId == _projectId)
                UnreadProjectCount++;
            else if (e.Scope == "direct" && e.ModelGuid == ModelGuid && e.TargetUsername == _currentUsername)
            {
                var sender = ActiveUsers.FirstOrDefault(u =>
                    string.Equals(u.Username, e.SenderUsername, StringComparison.OrdinalIgnoreCase));
                if (sender != null) sender.UnreadCount++;
            }
        }

        private void RunOnUiThread(Action action)
        {
            if (_dispatcher.CheckAccess())
                action();
            else
                _dispatcher.Invoke(action);
        }

        public void AddChatMessage(string sender, string text, bool isCurrentUser = false)
        {
            ChatMessages.Add(new ChatMessage
            {
                Sender = sender,
                Text = text,
                Timestamp = DateTime.Now,
                IsCurrentUser = isCurrentUser
            });
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public class ActiveUserItem : INotifyPropertyChanged
    {
        public int RowNumber { get; set; }
        public string SessionId { get; set; } = "";
        public string Username { get; set; } = "";
        public string? RevitUsername { get; set; }
        public string? UserEmail { get; set; }
        public string ComputerName { get; set; } = "";
        public DateTime OpenedAt { get; set; }

        public string UsernameDisplay => !string.IsNullOrEmpty(RevitUsername) ? RevitUsername : Username;

        public string SessionDuration
        {
            get
            {
                // Normalize OpenedAt to UTC so the subtraction below produces a real
                // wall-clock duration regardless of where the value originated:
                //   Kind=Utc          → use as-is
                //   Kind=Local        → convert to UTC (handles values that came in via
                //                       ToString()/Parse round-trips that flipped to local)
                //   Kind=Unspecified  → treat as UTC. Server payloads are always UTC; this
                //                       branch fires when System.Text.Json deserializes a
                //                       timestamp without a 'Z' suffix or when SQLite's
                //                       DateTime.Parse drops the kind. Without this, a 5:30h
                //                       (IST) offset produces a negative TimeSpan and the
                //                       dialog always shows "1m" no matter how long the user
                //                       has actually been in the model.
                DateTime openedUtc = OpenedAt.Kind switch
                {
                    DateTimeKind.Utc => OpenedAt,
                    DateTimeKind.Local => OpenedAt.ToUniversalTime(),
                    _ => DateTime.SpecifyKind(OpenedAt, DateTimeKind.Utc),
                };

                var ts = DateTime.UtcNow - openedUtc;
                if (ts < TimeSpan.Zero) ts = TimeSpan.Zero;

                if (ts.TotalHours >= 1)
                    return $"{(int)ts.TotalHours}h {ts.Minutes}m";
                if (ts.TotalMinutes < 1)
                    return "just now";
                return $"{(int)ts.TotalMinutes}m";
            }
        }

        public string UserTooltip => $"Windows: {Username}\nRevit: {RevitUsername ?? "N/A"}\nEmail: {UserEmail ?? "N/A"}\nComputer: {ComputerName}\nSession since: {OpenedAt:yyyy-MM-dd HH:mm}";

        private int _unreadCount;
        public int UnreadCount
        {
            get => _unreadCount;
            set { _unreadCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasUnread)); }
        }
        public bool HasUnread => _unreadCount > 0;

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        /// <summary>
        /// Tells WPF that <see cref="SessionDuration"/> changed so the bound TextBlock
        /// re-reads the getter. The getter computes the value from
        /// <c>DateTime.UtcNow - OpenedAt</c> every call, so just raising PropertyChanged
        /// is enough — no state to update. Called by the parent ViewModel's tick timer
        /// (~ every 30 seconds) so the "1m" / "5m" / "1h 12m" string keeps advancing
        /// while the dialog stays open, instead of being frozen at the first value WPF
        /// happened to read.
        /// </summary>
        public void RefreshSessionDuration() => OnPropertyChanged(nameof(SessionDuration));
    }

    public class UnmonitoredUserItem
    {
        public int RowNumber { get; set; }
        public string RevitUsername { get; set; } = "";
        public string DetectionSource { get; set; } = "";
        public string DetectedAt { get; set; } = "";

        public string DetectedAtDisplay
        {
            get
            {
                if (DateTime.TryParse(DetectedAt, out var dt))
                    return dt.ToLocalTime().ToString("MMM dd, HH:mm");
                return DetectedAt;
            }
        }
    }

    public class ChatMessage : INotifyPropertyChanged
    {
        public string Sender { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTime Timestamp { get; set; }
        public bool IsCurrentUser { get; set; }

        public string TimeDisplay => Timestamp.ToString("HH:mm");

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
