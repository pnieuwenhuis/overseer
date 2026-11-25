using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Overseer.Persistence;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// The session logs: what every agent session on a Task said and which tools it called, every session of it, and the messages the
/// human sent its agent, kept in PostgreSQL as it happens, so that it can be read while the agent works, after the Task has ended
/// and after a restart. While a session is open, the human can send its agent a message (<see cref="SendMessageAsync"/>), which
/// waits until it is delivered as one of the session's turns; one still waiting as the session ends is never delivered. No secret a
/// session was given (<see cref="SandboxRegistry.SecretsGiven"/>) goes into a log, so none is shown either: <c>***</c> takes its
/// place.
/// </summary>
public sealed class SessionLogs(SandboxRegistry sandboxes, IDbContextFactory<OverseerDbContext> databases)
{
    /// <summary>What the agent of each open session is doing, by its Task. In memory: no session outlives serve.</summary>
    private readonly ConcurrentDictionary<TaskKey, SessionActivity> _activities = new();

    /// <summary>The log of each open session, by its Task. In memory: no session outlives serve.</summary>
    private readonly ConcurrentDictionary<TaskKey, SessionLog> _open = new();

    /// <summary>
    /// Raised, with the Task, whenever a session on it starts or ends or stops taking messages, or its log or its messages change,
    /// on the thread that changed it, once the change is committed. Handlers must not throw; read again with <see cref="OfAsync"/>
    /// and <see cref="ActivityOn"/>.
    /// </summary>
    public event Action<TaskKey>? Changed;

    /// <summary>
    /// What the agent of the session now working on the Task is doing: the last it reported in the turn going on, in one line.
    /// <c>null</c> when no turn is, or it has reported nothing in it yet.
    /// </summary>
    public SessionActivity? ActivityOn(TaskKey task) => _activities.GetValueOrDefault(task);

    /// <summary>The logs of the sessions on the Task, in the order the sessions started; empty when it had none.</summary>
    public async Task<IReadOnlyList<LoggedSession>> OfAsync(TaskKey task, CancellationToken cancellationToken)
    {
        // Looked at before and after the read, so that no message is taken for one never delivered while its session opens or ends.
        var takingBefore = TakingMessages(task);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var stored = await database.AgentSessions.AsNoTracking()
            .Where(session => session.IntentId == task.IntentId && session.TaskNumber == task.Number)
            .Include(session => session.Entries)
            .Include(session => session.Undelivered)
            .OrderBy(session => session.Number)
            .ToListAsync(cancellationToken);
        var takingAfter = TakingMessages(task);
        return [.. stored.Select(session => new LoggedSession(
            session.Number, session.Role,
            [.. session.Entries.OrderBy(entry => entry.Number).Select(entry => new LoggedEntry(entry.Kind, entry.Text))],
            [.. session.Undelivered.OrderBy(message => message.Number).Select(message => message.Text)],
            session.Number == takingBefore || session.Number == takingAfter))];
    }

    /// <summary>
    /// The Intent's Tasks with messages of the human's that were never delivered, each with how many: those still waiting as the
    /// session they were sent to ended.
    /// </summary>
    public async Task<IReadOnlyList<NeverDelivered>> NeverDeliveredOfAsync(Guid intentId, CancellationToken cancellationToken)
    {
        // Looked at before and after the read, as in OfAsync.
        var takingBefore = TakingMessagesIn(intentId);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var undelivered = await database.UndeliveredMessages
            .Where(message => message.IntentId == intentId)
            .Select(message => new { message.TaskNumber, message.SessionNumber })
            .ToListAsync(cancellationToken);
        var taking = takingBefore.Union(TakingMessagesIn(intentId)).ToHashSet();
        return [.. undelivered
            .Where(message => !taking.Contains(new SessionKey(new TaskKey(intentId, message.TaskNumber), message.SessionNumber)))
            .GroupBy(message => message.TaskNumber)
            .Select(task => new NeverDelivered(task.Key, task.Count()))];
    }

    /// <summary>
    /// Sends a message to the agent of a session on the Task. An agent takes one message per turn, and its turn isn't
    /// interrupted: the message waits, and is delivered as a turn of its own once the current one ends, after any sent before it,
    /// and before the Task's work is verified. A session takes messages until it ends, or its work has passed verification with
    /// none waiting.
    /// </summary>
    /// <param name="session">The number of the session the message is for, among the Task's.</param>
    /// <returns>Why the message was refused; <c>null</c> once it waits to be delivered.</returns>
    public async Task<string?> SendMessageAsync(TaskKey task, int session, string message, CancellationToken cancellationToken)
    {
        var written = StorableText.OfWritten(message);
        if (written.Length == 0)
        {
            return "A message needs text: write what the agent should know.";
        }
        return _open.TryGetValue(task, out var log) && log.Key.Number == session
            && await log.Inbox.TryTakeOnAsync(await sandboxes.WithoutSecretsAsync(written, cancellationToken), cancellationToken)
                ? null
                : "The agent session it was written to takes no more messages: it has ended, or its work has passed verification.";
    }

