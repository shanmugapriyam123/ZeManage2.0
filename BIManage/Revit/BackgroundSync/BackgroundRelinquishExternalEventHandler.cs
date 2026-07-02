using System;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;
using BIManage.Revit.Helpers;

namespace BIManage.Revit.BackgroundSync
{
    /// <summary>
    /// ExternalEvent handler that relinquishes ownership of all user worksets on Revit's main thread.
    /// Releases checked-out elements and worksets so other users can edit them.
    /// </summary>
    public class BackgroundRelinquishExternalEventHandler : IExternalEventHandler
    {
        private readonly ILogger _logger;

        /// <summary>Model GUID to relinquish — set before calling Raise().</summary>
        public string PendingModelGuid { get; set; }

        /// <summary>Fired after relinquish completes. Args: modelGuid, succeeded.</summary>
        public event Action<string, bool> RelinquishCompleted;

        public BackgroundRelinquishExternalEventHandler(ILogger logger)
        {
            _logger = logger;
        }

        public void Execute(UIApplication app)
        {
            if (string.IsNullOrEmpty(PendingModelGuid))
                return;

            var modelGuid = PendingModelGuid;
            PendingModelGuid = null;

            Document doc = null;
            try
            {
                doc = FindWorksharedDoc(app, modelGuid);
                if (doc == null)
                {
                    _logger?.LogWarning($"[BackgroundRelinquish] Document not found for model {modelGuid}");
                    return;
                }

                _logger?.LogInfo($"[BackgroundRelinquish] Relinquishing ownership for {doc.Title}");

                var mainWindow = app.MainWindowHandle;
                RevitInteropHelper.SetStatusText(mainWindow, $"ZeManage: Relinquishing {doc.Title}...");

                var failuresHandler = new BackgroundFailuresHandler(_logger);
                failuresHandler.Attach(app.Application);
                try
                {
                    var relinquishOpts = new RelinquishOptions(true)
                    {
                        UserWorksets = true,
                        FamilyWorksets = false,
                        CheckedOutElements = true
                    };

                    WorksharingUtils.RelinquishOwnership(doc, relinquishOpts, new TransactWithCentralOptions());

                    _logger?.LogInfo($"[BackgroundRelinquish] Relinquish completed for {doc.Title}");
                    RevitInteropHelper.SetStatusText(mainWindow, $"ZeManage: Relinquish completed — {doc.Title}");
                    RelinquishCompleted?.Invoke(modelGuid, true);
                }
                finally
                {
                    failuresHandler.Detach(app.Application);
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[BackgroundRelinquish] Failed for {doc?.Title ?? modelGuid}: {ex.Message}", ex);
                RevitInteropHelper.ClearStatusText(app.MainWindowHandle);
                RelinquishCompleted?.Invoke(modelGuid, false);
            }
        }

        private Document FindWorksharedDoc(UIApplication app, string modelGuid)
        {
            try
            {
                foreach (Document doc in app.Application.Documents)
                {
                    if (doc.IsLinked || doc.IsFamilyDocument) continue;
                    if (!DocumentTypeHelper.IsWorkshared(doc)) continue;

                    var guid = ModelGuidHelper.GetModelGuid(doc);
                    if (string.Equals(guid, modelGuid, StringComparison.OrdinalIgnoreCase))
                        return doc;
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[BackgroundRelinquish] Error finding document: {ex.Message}", ex);
            }
            return null;
        }

        public string GetName() => "BIManage Background Relinquish";
    }
}
