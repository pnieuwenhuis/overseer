using Overseer.GitHub;
using Overseer.Sandboxes;

namespace Overseer.Repositories;

/// <summary>
/// The agent part of a Repository Setup: in a temporary Sandbox owned by the Repository Setup, one agent session writes
/// the new repository's starter README, agent guidance and Repository Manifest, and pushes them to main.
/// </summary>
public sealed class StarterFiles(SandboxRegistry sandboxes, IGitHubGateway gitHub)
{
    /// <summary>The Repository Manifest, which must be on main when the agent is done.</summary>
    public const string ManifestPath = RepositoryManifest.Path;

    /// <summary>
    /// Has an agent write the starter files of the Repository Setup's repository, and destroys its Sandbox again.
    /// </summary>
    /// <param name="agentWorking">Called once the Sandbox is ready, as the agent starts.</param>
    /// <returns>Why the starter files aren't on main; <c>null</c> once they are.</returns>
    public async Task<string?> WriteAsync(RepositorySetup setup, Func<Task> agentWorking, CancellationToken cancellationToken)
    {
        var owner = new SandboxOwner(SandboxOwnerKind.RepositorySetup, setup.Id);
        Sandbox sandbox;
        try
        {
            sandbox = await sandboxes.CreateAsync(owner, [setup.Repository.ToString()], cancellationToken);
        }
        catch (SandboxCreationFailedException exception)
        {
            return $"The Sandbox could not be created: {exception.Message}";
        }
        try
        {
            await agentWorking();
            await using (var session = await sandboxes.StartAgentSessionAsync(sandbox, AgentRole.Developer, cancellationToken))
            {
                var turn = await session.SendAsync(Instructions(setup), cancellationToken);
                await foreach (var _ in turn.Updates.WithCancellation(cancellationToken))
                {
                }
                AgentOutcome outcome;
                try
                {
                    outcome = await turn.Outcome.WaitAsync(cancellationToken);
                }
                catch (AgentSessionFailedException exception)
                {
                    return $"The agent session failed: {exception.Message}";
                }
                if (outcome is AgentOutcome.AskedQuestion asked)
                {
                    return $"The agent asked a question, which no one answers during a Repository Setup: {asked.Question}";
                }
            }
            return await gitHub.CheckFileAsync(setup.Repository, "main", ManifestPath, cancellationToken) switch
            {
                GitHubFileCheck.Found => null,
                GitHubFileCheck.NotFound => $"The agent finished, but {ManifestPath} is not on main of {setup.Repository}.",
                GitHubFileCheck.Fails fails => $"The agent finished, but GitHub could not say whether {ManifestPath} is on main: {fails.Error}",
                null => $"The agent finished, but without a GitHub token Overseer can't check that {ManifestPath} is on main.",
                _ => throw new InvalidOperationException("GitHub either finds the file, doesn't, or fails."),
            };
        }
        finally
        {
            await sandboxes.DestroyOrLeaveAsync(sandbox);
        }
    }

    /// <summary>What the agent is asked to do: everything it needs, since no one answers its questions.</summary>
    private static string Instructions(RepositorySetup setup) =>
        $"""
        You are setting up {setup.Repository}, a new GitHub repository ({setup.Repository.WebUrl}). It is empty: no commits,
        no branches. It is in this Sandbox; if you find no clone of it, clone it.

        What the repository is for, as the user stated it:

        {setup.Purpose}

        Its tech stack, as the user stated it:

        {setup.TechStack}

        Write these starter files, so that the repository is ready for agents to work in:

        - `README.md`: what the repository is for, its tech stack, and how to set it up, build it, test it and run it.
        - `AGENTS.md`: guidance for the AI agents that will work in it: its layout and conventions, the commands to build
          and test it, and anything an agent must know before changing it.
        - `.overseer.yml`: the Repository Manifest, which tells Overseer how to work with the repository. It holds the
          shell commands `setup` (prepares a fresh clone, such as installing dependencies), `build` and `test`, run from
          the repository's root, and optionally `compose`, the path of a Docker Compose file that starts the services the
          tests need. For example:

          ```yaml
          setup: npm ci
          build: npm run build
          test: npm test
          ```

        Add the smallest project skeleton the tech stack needs for those three commands to succeed, and run them to check
        that they do.

        Then commit everything and push it directly to the `main` branch: `git push origin HEAD:main`. Do not open a pull
        request.

        No one can answer questions during this setup. Where the purpose or tech stack leave something open, decide for
        yourself, and say what you decided in the README.
        """;
}
