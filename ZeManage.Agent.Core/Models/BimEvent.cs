namespace ZeManage.Agent.Core.Models;

public enum BimEventType
{
    SessionStart,
    SessionEnd,
    Crash,
    SyncDetected,
    ModelOpen
}

public sealed class BimEvent
{
    public long Id { get; set; }
    public required string UserName    { get; set; }
    public required string MachineName { get; set; }
    public string? WindowsSid        { get; set; }
    public required string Application { get; set; }
    public BimEventType EventType      { get; set; }
    public DateTime EventTime          { get; set; }
    public string? Details            { get; set; }
    public bool Synced                 { get; set; }
}
