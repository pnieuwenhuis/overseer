using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Overseer.Persistence;
using Overseer.Sandboxes;
using Overseer.Secrets;

namespace Overseer.Implementation;

/// <summary>
/// The session logs: what every agent session on a Task said and which tools it called, every session of it, kept in PostgreSQL as it
/// happens, so that it can be read while the agent works, after the Task has ended and after a restart. No secret a session was given
/// (<see cref="SandboxRegistry.SecretsGiven"/>) goes into a log, so none is shown either: <c>***</c> takes its place.
/// </summary>
public sealed class SessionLogs(SandboxRegistry sandboxes, IDbContextFactory<OverseerDbContext> databases)
{
    /// <summary>What the agent of each open session did last, by its Intent and Task. In memory: no session outlives serve.</summary>
    private readonly ConcurrentDictionary<(Guid IntentId, int TaskNumber), SessionActivity> _activities = new();

    /// <summary>
    /// Raised, with the Intent and the number of its Task, whenever a session on the Task starts or ends, or its log grows, on the
    /// thread that changed it, once the change is committed. Handlers must not throw; read again with <see cref="OfAsync"/> and
    /// <see cref="ActivityOn"/>.
    /// </summary>
    public event Action<Guid, int>? Changed;

    /// <summary>
    /// What the agent of the session now working on the Intent's Task is doing: the last it reported, in one line. <c>null</c> when
    /// no session is, or it has reported nothing yet.
    /// </summary>
    public SessionActivity? ActivityOn(Guid intentId, int taskNumber) => _activities.GetValueOrDefault((intentId, taskNumber));

    /// <summary>The logs of the sessions on the Intent's Task, in the order the sessions started; empty when it had none.</summary>
    public async Task<IReadOnlyList<LoggedSession>> OfAsync(Guid intentId, int taskNumber, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var stored = await database.AgentSessions.AsNoTracking()
            .Where(session => session.IntentId == intentId && session.TaskNumber == taskNumber)
            .Include(session => session.Entries)
            .OrderBy(session => session.Number)
            .ToListAsync(cancellationToken);
        return [.. stored.Select(session => new LoggedSession(
            session.Number, session.Role,
            [.. session.Entries.OrderBy(entry => entry.Number).Select(entry => new LoggedEntry(entry.Kind, entry.Text))]))];
    }

    /// <summary>
    /// Starts the log of a new agent session on the Intent's Task, which every turn of that session is then to be taken through
    /// (<see cref="SessionLog.TakeTurnAsync"/>). Dispose it as the session ends.
    /// </summary>
    public async Task<SessionLog> OpenAsync(Guid intentId, int taskNumber, AgentRole role, CancellationToken cancellationToken)
    {
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var last = await database.AgentSessions
            .Where(session => session.IntentId == intentId && session.TaskNumber == taskNumber)
            .MaxAsync(session => (int?)session.Number, cancellationToken);
        var number = (last ?? 0) + 1;
        database.AgentSessions.Add(new AgentSessionRecord
        {
            IntentId = intentId, TaskNumber = taskNumber, Number = number, Role = role, Entries = [],
        });
        await database.SaveChangesAsync(cancellationToken);
        Changed?.Invoke(intentId, taskNumber);
        return new SessionLog(
            sandboxes, databases, intentId, taskNumber, number, activity => Report(intentId, taskNumber, activity));
    }

    private void Report(Guid intentId, int taskNumber, SessionActivity? activity)
    {
        if (activity is null)
        {
            _activities.TryRemove((intentId, taskNumber), out _);
        }
        else
        {
            _activities[(intentId, taskNumber)] = activity;
        }
        Changed?.Invoke(intentId, taskNumber);
    }
}

/// <summary>The log of one agent session on a Task, as it is written. Disposing it says that the session has ended.</summary>
public sealed class SessionLog : IDisposable
{
    /// <summary>The least time between two writes while a turn goes on, so that a message streamed word by word isn't written as often.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(100);

