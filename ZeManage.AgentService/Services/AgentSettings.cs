namespace ZeManage.AgentService.Services;

/// <summary>Server-configured capture settings, fetched periodically by SyncService from
/// GET /api/v1/agentdb/group-worktime-setup/my-group and consumed by ProcessMonitor (idle
/// threshold) and ScreenshotMonitor (enabled/interval/schedule). Every field defaults to the
/// same always-on behavior the monitors had before this existed, so a device that hasn't synced
/// yet (or whose group has no settings) just keeps capturing on the local appsettings.json
/// defaults instead of going dark.</summary>
public sealed class AgentSettings
{
    public bool IsIdleTrackingEnabled { get; set; } = true;
    public int? IdleThresholdMinutes { get; set; }

    public bool IsScreenshotEnabled { get; set; } = true;
    public int? ScreenshotIntervalMinutes { get; set; }

    public bool IsActiveWindowEnabled { get; set; }
    public TimeSpan ScreenshotStartTime { get; set; } = TimeSpan.Zero;
    public TimeSpan ScreenshotEndTime { get; set; } = TimeSpan.FromHours(24);

    public bool ScreenshotMonday { get; set; } = true;
    public bool ScreenshotTuesday { get; set; } = true;
    public bool ScreenshotWednesday { get; set; } = true;
    public bool ScreenshotThursday { get; set; } = true;
    public bool ScreenshotFriday { get; set; } = true;
    public bool ScreenshotSaturday { get; set; } = true;
    public bool ScreenshotSunday { get; set; } = true;

    public DateTime? LastFetchedUtc { get; set; }

    public bool IsWithinSchedule(DateTime now)
    {
        var dayEnabled = now.DayOfWeek switch
        {
            DayOfWeek.Monday => ScreenshotMonday,
            DayOfWeek.Tuesday => ScreenshotTuesday,
            DayOfWeek.Wednesday => ScreenshotWednesday,
            DayOfWeek.Thursday => ScreenshotThursday,
            DayOfWeek.Friday => ScreenshotFriday,
            DayOfWeek.Saturday => ScreenshotSaturday,
            DayOfWeek.Sunday => ScreenshotSunday,
            _ => false
        };
        if (!IsActiveWindowEnabled) return dayEnabled;

        var t = now.TimeOfDay;
        return dayEnabled && t >= ScreenshotStartTime && t <= ScreenshotEndTime;
    }
}
