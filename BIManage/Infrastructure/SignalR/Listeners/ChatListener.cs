using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR.Listeners
{
    /// <summary>
    /// Listens for server-pushed ChatMessageReceived messages and publishes them
    /// to the internal SignalREventBus so ModelActivitiesViewModel can display them.
    /// </summary>
    public class ChatListener : SignalListenerBase
    {
        private readonly ISignalREventBus _eventBus;

        public override string Name => "Chat";

        public override IEnumerable<string> SupportedMethods => new[]
        {
            SignalRMethods.ChatMessageReceived
        };

        public ChatListener(ILogger logger, ISignalREventBus eventBus)
            : base(logger)
        {
            _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        }

        protected override Task ProcessMessageAsync(SignalRMessageInfo message)
        {
            LogMessageReceived(message);

            if (message.Method == SignalRMethods.ChatMessageReceived)
            {
                var payload = GetPayloadSafe<ChatMessagePayload>(message);
                if (payload != null)
                {
                    _logger?.LogInfo($"[Chat] Message from {payload.SenderDisplayName ?? payload.SenderUsername} (scope: {payload.Scope})");
                    _eventBus.Publish(new ChatMessageReceivedEvent
                    {
                        Scope = payload.Scope,
                        ModelGuid = payload.ModelGuid,
                        ProjectId = payload.ProjectId,
                        TargetUsername = payload.TargetUsername,
                        SenderUsername = payload.SenderUsername,
                        SenderDisplayName = payload.SenderDisplayName,
                        Text = payload.Text,
                        Source = Name
                    });
                }
            }

            return Task.CompletedTask;
        }
    }
}
