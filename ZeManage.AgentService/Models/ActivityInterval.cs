namespace ZeManage.AgentService.Models;

/// <summary>One continuous timeline segment — a span during which the two-state Activity
/// (Active/Idle) and the foreground application both stayed constant. A new row starts whenever
/// either changes. The currently-open row (EndTime == null) is re-sent every sync cycle so the
/// backend can show a "Running" segment for whatever's happening right now.</summary>
public sealed class ActivityInterval
{
    public string  LocalId  { get; set; } = Guid.NewGuid().ToString();
    public string  ApplicationId   { get; set; } = "";
    public required string Activity { get; set; } // "Active" or "Idle"
    public string? ApplicationName { get; set; }
    public string? ProcessName     { get; set; }
    public DateTime  StartTime     { get; set; }
    public DateTime? EndTime       { get; set; }
    public long ActiveSeconds      { get; set; }
    public long FocusSeconds       { get; set; }
    public long IdleSeconds        { get; set; }
    public bool  Synced            { get; set; }
    public DateTime CreatedAt      { get; set; }
    public DateTime UpdatedAt      { get; set; }
}
