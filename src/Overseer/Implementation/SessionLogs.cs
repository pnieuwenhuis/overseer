using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Overseer.Persistence;
using Overseer.Sandboxes;
using Overseer.Secrets;

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
    /// <summary>What the agent of each open session did last, by its Intent and Task. In memory: no session outlives serve.</summary>
    private readonly ConcurrentDictionary<(Guid IntentId, int TaskNumber), SessionActivity> _activities = new();

    /// <summary>The log of each open session, by its Intent and Task. In memory: no session outlives serve.</summary>
    private readonly ConcurrentDictionary<(Guid IntentId, int TaskNumber), SessionLog> _open = new();

    /// <summary>
    /// Raised, with the Intent and the number of its Task, whenever a session on the Task starts or ends or stops taking messages,
    /// or its log or its messages change, on the thread that changed it, once the change is committed. Handlers must not throw; read
    /// again with <see cref="OfAsync"/> and <see cref="ActivityOn"/>.
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
        // Looked at before and after the read, so that no message is taken for one never delivered while its session opens or ends.
        var takingBefore = TakingMessages(intentId, taskNumber);
        await using var database = await databases.CreateDbContextAsync(cancellationToken);
        var stored = await database.AgentSessions.AsNoTracking()
            .Where(session => session.IntentId == intentId && session.TaskNumber == taskNumber)
            .Include(session => session.Entries)
            .Include(session => session.Undelivered)
            .OrderBy(session => session.Number)
            .ToListAsync(cancellationToken);
        var takingAfter = TakingMessages(intentId, taskNumber);
        return [.. stored.Select(session => new LoggedSession(
            session.Number, session.Role,
            [.. session.Entries.OrderBy(entry => entry.Number).Select(entry => new LoggedEntry(entry.Kind, entry.Text))],
            [.. session.Undelivered.OrderBy(message => message.Number).Select(message => message.Text)],
            session.Number == takingBefore || session.Number == takingAfter))];
    }

    /// <summary>
    /// Sends a message to the agent of a session on the Intent's Task. An agent takes one message per turn, and its turn isn't
    /// interrupted: the message waits, and is delivered as a turn of its own once the current one ends, after any sent before it,
    /// and before the Task's work is verified. A session takes messages until it ends, or its work has passed verification with
    /// none waiting.
    /// </summary>
    /// <param name="session">The number of the session the message is for, among the Task's.</param>
    /// <returns>Why the message was refused; <c>null</c> once it waits to be delivered.</returns>
    public async Task<string?> SendMessageAsync(
        Guid intentId, int taskNumber, int session, string message, CancellationToken cancellationToken)
    {
        var text = StorableText.Of(message).Trim().ReplaceLineEndings("\n");
        if (text.Length == 0)
        {
            return "A message needs text: write what the agent should know.";
        }
        return _open.TryGetValue((intentId, taskNumber), out var log) && log.Number == session
            && await log.TryTakeOnAsync(text, cancellationToken)
                ? null
                : "The agent session it was written to takes no more messages: it has ended, or its work has passed verification.";
    }

    /// <summary>
    /// Starts the log of a new agent session on the Intent's Task, which every turn of that session is then to be taken through
    /// (<see cref="SessionLog.TakeTurnAsync"/>), and which takes messages for it from now on. Dispose it as the session ends.
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
            IntentId = intentId, TaskNumber = taskNumber, Number = number, Role = role, Entries = [], Undelivered = [],
        });
        await database.SaveChangesAsync(cancellationToken);
        var log = new SessionLog(
            sandboxes, databases, intentId, taskNumber, number, activity => Report(intentId, taskNumber, activity),
            () => Changed?.Invoke(intentId, taskNumber),
            closed => _open.TryRemove(KeyValuePair.Create((intentId, taskNumber), closed)));
        _open[(intentId, taskNumber)] = log;
        Changed?.Invoke(intentId, taskNumber);
        return log;
    }

    /// <summary>The number of the session on the Intent's Task that takes messages now; <c>null</c> when none does.</summary>
    private int? TakingMessages(Guid intentId, int taskNumber) =>
        _open.TryGetValue((intentId, taskNumber), out var log) && log.TakesMessages ? log.Number : null;

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

