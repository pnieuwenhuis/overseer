using Microsoft.EntityFrameworkCore;
using Overseer.Persistence;

namespace Overseer.Sandboxes;

/// <summary>
/// Creates and destroys Sandboxes through the configured Sandbox Provider, and keeps a record of each one in PostgreSQL.
/// Orchestration goes through here rather than to the provider, so no Sandbox goes unrecorded.
/// </summary>
public sealed class SandboxRegistry(ISandboxProvider provider, IDbContextFactory<OverseerDbContext> databases, TimeProvider time)
{
    /// <inheritdoc cref="ISandboxProvider.CreateAsync"/>
    public async Task<Sandbox> CreateAsync(SandboxOwner owner, IReadOnlyCollection<string> repositoriesInScope, CancellationToken cancellationToken)
    {
        var sandbox = await provider.CreateAsync(owner, repositoriesInScope, cancellationToken);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        database.Sandboxes.Add(new SandboxRecord
        {
            OwnerKind = owner.Kind,
            OwnerId = owner.Id,
            ResourceId = sandbox.ResourceId,
            CreatedAt = time.GetUtcNow(),
        });
        await database.SaveChangesAsync(cancellationToken);
        return sandbox;
    }

    /// <summary>Destroys the Sandbox and records when; the record itself is kept.</summary>
    public async Task DestroyAsync(Sandbox sandbox, CancellationToken cancellationToken)
    {
        await provider.DestroyAsync(sandbox, cancellationToken);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var destroyedAt = time.GetUtcNow();
        await database.Sandboxes
            .Where(record => record.ResourceId == sandbox.ResourceId && record.DestroyedAt == null)
            .ExecuteUpdateAsync(record => record.SetProperty(r => r.DestroyedAt, destroyedAt), cancellationToken);
    }
}

/// <summary>A Sandbox as PostgreSQL remembers it, including after it has been destroyed.</summary>
public sealed class SandboxRecord
{
    public Guid Id { get; init; }
    public required SandboxOwnerKind OwnerKind { get; init; }
    public required Guid OwnerId { get; init; }
    /// <summary>The provider's identifier for the machine, such as an Azure resource ID.</summary>
    public required string ResourceId { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DestroyedAt { get; set; }
}
