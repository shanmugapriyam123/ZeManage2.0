namespace ZeManage.AgentService.Models;

public sealed record AgentIdentity
{
    public required string MachineId   { get; init; }
    public required string MachineName { get; init; }
    public required string UserName    { get; init; }
    public string? WindowsSid          { get; init; }

    public string? CpuModel            { get; init; }
    public double  TotalRamGB          { get; init; }
    public double  UsableRamGB         { get; init; }
    public string? GpuModel            { get; init; }

    public double  StorageTotalGB      { get; init; }
    public double  StorageUsedGB       { get; init; }
    public string? StorageType         { get; init; }
    public string? RamType             { get; init; }

    public string? MacAddress          { get; init; }
    public string? IpAddress           { get; init; }

    public string? SerialNumber        { get; init; }
    public string? DeviceId            { get; init; }
    public string? SystemType          { get; init; }
    public string? BiosVersion         { get; init; }
    public string? MotherboardModel    { get; init; }

    public string? OsVersion           { get; init; }
    public string? WindowsEdition      { get; init; }
    public string? WindowsVersion      { get; init; }
    public string? OsBuild             { get; init; }

    public string? TimeZoneId          { get; init; }
}
