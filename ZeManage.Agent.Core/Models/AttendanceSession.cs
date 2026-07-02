namespace ZeManage.Agent.Core.Models;

public sealed class AttendanceSession
{
    public long Id { get; set; }
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public required string UserName { get; set; }
    public required string MachineName { get; set; }
    public string? WindowsSid { get; set; }
    public DateTime LoginTime { get; set; }
    public DateTime? LogoutTime { get; set; }
    public long ActiveSeconds { get; set; }
    public long IdleSeconds { get; set; }
    public bool Synced { get; set; }
}
