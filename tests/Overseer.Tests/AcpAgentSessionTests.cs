using System.Text.Json.Nodes;
using System.Threading.Channels;
using Overseer.Sandboxes;

namespace Overseer.Tests;

/// <summary>The Agent Client Protocol conversation of <see cref="AcpAgentSession"/>, against a scripted <c>opencode acp</c>.</summary>
public class AcpAgentSessionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static Task<AcpAgentSession> StartAsync(ScriptedAgentProcess agent, params string[] secrets) =>
        AcpAgentSession.StartAsync(agent, "/work", "developer", secrets, () => Task.CompletedTask, Token);

    [Fact]
    public async Task Starting_opens_a_session_in_the_work_directory_in_the_roles_agent()
    {
        var agent = new ScriptedAgentProcess();

        await using var session = await StartAsync(agent);

        Assert.Equal(["initialize", "session/new", "session/set_mode"], agent.Requests.Select(request => (string)request["method"]!));
        Assert.Equal("/work", (string)agent.Requests[1]["params"]!["cwd"]!);
        Assert.Equal("developer", (string)agent.Requests[2]["params"]!["modeId"]!);
    }

    [Fact]
    public async Task A_turn_streams_what_the_agent_says_as_it_happens_and_ends_done()
    {
        var agent = new ScriptedAgentProcess();
        await using var session = await StartAsync(agent);

        var turn = await session.SendAsync("Go", Token);
        await agent.PromptReceivedAsync();
        agent.Chunk("Working ");
        var updates = turn.Updates.GetAsyncEnumerator(Token);
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("Working ", updates.Current);
        agent.Chunk("on it.");
        agent.EndTurn();

        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("on it.", updates.Current);
        Assert.False(await updates.MoveNextAsync());
        Assert.Equal(new AgentOutcome.Done(), await turn.Outcome);
    }

    [Fact]
    public async Task A_turn_ending_on_the_agents_question_is_a_question_and_the_answer_continues_the_session()
    {
        var agent = new ScriptedAgentProcess();
        await using var session = await StartAsync(agent);

        var asking = await session.SendAsync("Go", Token);
        await agent.PromptReceivedAsync();
        agent.Chunk("Which colour do you prefer?");
        agent.EndTurn();
        await asking.Updates.ToListAsync(Token);
        Assert.Equal(new AgentOutcome.AskedQuestion("Which colour do you prefer?"), await asking.Outcome);

        var answered = await session.SendAsync("Blue", Token);
        await agent.PromptReceivedAsync();
        agent.Chunk("Blue it is.");
        agent.EndTurn();
        await answered.Updates.ToListAsync(Token);

        Assert.Equal(new AgentOutcome.Done(), await answered.Outcome);
        var prompts = agent.Requests.Where(request => (string)request["method"]! == "session/prompt").ToList();
        Assert.Equal(2, prompts.Count);
        Assert.Equal(agent.SessionId, (string)prompts[1]["params"]!["sessionId"]!);
    }

    [Fact]
    public async Task OpenCodes_question_tool_is_a_question_and_its_permission_is_refused()
    {
        var agent = new ScriptedAgentProcess();
        await using var session = await StartAsync(agent);

        var turn = await session.SendAsync("Go", Token);
        await agent.PromptReceivedAsync();
        var refusal = agent.AskPermission(new JsonObject
        {
            ["title"] = "question",
            ["rawInput"] = new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["question"] = "Postgres or SQLite?" }) },
        });
        Assert.Equal("cancelled", (string)(await refusal)["result"]!["outcome"]!["outcome"]!);
        agent.EndTurn();

        Assert.Equal(new AgentOutcome.AskedQuestion("Postgres or SQLite?"), await turn.Outcome);
    }

    [Fact]
    public async Task Another_permission_is_granted_for_the_agent_to_work_headless()
    {
        var agent = new ScriptedAgentProcess();
        await using var session = await StartAsync(agent);
        var turn = await session.SendAsync("Go", Token);
        await agent.PromptReceivedAsync();

        var answer = await agent.AskPermission(new JsonObject { ["title"] = "bash" });

        Assert.Equal("selected", (string)answer["result"]!["outcome"]!["outcome"]!);
        Assert.Equal("once", (string)answer["result"]!["outcome"]!["optionId"]!);
        agent.EndTurn();
        await turn.Outcome;
    }

    [Fact]
    public async Task A_process_that_exits_in_a_turn_fails_it_with_what_it_said_and_without_the_secrets()
    {
        var agent = new ScriptedAgentProcess();
        await using var session = await StartAsync(agent, "sk-secret");
        var turn = await session.SendAsync("Go", Token);
        await agent.PromptReceivedAsync();

        agent.Exit(3, "boom with sk-secret");

        var failure = await Assert.ThrowsAsync<AgentSessionFailedException>(() => turn.Outcome);
        Assert.Contains("code 3", failure.Message);
        Assert.Contains("boom", failure.Message);
        Assert.DoesNotContain("sk-secret", failure.Message);
    }

    [Fact]
    public async Task A_lost_connection_fails_the_turn_and_says_so()
    {
        var agent = new ScriptedAgentProcess();
        await using var session = await StartAsync(agent);
        var turn = await session.SendAsync("Go", Token);
        await agent.PromptReceivedAsync();

        agent.Drop();

        var failure = await Assert.ThrowsAsync<AgentSessionFailedException>(() => turn.Outcome);
        Assert.Contains("connection", failure.Message);
        await Assert.ThrowsAsync<AgentSessionFailedException>(async () => await turn.Updates.ToListAsync(Token));
    }

    [Fact]
    public async Task Disposing_in_the_middle_of_a_turn_stops_the_process_and_ends_the_turn()
    {
        var agent = new ScriptedAgentProcess();
        var ended = false;
        var session = await AcpAgentSession.StartAsync(agent, "/work", "developer", [], () => { ended = true; return Task.CompletedTask; }, Token);
        var turn = await session.SendAsync("Go", Token);
        await agent.PromptReceivedAsync();

        await session.DisposeAsync();

        Assert.True(agent.Disposed);
        Assert.True(ended);
        await Assert.ThrowsAsync<AgentSessionFailedException>(() => turn.Outcome);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.SendAsync("More", Token));
    }

    [Fact]
    public async Task An_agent_that_never_answers_the_greeting_but_exits_fails_the_start()
    {
        var agent = new ScriptedAgentProcess { Answering = false };
        agent.Exit(127, "opencode: command not found");

        var failure = await Assert.ThrowsAsync<AgentSessionFailedException>(() => StartAsync(agent));

        Assert.Contains("command not found", failure.Message);
    }
}

