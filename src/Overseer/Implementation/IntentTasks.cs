using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Overseer.GitHub;
using Overseer.Intents;
using Overseer.Persistence;
using Overseer.Sandboxes;
using Overseer.Specs;

namespace Overseer.Implementation;

/// <summary>
/// Each Intent's own Task list, which the Task Runner carries out, kept in PostgreSQL. It starts as the Approved Spec's Tasks and
/// is kept apart from them, as the Approved Spec stays locked: the human can change a Failed Task of this list.
/// </summary>
public sealed class IntentTasks(IDbContextFactory<OverseerDbContext> databases, SpecStore specs)
{
    /// <summary>The Intent's Tasks, in the order they are carried out; empty when it has none yet.</summary>
    public async Task<IReadOnlyList<IntentTask>> OfAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var stored = await database.IntentTasks.AsNoTracking()
            .Where(task => task.IntentId == intentId)
            .OrderBy(task => task.Number)
            .ToListAsync(cancellationToken);
        return [.. stored.Select(Read)];
    }

    /// <summary>
    /// The first of the Intent's Tasks that isn't Done, the one the Squad is at: no Task after it runs before it is Done.
    /// <c>null</c> once all are.
    /// </summary>
    public async Task<IntentTask?> NextAsync(Guid intentId, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var stored = await database.IntentTasks.AsNoTracking()
            .Where(task => task.IntentId == intentId && task.State != IntentTaskState.Done)
            .OrderBy(task => task.Number)
            .FirstOrDefaultAsync(cancellationToken);
        return stored is null ? null : Read(stored);
    }

    /// <summary>Records that the Intent's Task with this number is now in <paramref name="state"/>.</summary>
    public async Task EnterStateAsync(Guid intentId, int number, IntentTaskState state, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await database.IntentTasks
            .Where(task => task.IntentId == intentId && task.Number == number)
            .ExecuteUpdateAsync(task => task.SetProperty(record => record.State, state), cancellationToken);
    }

    /// <summary>
    /// Records that the Task's work passed verification and is committed, so that what is left is pushing the Intent branch of
    /// these repositories, the ones it changed.
    /// </summary>
    public async Task AwaitPushAsync(Guid intentId, int number, IReadOnlyList<RepositoryName> changed, CancellationToken cancellationToken)
    {
        var repositories = changed.Select(repository => repository.ToString()).ToList();
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await database.IntentTasks
            .Where(task => task.IntentId == intentId && task.Number == number)
            .ExecuteUpdateAsync(task => task.SetProperty(record => record.AwaitingPush, repositories), cancellationToken);
    }

    /// <summary>Records that the Task is Done: its work is verified and pushed.</summary>
    public async Task FinishAsync(Guid intentId, int number, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await database.IntentTasks
            .Where(task => task.IntentId == intentId && task.Number == number)
            .ExecuteUpdateAsync(
                task => task
                    .SetProperty(record => record.State, IntentTaskState.Done)
                    .SetProperty(record => record.AwaitingPush, (List<string>?)null),
                cancellationToken);
    }

    /// <summary>
    /// Forgets the verified work of the Intent's Tasks that is yet to be pushed, because the Sandbox that held it is gone: the Task
    /// it was of is carried out again.
    /// </summary>
    public Task ForgetUnpushedAsync(Guid intentId, CancellationToken cancellationToken) =>
        ForgetUnpushedAsync(task => task.IntentId == intentId, cancellationToken);

    /// <summary>As <see cref="ForgetUnpushedAsync(Guid, CancellationToken)"/>, for every Intent, as when no Sandbox is left.</summary>
    public Task ForgetAllUnpushedAsync(CancellationToken cancellationToken) => ForgetUnpushedAsync(task => true, cancellationToken);

    private async Task ForgetUnpushedAsync(Expression<Func<IntentTaskRecord, bool>> whose, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        await database.IntentTasks
            .Where(whose)
            .Where(task => task.AwaitingPush != null)
            .ExecuteUpdateAsync(task => task.SetProperty(record => record.AwaitingPush, (List<string>?)null), cancellationToken);
    }

    /// <summary>
    /// Gives the Intent its Task list, the Approved Spec's Tasks in their order, all Pending. Does nothing when it has one already,
    /// so that no Task that is done comes back, or when it has no Approved Spec.
    /// </summary>
    public async Task CreateFromApprovedSpecAsync(Intent intent, CancellationToken cancellationToken)
    {
        if (intent.ApprovedSpecVersion is not { } approved
            || await specs.VersionOfAsync(intent.Id, approved, cancellationToken) is not { } version)
        {
            return;
        }
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        if (await database.IntentTasks.AnyAsync(task => task.IntentId == intent.Id, cancellationToken))
        {
            return;
        }
        database.IntentTasks.AddRange(version.Spec.Tasks.Select(task => new IntentTaskRecord
        {
            IntentId = intent.Id,
            Number = task.Number,
            Title = task.Title,
            Description = task.Description,
            Role = task.Role,
            Repositories = [.. task.Repositories.Select(repository => repository.ToString())],
            State = IntentTaskState.Pending,
        }));
        await database.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Puts the Intent's Task that is Running, if one is, back to Pending in <paramref name="database"/>'s transaction: what ran it
    /// has ended without finishing it.
    /// </summary>
    internal static Task StopRunningAsync(OverseerDbContext database, Guid intentId, CancellationToken cancellationToken) =>
        database.IntentTasks
            .Where(task => task.IntentId == intentId && task.State == IntentTaskState.Running)
            .ExecuteUpdateAsync(task => task.SetProperty(record => record.State, IntentTaskState.Pending), cancellationToken);

    /// <summary>
    /// Puts the Intent's Failed Task with this number back to Pending in <paramref name="database"/>'s transaction, to be carried
    /// out again, changed as <paramref name="edit"/> has it when there is one. The Approved Spec's Task stays as it is.
    /// </summary>
    internal static async Task ReopenAsync(
        OverseerDbContext database, Guid intentId, int number, SpecTask? edit, CancellationToken cancellationToken)
    {
        var task = await database.IntentTasks.SingleAsync(task => task.IntentId == intentId && task.Number == number, cancellationToken);
        task.State = IntentTaskState.Pending;
        if (edit is not null)
        {
            task.Title = edit.Title;
            task.Description = edit.Description;
            task.Role = edit.Role;
            task.Repositories = [.. edit.Repositories.Select(repository => repository.ToString())];
        }
        await database.SaveChangesAsync(cancellationToken);
    }

    private static IntentTask Read(IntentTaskRecord task) => new(
        task.Number, task.Title, task.Description, task.Role, [.. task.Repositories.Select(RepositoryName.Parse)], task.State,
        task.AwaitingPush?.Select(RepositoryName.Parse).ToList());
}

