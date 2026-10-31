using System.Diagnostics.CodeAnalysis;

namespace Overseer.Sandboxes;

/// <summary>
/// The port to the machines Sandboxes run on: Azure VMs, or the fake for offline work and tests.
/// The provider keeps no records of its own; <see cref="ListAsync"/> reports what actually exists.
/// </summary>
public interface ISandboxProvider
{
    /// <summary>
    /// Creates a Sandbox holding the Repositories in scope, tagged with its owner, on the given machine. Completes once
    /// the Sandbox is ready for use.
    /// </summary>
    /// <param name="repositoriesInScope">GitHub repositories as <c>owner/name</c>.</param>
    /// <exception cref="SandboxCreationFailedException">The Sandbox could not be created; the message says why.</exception>
    Task<Sandbox> CreateAsync(
        SandboxOwner owner, SandboxMachine machine, IReadOnlyCollection<string> repositoriesInScope, CancellationToken cancellationToken);

    /// <summary>Runs a shell command in the Sandbox and waits for it to finish.</summary>
    Task<CommandResult> RunCommandAsync(Sandbox sandbox, string command, CancellationToken cancellationToken);

    /// <summary>
    /// Starts an agent in the given role, configured by <paramref name="harness"/> and running its role's agent with that
    /// agent's model, ready for its first message. Dispose the session to stop the agent.
    /// </summary>
    Task<IAgentSession> StartAgentSessionAsync(
        Sandbox sandbox, AgentRole role, AgentHarnessConfiguration harness, CancellationToken cancellationToken);

    /// <summary>Removes the Sandbox and everything created with it.</summary>
    Task DestroyAsync(Sandbox sandbox, CancellationToken cancellationToken);

    /// <summary>Every Sandbox that exists, with its owner tag.</summary>
    Task<IReadOnlyList<Sandbox>> ListAsync(CancellationToken cancellationToken);
}

/// <param name="ResourceId">The provider's identifier for the machine, such as an Azure resource ID.</param>
public sealed record Sandbox(string ResourceId, SandboxOwner Owner);

/// <summary>The machine a Sandbox runs on, as chosen on the Settings page when it is created.</summary>
/// <param name="VmSize">The name of one of <see cref="VmSizes"/>.</param>
/// <param name="Spot">
/// Whether it is an Azure spot instance: cheaper, but Azure may evict it when it needs the capacity back, which deletes it.
/// </param>
public sealed record SandboxMachine(string VmSize, bool Spot)
{
    /// <summary>The VM sizes a Sandbox can have, smallest first.</summary>
    public static IReadOnlyList<SandboxVmSize> VmSizes { get; } =
    [
        new("Standard_D2_v5", "2 vCPUs, 8 GiB"),
        // Enough for Docker, a build and a browser at once.
        new("Standard_D4s_v5", "4 vCPUs, 16 GiB"),
    ];

    public static SandboxMachine Default { get; } = new("Standard_D4s_v5", Spot: false);

    /// <summary>Whether <paramref name="name"/> is one of <see cref="VmSizes"/>.</summary>
    public static bool IsVmSize([NotNullWhen(true)] string? name) => VmSizes.Any(size => size.Name == name);
}

/// <param name="Name">Azure's name for the size.</param>
/// <param name="Description">How big it is, so the user can weigh speed against cost.</param>
public sealed record SandboxVmSize(string Name, string Description);

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
