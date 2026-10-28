using Microsoft.EntityFrameworkCore;
using Overseer.Sandboxes;

namespace Overseer.Persistence;

/// <summary>Overseer's PostgreSQL database. Its schema is migrated at startup.</summary>
public sealed class OverseerDbContext(DbContextOptions<OverseerDbContext> options) : DbContext(options)
{
    public DbSet<SandboxRecord> Sandboxes => Set<SandboxRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SandboxRecord>(sandbox =>
        {
            sandbox.Property(record => record.OwnerKind).HasConversion<string>();
            sandbox.HasIndex(record => record.ResourceId).IsUnique();
        });
    }
}
