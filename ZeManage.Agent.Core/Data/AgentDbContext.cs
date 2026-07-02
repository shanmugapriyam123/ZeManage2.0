using Microsoft.EntityFrameworkCore;
using ZeManage.Agent.Core.Models;

namespace ZeManage.Agent.Core.Data;

public sealed class AgentDbContext : DbContext
{
    public DbSet<AttendanceSession>  Attendance        => Set<AttendanceSession>();
    public DbSet<ApplicationUsage>   ApplicationUsages => Set<ApplicationUsage>();
    public DbSet<HardwareSnapshot>   HardwareSnapshots => Set<HardwareSnapshot>();
    public DbSet<NetworkSnapshot>    NetworkSnapshots  => Set<NetworkSnapshot>();
    public DbSet<BimEvent>           BimEvents         => Set<BimEvent>();
    public DbSet<BrowserActivity>    BrowserActivities => Set<BrowserActivity>();
    public DbSet<Screenshot>         Screenshots       => Set<Screenshot>();

    public AgentDbContext(DbContextOptions<AgentDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AttendanceSession>().HasIndex(x => x.Synced);
        b.Entity<ApplicationUsage>().HasIndex(x => x.Synced);
        b.Entity<HardwareSnapshot>().HasIndex(x => x.Synced);
        b.Entity<NetworkSnapshot>().HasIndex(x => x.Synced);
        b.Entity<BimEvent>().HasIndex(x => x.Synced);
        b.Entity<BrowserActivity>().HasIndex(x => x.Synced);
        b.Entity<Screenshot>().HasIndex(x => x.Synced);
        b.Entity<Screenshot>().HasIndex(x => x.CapturedAt);
    }
}
