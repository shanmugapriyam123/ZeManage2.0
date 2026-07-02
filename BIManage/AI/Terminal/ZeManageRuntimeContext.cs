using System;
using System.Collections.Generic;
using BIManage.AI;

namespace BIManage.AI.Terminal;

/// <summary>
/// Process-wide bridge between the ZestAi ViewModel (which has DI access to ModelContextService,
/// the backend HTTP client, etc.) and the AI tool layer (which is constructed by reflection and
/// has no DI). The ViewModel publishes the latest live data here so tools like
/// <c>get_health_alerts</c> can return real values without needing the bootstrapper to wire them.
/// </summary>
/// <remarks>
/// This is deliberately a static singleton with simple set/get semantics — every write replaces
/// the previous value. Concurrency is bounded by Revit's UI thread model: only one chat session
/// is active at a time, so we don't need a lock. The class lives in the Terminal layer rather
/// than the ViewModel layer to keep all tool-facing concerns under <c>BIManage.AI.Terminal</c>.
/// </remarks>
public static class ZeManageRuntimeContext
{
    /// <summary>
    /// Latest list of health alerts the ViewModel has computed for the currently-open model.
    /// Null/empty when no alerts have been fetched yet (or no model is open).
    /// </summary>
    public static IReadOnlyList<HealthAlert>? LatestHealthAlerts { get; private set; }

    /// <summary>The model GUID the alerts apply to. Helps tools sanity-check freshness.</summary>
    public static string? HealthAlertsModelGuid { get; private set; }

    /// <summary>When <see cref="LatestHealthAlerts"/> was last refreshed.</summary>
    public static DateTime? HealthAlertsCapturedAt { get; private set; }

    public static void SetHealthAlerts(string? modelGuid, IReadOnlyList<HealthAlert>? alerts)
    {
        HealthAlertsModelGuid = modelGuid;
        LatestHealthAlerts = alerts;
        HealthAlertsCapturedAt = DateTime.Now;
    }

    public static void Clear()
    {
        LatestHealthAlerts = null;
        HealthAlertsModelGuid = null;
        HealthAlertsCapturedAt = null;
    }
}
