using Microsoft.EntityFrameworkCore;
using ZeManage.AgentService.Models;

namespace ZeManage.AgentService.Data;

public sealed class AgentServiceDbContext : DbContext
{
    public DbSet<ApplicationUsage> ApplicationUsages => Set<ApplicationUsage>();
    public DbSet<NetworkSnapshot>  NetworkSnapshots  => Set<NetworkSnapshot>();
    public DbSet<BrowserActivity>  BrowserActivities => Set<BrowserActivity>();
    public DbSet<Screenshot>       Screenshots       => Set<Screenshot>();
    public DbSet<ActivityInterval> ActivityIntervals => Set<ActivityInterval>();
    public DbSet<MachineInfo>      MachineInfos      => Set<MachineInfo>();

    public AgentServiceDbContext(DbContextOptions<AgentServiceDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ApplicationUsage>().HasKey(x => x.LocalId);
        b.Entity<ApplicationUsage>().Property(x => x.LocalId).ValueGeneratedNever();
        b.Entity<ApplicationUsage>().HasIndex(x => x.Synced);

        b.Entity<NetworkSnapshot>().HasKey(x => x.Id);
        b.Entity<NetworkSnapshot>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<NetworkSnapshot>().HasIndex(x => x.Synced);

        b.Entity<BrowserActivity>().HasKey(x => x.Id);
        b.Entity<BrowserActivity>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<BrowserActivity>().HasIndex(x => x.Synced);

        b.Entity<Screenshot>().HasKey(x => x.Id);
        b.Entity<Screenshot>().Property(x => x.Id).ValueGeneratedNever();
        b.Entity<Screenshot>().HasIndex(x => x.Synced);
        b.Entity<Screenshot>().HasIndex(x => x.CapturedAt);

        b.Entity<ActivityInterval>().ToTable("ActivityTimeline");
        b.Entity<ActivityInterval>().HasKey(x => x.LocalId);
        b.Entity<ActivityInterval>().Property(x => x.LocalId).ValueGeneratedNever();
        b.Entity<ActivityInterval>().HasIndex(x => x.Synced);
        b.Entity<ActivityInterval>().HasIndex(x => x.EndTime);

        b.Entity<MachineInfo>().HasKey(x => x.MachineId);
    }
}

/// <summary>Single-row machine identity table — mirrors the reference Agent's machine_info
/// table, kept as a proper EF entity here since this service has no legacy INTEGER-PK migration
/// history to work around (fresh DB, fresh schema).</summary>
public sealed class MachineInfo
{
    public required string MachineId { get; set; }
    public string? Sid               { get; set; }
    public required string UserName  { get; set; }
    public required string HostName  { get; set; }
    public DateTime? CapturedAt      { get; set; }
    public string? CpuModel          { get; set; }
    public double RamGb              { get; set; }
    public double? RamUsableGb       { get; set; }
    public string? GpuModel          { get; set; }
    public double? StorageTotalGb    { get; set; }
    public double? StorageUsedGb     { get; set; }
    public string? StorageType       { get; set; }
    public string? RamType           { get; set; }
    public string? MacAddress        { get; set; }
    public string? IpAddress         { get; set; }
    public string? SerialNumber      { get; set; }
    public string? DeviceId          { get; set; }
    public string? SystemType        { get; set; }
    public string? OsVersion         { get; set; }
    public string? WindowsEdition    { get; set; }
    public string? WindowsVersion    { get; set; }
    public string? OsBuild           { get; set; }
    public string? BiosVersion       { get; set; }
    public string? MotherboardModel  { get; set; }
    public string? TimezoneId        { get; set; }
}
