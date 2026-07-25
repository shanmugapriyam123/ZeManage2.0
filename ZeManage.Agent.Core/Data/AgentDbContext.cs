using Microsoft.EntityFrameworkCore;
using ZeManage.Agent.Core.Models;

namespace ZeManage.Agent.Core.Data;

public sealed class AgentDbContext : DbContext
{
    public DbSet<ApplicationUsage>   ApplicationUsages => Set<ApplicationUsage>();
    public DbSet<NetworkSnapshot>    NetworkSnapshots  => Set<NetworkSnapshot>();
    public DbSet<BrowserActivity>    BrowserActivities => Set<BrowserActivity>();
    public DbSet<Screenshot>         Screenshots       => Set<Screenshot>();

    public AgentDbContext(DbContextOptions<AgentDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ApplicationUsage>()
            .HasKey(x => x.LocalId);
        b.Entity<ApplicationUsage>()
            .Property(x => x.LocalId).HasColumnName("local_id").ValueGeneratedNever();
        b.Entity<ApplicationUsage>()
            .Property(x => x.SessionId).HasColumnName("session_id");
        b.Entity<ApplicationUsage>().HasIndex(x => x.Synced);

        b.Entity<NetworkSnapshot>()
            .Property(x => x.Id).HasColumnName("network_snapshot_id").ValueGeneratedNever();
        b.Entity<NetworkSnapshot>().HasIndex(x => x.Synced);

        b.Entity<BrowserActivity>()
            .Property(x => x.Id).HasColumnName("browser_activity_id").ValueGeneratedNever();
        b.Entity<BrowserActivity>().HasIndex(x => x.Synced);

        b.Entity<Screenshot>()
            .Property(x => x.Id).HasColumnName("screenshot_id").ValueGeneratedNever();
        b.Entity<Screenshot>().HasIndex(x => x.Synced);
        b.Entity<Screenshot>().HasIndex(x => x.CapturedAt);
    }
}