    /// <summary>
    /// Starts the log of a new agent session on the Task, which every turn of that session is then to be taken through
    /// (<see cref="SessionLog.TakeTurnAsync"/>), and which takes messages for it from now on. Dispose it as the session ends.
    /// </summary>
    public async Task<SessionLog> OpenAsync(TaskKey task, AgentRole role, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var last = await database.AgentSessions
            .Where(session => session.IntentId == task.IntentId && session.TaskNumber == task.Number)
            .MaxAsync(session => (int?)session.Number, cancellationToken);
        var number = (last ?? 0) + 1;
        database.AgentSessions.Add(new AgentSessionRecord
        {
            IntentId = task.IntentId, TaskNumber = task.Number, Number = number, Role = role, Entries = [], Undelivered = [],
        });
        await database.SaveChangesAsync(cancellationToken);
        var log = new SessionLog(
            sandboxes, databases, new SessionKey(task, number), activity => Report(task, activity), () => Changed?.Invoke(task),
            closed => _open.TryRemove(KeyValuePair.Create(task, closed)));
        _open[task] = log;
        Changed?.Invoke(task);
        return log;
    }

    /// <summary>The number of the session on the Task that takes messages now; <c>null</c> when none does.</summary>
    private int? TakingMessages(TaskKey task) =>
        _open.TryGetValue(task, out var log) && log.Inbox.TakesMessages ? log.Key.Number : null;

    /// <summary>The sessions on the Intent's Tasks that take messages now.</summary>
    private List<SessionKey> TakingMessagesIn(Guid intentId) =>
        [.. _open.Values.Where(log => log.Key.Task.IntentId == intentId && log.Inbox.TakesMessages).Select(log => log.Key)];

    private void Report(TaskKey task, SessionActivity? activity)
    {
        if (activity is null)
        {
            _activities.TryRemove(task, out _);
        }
        else
        {
            _activities[task] = activity;
        }
        Changed?.Invoke(task);
    }
}

/// <summary>A Task of an Intent: the <paramref name="Number"/>th of its Task list, from 1.</summary>
public readonly record struct TaskKey(Guid IntentId, int Number);

/// <summary>An agent session on a Task: the <paramref name="Number"/>th on it, from 1.</summary>
internal readonly record struct SessionKey(TaskKey Task, int Number);

/// <summary>How many messages of the human's to the agents of a Task were never delivered.</summary>
public sealed record NeverDelivered(int TaskNumber, int Messages);

/// <summary>What the agent of a session is doing, in one line.</summary>
/// <param name="Kind">Whether the line is of what the agent says, or of a tool it calls.</param>
public sealed record SessionActivity(SessionEntryKind Kind, string Line)
{
    /// <summary>
    /// What an entry says the agent is doing: of a message, the last line with a word or number in it, as it is the most recent; of a
    /// tool call, whose later lines are the rest of a long command, the first. <c>null</c> when it has no such line.
    /// </summary>
    internal static SessionActivity? Of(SessionEntryKind kind, string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Any(char.IsLetterOrDigit));
        var line = kind == SessionEntryKind.Message ? lines.LastOrDefault() : lines.FirstOrDefault();
        return line is null ? null : new SessionActivity(kind, line);
    }
}

/// <summary>The log of one agent session on a Task: the <paramref name="Number"/>th on it, from 1.</summary>
/// <param name="Entries">
/// What the agent said, the tools it called, the messages delivered to it, and the questions it asked with the answers, in order.
/// </param>
/// <param name="Undelivered">
/// The human's messages to its agent that aren't delivered, in the order they were sent: waiting while the session takes messages,
/// and otherwise never delivered.
/// </param>
/// <param name="TakesMessages">Whether its agent can be sent a message now.</param>
public sealed record LoggedSession(
    int Number, AgentRole Role, IReadOnlyList<LoggedEntry> Entries, IReadOnlyList<string> Undelivered, bool TakesMessages);

/// <summary>
/// One thing in a session's log: a whole message of the agent's, a tool it called, a message delivered to it, a question it asked,
/// or the answer it was given.
/// </summary>
/// <param name="Text">The agent's Markdown or question, the tool and what it was called on, or what the human wrote.</param>
public sealed record LoggedEntry(SessionEntryKind Kind, string Text);

public enum SessionEntryKind
{
    /// <summary>What the agent said.</summary>
    Message,

    /// <summary>A tool the agent called.</summary>
    ToolCall,

    /// <summary>A message of the human's, delivered to the agent.</summary>
    UserMessage,

    /// <summary>A question the agent ended a turn with.</summary>
    Question,

    /// <summary>The human's answer to the agent's question.</summary>
    Answer,
}

/// <summary>An agent session on a Task, as PostgreSQL holds it.</summary>
public sealed class AgentSessionRecord
{
    public required Guid IntentId { get; init; }
    public required int TaskNumber { get; init; }
    public required int Number { get; init; }
    public required AgentRole Role { get; init; }
    public required List<AgentSessionEntryRecord> Entries { get; init; }
    public required List<UndeliveredMessageRecord> Undelivered { get; init; }
}

/// <summary>An entry of an agent session's log, as PostgreSQL holds it.</summary>
public sealed class AgentSessionEntryRecord
{
    public required Guid IntentId { get; init; }
    public required int TaskNumber { get; init; }
    public required int SessionNumber { get; init; }
    public required int Number { get; init; }
    public required SessionEntryKind Kind { get; init; }
    public required string Text { get; set; }
}

/// <summary>
/// A message of the human's to a session's agent that isn't delivered, as PostgreSQL holds it. As it is delivered, it becomes an
/// entry of the session's log instead.
/// </summary>
public sealed class UndeliveredMessageRecord
{
    public required Guid IntentId { get; init; }
    public required int TaskNumber { get; init; }
    public required int SessionNumber { get; init; }

    /// <summary>Which of the messages the session took on it is, from 1.</summary>
    public required int Number { get; init; }

    public required string Text { get; init; }
}
