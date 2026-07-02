namespace ZeManage.Agent.Core.Models;

public sealed class ApplicationUsage
{
    public long Id { get; set; }
    public required string UserName        { get; set; }
    public required string MachineName     { get; set; }
    public string? WindowsSid             { get; set; }
    public required string ApplicationName { get; set; }
    public required string ProcessName     { get; set; }
    public string? Version                { get; set; }
    public DateTime StartTime             { get; set; }
    public DateTime? EndTime              { get; set; }
    public long DurationSeconds           { get; set; }
    public int CrashCount                 { get; set; }
    public string Category                { get; set; } = "Other";
    public bool Synced                    { get; set; }
}
