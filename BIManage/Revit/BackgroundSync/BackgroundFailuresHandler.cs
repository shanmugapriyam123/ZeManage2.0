using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.BackgroundSync
{
    /// <summary>
    /// Reusable FailuresProcessing handler that auto-resolves warnings during background operations.
    /// Attach before a background operation (sync, relinquish, analyze) and detach immediately after.
    /// Prevents Revit modal warning dialogs from blocking the UI during automated operations.
    /// </summary>
    internal class BackgroundFailuresHandler
    {
        private readonly ILogger _logger;
        private bool _attached;

        public BackgroundFailuresHandler(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Attach the auto-resolver to Revit's FailuresProcessing event.
        /// Must be called on the Revit main thread, immediately before the background operation.
        /// </summary>
        public void Attach(Autodesk.Revit.ApplicationServices.Application app)
        {
            if (_attached || app == null) return;
            app.FailuresProcessing += OnFailuresProcessing;
            _attached = true;
        }

        /// <summary>
        /// Detach the auto-resolver. Must be called in a finally block after the operation completes.
        /// </summary>
        public void Detach(Autodesk.Revit.ApplicationServices.Application app)
        {
            if (!_attached || app == null) return;
            app.FailuresProcessing -= OnFailuresProcessing;
            _attached = false;
        }

        private void OnFailuresProcessing(object sender, FailuresProcessingEventArgs e)
        {
            try
            {
                var accessor = e.GetFailuresAccessor();
                var messages = accessor.GetFailureMessages();

                if (messages.Count == 0)
                    return;

                foreach (var msg in messages)
                {
                    var severity = msg.GetSeverity();

                    if (severity == FailureSeverity.Warning)
                    {
                        _logger?.LogDebug($"[BackgroundFailures] Auto-dismissing warning: {msg.GetDescriptionText()}");
                        accessor.DeleteWarning(msg);
                    }
                    else if (severity == FailureSeverity.Error)
                    {
                        _logger?.LogWarning($"[BackgroundFailures] Attempting to resolve error: {msg.GetDescriptionText()}");

                        if (msg.HasResolutions())
                        {
                            accessor.ResolveFailure(msg);
                        }
                    }
                }

                // Tell Revit to continue processing (don't show any dialogs)
                e.SetProcessingResult(FailureProcessingResult.Continue);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[BackgroundFailures] Error in failure handler: {ex.Message}", ex);
            }
        }
    }
}