/// <summary>A Task of an Intent's own Task list, numbered in the order the Tasks are carried out, from 1.</summary>
/// <param name="Description">Markdown.</param>
/// <param name="Role">Designer or Developer.</param>
/// <param name="Repositories">The Repositories in scope it touches.</param>
/// <param name="AwaitingPush">
/// Once its work has passed verification and until it is Done: the repositories it changed, whose Intent branch is still to be
/// pushed. <c>null</c> otherwise.
/// </param>
public sealed record IntentTask(
    int Number, string Title, string Description, AgentRole Role, IReadOnlyList<RepositoryName> Repositories, IntentTaskState State,
    IReadOnlyList<RepositoryName>? AwaitingPush);

public enum IntentTaskState
{
    /// <summary>Still to be carried out.</summary>
    Pending,

    /// <summary>An agent is carrying it out.</summary>
    Running,

    /// <summary>Its work passed verification and is pushed to the Intent branch.</summary>
    Done,

    /// <summary>
    /// A Failed Task: its work didn't pass verification in the allowed number of attempts. No Task after it runs while it is one.
    /// </summary>
    Failed,
}

/// <summary>A Task of an Intent, as PostgreSQL holds it.</summary>
public sealed class IntentTaskRecord
{
    public required Guid IntentId { get; init; }
    public required int Number { get; init; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required AgentRole Role { get; set; }

    /// <summary><c>owner/name</c> of each Repository in scope it touches.</summary>
    public required List<string> Repositories { get; set; }

    public required IntentTaskState State { get; set; }

    /// <summary>
    /// <c>owner/name</c> of each repository whose verified work is yet to be pushed; <c>null</c> unless the work is verified and
    /// the Task not yet Done.
    /// </summary>
    public List<string>? AwaitingPush { get; set; }
}
