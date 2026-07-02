using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BIManage.Infrastructure.Logging;

namespace BIManage.AI
{
    /// <summary>
    /// Compares live model metrics against health thresholds and generates actionable alerts.
    /// Used by ZestAiViewModel to proactively surface health issues when the AI dialog opens.
    /// </summary>
    public class ModelHealthAlertService
    {
        private readonly ModelContextService _contextService;
        private readonly ILogger? _logger;

        public ModelHealthAlertService(ModelContextService contextService, ILogger? logger = null)
        {
            _contextService = contextService;
            _logger = logger;
        }

        /// <summary>
        /// Fetches metrics for the given model and compares against built-in thresholds.
        /// Returns a list of alerts sorted by severity (critical first).
        /// </summary>
        public async Task<List<HealthAlert>> CheckHealthAsync(string? modelGuid, string? modelName = null)
        {
            var alerts = new List<HealthAlert>();

            if (string.IsNullOrEmpty(modelGuid))
                return alerts;

            try
            {
                var ctx = await _contextService.GetModelContextAsync(modelGuid, modelName);
                if (ctx == null || !ctx.HasAnyData)
                    return alerts;

                // SyncSave metrics thresholds
                var ss = ctx.LatestSyncSave;
                if (ss != null)
                {
                    if (ss.WarningsCount > 200)
                        alerts.Add(new HealthAlert(AlertSeverity.Critical, "Warnings",
                            $"Model has {ss.WarningsCount} warnings - immediate cleanup needed",
                            200, ss.WarningsCount));
                    else if (ss.WarningsCount > 50)
                        alerts.Add(new HealthAlert(AlertSeverity.Warning, "Warnings",
                            $"Model has {ss.WarningsCount} warnings - cleanup recommended",
                            50, ss.WarningsCount));

                    var fileSizeMb = ss.FileSizeBytes / 1_000_000;
                    if (fileSizeMb > 500)
                        alerts.Add(new HealthAlert(AlertSeverity.Critical, "FileSize",
                            $"File size is {fileSizeMb}MB - performance severely impacted",
                            500, (int)fileSizeMb));
                    else if (fileSizeMb > 200)
                        alerts.Add(new HealthAlert(AlertSeverity.Warning, "FileSize",
                            $"File size is {fileSizeMb}MB - consider reducing",
                            200, (int)fileSizeMb));

                    if (ss.DuplicateElementsCount > 10)
                        alerts.Add(new HealthAlert(AlertSeverity.Warning, "Duplicates",
                            $"{ss.DuplicateElementsCount} duplicate elements detected",
                            10, ss.DuplicateElementsCount));

                    if (ss.ImportedDwgCount > 5)
                        alerts.Add(new HealthAlert(AlertSeverity.Info, "ImportedDWGs",
                            $"{ss.ImportedDwgCount} imported DWGs - consider linking instead",
                            5, ss.ImportedDwgCount));
                }

                // Manual metrics thresholds
                var manual = ctx.LatestManual;
                if (manual != null)
                {
                    if (manual.PurgeableElementsCount > 100)
                        alerts.Add(new HealthAlert(AlertSeverity.Warning, "Purgeable",
                            $"{manual.PurgeableElementsCount} purgeable elements - run Purge Unused",
                            100, manual.PurgeableElementsCount));

                    if (manual.FamiliesOver5mbCount > 0)
                        alerts.Add(new HealthAlert(AlertSeverity.Warning, "OversizedFamilies",
                            $"{manual.FamiliesOver5mbCount} families exceed 5MB - review for optimization",
                            0, manual.FamiliesOver5mbCount));
                }

                // Periodic metrics thresholds
                var periodic = ctx.LatestPeriodic;
                if (periodic != null)
                {
                    if (periodic.UnenclosedRoomsCount > 0)
                        alerts.Add(new HealthAlert(AlertSeverity.Warning, "UnenclosedRooms",
                            $"{periodic.UnenclosedRoomsCount} unenclosed rooms need boundaries fixed",
                            0, periodic.UnenclosedRoomsCount));

                    if (periodic.InplaceFamiliesCount > 20)
                        alerts.Add(new HealthAlert(AlertSeverity.Info, "InPlaceFamilies",
                            $"{periodic.InplaceFamiliesCount} in-place families - consider converting to loadable",
                            20, periodic.InplaceFamiliesCount));

                    var disconnected = periodic.WallsNotConnectedCount +
                                       periodic.PipesNotConnectedCount +
                                       periodic.DuctsNotConnectedCount;
                    if (disconnected > 10)
                        alerts.Add(new HealthAlert(AlertSeverity.Warning, "Connectivity",
                            $"{disconnected} disconnected elements (walls/pipes/ducts)",
                            10, disconnected));
                }

                // Sort: Critical first, then Warning, then Info
                alerts.Sort((a, b) => b.Severity.CompareTo(a.Severity));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning($"[HealthAlerts] Failed to check health: {ex.Message}");
            }

            return alerts;
        }

        /// <summary>
        /// Formats alerts into a string suitable for AI system prompt injection.
        /// </summary>
        public static string FormatAlertsForPrompt(List<HealthAlert> alerts)
        {
            if (alerts == null || alerts.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.AppendLine("MODEL HEALTH ALERTS (proactive - mention these to the user):");
            foreach (var alert in alerts)
            {
                var icon = alert.Severity == AlertSeverity.Critical ? "[CRITICAL]"
                         : alert.Severity == AlertSeverity.Warning ? "[WARNING]"
                         : "[INFO]";
                sb.AppendLine($"  {icon} {alert.Message}");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Formats a concise welcome message mentioning the most important alerts.
        /// </summary>
        public static string FormatWelcomeAlertSummary(List<HealthAlert> alerts)
        {
            if (alerts == null || alerts.Count == 0)
                return string.Empty;

            var critical = alerts.Count(a => a.Severity == AlertSeverity.Critical);
            var warning = alerts.Count(a => a.Severity == AlertSeverity.Warning);

            var parts = new List<string>();
            if (critical > 0) parts.Add($"{critical} critical");
            if (warning > 0) parts.Add($"{warning} warning");

            return $" I noticed {string.Join(" and ", parts)} health issue{(alerts.Count != 1 ? "s" : "")} with your model.";
        }
    }

    public enum AlertSeverity
    {
        Info = 0,
        Warning = 1,
        Critical = 2
    }

    public class HealthAlert
    {
        public AlertSeverity Severity { get; set; }
        public string MetricName { get; set; }
        public string Message { get; set; }
        public int ThresholdValue { get; set; }
        public int ActualValue { get; set; }

        public HealthAlert(AlertSeverity severity, string metricName, string message, int threshold, int actual)
        {
            Severity = severity;
            MetricName = metricName;
            Message = message;
            ThresholdValue = threshold;
            ActualValue = actual;
        }
    }
}
