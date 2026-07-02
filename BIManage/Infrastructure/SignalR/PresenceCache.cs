using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR
{
    /// <summary>
    /// In-memory cache of active users per model, populated from SignalR
    /// UserJoined / UserLeft / ActiveUsersUpdate broadcasts.
    ///
    /// Subscribed at app startup (before any dialog opens) so late-opening
    /// dialogs can replay historical presence events. This compensates for
    /// the server's RequestActiveUsers returning an incomplete roster —
    /// if another user joined the model BEFORE we did, we may have missed
    /// their initial presence broadcast, but any subsequent UserJoined
    /// rebroadcast or cross-user heartbeat lands here.
    /// </summary>
    public class PresenceCache : IDisposable
    {
        private readonly ISignalREventBus _eventBus;
        private readonly ILogger? _logger;

        // modelGuid → (sessionId → user)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, UserPresencePayload>> _byModel
            = new ConcurrentDictionary<string, ConcurrentDictionary<string, UserPresencePayload>>(StringComparer.OrdinalIgnoreCase);

        private Action<UserJoinedEvent>? _onJoined;
        private Action<UserLeftEvent>? _onLeft;
        private Action<ActiveUsersUpdatedEvent>? _onRosterUpdate;

        private bool _disposed;

        public PresenceCache(ISignalREventBus eventBus, ILogger? logger = null)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
            _logger = logger;

            _onJoined = HandleJoined;
            _onLeft = HandleLeft;
            _onRosterUpdate = HandleRosterUpdate;

            _eventBus.Subscribe(_onJoined);
            _eventBus.Subscribe(_onLeft);
            _eventBus.Subscribe(_onRosterUpdate);

            _logger?.LogInfo("[PresenceCache] Subscribed to presence events");
        }

        /// <summary>
        /// Returns the cached list of users currently joined to the given model.
        /// May be empty if no presence broadcasts have been observed.
        /// </summary>
        public IReadOnlyList<UserPresencePayload> GetUsersForModel(string? modelGuid)
        {
            if (string.IsNullOrEmpty(modelGuid)) return Array.Empty<UserPresencePayload>();
            if (!_byModel.TryGetValue(modelGuid, out var map)) return Array.Empty<UserPresencePayload>();
            return map.Values.ToList();
        }

        private void HandleJoined(UserJoinedEvent e)
        {
            if (e?.User == null || string.IsNullOrEmpty(e.ModelGuid) || string.IsNullOrEmpty(e.User.SessionId))
                return;

            var map = _byModel.GetOrAdd(e.ModelGuid,
                _ => new ConcurrentDictionary<string, UserPresencePayload>(StringComparer.OrdinalIgnoreCase));
            map[e.User.SessionId] = e.User;
        }

        private void HandleLeft(UserLeftEvent e)
        {
            if (e == null || string.IsNullOrEmpty(e.ModelGuid) || string.IsNullOrEmpty(e.SessionId))
                return;

            if (_byModel.TryGetValue(e.ModelGuid, out var map))
                map.TryRemove(e.SessionId, out _);
        }

        private void HandleRosterUpdate(ActiveUsersUpdatedEvent e)
        {
            // Merge the server roster into the cache but don't remove users that aren't listed —
            // the server may be returning an incomplete roster. UserLeft events are the
            // authoritative source for removal.
            if (e?.Users == null || string.IsNullOrEmpty(e.ModelGuid)) return;

            var map = _byModel.GetOrAdd(e.ModelGuid,
                _ => new ConcurrentDictionary<string, UserPresencePayload>(StringComparer.OrdinalIgnoreCase));

            foreach (var u in e.Users)
            {
                if (u == null || string.IsNullOrEmpty(u.SessionId)) continue;
                map[u.SessionId] = u;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_onJoined != null) _eventBus.Unsubscribe(_onJoined);
                if (_onLeft != null) _eventBus.Unsubscribe(_onLeft);
                if (_onRosterUpdate != null) _eventBus.Unsubscribe(_onRosterUpdate);
            }
            catch { }

            _onJoined = null;
            _onLeft = null;
            _onRosterUpdate = null;

            _byModel.Clear();
        }
    }
}
