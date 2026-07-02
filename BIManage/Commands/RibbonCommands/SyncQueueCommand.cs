using System;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIManage.Data.SQLite;
using BIManage.Infrastructure.Logging;
using BIManage.Infrastructure.SignalR.Core;
using BIManage.Infrastructure.SignalR.Events;
using BIManage.Revit.Context;
using BIManage.Revit.Helpers;
using BIManageRevit.BIManage.Views.SyncQueue;

namespace BIManageRevit.Commands.RibbonCommands
{
    [Transaction(TransactionMode.ReadOnly)]
    [Regeneration(RegenerationOption.Manual)]
    public class SyncQueueCommand : IExternalCommand
    {
        // Static root so the non-modal Window isn't GC'd after Execute returns. Reset
        // by the dialog's Closed handler. Only one Sync Activity Monitor should exist
        // at a time; a second invocation brings the existing window to the front.
        private static SyncQueueDialog? _openDialog;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            if (CrossAlcDispatch.TryForward(this, commandData, ref message, elements) is Result __r) return __r;
            if (LicenseGuard.IsBlockedByLicense(commandData)) return Result.Succeeded;

            ILogger? logger = null;
            try
            {
                var app = global::BIManageRevit.BIManage.Revit.Applications.Application.Instance;
                var services = app?.GetType().GetField("_services",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    ?.GetValue(app) as global::BIManage.Infrastructure.DependencyInjection.ServiceRegistry;

                if (services == null)
                {
                    TaskDialog.Show("Sync Activity", "Services not available.\nPlease ensure BIManage is properly initialized.");
                    return Result.Succeeded;
                }

                logger = services.GetService<ILogger>();
                var eventBus = services.GetService<ISignalREventBus>();
                var connectionManager = services.GetService<ISignalRConnectionManager>();
                var revitContext = services.GetService<IRevitContext>();
                var syncRepository = services.GetService<SyncRepository>();
                var syncTrafficControl = services.GetService<global::BIManage.Revit.SyncTrafficControl.SyncTrafficControlService>();
                var sessionRepository = services.GetService<SessionRepository>();
                var sessionId = revitContext?.SessionId.ToString();

                // Get current model GUID to filter sync events
                string? modelGuid = null;
                var doc = commandData.Application.ActiveUIDocument?.Document;
                if (doc != null)
                    modelGuid = ModelGuidHelper.GetModelGuid(doc, logger);

                logger?.LogInfo("Opening Sync Activity Monitor dialog");

                // If the dialog is already open, just bring it to the front instead of
                // stacking a second instance. Check Activate's return — false means the
                // window can't be activated (in transition, disposed-but-not-null), in
                // which case we null out the cached ref and fall through to create a
                // fresh dialog. Without this check, a stale dialog reference makes the
                // command silently no-op while the user expects a visible monitor.
                if (_openDialog != null)
                {
                    try
                    {
                        if (_openDialog.Activate())
                        {
                            _openDialog.Focus();
                            logger?.LogInfo("Sync Activity Monitor dialog already open — brought to front");
                            return Result.Succeeded;
                        }
                        logger?.LogWarning("Sync Activity Monitor dialog Activate returned false — discarding stale reference and recreating");
                        _openDialog = null;
                    }
                    catch (Exception activateEx)
                    {
                        logger?.LogWarning($"Sync Activity Monitor dialog Activate threw ({activateEx.Message}) — discarding stale reference and recreating");
                        _openDialog = null;
                    }
                }

                var dialog = new SyncQueueDialog(logger, eventBus, modelGuid, connectionManager, sessionId, syncRepository, syncTrafficControl, sessionRepository);
                new System.Windows.Interop.WindowInteropHelper(dialog) { Owner = commandData.Application.MainWindowHandle };

                // NON-MODAL: Show() instead of ShowDialog(). A modal dialog (ShowDialog)
                // blocks Revit's UI thread and freezes the ExternalEvent queue — if the
                // user opens this monitor while waiting in the sync queue, the SyncReady
                // dialog's "Sync Now" → TriggerSync ExternalEvent never gets a chance to
                // execute until the monitor is closed. The user reproduces this as a
                // 5-minute "rehydrate loop" where their accepted queue turn never starts.
                // Keep a static reference so the Window isn't GC'd after this method
                // returns; clear it when the dialog closes.
                _openDialog = dialog;
                dialog.Closed += (s, e) =>
                {
                    if (ReferenceEquals(_openDialog, dialog)) _openDialog = null;
                    logger?.LogInfo("Sync Activity Monitor dialog closed");
                };
                dialog.Show();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                logger?.LogError($"SyncQueueCommand failed: {ex.Message}", ex);
                TaskDialog.Show("Sync Activity", $"Failed to open Sync Activity Monitor: {ex.Message}");
                message = ex.Message;
                return Result.Failed;
            }
        }
    }
}
