using System.Collections.Concurrent;
using System.Linq.Expressions;
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
    TimeProvider time,
    ILogger<SandboxRegistry> logger)
{
    /// <summary>One lock per Sandbox that was destroyed, or is being, since serve started, by resource ID.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _destroying = new();

    /// <summary>
    /// Creates a Sandbox holding the Repositories in scope, tagged with its owner, on the machine the Settings say now.
    /// Completes once the Sandbox is ready for use.
    /// </summary>
    /// <inheritdoc cref="ISandboxProvider.CreateAsync"/>
    public async Task<Sandbox> CreateAsync(SandboxOwner owner, IReadOnlyCollection<string> repositoriesInScope, bool mayPush, CancellationToken cancellationToken)
    {
        var machine = await settings.GetSandboxMachineAsync(cancellationToken);
        // Recorded before the provider is asked, so that whatever stops Overseer while the Sandbox is provisioned, a crash
        // among them, leaves it recorded for the next start's sweep.
        var sandbox = provider.Name(owner);
        await using (var database = await databases.CreateDbContextAsync(cancellationToken))
        {
            database.Sandboxes.Add(new SandboxRecord
            {
                OwnerKind = owner.Kind,
                OwnerId = owner.Id,
                ResourceId = sandbox.ResourceId,
                CreatedAt = time.GetUtcNow(),
            });
            await database.SaveChangesAsync(cancellationToken);
        }
        try
        {
            await provider.CreateAsync(sandbox, machine, repositoriesInScope, mayPush, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Whatever the provider left of it goes now. Not while Overseer stops, which shouldn't wait for that: the next
            // start's sweep sees to it then.
            await DestroyOrLeaveAsync(sandbox);
            throw;
        }
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

    /// <inheritdoc cref="ISandboxProvider.RunCommandAsync"/>
    public Task<CommandResult> RunCommandAsync(Sandbox sandbox, string command, CancellationToken cancellationToken) =>
        provider.RunCommandAsync(sandbox, command, cancellationToken);

    /// <inheritdoc cref="ISandboxProvider.ReadFileAsync"/>
    public Task<string?> ReadFileAsync(Sandbox sandbox, string path, CancellationToken cancellationToken) =>
        provider.ReadFileAsync(sandbox, path, cancellationToken);

    /// <inheritdoc cref="ISandboxProvider.TakeFileAsync"/>
    public Task<string?> TakeFileAsync(Sandbox sandbox, string path, CancellationToken cancellationToken) =>
        provider.TakeFileAsync(sandbox, path, cancellationToken);

    /// <summary>
    /// Whether the Sandbox is still there to work in: not recorded destroyed, and the provider still has it, which it hasn't once
    /// the machine was deleted behind Overseer's back, as an evicted spot instance is.
    /// </summary>
    public async Task<bool> ExistsAsync(Sandbox sandbox, CancellationToken cancellationToken) =>
        (await ListRecordedAsync(sandbox.Owner, cancellationToken)).Contains(sandbox)
            && (await provider.ListAsync(cancellationToken)).Any(existing => existing.ResourceId == sandbox.ResourceId);

    /// <summary>Every Sandbox that is recorded and not recorded destroyed.</summary>
    public Task<IReadOnlyList<Sandbox>> ListRecordedAsync(CancellationToken cancellationToken) =>
        ListUndestroyedAsync(record => true, cancellationToken);

    /// <summary>The Sandboxes of this owner that are recorded and not recorded destroyed.</summary>
    public Task<IReadOnlyList<Sandbox>> ListRecordedAsync(SandboxOwner owner, CancellationToken cancellationToken) =>
        ListUndestroyedAsync(record => record.OwnerKind == owner.Kind && record.OwnerId == owner.Id, cancellationToken);

    private async Task<IReadOnlyList<Sandbox>> ListUndestroyedAsync(
        Expression<Func<SandboxRecord, bool>> owned, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var records = await database.Sandboxes.Where(record => record.DestroyedAt == null).Where(owned).ToListAsync(cancellationToken);
        return [.. records.Select(record => new Sandbox(record.ResourceId, new SandboxOwner(record.OwnerKind, record.OwnerId)))];
    }

    /// <summary>
    /// Destroys the Sandbox and records when; the record itself is kept. When destroying fails, the Sandbox stays recorded
    /// undestroyed, so that the next start's sweep (<see cref="LeftOverSandboxes"/>) destroys it. A Sandbox is destroyed by one
    /// caller at a time, as when a Retry comes while the sweep is at it: the next waits, and does nothing when the one before it
    /// succeeded.
    /// </summary>
    public async Task DestroyAsync(Sandbox sandbox, CancellationToken cancellationToken)
    {
        var oneAtATime = _destroying.GetOrAdd(sandbox.ResourceId, _ => new SemaphoreSlim(1, 1));
        await oneAtATime.WaitAsync(cancellationToken);
        try
        {
            await using var database = await databases.CreateDbContextAsync(cancellationToken);
            var recorded = database.Sandboxes.Where(record => record.ResourceId == sandbox.ResourceId);
            if (await recorded.AnyAsync(record => record.DestroyedAt != null, cancellationToken))
            {
                return;
            }
            await provider.DestroyAsync(sandbox, cancellationToken);
            var destroyedAt = time.GetUtcNow();
            await recorded.ExecuteUpdateAsync(record => record.SetProperty(r => r.DestroyedAt, destroyedAt), cancellationToken);
        }
        finally
        {
            oneAtATime.Release();
        }
    }

    /// <summary>
    /// Destroys a Sandbox that work is done with, also as serve stops, so that a stopped Overseer leaves no Sandbox running.
    /// Should that fail, it is logged rather than thrown: the Sandbox stays recorded, and the next start's sweep destroys it.
    /// </summary>
    public async Task DestroyOrLeaveAsync(Sandbox sandbox)
    {
        try
        {
            await DestroyAsync(sandbox, CancellationToken.None);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "The Sandbox {Sandbox} of the {OwnerKind} {OwnerId} could not be destroyed; the next start destroys it.",
                sandbox.ResourceId, sandbox.Owner.Kind, sandbox.Owner.Id);
        }
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
