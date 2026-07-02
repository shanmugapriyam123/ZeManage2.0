using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR.Listeners
{
    /// <summary>
    /// Base class for SignalR listeners with common functionality
    /// Provides thread-safe message handling and Revit thread marshaling support
    /// </summary>
    public abstract class SignalListenerBase : ISignalListener, IDisposable
    {
        protected readonly ILogger _logger;
        private bool _isDisposed;

        /// <summary>
        /// Gets the name of this listener
        /// </summary>
        public abstract string Name { get; }

        /// <summary>
        /// Gets the method names this listener handles.
        /// Use constants from <see cref="SignalRMethods"/>.
        /// </summary>
        public abstract IEnumerable<string> SupportedMethods { get; }

        protected SignalListenerBase(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Handles an incoming SignalR message
        /// Override ProcessMessageAsync to implement message handling
        /// </summary>
        public async Task HandleMessageAsync(SignalRMessageInfo message)
        {
            if (_isDisposed || message == null)
                return;

            try
            {
                _logger?.LogDebug($"[{Name}] Processing message: {message.Method}");

                if (RequiresRevitThread(message.Method))
                {
                    // Queue for Revit main thread execution
                    await ExecuteOnRevitThreadAsync(message);
                }
                else
                {
                    // Process on background thread
                    await ProcessMessageAsync(message);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[{Name}] Error handling message {message.Method}: {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Override to implement message processing logic
        /// </summary>
        /// <param name="message">The message to process</param>
        protected abstract Task ProcessMessageAsync(SignalRMessageInfo message);

        /// <summary>
        /// Override to specify which methods require Revit main thread.
        /// Default returns false - override for methods that need Revit API access.
        /// </summary>
        /// <param name="method">The method name to check</param>
        /// <returns>True if the message should be processed on Revit main thread</returns>
        protected virtual bool RequiresRevitThread(string method)
        {
            return false;
        }

        /// <summary>
        /// Queues the message for execution on Revit's main thread
        /// Override to implement custom Revit thread marshaling
        /// </summary>
        /// <param name="message">The message to process</param>
        protected virtual Task ExecuteOnRevitThreadAsync(SignalRMessageInfo message)
        {
            // Default implementation: just process on current thread
            // Subclasses should override this to use ExternalEvent pattern
            _logger?.LogWarning($"[{Name}] ExecuteOnRevitThreadAsync not implemented - processing on background thread");
            return ProcessMessageAsync(message);
        }

        /// <summary>
        /// Called when the SignalR connection is lost
        /// Override to implement custom disconnection handling
        /// </summary>
        public virtual void OnDisconnected()
        {
            _logger?.LogDebug($"[{Name}] Disconnected from SignalR");
        }

        /// <summary>
        /// Called when the SignalR connection is restored
        /// Override to implement custom reconnection handling
        /// </summary>
        public virtual void OnReconnected()
        {
            _logger?.LogDebug($"[{Name}] Reconnected to SignalR");
        }

        // Methods that fire frequently as routine peer-presence pings — they don't
        // act on Revit state and don't need INFO-level visibility. Logged at DEBUG
        // so the log stays clean for actually-interesting events. Without this filter
        // a busy SignalR group spams ~50 INFO lines per session (one per peer that
        // opened Revit), obscuring real signals during a crash post-mortem.
        // Observed in the BIManageRevit_20260514_*.log set (User 4 crash investigation).
        private static readonly System.Collections.Generic.HashSet<string> _quietMethods
            = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
            {
                "RevitSessionCreated",
                "RevitSessionUpdated",
                "RevitSessionEnded",
                "ActiveUsersUpdate",
            };

        /// <summary>
        /// Helper to log message receipt. INFO for actionable methods, DEBUG for
        /// routine peer-presence pings (see <see cref="_quietMethods"/>).
        /// </summary>
        protected void LogMessageReceived(SignalRMessageInfo message)
        {
            if (message?.Method != null && _quietMethods.Contains(message.Method))
                _logger?.LogDebug($"[{Name}] Received {message.Method} from {message.SenderUsername ?? message.SenderUserId ?? "unknown"}");
            else
                _logger?.LogInfo($"[{Name}] Received {message.Method} from {message.SenderUsername ?? message.SenderUserId ?? "unknown"}");
        }

        /// <summary>
        /// Helper to safely deserialize message payload
        /// </summary>
        protected T GetPayloadSafe<T>(SignalRMessageInfo message) where T : class
        {
            try
            {
                return message?.GetPayload<T>();
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[{Name}] Failed to deserialize payload for {message?.Method}: {ex.Message}", ex);
                return null;
            }
        }

        public virtual void Dispose()
        {
            _isDisposed = true;
        }
    }
}