    private readonly SandboxRegistry _sandboxes;
    private readonly IDbContextFactory<OverseerDbContext> _databases;
    private readonly Guid _intentId;
    private readonly int _taskNumber;
    private readonly int _number;

    /// <summary>Told what the session's agent is doing whenever the log grows, and <c>null</c> once the session has ended.</summary>
    private readonly Action<SessionActivity?> _report;

    /// <summary>The entries that may differ from how they are stored.</summary>
    private readonly HashSet<Entry> _unsaved = [];

    /// <summary>The turn's tool calls, by ID.</summary>
    private readonly Dictionary<string, Entry> _calls = [];

    /// <summary>The message the agent is saying; <c>null</c> when it isn't saying one.</summary>
    private Entry? _message;

    /// <summary>The entry the agent reported last.</summary>
    private Entry? _latest;

    private SessionActivity? _activity;

    private int _entries;

    internal SessionLog(
        SandboxRegistry sandboxes, IDbContextFactory<OverseerDbContext> databases, Guid intentId, int taskNumber, int number,
        Action<SessionActivity?> report)
    {
        _sandboxes = sandboxes;
        _databases = databases;
        _intentId = intentId;
        _taskNumber = taskNumber;
        _number = number;
        _report = report;
    }

    public void Dispose() => _report(null);

    /// <summary>
    /// Sends the session's agent a message and writes what it reports to the log, as it happens, until the turn it starts ends.
    /// </summary>
    /// <param name="session">The session this is the log of.</param>
    /// <exception cref="AgentSessionFailedException">The turn broke off; what it reported until then is in the log.</exception>
    public async Task<AgentOutcome> TakeTurnAsync(IAgentSession session, string message, CancellationToken cancellationToken)
    {
        var turn = await session.SendAsync(message, cancellationToken);
        // Read apart from the writing, so that what arrives during a write is written together with the next one.
        var arrived = Channel.CreateUnbounded<AgentUpdate>();
        var reading = ReadAsync(turn.Updates, arrived.Writer, cancellationToken);
        _calls.Clear();
        var ended = false;
        try
        {
            while (await arrived.Reader.WaitToReadAsync(cancellationToken))
            {
                Take(arrived.Reader);
                await SaveAsync();
                await Task.WhenAny(reading, Task.Delay(Pause, cancellationToken));
            }
            var outcome = await turn.Outcome.WaitAsync(cancellationToken);
            ended = true;
            return outcome;
        }
        finally
        {
            // Also what a turn reported before it broke off, or serve stopped.
            Take(arrived.Reader);
            if (ended)
            {
                EndMessage();
            }
            await SaveAsync();
            // The next turn starts a message of its own.
            _message = null;
        }
    }

