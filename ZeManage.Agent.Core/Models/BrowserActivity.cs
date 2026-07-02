namespace ZeManage.Agent.Core.Models;

public sealed class BrowserActivity
{
    public long Id { get; set; }
    public required string UserName    { get; set; }
    public required string MachineName { get; set; }
    public string? WindowsSid        { get; set; }
    public required string Browser    { get; set; }
    public required string PageTitle  { get; set; }
    public DateTime StartTime         { get; set; }
    public DateTime EndTime           { get; set; }
    public long DurationSeconds       { get; set; }
    public bool Synced                { get; set; }
}
