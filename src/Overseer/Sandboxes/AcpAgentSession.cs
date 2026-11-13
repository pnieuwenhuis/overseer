using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Overseer.Sandboxes;

/// <summary>
/// An agent session over the Agent Client Protocol (JSON-RPC, a message per line) with an <c>opencode acp</c> process, which
/// <see cref="ISshProcess"/> carries from the Sandbox. A message to the agent is a <c>session/prompt</c> request; what the agent
/// says and does while it works arrives as <c>session/update</c> notifications, which stream out as the turn's updates; the
/// request's response ends the turn.
/// </summary>
/// <remarks>
/// <para>
/// How the agent asks a question: ACP has no message for it. The turn ends like any other (<c>end_turn</c>), and the user's
/// answer is the next prompt of the same session. Two things make a turn's end a question, and either maps onto
/// <see cref="AgentOutcome.AskedQuestion"/>:
/// OpenCode's <c>question</c> tool, which shows as a tool call titled <c>question</c> whose input lists the questions (the
/// session refuses that call, if it asks permission, and ends the turn there); or, with that tool unavailable, the agent's
/// last message ending in a question mark. The turn is then a question when no tool call follows the message.
/// </para>
/// <para>
/// The agent runs headless: a permission it asks for is granted, since <see cref="AgentHarnessConfiguration"/> allows what the
/// agents need and anything else is not for the user to answer.
/// </para>
/// </remarks>
public sealed class AcpAgentSession : IAgentSession
{
    private const int ProtocolVersion = 1;

    private readonly ISshProcess _process;
    private readonly Func<Task> _afterStopping;
    private readonly IReadOnlyCollection<string> _secrets;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writing = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private Task _pump = Task.CompletedTask;
    private long _nextId;
    private string _sessionId = "";
    private Turn? _turn;
    private string? _failure;
    private bool _disposed;

    private AcpAgentSession(ISshProcess process, IReadOnlyCollection<string> secrets, Func<Task> afterStopping)
    {
        _process = process;
        _secrets = secrets;
        _afterStopping = afterStopping;
    }

    /// <summary>
    /// Takes over a running <c>opencode acp</c> process: greets it, opens a session in <paramref name="directory"/> and selects
    /// the OpenCode agent <paramref name="agentName"/>.
    /// </summary>
    /// <param name="secrets">Values that must never show in what the session reports, such as in its failures.</param>
    /// <param name="afterStopping">Runs when the session has been disposed, to make sure that nothing of the process is left.</param>
    /// <exception cref="AgentSessionFailedException">The process did not answer as an ACP agent does.</exception>
    public static async Task<AcpAgentSession> StartAsync(
        ISshProcess process, string directory, string agentName, IReadOnlyCollection<string> secrets, Func<Task> afterStopping,
        CancellationToken cancellationToken)
    {
        var session = new AcpAgentSession(process, secrets, afterStopping);
        session._pump = Task.Run(session.PumpAsync);
        try
        {
            await session.RequestAsync("initialize", new JsonObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["clientCapabilities"] = new JsonObject(),
            }, cancellationToken);
            var created = await session.RequestAsync("session/new", new JsonObject
            {
                ["cwd"] = directory,
                ["mcpServers"] = new JsonArray(),
            }, cancellationToken);
            session._sessionId = created.TryGetProperty("sessionId", out var id) && id.GetString() is { Length: > 0 } sessionId
                ? sessionId
                : throw new AgentSessionFailedException("The agent did not open a session.");
            // OpenCode offers its agents as modes of the session; the agent brings its own model.
            await session.RequestAsync("session/set_mode", new JsonObject { ["sessionId"] = session._sessionId, ["modeId"] = agentName }, cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    public Task<AgentTurn> SendAsync(string message, CancellationToken cancellationToken)
    {
        Turn turn;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            turn = _turn = new Turn();
        }
        _ = RunTurnAsync(turn, message);
        return Task.FromResult(new AgentTurn(turn.Updates.Reader.ReadAllAsync(cancellationToken), turn.Outcome.Task));
    }

    private async Task RunTurnAsync(Turn turn, string message)
    {
        try
        {
            var response = await RequestAsync("session/prompt", new JsonObject
            {
                ["sessionId"] = _sessionId,
                ["prompt"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = message }),
            }, _stopping.Token);
            var stopReason = response.TryGetProperty("stopReason", out var reason) ? reason.GetString() : null;
            turn.Succeed(stopReason switch
            {
                "end_turn" or "cancelled" => turn.OutcomeAtEnd(),
                "max_tokens" => throw new AgentSessionFailedException("The agent stopped because it ran out of tokens for its answer."),
                "max_turn_requests" => throw new AgentSessionFailedException("The agent stopped because it reached the limit on its requests to the model."),
                "refusal" => throw new AgentSessionFailedException("The model refused to go on."),
                _ => throw new AgentSessionFailedException($"The agent ended its turn with an unknown reason: {stopReason}."),
            });
        }
        catch (Exception exception) when (exception is AgentSessionFailedException or OperationCanceledException)
        {
            turn.Fail(exception as AgentSessionFailedException ?? new AgentSessionFailedException("The agent session was stopped."));
        }
    }

