using Microsoft.EntityFrameworkCore;

namespace SaveLocker.Server.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Machine> Machines => Set<Machine>();
    public DbSet<Game> Games => Set<Game>();
    public DbSet<SaveVersion> SaveVersions => Set<SaveVersion>();
    public DbSet<SaveVersionFile> SaveVersionFiles => Set<SaveVersionFile>();
    public DbSet<Lease> Leases => Set<Lease>();
    public DbSet<ConflictFlag> Conflicts => Set<ConflictFlag>();
    public DbSet<AgentCommand> AgentCommands => Set<AgentCommand>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AppSetting> Settings => Set<AppSetting>();
    public DbSet<MachineSavePath> MachineSavePaths => Set<MachineSavePath>();
    public DbSet<MachineScanCandidate> MachineScanCandidates => Set<MachineScanCandidate>();
    public DbSet<EnrollmentToken> EnrollmentTokens => Set<EnrollmentToken>();
    public DbSet<AgentHealth> AgentHealth => Set<AgentHealth>();
    public DbSet<AgentEvent> AgentEvents => Set<AgentEvent>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Machine>().HasIndex(m => m.Name).IsUnique();
        b.Entity<Game>().HasIndex(g => g.Name).IsUnique();

        // One active lease per game.
        b.Entity<Lease>().HasIndex(l => l.GameId).IsUnique();

        b.Entity<SaveVersion>().HasIndex(v => new { v.GameId, v.CreatedAt });
        // Save history outlives the machine that produced it: deleting a machine must null the
        // link, never cascade. SaveVersion.MachineName keeps the uploader nameable afterwards.
        b.Entity<SaveVersion>()
            .HasOne(v => v.Machine).WithMany()
            .HasForeignKey(v => v.MachineId)
            .OnDelete(DeleteBehavior.SetNull);

        // One manifest row per (version, path); the lookup is always "give me this version's whole
        // manifest" or "does this version have one at all", both served by the PK/FK index alone.
        // Cascades with the version: a pruned/deleted SaveVersion's per-file baseline is meaningless
        // once the archive it describes is gone.
        b.Entity<SaveVersionFile>().HasKey(f => new { f.VersionId, f.Path });
        b.Entity<SaveVersionFile>()
            .HasOne(f => f.Version).WithMany()
            .HasForeignKey(f => f.VersionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<ConflictFlag>().HasIndex(c => new { c.GameId, c.Status });
        b.Entity<AuditLog>().HasIndex(a => a.Timestamp);
        b.Entity<AgentCommand>().HasIndex(c => new { c.MachineId, c.Status });

        b.Entity<AppSetting>().HasKey(s => s.Key);

        b.Entity<MachineSavePath>().HasKey(p => new { p.MachineId, p.GameId });

        // Same key shape as the confirmed path, so a machine reports at most one guess per game.
        // Both sides cascade: a guess is meaningless once its machine or its game is gone.
        b.Entity<MachineScanCandidate>().HasKey(c => new { c.MachineId, c.GameId });
        b.Entity<MachineScanCandidate>()
            .HasOne<Machine>().WithMany()
            .HasForeignKey(c => c.MachineId)
            .OnDelete(DeleteBehavior.Cascade);
        b.Entity<MachineScanCandidate>()
            .HasOne<Game>().WithMany()
            .HasForeignKey(c => c.GameId)
            .OnDelete(DeleteBehavior.Cascade);

        // Redeem looks a token up by hash; unique so a hash can never map to two rows.
        b.Entity<EnrollmentToken>().HasIndex(t => t.TokenHash).IsUnique();

        // One health row per machine, and it dies with the machine.
        b.Entity<AgentHealth>().HasKey(h => h.MachineId);
        b.Entity<AgentHealth>()
            .HasOne(h => h.Machine).WithOne()
            .HasForeignKey<AgentHealth>(h => h.MachineId)
            .OnDelete(DeleteBehavior.Cascade);

        // The dedupe lookup on every heartbeat: this machine's open event for this code+game.
        b.Entity<AgentEvent>().HasIndex(e => new { e.MachineId, e.Code, e.GameId, e.ResolvedAt });
        b.Entity<AgentEvent>()
            .HasOne(e => e.Machine).WithMany()
            .HasForeignKey(e => e.MachineId)
            .OnDelete(DeleteBehavior.Cascade);
        // A deleted game must not drag its events' machine rows down with it — null the link instead.
        b.Entity<AgentEvent>()
            .HasOne(e => e.Game).WithMany()
            .HasForeignKey(e => e.GameId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
