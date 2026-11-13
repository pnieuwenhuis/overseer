using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Overseer.GitHub;
using Overseer.Secrets;

namespace Overseer.Sandboxes;

/// <summary>
/// How the Azure Sandbox Provider reaches Azure and its Sandboxes, and where they go. How big each Sandbox is, and
/// whether it is a spot instance, comes with its creation (<see cref="SandboxMachine"/>).
/// </summary>
/// <param name="SubscriptionId">The ID of the subscription holding the Sandbox resource group.</param>
/// <param name="PrivateNetwork">
/// Deployed mode: Sandboxes have no public IP, and Overseer reaches them by private IP over the peering install makes
/// between its VNet and the Sandbox VNet. Otherwise local mode: a public IP that only this machine's reaches.
/// </param>
/// <param name="ManagedIdentityClientId">The user-assigned Managed Identity the Azure CLI logs in as; null for the CLI's own login.</param>
public sealed record AzureSandboxSettings(
    string SubscriptionId, string ResourceGroup, bool PrivateNetwork = false, string? ManagedIdentityClientId = null);

/// <summary>
/// Sandboxes as Azure VMs in the Sandbox resource group, managed only through the Azure CLI. In local mode (ADR 0004)
/// the CLI uses the developer's login, and each Sandbox has a public IP that only the developer's current public IP
/// reaches, over SSH. Deployed, the CLI logs in as the Managed Identity, and Sandboxes have only a private IP on the
/// Sandbox VNet, which only Overseer's subnet reaches. Each Sandbox has its own SSH key, kept in the secret store so
/// that Overseer still reaches it after a restart.
/// </summary>
public sealed partial class AzureSandboxProvider(
    AzureSandboxSettings settings,
    IReadOnlyDictionary<string, string?> environment,
    ISecretStore secrets,
    ISecretReader secretReader,
    ISandboxSsh ssh,
    Func<CancellationToken, Task<IPAddress>> developerAddress,
    TimeProvider time) : ISandboxProvider
{
    /// <summary>The user Overseer logs in to Sandboxes as.</summary>
    public const string Username = "overseer";

    private const int SshPort = 22;

    /// <summary>
    /// Where a deployed Overseer connects to Sandboxes from: the Container Apps subnet of the VNet install creates for it
    /// (<c>Installer/Scripts/create-network.sh</c>), peered with the Sandbox VNet.
    /// </summary>
    public const string OverseerSubnet = "10.40.0.0/23";

    public AzureSandboxSettings Settings => settings;

    /// <summary>Waits for cloud-init to finish, then shows how it went, with the end of its log when it failed.</summary>
    private const string SetupStatusCommand = """
        status=0
        cloud-init status --wait --long || status=$?
        if [ "$status" -ne 0 ] && [ "$status" -ne 2 ]; then sudo tail -n 40 /var/log/cloud-init-output.log; fi
        exit "$status"
        """;

    /// <summary>How long a Sandbox may take from its VM's creation until it is set up, which normally takes 5 to 10 minutes.</summary>
    private static readonly TimeSpan SetUpWithin = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ReachRetryInterval = TimeSpan.FromSeconds(10);

    private readonly AzureScripts _azure = new("Sandboxes", settings.ManagedIdentityClientId is null ? environment : WithOwnAzureCliLogin(environment));
    private readonly Once _logIn = new();
    private readonly Once _createNetwork = new();

    /// <summary>How to reach each Sandbox, by VM name, so that only the first command after a restart asks Azure.</summary>
    private readonly ConcurrentDictionary<string, SshTarget> _targets = new();

    public Sandbox Name(SandboxOwner owner) => new(ResourceId($"sandbox-{Guid.NewGuid():N}"), owner);

    public async Task CreateAsync(
        Sandbox sandbox, SandboxMachine machine, IReadOnlyCollection<string> repositoriesInScope, bool mayPush, CancellationToken cancellationToken)
    {
        var name = VirtualMachine(sandbox);
        try
        {
            await ProvisionAsync(name, sandbox.Owner, machine, repositoriesInScope, mayPush, cancellationToken);
        }
        catch (Exception exception)
        {
            // A Sandbox that isn't ready goes again: its VM would cost money for nothing.
            string? leftOver = null;
            try
            {
                await RemoveAsync(name, CancellationToken.None);
            }
            catch (Exception removing)
            {
                leftOver = $"\n\nRemoving the half-created Sandbox failed too, so delete what is left of {name} in {settings.ResourceGroup} yourself:\n{removing.Message}";
            }
            if (exception is AzureScriptException or SandboxUnreachableException or SandboxCreationFailedException)
            {
                throw new SandboxCreationFailedException(exception.Message + leftOver);
            }
            throw;
        }
    }

    /// <summary>Creates the Sandbox's VM and what it needs, and waits until it is set up.</summary>
    private async Task ProvisionAsync(
        string name, SandboxOwner owner, SandboxMachine machine, IReadOnlyCollection<string> repositoriesInScope, bool mayPush,
        CancellationToken cancellationToken)
    {
        // Checked before anything is created: a Sandbox that can't clone shouldn't cost a VM.
        var token = repositoriesInScope.Count == 0 ? null : await TokenForCloningAsync(repositoriesInScope, cancellationToken);

        var userKey = SshKeyPair.Generate();
        var hostKey = SshKeyPair.Generate();
        await secrets.SetAsync(KeySecret(name), userKey.PrivateKey, cancellationToken);

        // Deployed, nothing on the internet reaches a Sandbox: only Overseer, from its own subnet.
        var allowedAddress = settings.PrivateNetwork ? OverseerSubnet : (await developerAddress(cancellationToken)).ToString();
        await _createNetwork.RunAsync(() => RunAsync("create-sandbox-network", ScriptParameters(), cancellationToken), cancellationToken);
        // The host's private key goes to Azure in a file only the user may read, rather than on a command line.
        var customData = Path.Combine(Path.GetTempPath(), $"overseer-{name}-cloud-init.yaml");
        string address;
        try
        {
            await using (var file = new StreamWriter(UserOnlyFile.CreateNew(customData)))
            {
                await file.WriteAsync(SandboxCloudInit.For(hostKey));
            }
            var created = await RunAsync("create-sandbox", new Dictionary<string, string>(ScriptParameters(name))
            {
                ["Size"] = machine.VmSize,
                ["Spot"] = machine.Spot ? "true" : "false",
                ["Username"] = Username,
                ["OwnerKind"] = owner.Kind.ToString(),
                ["OwnerId"] = owner.Id.ToString(),
                ["HostKeyFingerprint"] = hostKey.Fingerprint,
                ["PublicKey"] = userKey.PublicKey,
                ["CustomDataFile"] = customData,
                ["AllowedAddress"] = allowedAddress,
            }, cancellationToken);
            address = created is [.., var last] ? last : throw new SandboxCreationFailedException($"Azure gave no IP address for the Sandbox {name}.");
        }
        finally
        {
            File.Delete(customData);
        }

        var target = new SshTarget(address, SshPort, Username, userKey.PrivateKey, hostKey.Fingerprint);
        await WaitForSetupAsync(target, cancellationToken);
        if (token is not null)
        {
            await CloneAsync(target, token, repositoriesInScope, mayPush, cancellationToken);
        }
        _targets[name] = target;
    }

    /// <summary>Where each Repository in scope is cloned in a Sandbox: <c>/home/overseer/repositories/owner/name</c>.</summary>
    public static string CloneDirectory(string repository) => $"/home/{Username}/repositories/{repository}";

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$")]
    private static partial Regex RepositoryNamePattern();

    private async Task<string> TokenForCloningAsync(IReadOnlyCollection<string> repositoriesInScope, CancellationToken cancellationToken)
    {
        foreach (var repository in repositoriesInScope)
        {
            if (!RepositoryNamePattern().IsMatch(repository) || repository.Split('/').Any(part => part is "." or ".."))
            {
                throw new SandboxCreationFailedException($"{repository} is not a GitHub repository as owner/name, so it can't be cloned into the Sandbox.");
            }
        }
        var token = await secretReader.ReadAsync(GitHubToken.SecretName, cancellationToken);
        return token is { Length: > 0 } && GitHubToken.IsWellFormed(token)
            ? token
            : throw new SandboxCreationFailedException("Set a GitHub token first: Overseer clones the Repositories in scope with it.");
    }

    /// <summary>
    /// Clones the Repositories in scope on their default branches, with the token from a git credential helper and from
    /// <c>gh</c>'s login, never in a remote URL. Without push access, the token and both logins are removed afterwards.
    /// </summary>
    private async Task CloneAsync(
        SshTarget target, string token, IReadOnlyCollection<string> repositoriesInScope, bool mayPush, CancellationToken cancellationToken)
    {
        // The token reaches the Sandbox in this one command over SSH, which nothing logs; it goes in a file only its user may read.
        await RunStepAsync(target, token, "Giving the Sandbox the GitHub token failed", $$"""
            set -e
            umask 077
            mkdir -p "$HOME/.overseer"
            printf '%s' '{{token}}' > "$HOME/.overseer/github-token"
            git config --global credential.helper '!f() { test "$1" = get && printf "username=x-access-token\npassword=%s\n" "$(cat "$HOME/.overseer/github-token")"; }; f'
            gh auth login --hostname github.com --with-token < "$HOME/.overseer/github-token"
            """, cancellationToken);

        foreach (var repository in repositoriesInScope)
        {
            var directory = CloneDirectory(repository);
            await RunStepAsync(target, token, $"Cloning {repository} failed", $"""
                set -e
                export GIT_TERMINAL_PROMPT=0
                mkdir -p '{directory}'
                git clone --quiet 'https://github.com/{repository}.git' '{directory}'
                """, cancellationToken);
        }

        if (!mayPush)
        {
            // Without the token file, the helper has nothing to give, and gh has no login: nothing in the Sandbox can write to GitHub.
            await RunStepAsync(target, token, "Taking the GitHub credentials away from the Sandbox failed", """
                rm -f "$HOME/.overseer/github-token"
                gh auth logout --hostname github.com || true
                rm -rf "$HOME/.config/gh"
                git config --global --unset-all credential.helper || true
                ! git config --global --get-all credential.helper
                """, cancellationToken);
        }
    }

    /// <summary>Runs one step of cloning, failing the creation with <paramref name="failure"/> and the step's output, without the token, when it fails.</summary>
    private async Task RunStepAsync(SshTarget target, string token, string failure, string command, CancellationToken cancellationToken)
    {
        var result = await ssh.RunAsync(target, command, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new SandboxCreationFailedException($"{failure}: {result.Output.Replace(token, "***").Trim()}");
        }
    }

    /// <exception cref="InvalidOperationException">The Sandbox does not exist, or its key is gone.</exception>
    /// <exception cref="SandboxUnreachableException">The Sandbox does not accept SSH, for instance from a new public IP of the developer's.</exception>
    public async Task<CommandResult> RunCommandAsync(Sandbox sandbox, string command, CancellationToken cancellationToken) =>
        await ssh.RunAsync(await TargetAsync(VirtualMachine(sandbox), cancellationToken), command, cancellationToken);

    /// <summary>
    /// Runs <c>opencode acp</c> over SSH, with the Sandbox's own key, as <see cref="AcpAgentSession"/> talks to it. The harness
    /// configuration and the environment reach the process over its standard input, not in the command: a command line shows
    /// to everyone on the Sandbox, and the environment holds model provider tokens.
    /// </summary>
    /// <exception cref="InvalidOperationException">The Sandbox does not exist, or its key is gone; or a variable's name is not one.</exception>
    /// <exception cref="SandboxUnreachableException">The Sandbox does not accept SSH, for instance from a new public IP of the developer's.</exception>
    /// <exception cref="AgentSessionFailedException">The agent did not start.</exception>
    public async Task<IAgentSession> StartAgentSessionAsync(
        Sandbox sandbox, AgentRole role, AgentHarnessConfiguration harness, IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var variables = new Dictionary<string, string>(environment) { ["OPENCODE_CONFIG_CONTENT"] = harness.Json };
        foreach (var name in variables.Keys.Where(name => !EnvironmentVariableName().IsMatch(name)))
        {
            throw new InvalidOperationException($"'{name}' is not a name an environment variable can have.");
        }

        var exports = string.Concat(variables.Select(variable => $"export {variable.Key}='{variable.Value.Replace("'", "'\\''")}'\n"));
        var setup = Convert.ToBase64String(Encoding.UTF8.GetBytes(exports));
        var target = await TargetAsync(VirtualMachine(sandbox), cancellationToken);
        var pidFile = $"$HOME/.overseer/agent-{Guid.NewGuid():N}.pid";
        var process = await ssh.StartAsync(target, $$"""
            set -e
            IFS= read -r setup
            eval "$(printf '%s' "$setup" | base64 -d)"
            unset setup
            mkdir -p "$HOME/.overseer" '{{AgentDirectory}}'
            echo $$ > "{{pidFile}}"
            cd '{{AgentDirectory}}'
            exec opencode acp
            """, cancellationToken);
        try
        {
            await process.WriteLineAsync(setup, cancellationToken);
            return await AcpAgentSession.StartAsync(
                process, AgentDirectory, AgentHarnessConfiguration.AgentName(role),
                [.. environment.Values.Where(value => value.Length > 0)], () => EndAgentAsync(target, pidFile), cancellationToken);
        }
        catch (IOException exception)
        {
            await process.DisposeAsync();
            throw new AgentSessionFailedException($"The agent did not start: {exception.Message}");
        }
        catch
        {
            await process.DisposeAsync();
            throw;
        }
    }

    /// <summary>Where agents work in a Sandbox: above the Repositories in scope, which are cloned below it.</summary>
    private static string AgentDirectory => $"/home/{Username}/repositories";

    /// <summary>
    /// Ends the agent process and what it started, should closing the connection not have: OpenCode's launcher starts the
    /// program that does the work, which a signal to the launcher alone leaves running.
    /// </summary>
    private async Task EndAgentAsync(SshTarget target, string pidFile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await ssh.RunAsync(target, $$"""
            end() { for child in $(pgrep -P "$1"); do end "$child"; done; kill -TERM "$1" 2>/dev/null || true; }
            [ -f "{{pidFile}}" ] && end "$(cat "{{pidFile}}")"
            rm -f "{{pidFile}}"
            exit 0
            """, timeout.Token);
    }

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex EnvironmentVariableName();

    /// <inheritdoc cref="ISandboxProvider.TakeFileAsync"/>
    /// <exception cref="InvalidOperationException">
    /// The Sandbox does not exist, or its key is gone; or the file is there, but couldn't be read or removed.
    /// </exception>
    /// <exception cref="SandboxUnreachableException">The Sandbox does not accept SSH, for instance from a new public IP of the developer's.</exception>
    public async Task<string?> TakeFileAsync(Sandbox sandbox, string path, CancellationToken cancellationToken)
    {
        const int NoSuchFile = 3;
        var quoted = $"'{path.Replace("'", "'\\''")}'";
        var result = await RunCommandAsync(sandbox, $"""
            [ -f {quoted} ] || exit {NoSuchFile}
            cat -- {quoted} && rm -f -- {quoted}
            """, cancellationToken);
        return result.ExitCode switch
        {
            0 => result.Output,
            NoSuchFile => null,
            _ => throw new InvalidOperationException($"The file {path} in the Sandbox could not be taken: {result.Output}"),
        };
    }

    public Task DestroyAsync(Sandbox sandbox, CancellationToken cancellationToken) => RemoveAsync(VirtualMachine(sandbox), cancellationToken);

    public async Task<IReadOnlyList<Sandbox>> ListAsync(CancellationToken cancellationToken)
    {
        var sandboxes = new List<Sandbox>();
        foreach (var line in await RunAsync("list-sandboxes", ScriptParameters(), cancellationToken))
        {
            // A VM tagged with an owner Overseer doesn't know isn't one of its Sandboxes.
            if (line.Split('\t') is [var name, var kind, var id]
                && Enum.TryParse<SandboxOwnerKind>(kind, out var ownerKind)
                && Guid.TryParse(id, out var ownerId))
            {
                sandboxes.Add(new Sandbox(ResourceId(name), new SandboxOwner(ownerKind, ownerId)));
            }
        }
        return sandboxes;
    }

    /// <summary>Where the Sandbox is, from Azure, and the key to it, from the secret store, unless Overseer knows already.</summary>
    private async Task<SshTarget> TargetAsync(string name, CancellationToken cancellationToken)
    {
        if (_targets.TryGetValue(name, out var known))
        {
            return known;
        }

        string[] found;
        try
        {
            found = await RunAsync("find-sandbox", ScriptParameters(name), cancellationToken);
        }
        catch (AzureScriptException exception)
        {
            throw new InvalidOperationException($"The Sandbox {name} could not be found in {settings.ResourceGroup}: {exception.Message}", exception);
        }
        var key = await secretReader.ReadAsync(KeySecret(name), cancellationToken)
            ?? throw new InvalidOperationException($"The SSH key of the Sandbox {name} is not in the secret store, so Overseer can't log in to it.");
        return found is [var address, var hostKey]
            ? _targets.GetOrAdd(name, new SshTarget(address, SshPort, Username, key, hostKey))
            : throw new InvalidOperationException(
                $"The Sandbox {name} has no {(settings.PrivateNetwork ? "private" : "public")} IP address or host key tag, so Overseer can't log in to it.");
    }

    /// <summary>Deletes the Sandbox's VM and everything created with it, then its key.</summary>
    private async Task RemoveAsync(string name, CancellationToken cancellationToken)
    {
        _targets.TryRemove(name, out _);
        await RunAsync("destroy-sandbox", ScriptParameters(name), cancellationToken);
        // The key goes last: until the VM is gone, it is how Overseer reaches it.
        await secrets.RemoveAsync(KeySecret(name), cancellationToken);
    }

    /// <summary>
    /// Runs one of the provider's scripts and returns the lines it printed, once the Azure CLI is logged in as the
    /// Managed Identity, if there is one.
    /// </summary>
    private async Task<string[]> RunAsync(string script, Dictionary<string, string> parameters, CancellationToken cancellationToken)
    {
        if (settings.ManagedIdentityClientId is { } clientId)
        {
            await _logIn.RunAsync(() => _azure.RunAsync("log-in", new Dictionary<string, string> { ["ClientId"] = clientId }, cancellationToken), cancellationToken);
        }
        return Lines(await _azure.RunAsync(script, parameters, cancellationToken));
    }

    /// <summary>
    /// The environment with an Azure CLI configuration of the provider's own, so that logging in as the Managed Identity
    /// replaces no other login on the machine.
    /// </summary>
    private static Dictionary<string, string?> WithOwnAzureCliLogin(IReadOnlyDictionary<string, string?> environment) =>
        new(environment) { ["AZURE_CONFIG_DIR"] = Directory.CreateTempSubdirectory("overseer-azure-cli-").FullName };

    /// <summary>
    /// Waits until the Sandbox accepts SSH and cloud-init has set it up, reconnecting while it boots or when the connection
    /// drops, but no longer than <see cref="SetUpWithin"/>.
    /// </summary>
    private async Task WaitForSetupAsync(SshTarget target, CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(SetUpWithin, time);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        SandboxUnreachableException? lastUnreachable = null;
        try
        {
            while (true)
            {
                CommandResult status;
                try
                {
                    status = await ssh.RunAsync(target, SetupStatusCommand, waiting.Token);
                }
                catch (SandboxUnreachableException unreachable)
                {
                    lastUnreachable = unreachable;
                    await Task.Delay(ReachRetryInterval, time, waiting.Token);
                    continue;
                }

                // cloud-init exits with 2 when it finished despite recoverable errors, such as a deprecated setting.
                if (status.ExitCode is 0 or 2)
                {
                    return;
                }
                throw new SandboxCreationFailedException($"Setting up the Sandbox failed. cloud-init reported:\n{status.Output}");
            }
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new SandboxCreationFailedException(
                $"The Sandbox was not set up within {SetUpWithin.TotalMinutes:0} minutes of its creation." +
                (lastUnreachable is null ? "" : $" The last attempt to reach it over SSH failed: {lastUnreachable.Message}"));
        }
    }

    /// <summary>
    /// What every script needs: where the Sandboxes are, whether they are on the private network, and the VM of the
    /// Sandbox it is about, if any.
    /// </summary>
    private Dictionary<string, string> ScriptParameters(string? virtualMachine = null)
    {
        var parameters = new Dictionary<string, string>
        {
            ["SubscriptionId"] = settings.SubscriptionId,
            ["ResourceGroup"] = settings.ResourceGroup,
            ["PrivateNetwork"] = settings.PrivateNetwork ? "true" : "false",
        };
        if (virtualMachine is not null)
        {
            parameters["VirtualMachine"] = virtualMachine;
        }
        return parameters;
    }

    private string ResourceId(string name) =>
        $"/subscriptions/{settings.SubscriptionId}/resourceGroups/{settings.ResourceGroup}/providers/Microsoft.Compute/virtualMachines/{name}";

    /// <summary>The name of the Sandbox's VM, which everything else of it is named after.</summary>
    /// <exception cref="ArgumentException">The Sandbox is not a VM in the Sandbox resource group.</exception>
    private string VirtualMachine(Sandbox sandbox)
    {
        var inGroup = ResourceId("");
        return sandbox.ResourceId.StartsWith(inGroup, StringComparison.OrdinalIgnoreCase) && sandbox.ResourceId[inGroup.Length..] is { Length: > 0 } name && !name.Contains('/')
            ? name
            : throw new ArgumentException($"{sandbox.ResourceId} is not a VM in the Sandbox resource group {settings.ResourceGroup}.", nameof(sandbox));
    }

    /// <summary>The secret holding the private key Overseer logs in to the Sandbox with.</summary>
    private static string KeySecret(string name) => $"{name}-ssh-key";

    private static string[] Lines(string output) => output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Something done once per process, by one caller at a time, and tried again by the next caller if it failed.</summary>
    private sealed class Once
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private bool _done;

        public async Task RunAsync(Func<Task> action, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken);
            try
            {
                if (!_done)
                {
                    await action();
                    _done = true;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