/// <summary>
/// The log of one agent session on a Task, as it is written, with the messages the human sends its agent. Disposing it says that the
/// session has ended: a message still waiting then is never delivered.
/// </summary>
public sealed class SessionLog : IAsyncDisposable
{
    /// <summary>The least time between two writes while a turn goes on, so that a message streamed word by word isn't written as often.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(100);

    private readonly SandboxRegistry _sandboxes;
    private readonly IDbContextFactory<OverseerDbContext> _databases;
    private readonly Guid _intentId;
    private readonly int _taskNumber;

    /// <summary>Told what the session's agent is doing whenever the log grows, and <c>null</c> once the session has ended.</summary>
    private readonly Action<SessionActivity?> _report;

    /// <summary>Told whenever the session's messages change, or it stops taking them.</summary>
    private readonly Action _messagesChanged;

    private readonly Action<SessionLog> _closed;

    /// <summary>
    /// Held while a message is taken on, and while the log stops taking them, so that each one taken on is seen by whoever asks for
    /// the next one afterwards.
    /// </summary>
    private readonly SemaphoreSlim _inbox = new(1, 1);

    /// <summary>The messages waiting to be delivered, in the order they were sent.</summary>
    private readonly ConcurrentQueue<SentMessage> _waiting = new();

    /// <summary>How many messages have been taken on.</summary>
    private int _messages;

