namespace BIManage.Infrastructure.SignalR.Events
{
    /// <summary>
    /// Published via SignalREventBus when a ChatMessageReceived SignalR message arrives.
    /// ModelActivitiesViewModel subscribes to display incoming chat messages in real time.
    /// </summary>
    public class ChatMessageReceivedEvent : SignalREvent
    {
        public string Scope { get; set; }              // "model", "project", "direct"
        public string ModelGuid { get; set; }
        public string ProjectId { get; set; }
        public string TargetUsername { get; set; }
        public string SenderUsername { get; set; }
        public string SenderDisplayName { get; set; }
        public string Text { get; set; }
    }
}
