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
    /// <summary>
    /// Creates a Sandbox holding the Repositories in scope, tagged with its owner, on the machine the Settings say now.
    /// Completes once the Sandbox is ready for use.
    /// </summary>
    /// <inheritdoc cref="ISandboxProvider.CreateAsync"/>
    public async Task<Sandbox> CreateAsync(SandboxOwner owner, IReadOnlyCollection<string> repositoriesInScope, CancellationToken cancellationToken)
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
            await provider.CreateAsync(sandbox, machine, repositoriesInScope, cancellationToken);
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
    /// The environment variable that gives an agent OpenCode's <c>websearch</c> tool, which OpenCode offers only with it set,
    /// unless the model comes through OpenCode's own provider (https://opencode.ai/docs/tools/#websearch). It searches through
    /// Exa's hosted service, without a key. <c>webfetch</c>, reading a page, needs nothing.
    /// </summary>
    public const string WebSearchVariable = "OPENCODE_ENABLE_EXA";

    /// <summary>
    /// Starts an agent in the given role, with the agent harness configuration the Settings hold now, every model provider
    /// token stored now in its environment variable, and web search (<see cref="WebSearchVariable"/>).
    /// </summary>
    /// <inheritdoc cref="ISandboxProvider.StartAgentSessionAsync"/>
    public async Task<IAgentSession> StartAgentSessionAsync(Sandbox sandbox, AgentRole role, CancellationToken cancellationToken)
    {
        var harness = await settings.GetAgentHarnessAsync(cancellationToken);
        var environment = new Dictionary<string, string> { [WebSearchVariable] = "1" };
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

    /// <summary>The Sandboxes of an owner of <paramref name="ownerKind"/> that are recorded and not recorded destroyed.</summary>
    public async Task<IReadOnlyList<Sandbox>> ListRecordedAsync(SandboxOwnerKind ownerKind, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var records = await database.Sandboxes
            .Where(record => record.OwnerKind == ownerKind && record.DestroyedAt == null)
            .ToListAsync(cancellationToken);
        return [.. records.Select(record => new Sandbox(record.ResourceId, new SandboxOwner(record.OwnerKind, record.OwnerId)))];
    }

    /// <summary>
    /// Destroys the Sandbox and records when; the record itself is kept. When destroying fails, the Sandbox stays recorded
    /// undestroyed, so that the next start's sweep (<see cref="ListRecordedAsync"/>) destroys it.
    /// </summary>
    public async Task DestroyAsync(Sandbox sandbox, CancellationToken cancellationToken)
    {
        await provider.DestroyAsync(sandbox, cancellationToken);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var destroyedAt = time.GetUtcNow();
        await database.Sandboxes
            .Where(record => record.ResourceId == sandbox.ResourceId && record.DestroyedAt == null)
            .ExecuteUpdateAsync(record => record.SetProperty(r => r.DestroyedAt, destroyedAt), cancellationToken);
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
