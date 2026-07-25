namespace ZeManage.Agent.Core.Models;

public sealed record AgentIdentity
{
    public required string MachineId      { get; init; }
    public required string MachineName    { get; init; }
    public required string UserName       { get; init; }
    public string? WindowsSid            { get; init; }

    // Processor
    public string? CpuModel             { get; init; }

    // RAM
    public double  TotalRamGB           { get; init; }
    public double  UsableRamGB          { get; init; }

    // GPU
    public string? GpuModel             { get; init; }

    // Storage (system drive)
    public double  StorageTotalGB       { get; init; }
    public double  StorageUsedGB        { get; init; }

    // Network
    public string? MacAddress           { get; init; }
    public string? IpAddress            { get; init; }

    // System identity
    public string? SerialNumber         { get; init; }
    public string? DeviceId             { get; init; }
    public string? SystemType           { get; init; }
    public string? BiosVersion          { get; init; }
    public string? MotherboardModel     { get; init; }

    // Windows OS
    public string? OsVersion            { get; init; }
    public string? WindowsEdition       { get; init; }
    public string? WindowsVersion       { get; init; }
    public string? OsBuild              { get; init; }
}
