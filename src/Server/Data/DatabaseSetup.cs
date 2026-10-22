using SaveLocker.Server.Services;
using Microsoft.EntityFrameworkCore;

namespace SaveLocker.Server.Data;

/// <summary>
/// Brings a database file up to this build: the pre-migration fix-ups, <c>Migrate()</c>, the post-migration
/// chores and WAL mode. Run at startup AND after a backup restore — a restored database can be as old as any
/// backup, so it needs exactly what a start would give it (<c>Migrate()</c> alone throws "table already exists"
/// on a pre-migration file, after the live database has already been replaced).
/// </summary>
public static class DatabaseSetup
{
    public static async Task PrepareAsync(IServiceProvider scoped, CancellationToken ct = default)
    {
        var db = scoped.GetRequiredService<AppDbContext>();

        var historyExists = db.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'")
            .Single() > 0;

        if (!historyExists)
        {
            var gamesTableExists = db.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type='table' AND name='Games'")
                .Single() > 0;

            if (gamesTableExists)
            {
                // Pre-migration DB: schema is already at InitialSchema; just seed the history table.
                db.Database.ExecuteSqlRaw("""
                    CREATE TABLE "__EFMigrationsHistory" (
                        "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                        "ProductVersion" TEXT NOT NULL
                    );
                    """);
                db.Database.ExecuteSqlRaw("""
                    INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260624011934_InitialSchema', '9.0.9');
                    """);
                historyExists = true;

                // If RetainVersions was already added by the pre-migration manual workaround,
                // stamp the migration as applied so EF doesn't attempt the ALTER TABLE again.
                var hasRetainVersions = db.Database
                    .SqlQueryRaw<string>("SELECT name FROM pragma_table_info('Games')")
                    .ToList()
                    .Contains("RetainVersions");
                if (hasRetainVersions)
                    db.Database.ExecuteSqlRaw("""
                        INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                        VALUES ('20260626031438_AddGameRetainVersions', '9.0.9');
                        """);
            }
        }

        // MachineSavePaths used to be created out-of-band via CREATE TABLE IF NOT EXISTS
        // (it predates being an EF entity). On any DB where that table already exists,
        // stamp the AddMachineSavePaths migration as applied so Migrate() doesn't try to
        // recreate it (which would throw "table already exists"). Only meaningful once a
        // history table is present — a fresh DB has neither and gets the table from Migrate().
        if (historyExists)
        {
            var machineSavePathsExists = db.Database
                .SqlQuery<int>($"SELECT COUNT(*) AS \"Value\" FROM sqlite_master WHERE type='table' AND name='MachineSavePaths'")
                .Single() > 0;
            if (machineSavePathsExists)
                db.Database.ExecuteSqlRaw("""
                    INSERT OR IGNORE INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
                    VALUES ('20260706022305_AddMachineSavePaths', '9.0.9');
                    """);
        }

        await db.Database.MigrateAsync(ct);
        var settings = scoped.GetRequiredService<SettingsService>();
        await settings.SetAsync(Program.LastStartedVersionKey, BuildInfo.Current.Version, ct);
        // A restored pre-encryption database brings its secrets back in plain text: encrypt them now, not at the next start.
        await settings.EncryptPlaintextSecretsAsync(ct);

        // WAL mode: allows concurrent readers alongside the single writer, which prevents
        // "database is locked" 500s when the dashboard fires several parallel API calls.
        db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }
}
