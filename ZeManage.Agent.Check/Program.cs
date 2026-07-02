using Microsoft.EntityFrameworkCore;
using ZeManage.Agent.Core.Data;

var db = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ZeManage", "agent.db");

if (!File.Exists(db))
{
    Console.WriteLine($"No DB at {db}");
    return 1;
}

var opts = new DbContextOptionsBuilder<AgentDbContext>()
    .UseSqlite($"Data Source={db}")
    .Options;

await using var ctx = new AgentDbContext(opts);

Console.WriteLine($"DB: {db}\n");
Console.WriteLine($"Attendance        : {await ctx.Attendance.CountAsync()}  (unsynced: {await ctx.Attendance.CountAsync(x => !x.Synced)})");
Console.WriteLine($"ApplicationUsages : {await ctx.ApplicationUsages.CountAsync()}  (unsynced: {await ctx.ApplicationUsages.CountAsync(x => !x.Synced)})");
Console.WriteLine($"HardwareSnapshots : {await ctx.HardwareSnapshots.CountAsync()}  (unsynced: {await ctx.HardwareSnapshots.CountAsync(x => !x.Synced)})");
Console.WriteLine($"NetworkSnapshots  : {await ctx.NetworkSnapshots.CountAsync()}  (unsynced: {await ctx.NetworkSnapshots.CountAsync(x => !x.Synced)})");
Console.WriteLine($"BimEvents         : {await ctx.BimEvents.CountAsync()}  (unsynced: {await ctx.BimEvents.CountAsync(x => !x.Synced)})");
Console.WriteLine($"BrowserActivities : {await ctx.BrowserActivities.CountAsync()}  (unsynced: {await ctx.BrowserActivities.CountAsync(x => !x.Synced)})");
Console.WriteLine($"Screenshots       : {await ctx.Screenshots.CountAsync()}  (unsynced: {await ctx.Screenshots.CountAsync(x => !x.Synced)})");

Console.WriteLine("\n-- Latest Attendance --");
foreach (var a in await ctx.Attendance.OrderByDescending(x => x.Id).Take(3).ToListAsync())
    Console.WriteLine($"  {a.UserName}@{a.MachineName}  login={a.LoginTime:u}  logout={a.LogoutTime:u}  active={a.ActiveSeconds}s  idle={a.IdleSeconds}s  sid={a.WindowsSid ?? "N/A"}");

Console.WriteLine("\n-- Latest Hardware --");
foreach (var h in await ctx.HardwareSnapshots.OrderByDescending(x => x.Id).Take(5).ToListAsync())
    Console.WriteLine($"  {h.CapturedAt:u}  CPU={h.CpuUsagePercent}%  RAM={h.RamUsagePercent}% ({h.RamUsedGB}/{h.RamTotalGB} GB)  GPU={h.GpuUsagePercent}%  Disk={h.DiskUsagePercent}%");

Console.WriteLine("\n-- Latest Network --");
foreach (var n in await ctx.NetworkSnapshots.OrderByDescending(x => x.Id).Take(5).ToListAsync())
    Console.WriteLine($"  {n.CapturedAt:u}  latency={n.LatencyMs}ms  loss={n.PacketLossPercent}%  health={n.HealthScore}  vpn={n.VpnConnected}  adapter={n.ActiveAdapter}");

Console.WriteLine("\n-- Latest Applications --");
foreach (var u in await ctx.ApplicationUsages.OrderByDescending(x => x.Id).Take(10).ToListAsync())
    Console.WriteLine($"  {u.ApplicationName} ({u.ProcessName})  {u.DurationSeconds}s  cat={u.Category}  sid={u.WindowsSid ?? "N/A"}");

Console.WriteLine("\n-- Latest BIM Events --");
foreach (var b in await ctx.BimEvents.OrderByDescending(x => x.Id).Take(10).ToListAsync())
    Console.WriteLine($"  {b.EventTime:u}  {b.Application}  {b.EventType}  {b.Details}  sid={b.WindowsSid ?? "N/A"}");

Console.WriteLine("\n-- Latest Browser Activity --");
foreach (var br in await ctx.BrowserActivities.OrderByDescending(x => x.Id).Take(10).ToListAsync())
    Console.WriteLine($"  {br.StartTime:u}  [{br.Browser}]  {br.PageTitle}  ({br.DurationSeconds}s)  sid={br.WindowsSid ?? "N/A"}");

return 0;