    /// <summary>Reads what the agent writes until it ends, and fails what waits for more when it does.</summary>
    private async Task PumpAsync()
    {
        string failure;
        try
        {
            while (await _process.ReadLineAsync(_stopping.Token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line) || !TryParse(line, out var message))
                {
                    // OpenCode may print what is not ACP, such as a log line; it is not for the session.
                    continue;
                }
                await HandleAsync(message);
            }
            var exitCode = await _process.ExitCode;
            var said = _process.StandardErrorTail;
            failure = $"The agent process exited with code {exitCode}" + (said.Length == 0 ? "." : $": {said}");
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            failure = "The agent session was stopped.";
        }
        catch (SandboxUnreachableException exception)
        {
            failure = exception.Message;
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            failure = $"The agent session broke off: {exception.Message}";
        }

        failure = Scrub(failure);
        lock (_gate)
        {
            _failure = failure;
        }
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new AgentSessionFailedException(failure));
        }
        _turn?.Fail(new AgentSessionFailedException(failure));
    }

    private async Task HandleAsync(JsonElement message)
    {
        var hasId = message.TryGetProperty("id", out var id);
        var method = message.TryGetProperty("method", out var methodName) ? methodName.GetString() : null;
        if (method is null)
        {
            // A response to one of ours.
            if (hasId && id.TryGetInt64(out var answered) && _pending.TryRemove(answered, out var waiting))
            {
                if (message.TryGetProperty("error", out var error))
                {
                    var said = error.TryGetProperty("message", out var text) ? text.GetString() : error.ToString();
                    waiting.TrySetException(new AgentSessionFailedException(Scrub($"The agent answered with an error: {said}")));
                }
                else
                {
                    waiting.TrySetResult(message.TryGetProperty("result", out var result) ? result.Clone() : default);
                }
            }
            return;
        }

        var parameters = message.TryGetProperty("params", out var found) ? found : default;
        switch (method)
        {
            case "session/update":
                HandleUpdate(parameters);
                break;
            case "session/request_permission" when hasId:
                await RespondAsync(id, new JsonObject { ["outcome"] = Decide(parameters) });
                break;
            case var _ when hasId:
                // Nothing else is offered to the agent (no files, no terminals): it asked for what this client doesn't have.
                await RespondAsync(id, error: new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" });
                break;
        }
    }

    private void HandleUpdate(JsonElement parameters)
    {
        if (_turn is not { } turn || !parameters.TryGetProperty("update", out var update)
            || !update.TryGetProperty("sessionUpdate", out var kind))
        {
            return;
        }

        switch (kind.GetString())
        {
            case "agent_message_chunk"
                when update.TryGetProperty("content", out var content) && content.TryGetProperty("text", out var text) && text.GetString() is { Length: > 0 } chunk:
                turn.Message.Append(chunk);
                turn.Updates.Writer.TryWrite(Scrub(chunk));
                break;
            case "tool_call":
                turn.Message.Clear();
                if (QuestionOf(update) is { } question)
                {
                    turn.Question = question;
                }
                else if (update.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } called)
                {
                    turn.Updates.Writer.TryWrite(Scrub(called));
                }
                break;
            case "tool_call_update" when QuestionOf(update) is { } updatedQuestion:
                turn.Question = updatedQuestion;
                break;
        }
    }

    /// <summary>The permission decision for a request: to go ahead, except with a question, which is for the user.</summary>
    private JsonObject Decide(JsonElement parameters)
    {
        if (parameters.TryGetProperty("toolCall", out var toolCall) && QuestionOf(toolCall) is { } question)
        {
            if (_turn is { } turn)
            {
                turn.Question = question;
            }
            return new JsonObject { ["outcome"] = "cancelled" };
        }

        if (parameters.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var kind in new[] { "allow_always", "allow_once" })
            {
                foreach (var option in options.EnumerateArray())
                {
                    if (option.TryGetProperty("kind", out var optionKind) && optionKind.GetString() == kind
                        && option.TryGetProperty("optionId", out var optionId) && optionId.GetString() is { } chosen)
                    {
                        return new JsonObject { ["outcome"] = "selected", ["optionId"] = chosen };
                    }
                }
            }
        }
        return new JsonObject { ["outcome"] = "cancelled" };
    }

    /// <summary>The question in an OpenCode <c>question</c> tool call, or null when the tool call is another one.</summary>
    private static string? QuestionOf(JsonElement toolCall)
    {
        if (!toolCall.TryGetProperty("title", out var title) || !string.Equals(title.GetString(), "question", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var questions = new List<string>();
        if (toolCall.TryGetProperty("rawInput", out var input) && input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty("questions", out var asked) && asked.ValueKind == JsonValueKind.Array)
        {
            questions.AddRange(asked.EnumerateArray()
                .Select(one => one.ValueKind == JsonValueKind.String ? one.GetString()
                    : one.ValueKind == JsonValueKind.Object && one.TryGetProperty("question", out var text) ? text.GetString() : null)
                .OfType<string>()
                .Where(text => text.Length > 0));
        }
        return questions.Count > 0 ? string.Join("\n", questions) : "The agent asked a question.";
    }

    private async Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _nextId);
        var waiting = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = waiting;
        lock (_gate)
        {
            if (_failure is not null)
            {
                throw new AgentSessionFailedException(_failure);
            }
        }

        await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters }, cancellationToken);
        return await waiting.Task.WaitAsync(cancellationToken);
    }

    private Task RespondAsync(JsonElement id, JsonObject? result = null, JsonObject? error = null) =>
        WriteAsync(new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = JsonNode.Parse(id.GetRawText()),
            [error is null ? "result" : "error"] = error ?? result,
        }, _stopping.Token);

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        await _writing.WaitAsync(cancellationToken);
        try
        {
            await _process.WriteLineAsync(message.ToJsonString(), cancellationToken);
        }
        catch (IOException exception)
        {
            // The pump finds out what happened; until it has, say what is known.
            await Task.WhenAny(_pump, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
            lock (_gate)
            {
                throw new AgentSessionFailedException(_failure ?? Scrub($"The agent session broke off: {exception.Message}"));
            }
        }
        finally
        {
            _writing.Release();
        }
    }

    private static bool TryParse(string line, out JsonElement message)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            message = document.RootElement.Clone();
            return message.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            message = default;
            return false;
        }
    }

    private string Scrub(string text)
    {
        foreach (var secret in _secrets)
        {
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        }
        return text;
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }

        // Tells the agent to stop what it is doing, then ends it, also when it is in the middle of a turn.
        if (_sessionId.Length > 0)
        {
            try
            {
                using var asking = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await WriteAsync(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = "session/cancel", ["params"] = new JsonObject { ["sessionId"] = _sessionId } }, asking.Token);
            }
            catch (Exception exception) when (exception is AgentSessionFailedException or OperationCanceledException)
            {
            }
        }
        await _stopping.CancelAsync();
        await _process.DisposeAsync();
        try
        {
            await _afterStopping();
        }
        catch (Exception exception) when (exception is SandboxUnreachableException or InvalidOperationException)
        {
            // The Sandbox may be gone with the process in it.
        }
        await _pump;
        _stopping.Dispose();
    }

    /// <summary>What one turn collects while the agent works.</summary>
    private sealed class Turn
    {
        public Channel<string> Updates { get; } = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

        public TaskCompletionSource<AgentOutcome> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>What the agent has said since its last tool call.</summary>
        public StringBuilder Message { get; } = new();

        public string? Question { get; set; }

        public AgentOutcome OutcomeAtEnd()
        {
            if (Question is { } asked)
            {
                return new AgentOutcome.AskedQuestion(asked);
            }

            var said = Message.ToString().Trim();
            return said.EndsWith('?') ? new AgentOutcome.AskedQuestion(said) : new AgentOutcome.Done();
        }

        public void Succeed(AgentOutcome outcome)
        {
            Updates.Writer.TryComplete();
            Outcome.TrySetResult(outcome);
        }

        public void Fail(AgentSessionFailedException failure)
        {
            Updates.Writer.TryComplete(failure);
            Outcome.TrySetException(failure);
        }
    }
}
