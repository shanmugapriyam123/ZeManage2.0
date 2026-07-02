namespace BIManage.Infrastructure.SignalR.Events
{
    /// <summary>
    /// Published via SignalREventBus when a ModelRegistered message is processed.
    /// UI components or other services can subscribe to this event.
    /// </summary>
    public class ModelRegisteredEvent : SignalREvent
    {
        public string ModelGuid { get; set; }
        public string ModelName { get; set; }
        public bool IsActive { get; set; }

        /// <summary>
        /// True if the model was newly inserted into local SQLite, false if it was updated.
        /// </summary>
        public bool IsNew { get; set; }
    }
}
