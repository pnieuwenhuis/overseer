namespace Overseer.Sandboxes;

/// <summary>
/// The port to the machines Sandboxes run on: Azure VMs, or the fake for offline work and tests.
/// The provider keeps no records of its own; <see cref="ListAsync"/> reports what actually exists.
/// </summary>
public interface ISandboxProvider
{
    /// <summary>Creates a Sandbox holding the Repositories in scope, tagged with its owner. Completes once the Sandbox is ready for use.</summary>
    /// <param name="repositoriesInScope">GitHub repositories as <c>owner/name</c>.</param>
    /// <exception cref="SandboxCreationFailedException">The Sandbox could not be created; the message says why.</exception>
    Task<Sandbox> CreateAsync(SandboxOwner owner, IReadOnlyCollection<string> repositoriesInScope, CancellationToken cancellationToken);

    /// <summary>Runs a shell command in the Sandbox and waits for it to finish.</summary>
    Task<CommandResult> RunCommandAsync(Sandbox sandbox, string command, CancellationToken cancellationToken);

    /// <summary>Starts an agent in the given role on <paramref name="prompt"/>. Completes once the session runs; follow it through the returned session.</summary>
    Task<IAgentSession> StartAgentSessionAsync(Sandbox sandbox, AgentRole role, string prompt, CancellationToken cancellationToken);

    /// <summary>Removes the Sandbox and everything created with it.</summary>
    Task DestroyAsync(Sandbox sandbox, CancellationToken cancellationToken);

    /// <summary>Every Sandbox that exists, with its owner tag.</summary>
    Task<IReadOnlyList<Sandbox>> ListAsync(CancellationToken cancellationToken);
}

/// <param name="ResourceId">The provider's identifier for the machine, such as an Azure resource ID.</param>
public sealed record Sandbox(string ResourceId, SandboxOwner Owner);

/// <param name="Output">Standard output and standard error together, as the command wrote them.</param>
public sealed record CommandResult(int ExitCode, string Output);

/// <summary>What a Sandbox was created for: an Intent or a Repository Setup.</summary>
public sealed record SandboxOwner(SandboxOwnerKind Kind, Guid Id);

public enum SandboxOwnerKind
{
    Intent,
    RepositorySetup,
}

/// <summary>The provider could not create a Sandbox. The message is the provider's own error, such as Azure's, to show the user.</summary>
public sealed class SandboxCreationFailedException(string message) : Exception(message);
