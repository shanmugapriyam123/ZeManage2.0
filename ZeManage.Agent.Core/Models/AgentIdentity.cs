namespace ZeManage.Agent.Core.Models;

public sealed record AgentIdentity
{
    public required string MachineName { get; init; }
    public required string UserName    { get; init; }
    public required string OsVersion   { get; init; }
    public required string MachineId   { get; init; }
    public string? WindowsSid   { get; init; }
    public Guid?   ZeUserId     { get; init; }
    public string? CpuModel     { get; init; }
    public double  TotalRamGB   { get; init; }
    public string? MacAddress   { get; init; }
    public string? SerialNumber { get; init; }
    public string? IpAddress    { get; init; }
}
