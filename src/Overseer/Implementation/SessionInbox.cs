using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Overseer.Persistence;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// The messages the human sends the agent of one session on a Task: taken on while the session takes them, and waiting, in the order
/// they were sent and kept in PostgreSQL, until each is delivered. One still waiting once the inbox takes no more is never delivered.
/// </summary>
public sealed class SessionInbox
{
    private readonly IDbContextFactory<OverseerDbContext> _databases;
    private readonly SessionKey _session;

    /// <summary>Told whenever the messages change, or the inbox stops taking them.</summary>
    private readonly Action _changed;

    /// <summary>
    /// Held while a message is taken on, and while the inbox stops taking them, so that each one taken on is seen by whoever asks for
    /// the next one afterwards.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The messages waiting to be delivered, in the order they were sent.</summary>
    private readonly ConcurrentQueue<SentMessage> _waiting = new();

    /// <summary>How many messages have been taken on.</summary>
    private int _takenOn;

    private volatile bool _takesMessages = true;

    internal SessionInbox(IDbContextFactory<OverseerDbContext> databases, SessionKey session, Action changed)
    {
        _databases = databases;
        _session = session;
        _changed = changed;
    }

    /// <summary>Whether a message sent now would be taken on.</summary>
    internal bool TakesMessages => _takesMessages;

    /// <summary>Takes on a message for the session's agent, unless the inbox takes no more.</summary>
    /// <param name="text">The message, as <see cref="SandboxRegistry.WithoutSecretsAsync"/> gives it.</param>
    internal async Task<bool> TryTakeOnAsync(string text, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_takesMessages)
            {
                return false;
            }
            var message = new SentMessage(_takenOn + 1, text);
            await using var database = await _databases.CreateDbContextAsync(cancellationToken);
            database.UndeliveredMessages.Add(new UndeliveredMessageRecord
            {
                IntentId = _session.Task.IntentId, TaskNumber = _session.Task.Number, SessionNumber = _session.Number,
                Number = message.Number, Text = message.Text,
            });
            // Not cancelled: a write broken off could leave a message stored that is never delivered.
            await database.SaveChangesAsync(CancellationToken.None);
            _takenOn = message.Number;
            _waiting.Enqueue(message);
        }
        finally
        {
            _gate.Release();
        }
        _changed();
        return true;
    }

    /// <summary>
    /// The message to deliver next, which a turn taken with it (<see cref="SessionLog.TakeTurnAsync"/>) does; <c>null</c> when none
    /// waits.
    /// </summary>
    public async Task<SentMessage?> NextAsync(CancellationToken cancellationToken)
    {
        // Once any being taken on just now has been.
        await _gate.WaitAsync(cancellationToken);
        _gate.Release();
        return _waiting.TryPeek(out var next) ? next : null;
    }

    /// <summary>
    /// As <see cref="NextAsync"/>, for work that is finished unless a message waits: when none does, none is taken on from now, so
    /// that none is left behind.
    /// </summary>
    public async Task<SentMessage?> NextOrTakeNoMoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
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
            _gate.Release();
        }
        _changed();
        return null;
    }

    /// <summary>Takes no more messages, as the session ends.</summary>
    internal async Task CloseAsync()
    {
        await _gate.WaitAsync();
        _takesMessages = false;
        _gate.Release();
    }

    /// <summary>Records that the message that waited longest is delivered: it waits no more.</summary>
    /// <param name="record">Adds what is to be saved in the same transaction, as the message's entry in the session's log.</param>
    // Not cancelled with the turn: the agent has the message by now, and a write broken off could leave what is stored unknown.
    internal async Task DeliveredAsync(SentMessage message, Action<OverseerDbContext> record)
    {
        await using (var database = await _databases.CreateDbContextAsync(CancellationToken.None))
        {
            await using var transaction = await database.Database.BeginTransactionAsync(CancellationToken.None);
            await database.UndeliveredMessages
                .Where(stored => stored.IntentId == _session.Task.IntentId && stored.TaskNumber == _session.Task.Number
                    && stored.SessionNumber == _session.Number && stored.Number == message.Number)
                .ExecuteDeleteAsync(CancellationToken.None);
            record(database);
            await database.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }
        _waiting.TryDequeue(out _);
        _changed();
    }
}

/// <summary>A message the human sent a session's agent: the <paramref name="Number"/>th the session took on, from 1.</summary>
/// <param name="Text">What they wrote, without any secret.</param>
public sealed record SentMessage(int Number, string Text);
