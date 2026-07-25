namespace BIManage.Infrastructure.SignalR.Messages
{
    /// <summary>
    /// String constants for all SignalR method names.
    /// These match the method names used by the API server (SendAsync wire names).
    /// Both sides use the same strings — this is the contract.
    /// </summary>
    public static class SignalRMethods
    {
        // =============================================
        // Protection & Rules
        // =============================================

        public const string RuleUpdate = "RuleUpdate";
        public const string ProtectionSettingsChange = "ProtectionSettingsChange";
        public const string CommandBlockedNotify = "CommandBlockedNotify";
        public const string PinProtectionChange = "PinProtectionChange";

        // =============================================
        // Model Registration
        // =============================================

        public const string ModelRegistered = "ModelRegistered";
        public const string ModelDeregistered = "ModelDeregistered";
        public const string ModelSettingsChanged = "ModelSettingsChanged";

        // =============================================
        // Sync Coordination
        // =============================================

        public const string SyncQueueUpdate = "SyncQueueUpdate";
        public const string SyncStarting = "SyncStarting";
        public const string SyncCompleted = "SyncCompleted";
        public const string SyncCancelled = "SyncCancelled";
        public const string SyncRequest = "SyncRequest";
        public const string SyncQueueJoin = "SyncQueueJoin";
        public const string SyncQueueLeave = "SyncQueueLeave";

        // Server-arbitrated cloud sync gate (Option D). Cloud sync clicks bypass
        // AddInCommandBinding.BeforeExecuted because Revit's CloudWorksharing.UI
        // routes around it — so the client asks the server for a sync slot before
        // letting Revit's pipeline start. The server replies with Granted or Denied;
        // if no reply arrives within the client timeout the client falls back to
        // the local STC gate (today's behavior). All three names safe on old servers
        // — server ignores unknown inbound methods, client times out and falls back.
        public const string RequestSyncSlot = "RequestSyncSlot";
        public const string SyncSlotGranted = "SyncSlotGranted";
        public const string SyncSlotDenied = "SyncSlotDenied";

        // =============================================
        // User Presence
        // =============================================

        public const string UserJoined = "UserJoined";
        public const string UserLeft = "UserLeft";
        public const string ActiveUsersUpdate = "ActiveUsersUpdate";
        public const string UserHeartbeat = "UserHeartbeat";

        // =============================================
        // Admin & System
        // =============================================

        public const string AdminBroadcast = "AdminBroadcast";
        public const string FeatureToggle = "FeatureToggle";
        public const string ConfigurationUpdate = "ConfigurationUpdate";
        public const string MaintenanceNotice = "MaintenanceNotice";
        public const string ForceLogout = "ForceLogout";
        public const string ForceTokenRefresh = "ForceTokenRefresh";

        // =============================================
        // Session & Metrics
        // =============================================

        public const string RevitSessionCreated = "RevitSessionCreated";
        public const string RevitSessionUpdated = "RevitSessionUpdated";
        public const string RevitSessionEnded = "RevitSessionEnded";
        public const string SessionActivity = "SessionActivity";
        public const string MetricsRequest = "MetricsRequest";

        // =============================================
        // Chat
        // =============================================

        public const string SendChatMessage = "SendChatMessage";
        public const string ChatMessageReceived = "ChatMessageReceived";

        // =============================================
        // Idle Time Tracking
        // =============================================

        // Sent by the Revit add-in when the user has been idle for >= company-settings
        // idleThresholdMinutes. Carries the cumulative idle seconds so the server can
        // record productivity loss without polling.
        public const string IdleTimeUpdate = "IdleTimeUpdate";
    }
}
