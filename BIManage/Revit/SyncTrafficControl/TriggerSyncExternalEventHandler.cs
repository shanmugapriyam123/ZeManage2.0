using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.SyncTrafficControl
{
    /// <summary>
    /// ExternalEventHandler that triggers SynchronizeWithCentral on Revit's main thread.
    /// Raised when the server sends a SyncRequest (it's this user's turn in the queue).
    /// </summary>
    public class TriggerSyncExternalEventHandler : IExternalEventHandler
    {
        private readonly ILogger _logger;
        public string PendingModelGuid { get; set; }

        public TriggerSyncExternalEventHandler(ILogger logger)
        {
            _logger = logger;
        }

        public void Execute(UIApplication app)
        {
            if (string.IsNullOrEmpty(PendingModelGuid))
                return;

            var modelGuid = PendingModelGuid;
            PendingModelGuid = null;

            try
            {
                _logger?.LogInfo($"[TriggerSync] Posting SynchronizeWithCentral for {modelGuid}");
                // User already cleared Command Protection at queue-entry; arming the one-shot
                // marker prevents the next DocumentSynchronizingWithCentral handler from
                // re-prompting on this server-dispatched "your turn" sync.
                SyncProtectionGate.ArmNextSync();
                var commandId = RevitCommandId.LookupPostableCommandId(PostableCommand.SynchronizeNow);
                app.PostCommand(commandId);
            }
            catch (System.Exception ex)
            {
                _logger?.LogError($"[TriggerSync] Failed to post sync command: {ex.Message}", ex);
            }
        }

        public string GetName() => "BIManage Trigger Sync";
    }
}