    private volatile bool _takesMessages = true;

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
        Action<SessionActivity?> report, Action messagesChanged, Action<SessionLog> closed)
    {
        _sandboxes = sandboxes;
        _databases = databases;
        _intentId = intentId;
        _taskNumber = taskNumber;
        Number = number;
        _report = report;
        _messagesChanged = messagesChanged;
        _closed = closed;
    }

    /// <summary>Which of the Task's sessions this is the log of, from 1.</summary>
    internal int Number { get; }

    /// <summary>Whether a message sent now would be taken on.</summary>
    internal bool TakesMessages => _takesMessages;

    public async ValueTask DisposeAsync()
    {
        await _inbox.WaitAsync();
        _takesMessages = false;
        _inbox.Release();
        _closed(this);
        _report(null);
    }

    /// <summary>Takes on a message for the session's agent, unless the log takes no more.</summary>
    /// <param name="text">The message, which may be stored once secrets are taken out of it.</param>
    internal async Task<bool> TryTakeOnAsync(string text, CancellationToken cancellationToken)
    {
        await _inbox.WaitAsync(cancellationToken);
        try
        {
            if (!_takesMessages)
            {
                return false;
            }
            var message = new SentMessage(_messages + 1, Redaction.Without(text, _sandboxes.SecretsGiven));
            await using var database = await _databases.CreateDbContextAsync(cancellationToken);
            database.UndeliveredMessages.Add(new UndeliveredMessageRecord
            {
                IntentId = _intentId, TaskNumber = _taskNumber, SessionNumber = Number, Number = message.Number, Text = message.Text,
            });
            // Not cancelled: a write broken off could leave a message stored that is never delivered.
            await database.SaveChangesAsync(CancellationToken.None);
            _messages = message.Number;
            _waiting.Enqueue(message);
        }
        finally
        {
            _inbox.Release();
        }
        _messagesChanged();
        return true;
    }

    /// <summary>
    /// The message to deliver next, which a turn taken with it (<see cref="TakeTurnAsync"/>) does; <c>null</c> when none waits.
    /// </summary>
    public async Task<SentMessage?> NextMessageAsync(CancellationToken cancellationToken)
    {
        // Once any being taken on just now has been.
        await _inbox.WaitAsync(cancellationToken);
        _inbox.Release();
        return _waiting.TryPeek(out var next) ? next : null;
    }

    /// <summary>
    /// As <see cref="NextMessageAsync"/>, for work that is finished unless a message waits: when none does, none is taken on from
    /// now, so that none is left behind.
    /// </summary>
    public async Task<SentMessage?> NextMessageOrTakeNoMoreAsync(CancellationToken cancellationToken)
    {
        await _inbox.WaitAsync(cancellationToken);
        try
        {
            if (_waiting.TryPeek(out var next))
            {
                return next;
            }
            _takesMessages = false;
        }
        finally
        {
            _inbox.Release();
        }
        _messagesChanged();
        return null;
    }

    /// <summary>
    /// Sends the session's agent a message and writes what it reports to the log, as it happens, until the turn it starts ends.
    /// </summary>
    /// <param name="session">The session this is the log of.</param>
    /// <param name="delivering">
    /// The human's message that <paramref name="message"/> passes on, as <see cref="NextMessageAsync"/> gave it: once sent, it is
    /// delivered, and goes into the log where the turn begins.
    /// </param>
    /// <exception cref="AgentSessionFailedException">
    /// The message couldn't be sent, so the one it passes on stays undelivered; or the turn broke off, with what it reported until
    /// then in the log.
    /// </exception>
    public async Task<AgentOutcome> TakeTurnAsync(
        IAgentSession session, string message, CancellationToken cancellationToken, SentMessage? delivering = null)
    {
        var turn = await session.SendAsync(message, cancellationToken);
        if (delivering is not null)
        {
            await DeliveredAsync(delivering);
        }
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

    /// <summary>Records that the message that waited longest is delivered: it is an entry of the log from now on.</summary>
    // Not cancelled with the turn: the agent has the message by now, and a write broken off could leave what is stored unknown.
    private async Task DeliveredAsync(SentMessage message)
    {
        await using (var database = await _databases.CreateDbContextAsync(CancellationToken.None))
        {
            await using var transaction = await database.Database.BeginTransactionAsync(CancellationToken.None);
            await database.UndeliveredMessages
                .Where(stored => stored.IntentId == _intentId && stored.TaskNumber == _taskNumber
                    && stored.SessionNumber == Number && stored.Number == message.Number)
                .ExecuteDeleteAsync(CancellationToken.None);
            database.AgentSessionEntries.Add(new AgentSessionEntryRecord
            {
                IntentId = _intentId, TaskNumber = _taskNumber, SessionNumber = Number, Number = ++_entries,
                Kind = SessionEntryKind.UserMessage, Text = message.Text,
            });
            await database.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        _waiting.TryDequeue(out _);
        _messagesChanged();
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
                        IntentId = _intentId, TaskNumber = _taskNumber, SessionNumber = Number, Number = entry.Number,
                        Kind = entry.Kind, Text = text,
                    });
                }
                else
                {
                    await database.AgentSessionEntries
                        .Where(stored => stored.IntentId == _intentId && stored.TaskNumber == _taskNumber
                            && stored.SessionNumber == Number && stored.Number == entry.Number)
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

/// <summary>A message the human sent a session's agent: the <paramref name="Number"/>th the session took on, from 1.</summary>
/// <param name="Text">What they wrote, without any secret.</param>
public sealed record SentMessage(int Number, string Text);

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
/// <param name="Entries">What the agent said, the tools it called and the messages delivered to it, in order.</param>
/// <param name="Undelivered">
/// The human's messages to its agent that aren't delivered, in the order they were sent: waiting while the session takes messages,
/// and otherwise never delivered.
/// </param>
/// <param name="TakesMessages">Whether its agent can be sent a message now.</param>
public sealed record LoggedSession(
    int Number, AgentRole Role, IReadOnlyList<LoggedEntry> Entries, IReadOnlyList<string> Undelivered, bool TakesMessages);

/// <summary>One thing in a session's log: a whole message of the agent's, a tool it called, or a message delivered to it.</summary>
/// <param name="Text">The agent's Markdown, the tool and what it was called on, or what the human wrote.</param>
public sealed record LoggedEntry(SessionEntryKind Kind, string Text);

public enum SessionEntryKind
{
    /// <summary>What the agent said.</summary>
    Message,

    /// <summary>A tool the agent called.</summary>
    ToolCall,

    /// <summary>A message of the human's, delivered to the agent.</summary>
    UserMessage,
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
