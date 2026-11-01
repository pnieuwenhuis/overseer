using Microsoft.EntityFrameworkCore;
using Overseer.Repositories;
using Overseer.Sandboxes;
using Overseer.Settings;

namespace Overseer.Persistence;

/// <summary>Overseer's PostgreSQL database. Its schema is migrated at startup.</summary>
public sealed class OverseerDbContext(DbContextOptions<OverseerDbContext> options) : DbContext(options)
{
    public DbSet<SandboxRecord> Sandboxes => Set<SandboxRecord>();
    public DbSet<SettingRecord> Settings => Set<SettingRecord>();
    public DbSet<ConfiguredRepositoryRecord> ConfiguredRepositories => Set<ConfiguredRepositoryRecord>();
    public DbSet<RepositorySetupRecord> RepositorySetups => Set<RepositorySetupRecord>();

    private const string CaseInsensitive = "case_insensitive";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SandboxRecord>(sandbox =>
        {
            sandbox.Property(record => record.OwnerKind).HasConversion<string>();
            sandbox.HasIndex(record => record.ResourceId).IsUnique();
        });
        modelBuilder.Entity<SettingRecord>().HasKey(setting => setting.Key);
        // GitHub matches repository names regardless of case, so the key does too.
        modelBuilder.HasCollation(CaseInsensitive, locale: "und-u-ks-level2", provider: "icu", deterministic: false);
        modelBuilder.Entity<ConfiguredRepositoryRecord>(repository =>
        {
            repository.HasKey(record => record.FullName);
            repository.Property(record => record.FullName).UseCollation(CaseInsensitive);
        });
    }
}
