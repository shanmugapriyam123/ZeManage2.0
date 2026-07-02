using System;
using System.Collections.Generic;
using System.Linq;
using BIManage.Infrastructure.Logging;

namespace BIManage.Infrastructure.SignalR.Events
{
    /// <summary>
    /// Lightweight pub/sub event bus for internal SignalR message distribution
    /// Thread-safe implementation for use across multiple listeners
    /// </summary>
    public class SignalREventBus : ISignalREventBus
    {
        private readonly Dictionary<Type, List<Delegate>> _handlers = new Dictionary<Type, List<Delegate>>();
        private readonly object _lock = new object();
        private readonly ILogger _logger;
        private bool _isDisposed;

        public SignalREventBus(ILogger logger = null)
        {
            _logger = logger;
        }

        /// <inheritdoc />
        public void Subscribe<TEvent>(Action<TEvent> handler) where TEvent : SignalREvent
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            if (_isDisposed)
                return;

            var eventType = typeof(TEvent);

            lock (_lock)
            {
                if (!_handlers.ContainsKey(eventType))
                {
                    _handlers[eventType] = new List<Delegate>();
                }

                if (!_handlers[eventType].Contains(handler))
                {
                    _handlers[eventType].Add(handler);
                    _logger?.LogDebug($"SignalREventBus: Subscribed to {eventType.Name} (total: {_handlers[eventType].Count})");
                }
            }
        }

        /// <inheritdoc />
        public void Unsubscribe<TEvent>(Action<TEvent> handler) where TEvent : SignalREvent
        {
            if (handler == null)
                return;

            var eventType = typeof(TEvent);

            lock (_lock)
            {
                if (_handlers.ContainsKey(eventType))
                {
                    _handlers[eventType].Remove(handler);
                    _logger?.LogDebug($"SignalREventBus: Unsubscribed from {eventType.Name} (remaining: {_handlers[eventType].Count})");

                    // Clean up empty lists
                    if (_handlers[eventType].Count == 0)
                    {
                        _handlers.Remove(eventType);
                    }
                }
            }
        }

        /// <inheritdoc />
        public void Publish<TEvent>(TEvent eventData) where TEvent : SignalREvent
        {
            if (eventData == null || _isDisposed)
                return;

            var eventType = typeof(TEvent);
            List<Delegate> handlersToInvoke;

            lock (_lock)
            {
                if (!_handlers.ContainsKey(eventType) || _handlers[eventType].Count == 0)
                {
                    _logger?.LogDebug($"SignalREventBus: No subscribers for {eventType.Name}");
                    return;
                }

                // Create a copy to avoid holding lock during invocation
                handlersToInvoke = _handlers[eventType].ToList();
            }

            _logger?.LogDebug($"SignalREventBus: Publishing {eventType.Name} to {handlersToInvoke.Count} subscribers");

            foreach (var handler in handlersToInvoke)
            {
                try
                {
                    ((Action<TEvent>)handler)(eventData);
                }
                catch (Exception ex)
                {
                    _logger?.LogError($"SignalREventBus: Error invoking handler for {eventType.Name}: {ex.Message}", ex);
                    // Don't propagate - continue to other handlers
                }
            }
        }

        /// <inheritdoc />
        public int GetSubscriberCount<TEvent>() where TEvent : SignalREvent
        {
            var eventType = typeof(TEvent);

            lock (_lock)
            {
                if (_handlers.ContainsKey(eventType))
                {
                    return _handlers[eventType].Count;
                }
                return 0;
            }
        }

        /// <summary>
        /// Gets total subscriber count across all event types
        /// </summary>
        public int TotalSubscriberCount
        {
            get
            {
                lock (_lock)
                {
                    return _handlers.Values.Sum(h => h.Count);
                }
            }
        }

        /// <summary>
        /// Gets all subscribed event types
        /// </summary>
        public IReadOnlyList<Type> SubscribedEventTypes
        {
            get
            {
                lock (_lock)
                {
                    return _handlers.Keys.ToList();
                }
            }
        }

        /// <summary>
        /// Clears all subscriptions
        /// </summary>
        public void Clear()
        {
            lock (_lock)
            {
                _handlers.Clear();
                _logger?.LogInfo("SignalREventBus: All subscriptions cleared");
            }
        }

        public void Dispose()
        {
            if (_isDisposed)
                return;

            _isDisposed = true;

            lock (_lock)
            {
                _handlers.Clear();
            }

            _logger?.LogInfo("SignalREventBus: Disposed");
        }
    }
}
