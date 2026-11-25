using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Overseer.Persistence;
using Overseer.Sandboxes;
using Overseer.Secrets;

namespace Overseer.Implementation;

/// <summary>
/// The log of one agent session on a Task, as it is written, with the messages the human sends its agent waiting for their turn
/// (<see cref="Inbox"/>). Disposing it says that the session has ended: a message still waiting then is never delivered.
/// </summary>
public sealed class SessionLog : IAsyncDisposable
{
    /// <summary>The least time between two writes while a turn goes on, so that a message streamed word by word isn't written as often.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromMilliseconds(100);

    private readonly SandboxRegistry _sandboxes;
    private readonly IDbContextFactory<OverseerDbContext> _databases;

    /// <summary>Told what the session's agent is doing whenever the log grows, and <c>null</c> once the session has ended.</summary>
    private readonly Action<SessionActivity?> _report;

    /// <summary>Told as the agent's question and the answer to it are written to the log.</summary>
    private readonly Action _changed;

    private readonly Action<SessionLog> _closed;

    /// <summary>The entries that may differ from how they are stored.</summary>
    private readonly HashSet<Entry> _unsaved = [];

    /// <summary>The turn's tool calls, by ID.</summary>
    private readonly Dictionary<string, Entry> _calls = [];

    /// <summary>The message the agent is saying; <c>null</c> when it isn't saying one.</summary>
    private Entry? _message;

    /// <summary>The entry the agent reported last.</summary>
    private Entry? _latest;

    /// <summary>The entry the agent began last in the turn; <c>null</c> when it has begun none.</summary>
    private Entry? _lastBegun;

    private SessionActivity? _activity;

    private int _entries;

    internal SessionLog(
        SandboxRegistry sandboxes, IDbContextFactory<OverseerDbContext> databases, SessionKey key, Action<SessionActivity?> report,
        Action changed, Action<SessionLog> closed)
    {
        _sandboxes = sandboxes;
        _databases = databases;
        Key = key;
        Inbox = new SessionInbox(databases, key, changed);
        _report = report;
        _changed = changed;
        _closed = closed;
    }

    /// <summary>The human's messages to the session's agent.</summary>
    public SessionInbox Inbox { get; }

    /// <summary>Which session this is the log of.</summary>
    internal SessionKey Key { get; }

    public async ValueTask DisposeAsync()
    {
        await Inbox.CloseAsync();
        _closed(this);
        _report(null);
    }

    /// <summary>
    /// Sends the session's agent a message and writes what it reports to the log, as it happens, until the turn it starts ends.
    /// </summary>
    /// <param name="session">The session this is the log of.</param>
    /// <param name="delivering">
    /// The human's message that <paramref name="message"/> passes on, as the <see cref="Inbox"/> gave it: once sent, it is
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
        _lastBegun = null;
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
            // Until then, the agent is doing nothing: what it did last isn't what is going on, as while its work is verified.
            _latest = null;
            _activity = null;
            _report(null);
        }
    }

    /// <summary>Records that the message that waited longest is delivered: it is an entry of the log from now on.</summary>
    private Task DeliveredAsync(SentMessage message) =>
        Inbox.DeliveredAsync(
            message, database => database.AgentSessionEntries.Add(Record(++_entries, SessionEntryKind.UserMessage, message.Text)));

    /// <summary>
    /// Writes the question the turn just taken ended with to the log. Where the agent asked it in the last thing it said, that
    /// message is the question's entry from now on, so that the log doesn't hold it twice.
    /// </summary>
    /// <param name="question">As <see cref="SandboxRegistry.WithoutSecretsAsync"/> gives it.</param>
    // Not cancelled, like every write of the log.
    public async Task AskedAsync(string question)
    {
        if (_lastBegun is { Kind: SessionEntryKind.Message } said && said.Stored == question.Trim())
        {
            await using var database = await _databases.CreateDbContextAsync(CancellationToken.None);
            await Stored(database, said.Number).ExecuteUpdateAsync(
                stored => stored.SetProperty(record => record.Kind, SessionEntryKind.Question), CancellationToken.None);
        }
        else
        {
            await AddAsync(SessionEntryKind.Question, question);
        }
        _changed();
    }

    /// <summary>Writes the human's answer to the question asked last to the log.</summary>
    /// <param name="answer">As <see cref="TaskQuestions.AskAsync"/> gave it.</param>
    public async Task AnsweredAsync(string answer)
    {
        await AddAsync(SessionEntryKind.Answer, answer);
        _changed();
    }

    private async Task AddAsync(SessionEntryKind kind, string text)
    {
        await using var database = await _databases.CreateDbContextAsync(CancellationToken.None);
        database.AgentSessionEntries.Add(Record(++_entries, kind, text));
        await database.SaveChangesAsync(CancellationToken.None);
    }

    private AgentSessionEntryRecord Record(int number, SessionEntryKind kind, string text) => new()
    {
        IntentId = Key.Task.IntentId, TaskNumber = Key.Task.Number, SessionNumber = Key.Number, Number = number, Kind = kind,
        Text = text,
    };

    /// <summary>The session's entry with this number, as stored.</summary>
    private IQueryable<AgentSessionEntryRecord> Stored(OverseerDbContext database, int number) =>
        database.AgentSessionEntries.Where(stored => stored.IntentId == Key.Task.IntentId && stored.TaskNumber == Key.Task.Number
            && stored.SessionNumber == Key.Number && stored.Number == number);

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
                    _message ??= _lastBegun = new Entry(++_entries, SessionEntryKind.Message);
                    _message.Reported.Append(said.Text);
                    _unsaved.Add(_message);
                    _latest = _message;
                    break;
                case AgentUpdate.ToolCall call:
                    if (!_calls.TryGetValue(call.Id, out var entry))
                    {
                        EndMessage();
                        _calls[call.Id] = entry = _lastBegun = new Entry(++_entries, SessionEntryKind.ToolCall) { Whole = true };
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
                    database.AgentSessionEntries.Add(Record(entry.Number, entry.Kind, text));
                }
                else
                {
                    await Stored(database, entry.Number)
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
