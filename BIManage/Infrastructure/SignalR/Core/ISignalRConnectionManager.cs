using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BIManage.Infrastructure.SignalR.Listeners;

namespace BIManage.Infrastructure.SignalR.Core
{
    /// <summary>
    /// Interface for the SignalR connection manager
    /// Coordinates listeners and manages group subscriptions based on document context
    /// </summary>
    public interface ISignalRConnectionManager : IDisposable
    {
        /// <summary>
        /// Gets whether the SignalR service is currently connected
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// Gets the current connection state
        /// </summary>
        SignalRConnectionState State { get; }

        /// <summary>
        /// Gets the list of currently active groups
        /// </summary>
        IReadOnlyList<string> ActiveGroups { get; }

        /// <summary>
        /// Gets the number of registered listeners
        /// </summary>
        int ListenerCount { get; }

        /// <summary>
        /// Event raised when connection state changes
        /// </summary>
        event EventHandler<SignalRConnectionStateChangedEventArgs> ConnectionStateChanged;

        /// <summary>
        /// Registers a listener to receive SignalR messages
        /// Starts connection if this is the first listener
        /// </summary>
        /// <param name="listener">Listener to register</param>
        void RegisterListener(ISignalListener listener);

        /// <summary>
        /// Unregisters a listener from receiving SignalR messages
        /// Stops connection if this was the last listener
        /// </summary>
        /// <param name="listener">Listener to unregister</param>
        void UnregisterListener(ISignalListener listener);

        /// <summary>
        /// Called when a document is opened - joins document-specific groups (model, project).
        /// Identity groups (company, machine) are auto-joined server-side via JWT claims.
        /// </summary>
        /// <param name="modelGuid">Model GUID</param>
        /// <param name="projectId">Project ID (Ze AI or Cloud)</param>
        Task OnDocumentOpenedAsync(string modelGuid, string projectId);

        /// <summary>
        /// Called when a document is closed - leaves appropriate groups
        /// </summary>
        /// <param name="modelGuid">Model GUID</param>
        Task OnDocumentClosedAsync(string modelGuid);

        /// <summary>
        /// Manually connects to SignalR (normally automatic based on listeners)
        /// </summary>
        /// <param name="accessToken">Optional access token</param>
        Task ConnectAsync(string accessToken = null);

        /// <summary>
        /// Manually disconnects from SignalR
        /// </summary>
        Task DisconnectAsync();

        /// <summary>
        /// Joins a specific group
        /// </summary>
        /// <param name="groupName">Group name to join</param>
        Task JoinGroupAsync(string groupName);

        /// <summary>
        /// Leaves a specific group
        /// </summary>
        /// <param name="groupName">Group name to leave</param>
        Task LeaveGroupAsync(string groupName);

        /// <summary>
        /// Announces this user's presence on a model to the server via SignalR.
        /// Called after subscribing to the model group so the server can broadcast
        /// UserJoined to all other clients on the same model.
        /// </summary>
        Task AnnouncePresenceAsync(Messages.UserPresencePayload payload);

        /// <summary>
        /// Announces that this user is leaving a model.
        /// Called before unsubscribing from the model group so the server can
        /// broadcast UserLeft to remaining clients.
        /// </summary>
        Task AnnounceLeaveAsync(string modelGuid, string sessionId);

        /// <summary>
        /// Sends a hub invocation message to the server.
        /// </summary>
        Task SendAsync(string method, params object[] args);

        /// <summary>
        /// Server-arbitrated cloud sync gate (Option D). Asks the server whether
        /// this client may proceed with a sync now. Sends RequestSyncSlot (fire-and-forget
        /// over the raw WebSocket) and awaits the matching SyncSlotGranted /
        /// SyncSlotDenied reply via <see cref="SyncSlotResponseCoordinator"/>. If no
        /// reply arrives within the timeout (server unreachable, old server without
        /// this method, etc.), returns <see cref="SyncSlotDecision.Unavailable"/> so
        /// the caller falls back to the client-side STC gate. Backward-compatible: an
        /// old server simply ignores the unknown method and the client times out.
        /// </summary>
        Task<SyncSlotDecision> RequestSyncSlotAsync(
            string modelGuid, string sessionId, string username, string revitUsername,
            DateTime claimedAtUtc, bool isCloud, TimeSpan timeout,
            System.Threading.CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the model GUIDs and project IDs of all currently open documents.
        /// Used by ProtectionChangeListener to resolve scope-based refresh targets.
        /// </summary>
        IReadOnlyList<(string ModelGuid, string ProjectId)> GetOpenDocuments();
    }
}
