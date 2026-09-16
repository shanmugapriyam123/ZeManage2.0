namespace ZeManage.Agent.Core.Models;

/// <summary>One continuous timeline segment — a span during which the two-state Activity
/// (Active or Idle) AND the foreground application both stayed constant. A NEW row starts
/// whenever either changes (app switch while active, or an active↔idle transition), matching a
/// real employee-monitoring timeline: e.g. Chrome 09:00-09:15 Active, 09:15-09:20 Idle,
/// Chrome-again 09:20-09:45 Active are three separate rows, not one. Finer-grained than
/// <see cref="ApplicationUsage"/> (which tracks open→close of the process itself, not foreground
/// focus or idle state). Synced to the backend on the periodic (~60s) batch pass, not the
/// real-time SignalR channel — see SyncService. The currently-open row is re-sent (upserted) on
/// every sync cycle so the backend/web can always show a "Running" segment for whatever's
/// happening right now, not just once it closes.</summary>
public sealed class ActivityInterval
{
    public string  LocalId  { get; set; } = Guid.NewGuid().ToString(); // local PK, never changes — also the correlation id the backend upserts by
    public string? ApplicationUsageLocalId { get; set; } // parent ApplicationUsage.LocalId, if any (null for browsers/Idle rows)
    public string  ApplicationId   { get; set; } = "";
    /// <summary>"Active" or "Idle" — the two-state classification driving segment boundaries
    /// (Active here covers both the tick loop's Active and Focus buckets — see ProcessMonitor).</summary>
    public required string Activity { get; set; }
    /// <summary>Null for Idle rows — matches the reference timeline design where an idle span has
    /// no meaningful associated application.</summary>
    public string? ApplicationName { get; set; }
    public string? ProcessName     { get; set; }
    public DateTime  StartTime     { get; set; } // segment began
    public DateTime? EndTime       { get; set; } // segment ended (state or app changed) / day rollover / crash-recovery close; null while ongoing
    public long ActiveSeconds      { get; set; }
    public long FocusSeconds       { get; set; }
    public long IdleSeconds        { get; set; }
    public bool  Synced            { get; set; }
    public DateTime CreatedAt      { get; set; }
    public DateTime UpdatedAt      { get; set; }
}
