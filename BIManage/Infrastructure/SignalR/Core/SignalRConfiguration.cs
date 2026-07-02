using System;

namespace BIManage.Infrastructure.SignalR.Core
{
    /// <summary>
    /// Configuration settings for SignalR connection
    /// </summary>
    public class SignalRConfiguration
    {
        /// <summary>
        /// SignalR hub URL (e.g., "https://api.zemanage.com/signalr")
        /// </summary>
        public string HubUrl { get; set; }

        /// <summary>
        /// Name of the SignalR hub to connect to
        /// </summary>
        public string HubName { get; set; } = "NotificationHub";

        /// <summary>
        /// Whether to automatically reconnect on disconnection
        /// </summary>
        public bool AutoReconnect { get; set; } = true;

        /// <summary>
        /// Delays between reconnection attempts (in milliseconds)
        /// Uses exponential backoff pattern with ±20% jitter
        /// </summary>
        public int[] ReconnectDelays { get; set; } = { 1000, 2000, 5000, 10000, 30000, 60000 };

        /// <summary>
        /// Interval for heartbeat/keep-alive messages
        /// </summary>
        public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Timeout for initial connection attempt
        /// </summary>
        public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Timeout for server response
        /// </summary>
        public TimeSpan ServerTimeout { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>
        /// Company identifier for group subscriptions.
        /// Set after device authentication from DeviceAuthResponse.CompanyId.
        /// </summary>
        public string CompanyId { get; set; }

        /// <summary>
        /// Device identifier (hardware-based GUID from MachineIdentifier).
        /// Available immediately at bootstrap — does not require authentication.
        /// Used as the primary device identifier for all SignalR communication.
        /// </summary>
        public string MachineId { get; set; }

        /// <summary>
        /// Current Windows username for display
        /// </summary>
        public string Username { get; set; }

        /// <summary>
        /// Interval for slow reconnection timer (ms) after fast attempts are exhausted
        /// </summary>
        public int SlowReconnectIntervalMs { get; set; } = 120_000;

        /// <summary>
        /// Maximum number of slow reconnect attempts before giving up entirely
        /// </summary>
        public int MaxSlowReconnectAttempts { get; set; } = 50;

        /// <summary>
        /// Delay before retrying after an auth failure (401/403) to allow token refresh
        /// </summary>
        public int AuthRetryDelayMs { get; set; } = 5000;

        /// <summary>
        /// Validates the configuration
        /// </summary>
        public bool IsValid => !string.IsNullOrEmpty(HubUrl) && !string.IsNullOrEmpty(MachineId);
    }

    /// <summary>
    /// Connection state for SignalR service
    /// </summary>
    public enum SignalRConnectionState
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
        Disconnecting
    }

    /// <summary>
    /// Event args for connection state changes
    /// </summary>
    public class SignalRConnectionStateChangedEventArgs : EventArgs
    {
        public SignalRConnectionState OldState { get; }
        public SignalRConnectionState NewState { get; }
        public Exception Error { get; }

        public SignalRConnectionStateChangedEventArgs(
            SignalRConnectionState oldState,
            SignalRConnectionState newState,
            Exception error = null)
        {
            OldState = oldState;
            NewState = newState;
            Error = error;
        }
    }
}
