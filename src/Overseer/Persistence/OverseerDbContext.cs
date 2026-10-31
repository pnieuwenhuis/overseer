using Microsoft.EntityFrameworkCore;
using Overseer.Sandboxes;
using Overseer.Settings;

namespace Overseer.Persistence;

/// <summary>Overseer's PostgreSQL database. Its schema is migrated at startup.</summary>
public sealed class OverseerDbContext(DbContextOptions<OverseerDbContext> options) : DbContext(options)
{
    public DbSet<SandboxRecord> Sandboxes => Set<SandboxRecord>();
    public DbSet<SettingRecord> Settings => Set<SettingRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SandboxRecord>(sandbox =>
        {
            sandbox.Property(record => record.OwnerKind).HasConversion<string>();
            sandbox.HasIndex(record => record.ResourceId).IsUnique();
        });
        modelBuilder.Entity<SettingRecord>().HasKey(setting => setting.Key);
    }
}
