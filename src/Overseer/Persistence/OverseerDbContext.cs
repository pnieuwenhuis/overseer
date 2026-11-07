using Microsoft.EntityFrameworkCore;
using Overseer.Intents;
using Overseer.Refinement;
using Overseer.Repositories;
using Overseer.Sandboxes;
using Overseer.Settings;
using Overseer.Specs;

namespace Overseer.Persistence;

/// <summary>Overseer's PostgreSQL database. Its schema is migrated at startup.</summary>
public sealed class OverseerDbContext(DbContextOptions<OverseerDbContext> options) : DbContext(options)
{
    public DbSet<SandboxRecord> Sandboxes => Set<SandboxRecord>();
    public DbSet<SettingRecord> Settings => Set<SettingRecord>();
    public DbSet<ConfiguredRepositoryRecord> ConfiguredRepositories => Set<ConfiguredRepositoryRecord>();
    public DbSet<RepositorySetupRecord> RepositorySetups => Set<RepositorySetupRecord>();
    public DbSet<IntentRecord> Intents => Set<IntentRecord>();
    public DbSet<RefinementRecord> Refinements => Set<RefinementRecord>();
    public DbSet<RoundRecord> Rounds => Set<RoundRecord>();
    public DbSet<SpecVersionRecord> SpecVersions => Set<SpecVersionRecord>();

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
            refinement.HasOne<IntentRecord>().WithOne().HasForeignKey<RefinementRecord>(record => record.IntentId);
        });
        modelBuilder.Entity<RoundRecord>(round =>
        {
            round.ToTable("Rounds");
            round.HasKey(record => new { record.IntentId, record.Number });
            round.HasOne<IntentRecord>().WithMany().HasForeignKey(record => record.IntentId);
            round.HasMany(record => record.Questions).WithOne().HasForeignKey(question => new { question.IntentId, question.RoundNumber });
        });
        modelBuilder.Entity<RoundQuestionRecord>(question =>
        {
            question.ToTable("RoundQuestions");
            question.HasKey(record => new { record.IntentId, record.RoundNumber, record.Number });
        });
        modelBuilder.Entity<SpecVersionRecord>(version =>
        {
            version.ToTable("SpecVersions");
            version.HasKey(record => new { record.IntentId, record.Number });
            version.HasOne<IntentRecord>().WithMany().HasForeignKey(record => record.IntentId);
            version.HasMany(record => record.Decisions).WithOne().HasForeignKey(decision => new { decision.IntentId, decision.SpecVersion });
            version.HasMany(record => record.Tasks).WithOne().HasForeignKey(task => new { task.IntentId, task.SpecVersion });
        });
        modelBuilder.Entity<SpecDecisionRecord>(decision =>
        {
            decision.ToTable("SpecDecisions");
            decision.HasKey(record => new { record.IntentId, record.SpecVersion, record.Number });
        });
        modelBuilder.Entity<SpecTaskRecord>(task =>
        {
            task.ToTable("SpecTasks");
            task.HasKey(record => new { record.IntentId, record.SpecVersion, record.Number });
            task.Property(record => record.Role).HasConversion<string>();
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
