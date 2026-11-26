using System.Collections.Concurrent;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Overseer.GitHub;
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
    /// <summary>How long a Sandbox may take to take a GitHub token before it is given up on.</summary>
    private static readonly TimeSpan GitHubTokenLimit = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Every GitHub token a Sandbox may have been given since serve started, which what comes out of it can hold, also once the
    /// token is replaced. In memory only: no Sandbox outlives a restart.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _gitHubTokens = new();

    /// <summary>
    /// Every model provider token an agent session was started with since serve started, and every one found stored when a text had
    /// its secrets taken out (<see cref="WithoutSecretsAsync"/>), also once it is replaced.
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _providerTokens = new();

    /// <summary>
    /// The secrets that what comes out of a Sandbox or its agents can hold: every GitHub token a Sandbox may have been given and every
    /// model provider token an agent session was started with since serve started, also those replaced or removed since; and the
    /// tokens found stored when a text had its secrets taken out (<see cref="WithoutSecretsAsync"/>).
    /// </summary>
    public IReadOnlyCollection<string> SecretsGiven => [.. _gitHubTokens.Keys, .. _providerTokens.Keys];

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
        // The one the provider clones with.
        await RememberedGitHubTokenAsync(cancellationToken);
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
        var environment = await RememberedProviderTokensAsync(cancellationToken);
        return await provider.StartAgentSessionAsync(sandbox, role, harness, environment, cancellationToken);
    }

    /// <summary>The model provider tokens stored now, by environment variable, each remembered as one to take out.</summary>
    private async Task<Dictionary<string, string>> RememberedProviderTokensAsync(CancellationToken cancellationToken)
    {
        var environment = new Dictionary<string, string>();
        foreach (var token in await providerTokens.ListAsync(cancellationToken))
        {
            // Null when the token was removed since it was listed.
            if (await secrets.ReadAsync(token.SecretName, cancellationToken) is { } value)
            {
                environment[token.EnvironmentVariable] = value;
                _providerTokens.TryAdd(value, true);
            }
        }
        return environment;
    }

    /// <summary>
    /// <paramref name="text"/> as it may be stored and shown, for a text that comes whole, such as a question of an agent's or what
    /// the human wrote to one: storable (<see cref="StorableText.Of"/>), and with <c>***</c> in place of every secret it holds, of
    /// <see cref="SecretsGiven"/> and of the tokens stored now, which may not have been used yet, as one saved a moment ago hasn't.
    /// A secret never goes into PostgreSQL.
    /// </summary>
    public async Task<string> WithoutSecretsAsync(string text, CancellationToken cancellationToken)
    {
        await RememberedGitHubTokenAsync(cancellationToken);
        await RememberedProviderTokensAsync(cancellationToken);
        // Made storable first, as taking characters out afterwards could put a secret together.
        return Redaction.Without(StorableText.Of(text), SecretsGiven);
    }

    /// <summary>
    /// Gives a Sandbox that may push the GitHub token Overseer uses now, in place of the one it holds: to be done before it is
    /// used again, as the token may have been replaced since.
    /// </summary>
    /// <exception cref="InvalidOperationException">No GitHub token is set, or as for the provider.</exception>
    /// <exception cref="TimeoutException">The Sandbox didn't take it in time.</exception>
    /// <inheritdoc cref="ISandboxProvider.SetGitHubTokenAsync"/>
    public async Task RefreshGitHubTokenAsync(Sandbox sandbox, CancellationToken cancellationToken)
    {
        var token = await RememberedGitHubTokenAsync(cancellationToken)
            ?? throw new InvalidOperationException("No GitHub token is set: set one in Settings.");
        using var timedOut = new CancellationTokenSource(GitHubTokenLimit, time);
        using var giving = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timedOut.Token);
        try
        {
            await provider.SetGitHubTokenAsync(sandbox, token, giving.Token).WaitAsync(giving.Token);
        }
        catch (Exception) when (timedOut.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The Sandbox didn't take it within {GitHubTokenLimit.TotalMinutes:0} minutes.");
        }
    }

    /// <summary>
    /// <paramref name="text"/> without any GitHub token that what comes out of a Sandbox can hold: the one Overseer uses now, and
    /// those Sandboxes were given before. A secret never goes into PostgreSQL.
    /// </summary>
    public async Task<string> WithoutGitHubTokensAsync(string text, CancellationToken cancellationToken)
    {
        if (text.Length == 0)
        {
            return text;
        }
        await RememberedGitHubTokenAsync(cancellationToken);
        return Redaction.Without(text, _gitHubTokens.Keys);
    }

    /// <summary>The GitHub token Overseer uses now, remembered as one to take out; <c>null</c> when none is set.</summary>
    private async Task<string?> RememberedGitHubTokenAsync(CancellationToken cancellationToken)
    {
        var token = await secrets.ReadAsync(GitHubToken.SecretName, cancellationToken);
        if (token is { Length: > 0 })
        {
            _gitHubTokens.TryAdd(token, true);
        }
        return token;
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

    /// <inheritdoc cref="ISandboxProvider.TakeBinaryFileAsync"/>
    public Task<byte[]?> TakeBinaryFileAsync(Sandbox sandbox, string path, int maxBytes, CancellationToken cancellationToken) =>
        provider.TakeBinaryFileAsync(sandbox, path, maxBytes, cancellationToken);

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
