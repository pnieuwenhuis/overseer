using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Overseer.Persistence;

/// <summary>Lets <c>dotnet ef migrations add</c> create the context without running <c>serve</c>; it never connects.</summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<OverseerDbContext>
{
    public OverseerDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OverseerDbContext>().UseNpgsql("Host=localhost").Options);
}
