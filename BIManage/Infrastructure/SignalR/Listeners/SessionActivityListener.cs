using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR.Listeners
{
    /// <summary>
    /// Listens for server-pushed user-presence and session-activity messages
    /// and publishes them to the internal SignalREventBus so ViewModels
    /// (e.g. ModelActivitiesViewModel) can update in real-time without polling.
    ///
    /// Handled methods:
    ///   UserJoined          → UserJoinedEvent
    ///   UserLeft            → UserLeftEvent
    ///   ActiveUsersUpdate   → ActiveUsersUpdatedEvent
    ///   RevitSessionCreated / RevitSessionUpdated / RevitSessionEnded → logged only
    /// </summary>
    public class SessionActivityListener : SignalListenerBase
    {
        private readonly ISignalREventBus _eventBus;

        public override string Name => "SessionActivity";

        public override IEnumerable<string> SupportedMethods => new[]
        {
            SignalRMethods.UserJoined,
            SignalRMethods.UserLeft,
            SignalRMethods.ActiveUsersUpdate,
            SignalRMethods.RevitSessionCreated,
            SignalRMethods.RevitSessionUpdated,
            SignalRMethods.RevitSessionEnded
        };

        public SessionActivityListener(ILogger logger, ISignalREventBus eventBus)
            : base(logger)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        }

        protected override Task ProcessMessageAsync(SignalRMessageInfo message)
        {
            LogMessageReceived(message);

            switch (message.Method)
            {
                case SignalRMethods.UserJoined:
                    HandleUserJoined(message);
                    break;

                case SignalRMethods.UserLeft:
                    HandleUserLeft(message);
                    break;

                case SignalRMethods.ActiveUsersUpdate:
                    HandleActiveUsersUpdate(message);
                    break;

                case SignalRMethods.RevitSessionCreated:
                case SignalRMethods.RevitSessionUpdated:
                case SignalRMethods.RevitSessionEnded:
                    _logger?.LogDebug($"[SessionActivity] {message.Method} received — session: {message.SenderSessionId}");
                    break;
            }

            return Task.CompletedTask;
        }

        private void HandleUserJoined(SignalRMessageInfo message)
        {
            var payload = GetPayloadSafe<UserPresencePayload>(message);
            if (payload == null)
            {
                _logger?.LogWarning("[SessionActivity] Failed to deserialize UserJoined payload");
                return;
            }

            _logger?.LogInfo($"[SessionActivity] UserJoined model {payload.ModelGuid}: {payload.RevitUsername ?? payload.Username} ({payload.ComputerName})");

            _eventBus.Publish(new UserJoinedEvent
            {
                ModelGuid = payload.ModelGuid,
                User      = payload,
                Source    = Name
            });
        }

        private void HandleUserLeft(SignalRMessageInfo message)
        {
            var payload = GetPayloadSafe<UserLeftPayload>(message);
            if (payload == null)
            {
                _logger?.LogWarning("[SessionActivity] Failed to deserialize UserLeft payload");
                return;
            }

            _logger?.LogInfo($"[SessionActivity] UserLeft model {payload.ModelGuid}: session {payload.SessionId}");

            _eventBus.Publish(new UserLeftEvent
            {
                ModelGuid = payload.ModelGuid,
                SessionId = payload.SessionId,
                Source    = Name
            });
        }

        private void HandleActiveUsersUpdate(SignalRMessageInfo message)
        {
            var payload = GetPayloadSafe<ActiveUsersPayload>(message);
            if (payload == null)
            {
                _logger?.LogWarning("[SessionActivity] Failed to deserialize ActiveUsersUpdate payload");
                return;
            }

            _logger?.LogInfo($"[SessionActivity] ActiveUsersUpdate model {payload.ModelGuid}: {payload.Users?.Count ?? 0} users");

            _eventBus.Publish(new ActiveUsersUpdatedEvent
            {
                ModelGuid = payload.ModelGuid,
                Users     = payload.Users,
                Source    = Name
            });
        }
    }
}
