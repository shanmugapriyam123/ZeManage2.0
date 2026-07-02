using System;
using Autodesk.Revit.UI;
using BIManage.Infrastructure.DependencyInjection;
using BIManage.Infrastructure.Logging;
using BIManageRevit.BIManage.Views.Metrics;

namespace BIManage.Core.Metrics
{
    /// <summary>
    /// External event handler that launches a non-blocking chunked model analysis.
    /// The command returns immediately; Revit remains fully interactive while analysis
    /// progresses via Idling event callbacks. All 3 metric tiers (fast + medium + expensive)
    /// are collected and saved with manual capture flags.
    /// </summary>
    public class AnalyzeModelExternalEventHandler : IExternalEventHandler
    {
        private readonly ServiceRegistry _services;
        private readonly ILogger? _logger;

        /// <summary>
        /// When set to true, the next Execute() call will start the analysis.
        /// Reset to false after execution.
        /// </summary>
        public bool IsRequested { get; set; }

        public AnalyzeModelExternalEventHandler(ServiceRegistry services, ILogger? logger)
        {
            _services = services ?? throw new ArgumentNullException(nameof(services));
            _logger = logger;
        }

        public void Execute(UIApplication app)
        {
            if (!IsRequested)
                return;

            IsRequested = false;

            var uiDoc = app.ActiveUIDocument;
            var doc = uiDoc?.Document;

            if (doc == null || doc.IsFamilyDocument)
            {
                _logger?.LogWarning("AnalyzeModel: No active project document when external event fired");
                return;
            }

            // Show modeless progress dialog — Revit stays interactive
            var dialog = new AnalyzeModelDialog(doc.Title, startInLoadingMode: true);
            dialog.Show();

            // Launch chunked analyzer — work is split across Idling callbacks
            var analyzer = new ChunkedMetricsAnalyzer(app, _services, dialog, doc, _logger);
            analyzer.Start();

            // Execute() returns immediately — Revit is NOT blocked
        }

        public string GetName() => "BIManage Analyze Model Metrics";
    }
}
