using Microsoft.EntityFrameworkCore;
using Overseer.Intents;
using Overseer.Refinement;
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
    public DbSet<IntentRecord> Intents => Set<IntentRecord>();

    private const string CaseInsensitive = "case_insensitive";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SandboxRecord>(sandbox =>
        {
            sandbox.Property(record => record.OwnerKind).HasConversion<string>();
            sandbox.HasIndex(record => record.ResourceId).IsUnique();
        });
        modelBuilder.Entity<RepositorySetupRecord>().Property(record => record.Phase).HasConversion<string>();
        modelBuilder.Entity<IntentRecord>().Property(record => record.State).HasConversion<string>();
        modelBuilder.Entity<SettingRecord>().HasKey(setting => setting.Key);
        // GitHub matches repository names regardless of case, so the key does too.
        modelBuilder.HasCollation(CaseInsensitive, locale: "und-u-ks-level2", provider: "icu", deterministic: false);
        modelBuilder.Entity<ConfiguredRepositoryRecord>(repository =>
        {
            repository.HasKey(record => record.FullName);
            repository.Property(record => record.FullName).UseCollation(CaseInsensitive);
        });
        modelBuilder.Entity<RepositoryInScopeRecord>(repository =>
        {
            repository.ToTable("RepositoriesInScope");
            repository.HasKey(record => new { record.IntentId, record.FullName });
            repository.Property(record => record.FullName).UseCollation(CaseInsensitive);
            repository.HasOne<IntentRecord>().WithMany(intent => intent.RepositoriesInScope).HasForeignKey(record => record.IntentId);
        });
        modelBuilder.Entity<RefinementRecord>(refinement =>
        {
            refinement.ToTable("Refinements");
            refinement.HasKey(record => record.IntentId);
            refinement.Property(record => record.Phase).HasConversion<string>();
            refinement.HasOne<IntentRecord>().WithOne(intent => intent.Refinement).HasForeignKey<RefinementRecord>(record => record.IntentId);
        });
        modelBuilder.Entity<IntentTransitionRecord>(transition =>
        {
            transition.ToTable("IntentTransitions");
            transition.Property(record => record.From).HasConversion<string>();
            transition.Property(record => record.To).HasConversion<string>();
            transition.HasOne<IntentRecord>().WithMany(intent => intent.Transitions).HasForeignKey(record => record.IntentId);
        });
    }
}