    private static async Task ReadAsync(
        IAsyncEnumerable<AgentUpdate> updates, ChannelWriter<AgentUpdate> arrived, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var update in updates.WithCancellation(cancellationToken))
            {
                arrived.TryWrite(update);
            }
            arrived.Complete();
        }
        catch (Exception exception)
        {
            // The reader of the channel gets it.
            arrived.Complete(exception);
        }
    }

    /// <summary>Adds what has arrived to the log's entries.</summary>
    private void Take(ChannelReader<AgentUpdate> arrived)
    {
        while (arrived.TryRead(out var update))
        {
            switch (update)
            {
                case AgentUpdate.Message said:
                    _message ??= new Entry(++_entries, SessionEntryKind.Message);
                    _message.Reported.Append(said.Text);
                    _unsaved.Add(_message);
                    _latest = _message;
                    break;
                case AgentUpdate.ToolCall call:
                    if (!_calls.TryGetValue(call.Id, out var entry))
                    {
                        EndMessage();
                        _calls[call.Id] = entry = new Entry(++_entries, SessionEntryKind.ToolCall) { Whole = true };
                    }
                    entry.Reported.Clear().Append(call.Title);
                    _unsaved.Add(entry);
                    _latest = entry;
                    break;
            }
        }
    }

    /// <summary>Ends the message the agent is saying, if it is saying one: nothing more is added to it.</summary>
    private void EndMessage()
    {
        if (_message is not null)
        {
            _message.Whole = true;
            _unsaved.Add(_message);
            _message = null;
        }
    }

    // Not cancelled with the turn: a write broken off could leave what is stored unknown.
    private async Task SaveAsync()
    {
        var secrets = _sandboxes.SecretsGiven;
        var changed = _unsaved
            .Select(entry => (Entry: entry, Text: TextToStore(entry, secrets)))
            .Where(change => change.Text.Length > 0 && change.Text != change.Entry.Stored)
            .ToList();
        if (changed.Count > 0)
        {
            await using var database = await _databases.CreateDbContextAsync(CancellationToken.None);
            await using var transaction = await database.Database.BeginTransactionAsync(CancellationToken.None);
            foreach (var (entry, text) in changed)
            {
                if (entry.Stored is null)
                {
                    database.AgentSessionEntries.Add(new AgentSessionEntryRecord
                    {
                        IntentId = _intentId, TaskNumber = _taskNumber, SessionNumber = _number, Number = entry.Number,
                        Kind = entry.Kind, Text = text,
                    });
                }
                else
                {
                    await database.AgentSessionEntries
                        .Where(stored => stored.IntentId == _intentId && stored.TaskNumber == _taskNumber
                            && stored.SessionNumber == _number && stored.Number == entry.Number)
                        .ExecuteUpdateAsync(stored => stored.SetProperty(record => record.Text, text), CancellationToken.None);
                }
            }
            await database.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
            foreach (var (entry, text) in changed)
            {
                entry.Stored = text;
            }
            // From what is stored, which holds no secret. Until the latest entry says something, the activity before it stands.
            if (_latest?.Stored is { } latest && SessionActivity.Of(_latest.Kind, latest) is { } activity)
            {
                _activity = activity;
            }
            _report(_activity);
        }
        _unsaved.Clear();
    }

    /// <summary>The entry's text as it may be stored now: without secrets, and for a message still arriving, without one begun.</summary>
    private static string TextToStore(Entry entry, IReadOnlyCollection<string> secrets)
    {
        // Made storable first, as taking characters out afterwards could put a secret together.
        var reported = StorableText.Of(entry.Reported.ToString());
        return (entry.Whole ? Redaction.Without(reported, secrets) : Redaction.WithoutSoFar(reported, secrets)).Trim();
    }

    private sealed class Entry(int number, SessionEntryKind kind)
    {
        /// <summary>Which of the session's entries it is, in the order they began, from 1.</summary>
        public int Number { get; } = number;

        public SessionEntryKind Kind { get; } = kind;

        /// <summary>A message's fragments so far, or a tool call's title.</summary>
        public StringBuilder Reported { get; } = new();

        /// <summary>Whether nothing more of its text is still to come: a message that has ended, or a tool call's title.</summary>
        public bool Whole { get; set; }

        /// <summary>Its text as PostgreSQL holds it; <c>null</c> until it is stored.</summary>
        public string? Stored { get; set; }
    }
}

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
/// <param name="Entries">What the agent said and the tools it called, in order.</param>
public sealed record LoggedSession(int Number, AgentRole Role, IReadOnlyList<LoggedEntry> Entries);

/// <summary>One thing an agent session reported: a whole message of the agent's, or a tool it called.</summary>
/// <param name="Text">The message's Markdown, or the tool and what it was called on.</param>
public sealed record LoggedEntry(SessionEntryKind Kind, string Text);

public enum SessionEntryKind
{
    /// <summary>What the agent said.</summary>
    Message,

    /// <summary>A tool the agent called.</summary>
    ToolCall,
}

/// <summary>An agent session on a Task, as PostgreSQL holds it.</summary>
public sealed class AgentSessionRecord
{
    public required Guid IntentId { get; init; }
    public required int TaskNumber { get; init; }
    public required int Number { get; init; }
    public required AgentRole Role { get; init; }
    public required List<AgentSessionEntryRecord> Entries { get; init; }
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