/// <summary>An <c>opencode acp</c> that answers as the test scripts it.</summary>
internal sealed class ScriptedAgentProcess : ISshProcess
{
    private readonly Channel<string?> _output = Channel.CreateUnbounded<string?>();
    private readonly Channel<JsonNode> _prompts = Channel.CreateUnbounded<JsonNode>();
    private readonly TaskCompletionSource<int> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<JsonNode> _requests = [];
    private readonly Dictionary<long, TaskCompletionSource<JsonNode>> _permissions = [];
    private long _promptId;
    private long _agentRequestId = 1000;

    public string SessionId => "session-1";

    public bool Answering { get; init; } = true;

    public bool Disposed { get; private set; }

    public string StandardErrorTail { get; private set; } = "";

    public Task<int> ExitCode => _exit.Task;

    public IReadOnlyList<JsonNode> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>What was written before the protocol began: the first line the process read.</summary>
    public List<string> Input { get; } = [];

    public Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        if (!line.StartsWith('{'))
        {
            Input.Add(line);
            return Task.CompletedTask;
        }

        if (Disposed || _exit.Task.IsCompleted)
        {
            throw new IOException("closed");
        }

        var message = JsonNode.Parse(line)!;
        if (message["method"] is null)
        {
            // A response to what the agent asked.
            lock (_permissions)
            {
                if (_permissions.Remove((long)message["id"]!, out var asked))
                {
                    asked.SetResult(message);
                }
            }
            return Task.CompletedTask;
        }

        lock (_requests)
        {
            _requests.Add(message);
        }
        var id = (long?)message["id"];
        switch ((string)message["method"]!)
        {
            case "initialize" when Answering:
                Send(new JsonObject { ["id"] = id, ["result"] = new JsonObject { ["protocolVersion"] = 1 } });
                break;
            case "session/new" when Answering:
                Send(new JsonObject { ["id"] = id, ["result"] = new JsonObject { ["sessionId"] = SessionId } });
                break;
            case "session/set_mode" when Answering:
                Send(new JsonObject { ["id"] = id, ["result"] = new JsonObject() });
                break;
            case "session/prompt":
                _promptId = id!.Value;
                _prompts.Writer.TryWrite(message);
                break;
        }
        return Task.CompletedTask;
    }

    public async Task<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        var line = await _output.Reader.ReadAsync(cancellationToken);
        if (line is null && _lost is { } lost)
        {
            throw lost;
        }
        return line;
    }

    private SandboxUnreachableException? _lost;

    public Task<JsonNode> PromptReceivedAsync() => _prompts.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();

    public void Chunk(string text) => Update(new JsonObject
    {
        ["sessionUpdate"] = "agent_message_chunk",
        ["content"] = new JsonObject { ["type"] = "text", ["text"] = text },
    });

    public void EndTurn() =>
        Send(new JsonObject { ["id"] = _promptId, ["result"] = new JsonObject { ["stopReason"] = "end_turn" } });

    /// <summary>Asks for a permission to run a tool call, as the agent does, and gives the client's answer.</summary>
    public Task<JsonNode> AskPermission(JsonObject toolCall)
    {
        var id = Interlocked.Increment(ref _agentRequestId);
        var answer = new TaskCompletionSource<JsonNode>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_permissions)
        {
            _permissions[id] = answer;
        }
        Send(new JsonObject
        {
            ["id"] = id,
            ["method"] = "session/request_permission",
            ["params"] = new JsonObject
            {
                ["sessionId"] = SessionId,
                ["toolCall"] = toolCall,
                ["options"] = new JsonArray(
                    new JsonObject { ["optionId"] = "reject", ["kind"] = "reject_once" },
                    new JsonObject { ["optionId"] = "once", ["kind"] = "allow_once" }),
            },
        });
        return answer.Task;
    }

    public void Exit(int code, string said)
    {
        StandardErrorTail = said;
        _exit.TrySetResult(code);
        _output.Writer.TryWrite(null);
    }

    /// <summary>The SSH connection breaks.</summary>
    public void Drop()
    {
        _lost = new SandboxUnreachableException("The SSH connection to the Sandbox was lost.");
        _output.Writer.TryWrite(null);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _lost ??= null;
        _output.Writer.TryWrite(null);
        _exit.TrySetResult(-1);
        return ValueTask.CompletedTask;
    }

    private void Update(JsonObject update) => Send(new JsonObject
    {
        ["method"] = "session/update",
        ["params"] = new JsonObject { ["sessionId"] = SessionId, ["update"] = update },
    });

    private void Send(JsonObject message)
    {
        message["jsonrpc"] = "2.0";
        _output.Writer.TryWrite(message.ToJsonString());
    }
}
