using System;
using System.Collections.Generic;
using System.Threading;
using Autodesk.Revit.UI;
using BIManage.AI;
using BIManage.AI.Terminal;
using BIManage.AI.Terminal.Execution;

namespace BIManage.AI.Terminal.Tools.GetHealthAlerts;

/// <summary>
/// Handler for <c>get_health_alerts</c>. Reads from <see cref="ZeManageRuntimeContext"/>,
/// which the ZestAi ViewModel populates at chat startup. No Revit API or HTTP I/O needed.
/// </summary>
internal sealed class GetHealthAlertsEventHandler : IWaitableExternalEventHandler
{
    private readonly ManualResetEvent _resetEvent = new(false);

    public HealthAlertsResult? Result { get; private set; }

    public bool WaitForCompletion(int timeoutMilliseconds = 10000)
    {
        _resetEvent.Reset();
        return _resetEvent.WaitOne(timeoutMilliseconds);
    }

    public void Execute(UIApplication app)
    {
        try
        {
            var alerts = ZeManageRuntimeContext.LatestHealthAlerts;
            var modelGuid = ZeManageRuntimeContext.HealthAlertsModelGuid;
            var capturedAt = ZeManageRuntimeContext.HealthAlertsCapturedAt;

            if (alerts == null)
            {
                // The chat started before the health-alert fetch finished, or no model is open,
                // or the backend metrics endpoint hasn't returned data for this model yet.
                Result = new HealthAlertsResult
                {
                    Success = true,
                    AlertCount = 0,
                    Status = "no_data",
                    Message = "No health alerts available — either no model is open, the model's " +
                              "backend metrics haven't synced yet, or the health check is still running.",
                    Alerts = new List<HealthAlertRow>()
                };
                return;
            }

            if (alerts.Count == 0)
            {
                Result = new HealthAlertsResult
                {
                    Success = true,
                    AlertCount = 0,
                    Status = "healthy",
                    ModelGuid = modelGuid,
                    CapturedAtUtc = capturedAt?.ToUniversalTime().ToString("o"),
                    Message = "No health issues detected — model passes all threshold checks.",
                    Alerts = new List<HealthAlertRow>()
                };
                return;
            }

            var rows = new List<HealthAlertRow>(alerts.Count);
            int critical = 0, warning = 0, info = 0;
            foreach (var a in alerts)
            {
                rows.Add(new HealthAlertRow
                {
                    Severity = a.Severity.ToString(),
                    MetricName = a.MetricName,
                    Message = a.Message,
                    ThresholdValue = a.ThresholdValue,
                    ActualValue = a.ActualValue
                });
                switch (a.Severity)
                {
                    case AlertSeverity.Critical: critical++; break;
                    case AlertSeverity.Warning:  warning++;  break;
                    case AlertSeverity.Info:     info++;     break;
                }
            }

            Result = new HealthAlertsResult
            {
                Success = true,
                AlertCount = alerts.Count,
                CriticalCount = critical,
                WarningCount = warning,
                InfoCount = info,
                Status = critical > 0 ? "critical" : (warning > 0 ? "warning" : "info"),
                ModelGuid = modelGuid,
                CapturedAtUtc = capturedAt?.ToUniversalTime().ToString("o"),
                Alerts = rows
            };
        }
        catch (Exception ex)
        {
            Result = new HealthAlertsResult
            {
                Success = false,
                Status = "error",
                Message = ex.Message,
                Alerts = new List<HealthAlertRow>()
            };
        }
        finally
        {
            _resetEvent.Set();
        }
    }

    public string GetName() => "ZeManage Get Health Alerts";
}

public sealed class HealthAlertsResult
{
    public bool Success { get; set; }
    public string? Status { get; set; }         // healthy | warning | critical | no_data | error
    public string? Message { get; set; }        // populated when Status is no_data / error / healthy
    public string? ModelGuid { get; set; }
    public string? CapturedAtUtc { get; set; }
    public int AlertCount { get; set; }
    public int CriticalCount { get; set; }
    public int WarningCount { get; set; }
    public int InfoCount { get; set; }
    public List<HealthAlertRow> Alerts { get; set; } = new();
}

public sealed class HealthAlertRow
{
    public string Severity { get; set; } = "";
    public string MetricName { get; set; } = "";
    public string Message { get; set; } = "";
    public int ThresholdValue { get; set; }
    public int ActualValue { get; set; }
}
