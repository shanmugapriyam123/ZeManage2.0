using System;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.Logging;

namespace BIManage.Revit.BackgroundSync
{
    /// <summary>
    /// ExternalEvent handler that runs the BackgroundSyncEngine's document scan
    /// on the Revit main thread. This is required because iterating
    /// <see cref="Autodesk.Revit.ApplicationServices.Application.Documents"/> from
    /// a background thread (as a System.Timers.Timer does) causes AccessViolationException
    /// when Revit is concurrently closing a document — the native document pointers
    /// become invalid mid-iteration.
    /// </summary>
    public class BackgroundScanExternalEventHandler : IExternalEventHandler
    {
        private readonly ILogger? _logger;

        /// <summary>Callback invoked on the Revit main thread. Set before calling Raise().</summary>
        public Action<UIApplication>? ScanAction { get; set; }

        public BackgroundScanExternalEventHandler(ILogger? logger)
        {
            _logger = logger;
        }

        public void Execute(UIApplication app)
        {
            var action = ScanAction;
            ScanAction = null; // one-shot
            if (action == null) return;

            try
            {
                action(app);
            }
            catch (Exception ex)
            {
                _logger?.LogError($"[BackgroundScan] Main-thread scan failed: {ex.Message}", ex);
            }
        }

        public string GetName() => "BIManage.BackgroundScan";
    }
}
