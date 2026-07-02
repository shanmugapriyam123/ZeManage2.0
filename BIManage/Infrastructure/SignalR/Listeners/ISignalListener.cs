using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR.Listeners
{
    /// <summary>
    /// Interface for components that handle SignalR messages
    /// </summary>
    public interface ISignalListener
    {
        /// <summary>
        /// Gets the name of this listener for logging/debugging
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Gets the method names this listener handles.
        /// Use constants from <see cref="SignalRMethods"/>.
        /// </summary>
        IEnumerable<string> SupportedMethods { get; }

        /// <summary>
        /// Handles an incoming SignalR message
        /// </summary>
        /// <param name="message">The message to handle</param>
        Task HandleMessageAsync(SignalRMessageInfo message);

        /// <summary>
        /// Called when the SignalR connection is lost
        /// </summary>
        void OnDisconnected();

        /// <summary>
        /// Called when the SignalR connection is restored
        /// </summary>
        void OnReconnected();
    }
}
