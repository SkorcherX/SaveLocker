using Microsoft.EntityFrameworkCore;

namespace SaveLocker.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<SaveVersion> SaveVersions => Set<SaveVersion>();
    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<ConflictFlag> Conflicts => Set<ConflictFlag>();
    public DbSet<AgentCommand> AgentCommands => Set<AgentCommand>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<MachineSavePath> MachineSavePaths => Set<MachineSavePath>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Machine>().HasIndex(m => m.Name).IsUnique();
        b.Entity<Game>().HasIndex(g => g.Name).IsUnique();

        // One active lease per game.
        b.Entity<Lease>().HasIndex(l => l.GameId).IsUnique();

        b.Entity<SaveVersion>().HasIndex(v => new { v.GameId, v.CreatedAt });
        b.Entity<ConflictFlag>().HasIndex(c => new { c.GameId, c.Status });
        b.Entity<AuditLog>().HasIndex(a => a.Timestamp);
        b.Entity<AgentCommand>().HasIndex(c => new { c.MachineId, c.Status });

        b.Entity<AppSetting>().HasKey(s => s.Key);

        b.Entity<MachineSavePath>().HasKey(p => new { p.MachineId, p.GameId });
    }
}
