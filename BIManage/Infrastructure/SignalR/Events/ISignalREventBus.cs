using System;

namespace BIManage.Infrastructure.SignalR.Events
{
    /// <summary>
    /// Interface for internal SignalR event distribution
    /// Provides a lightweight pub/sub mechanism for routing SignalR messages
    /// </summary>
    public interface ISignalREventBus : IDisposable
    {
        /// <summary>
        /// Subscribes to events of a specific type
        /// </summary>
        /// <typeparam name="TEvent">Event type to subscribe to</typeparam>
        /// <param name="handler">Handler to invoke when event is published</param>
        void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : SignalREvent;




        /// <summary>
        /// Unsubscribes from events of a specific type
        /// </summary>
        /// <typeparam name="TEvent">Event type to unsubscribe from</typeparam>
        /// <param name="handler">Handler to remove</param>
        void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : SignalREvent;

        /// <summary>
        /// Publishes an event to all subscribers
        /// </summary>
        /// <typeparam name="TEvent">Event type to publish</typeparam>
        /// <param name="eventData">Event data to publish</param>
        void Publish<TEvent>(TEvent eventData) where TEvent : SignalREvent;

        /// <summary>
        /// Gets the number of subscribers for a specific event type
        /// </summary>
        /// <typeparam name="TEvent">Event type to check</typeparam>
        /// <returns>Number of subscribers</returns>
        int GetSubscriberCount<TEvent>() where TEvent : SignalREvent;
    }

    /// <summary>
    /// Base class for all SignalR events
    /// </summary>
    public abstract class SignalREvent
    {
        /// <summary>
        /// Timestamp when the event was created
        /// </summary>
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;

        /// <summary>
        /// Source of the event (e.g., listener name)
        /// </summary>
        public string Source { get; set; }
    }

}
