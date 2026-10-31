using Microsoft.EntityFrameworkCore;
using Overseer.ModelProviders;
using Overseer.Persistence;
using Overseer.Secrets;
using Overseer.Settings;

namespace Overseer.Sandboxes;

/// <summary>
/// Creates and destroys Sandboxes through the configured Sandbox Provider, and keeps a record of each one in PostgreSQL.
/// Orchestration goes through here rather than to the provider, so no Sandbox goes unrecorded.
/// </summary>
public sealed class SandboxRegistry(
    ISandboxProvider provider,
    SettingsStore settings,
    ModelProviderTokens providerTokens,
    ISecretReader secrets,
    IDbContextFactory<OverseerDbContext> databases,
    TimeProvider time)
{
    /// <summary>
    /// Creates a Sandbox holding the Repositories in scope, tagged with its owner, on the machine the Settings say now.
    /// Completes once the Sandbox is ready for use.
    /// </summary>
    /// <inheritdoc cref="ISandboxProvider.CreateAsync"/>
    public async Task<Sandbox> CreateAsync(SandboxOwner owner, IReadOnlyCollection<string> repositoriesInScope, CancellationToken cancellationToken)
    {
        var machine = await settings.GetSandboxMachineAsync(cancellationToken);
        var sandbox = await provider.CreateAsync(owner, machine, repositoriesInScope, cancellationToken);
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

    /// <summary>
    /// Starts an agent in the given role, with the agent harness configuration the Settings hold now, and every model
    /// provider token stored now in its environment variable.
    /// </summary>
    /// <inheritdoc cref="ISandboxProvider.StartAgentSessionAsync"/>
    public async Task<IAgentSession> StartAgentSessionAsync(Sandbox sandbox, AgentRole role, CancellationToken cancellationToken)
    {
        var harness = await settings.GetAgentHarnessAsync(cancellationToken);
        var environment = new Dictionary<string, string>();
        foreach (var token in await providerTokens.ListAsync(cancellationToken))
        {
            // Null when the token was removed since it was listed.
            if (await secrets.ReadAsync(token.SecretName, cancellationToken) is { } value)
            {
                environment[token.EnvironmentVariable] = value;
            }
        }
        return await provider.StartAgentSessionAsync(sandbox, role, harness, environment, cancellationToken);
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
