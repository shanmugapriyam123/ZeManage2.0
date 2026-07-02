using System.Collections.Generic;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR.Events
{
    /// <summary>
    /// Published via SignalREventBus when a UserJoined message is processed.
    /// ModelActivitiesViewModel subscribes to add the user to the active list.
    /// </summary>
    public class UserJoinedEvent : SignalREvent
    {
        public string ModelGuid { get; set; }
        public UserPresencePayload User { get; set; }
    }

    /// <summary>
    /// Published via SignalREventBus when a UserLeft message is processed.
    /// ModelActivitiesViewModel subscribes to remove the user from the active list.
    /// </summary>
    public class UserLeftEvent : SignalREvent
    {
        public string ModelGuid { get; set; }
        public string SessionId { get; set; }
    }

    /// <summary>
    /// Published via SignalREventBus when an ActiveUsersUpdate message is processed.
    /// Contains the full current roster for a model — replaces the local list entirely.
    /// </summary>
    public class ActiveUsersUpdatedEvent : SignalREvent
    {
        public string ModelGuid { get; set; }
        public List<UserPresencePayload> Users { get; set; }
    }
}
