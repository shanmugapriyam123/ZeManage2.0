using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.Infrastructure.SignalR.Messages;

namespace BIManage.Infrastructure.SignalR.Core
{
    /// <summary>
    /// Interface for the SignalR connection service
    /// Manages connection lifecycle and message handling
    /// </summary>
    public interface ISignalRService : IDisposable
    {
        /// <summary>
        /// Gets whether the connection is currently established
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Gets the current connection state
        /// </summary>
        SignalRConnectionState State { get; }

        /// <summary>
        /// Gets the connection ID assigned by the server
        /// </summary>
        string ConnectionId { get; }

        /// <summary>
        /// Gets whether the maximum reconnect attempts have been reached
        /// </summary>
        bool HasReachedMaxReconnectAttempts { get; }

        // --- Diagnostic properties ---

        /// <summary>Current reconnect attempt counter</summary>
        int ReconnectAttemptCount { get; }

        /// <summary>UTC time until which the client is rate-limited (DateTime.MinValue if not)</summary>
        DateTime RateLimitedUntil { get; }

        /// <summary>The hub URL being connected to</summary>
        string HubUrl { get; }

        /// <summary>Names of currently joined groups</summary>
        IReadOnlyList<string> JoinedGroups { get; }

        /// <summary>Last error message, if any</summary>
        string LastError { get; }

        /// <summary>UTC timestamp of last successful connection</summary>
        DateTime? LastConnectedAt { get; }

        /// <summary>
        /// Event raised when connection state changes
        /// </summary>
        event EventHandler<SignalRConnectionStateChangedEventArgs> ConnectionStateChanged;

        /// <summary>
        /// Event raised when a message is received from the server
        /// </summary>
        event EventHandler<SignalRMessageInfo> MessageReceived;

        /// <summary>
        /// Optional provider that returns a JWT access token for hub authentication.
        /// Set this before connecting so ConnectAsync and reconnects can obtain a fresh token.
        /// </summary>
        Func<Task<string>> AccessTokenProvider { get; set; }

        /// <summary>
        /// Resets the reconnect attempt counter. Call before ConnectAsync() to retry after max attempts were reached.
        /// </summary>
        void ResetReconnectAttempts();

        /// <summary>
        /// Establishes connection to the SignalR hub
        /// </summary>
        /// <param name="accessToken">Optional access token for authentication</param>
        Task ConnectAsync(string accessToken = null);

        /// <summary>
        /// Gracefully disconnects from the SignalR hub
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// Joins a group to receive group-specific messages
        /// </summary>
        /// <param name="groupName">Name of the group to join</param>
        Task JoinGroupAsync(string groupName);

        /// <summary>
        /// Leaves a group to stop receiving group-specific messages
        /// </summary>
        /// <param name="groupName">Name of the group to leave</param>
        Task LeaveGroupAsync(string groupName);

        /// <summary>
        /// Sends a message to the server
        /// </summary>
        /// <param name="method">Server method name to invoke</param>
        /// <param name="args">Arguments to pass to the method</param>
        Task SendAsync(string method, params object[] args);
    }
}
