using Microsoft.EntityFrameworkCore;

namespace Overseer.Persistence;

/// <summary>Overseer's PostgreSQL database. Its schema is migrated at startup.</summary>
public sealed class OverseerDbContext(DbContextOptions<OverseerDbContext> options) : DbContext(options);
