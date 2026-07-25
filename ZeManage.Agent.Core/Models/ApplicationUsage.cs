namespace ZeManage.Agent.Core.Models;

public sealed class ApplicationUsage
{
    public string  LocalId         { get; set; } = Guid.NewGuid().ToString(); // local PK, never changes
    public string? SessionId       { get; set; }  // null until server POST returns it
    public string  ApplicationId   { get; set; } = "";
    public required string ApplicationName { get; set; }
    public string? IconBase64      { get; set; }
    public required string ProcessName    { get; set; }
    public string? Version         { get; set; }
    public string  ApplicationType { get; set; } = "Software";
    public DateTime  StartTime     { get; set; }
    public DateTime? EndTime       { get; set; }
    public long ActiveSeconds      { get; set; }
    public long FocusSeconds       { get; set; }
    public long IdleSeconds        { get; set; }
    public int  CrashCount         { get; set; }
    public string Status           { get; set; } = "Running";
    public bool  Synced            { get; set; }
    public DateTime CreatedAt      { get; set; }
    public DateTime UpdatedAt      { get; set; }
}
